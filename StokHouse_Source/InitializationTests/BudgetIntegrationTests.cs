using System.Xml.Linq;
using StardewValley;
using StokHouse.Core;
using StokHouse.FamilyBudget;

namespace StokHouse;

internal static class BudgetIntegrationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("family budgets require an explicit playtest build", () =>
        {
#if FAMILY_BUDGET_PLAYTEST
            Check(ModEntry.BudgetBuildEnabled);
#else
            Check(!ModEntry.BudgetBuildEnabled);
#endif
        });

        test("native-copy proof activates only the consenting pair and preserves a personal wallet", () =>
        {
            using var world = new World();
            var id = world.Invite(1, ChangeKind.Merge);
            Reject(() => world.Session.Answer(7, id, true));
            world.Session.Answer(2, id, true);
            world.Session.PrepareNight();
            Check(world.Session.View(1).Account is null);
            Check(world.Gold(1) == 0 && world.Gold(2) == 0 && world.Gold(7) == 700);
            world.WriteSave();
            Check(world.Session.CompleteNight() == DiskState.Written);
            Check(world.Session.View(1).Available == 15_000 && world.Session.View(2).Available == 15_000 &&
                world.Session.View(7).Available is null);
            Check(world.Reload().View(1).Account == id);
            Check(!world.Session.MayLeave(2));
        });

        test("three independently staged pairs collect only their own remaining gold", () =>
        {
            using var world = new World();
            foreach (long owner in new long[] { 1, 3, 5 })
                world.Session.Answer(owner + 1, world.Invite(owner, ChangeKind.Merge), true);
            world.Save();
            world.SetGold(1, 100);
            world.SetGold(2, 200);
            world.SetGold(4, 400);
            world.SetGold(7, 900);
            world.Session.PrepareNight();
            Check(world.Session.View(1).Available == 15_000);
            world.WriteSave();
            Check(world.Session.CompleteNight() == DiskState.Written);
            Check(world.Session.View(1).Available == 15_300 && world.Session.View(4).Available == 3_800 &&
                world.Session.View(6).Available == 1_100 && world.Session.View(7).Available is null);
            Check(world.Gold(7) == 900 && world.Gold(1) == 0 && world.Gold(4) == 0);
        });

        test("a split requires both residents and applies the same quoted amounts after disk confirmation", () =>
        {
            using var world = new World();
            world.Session.Answer(2, world.Invite(1, ChangeKind.Merge), true);
            world.Save();
            var request = world.Invite(2, ChangeKind.Split);
            Check(world.Session.View(1).Proposal?.Quote == new SplitQuote(7_500, 7_500));
            Reject(() => world.Session.Answer(2, request, true));
            world.Session.Answer(1, request, true);
            world.Session.PrepareNight();
            Check(!world.Session.MayLeave(2));
            world.WriteSave();
            Check(world.Session.CompleteNight() == DiskState.Written);
            Check(world.Session.MayLeave(2) && world.Gold(1) == 7_500 && world.Gold(2) == 7_500);
            Check(world.Reload().View(2).Account is null);
        });

        test("a partial native copy cannot publish a family balance or accept a second debit", () =>
        {
            using var world = new World();
            world.Session.Answer(2, world.Invite(1, ChangeKind.Merge), true);
            world.Session.PrepareNight();
            world.WriteSave(staleFarmer: 2);
            Check(world.Session.CompleteNight() == DiskState.Unknown);
            Check(world.Session.Saving && world.Session.View(2).Account is null);
            Reject(() => world.Reload());
            Reject(() => world.Invite(3, ChangeKind.Merge));
            world.WriteSave();
            Check(world.Session.CompleteNight() == DiskState.Written && world.Session.View(2).Available == 15_000);
        });

        test("when the saved file is still the previous snapshot wallet changes are undone", () =>
        {
            using var world = new World();
            world.WriteSave();
            world.Session.Answer(2, world.Invite(1, ChangeKind.Merge), true);
            world.Session.PrepareNight();
            Check(world.Session.CompleteNight() == DiskState.NotWritten);
            Check(world.Gold(1) == 10_000 && world.Gold(2) == 5_000);
            Check(world.Session.View(1).Proposal?.Accepted == true);
        });

        test("a different file without the new journal is not treated as the previous save", () =>
        {
            using var world = new World();
            world.WriteSave();
            world.Session.Answer(2, world.Invite(1, ChangeKind.Merge), true);
            world.Session.PrepareNight();
            world.WriteSave(omitJournal: true);
            Check(world.Session.CompleteNight() == DiskState.Unknown && world.Session.Saving);
            Check(world.Session.View(2).Account is null && world.Gold(2) == 0);
        });

        test("parting during a pending budget and redirecting another pair's reservation are denied", () =>
        {
            using var world = new World();
            world.Session.Answer(2, world.Invite(1, ChangeKind.Merge), true);
            world.Session.Answer(4, world.Invite(3, ChangeKind.Merge), true);
            world.Save();
            Check(!world.Session.MayLeave(2));
            var good = new UpgradeRequest(Guid.NewGuid(), 1, 2, 0, DateTimeOffset.UtcNow.AddMinutes(1));
            Reject(() => FamilyConstruction.Authorize(world.Session.Book, good with { Owner = 3 }, true));
            var order = FamilyConstruction.Authorize(world.Session.Book, good, true);
            FamilyConstruction.VerifySavedOrder(order.Book, order.Order);
            Reject(() => FamilyConstruction.VerifySavedOrder(order.Book, order.Order with { FamilyAccount = Guid.NewGuid() }));
            var pending = order.Advance(new(good.Id, ReceiptKind.MaterialsHeld), null, false, false, null);
            var paid = pending.Advance(null, new(good.Id, ReceiptKind.Started), false, false, null);
            var repeat = paid.Advance(null, new(good.Id, ReceiptKind.Started), false, false, null);
            Check(repeat == paid && paid.Book.ForPlayer(1)?.Balance == 5_000);
            Check(paid.Book.ForPlayer(3)?.Balance == 3_400 && world.Gold(1) == 0 && world.Gold(2) == 0);
        });

        test("a household with extra occupants or a solo farmer cannot be presented as a pair", () =>
        {
            var homes = new Households(new HouseholdData(1, new() { [2] = 1, [3] = 1, [5] = 4 }, new()));
            Check(ModEntry.BudgetHousemate(homes, 2) is null && ModEntry.BudgetHousemate(homes, 7) is null);
            Check(ModEntry.BudgetHousemate(homes, 4) == 5 && ModEntry.BudgetHousemate(homes, 5) == 4);
        });
    }

    private static void Check(bool result)
    {
        if (!result)
            throw new InvalidOperationException("Family budget integration assertion failed.");
    }

    private static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("The integration accepted an invalid action.");
    }

    private sealed class World : IDisposable
    {
        private readonly string path = Path.GetTempFileName();
        private readonly GameApi game = new();
        private readonly Households homes = new(new HouseholdData(1, new() { [2] = 1, [4] = 3, [6] = 5 }, new()));
        private readonly Dictionary<long, Farmer> players;
        internal readonly BudgetSession Session;

        internal World()
        {
            Game1.MasterPlayer.team = new TestTeam { Separate = true };
            players = Enumerable.Range(1, 7).ToDictionary(id => (long)id, id => id == 1 ? Game1.MasterPlayer :
                new Farmer { UniqueMultiplayerID = id, team = Game1.MasterPlayer.team });
            var balances = new[] { 10_000, 5_000, 3_000, 400, 500, 600, 700 };
            foreach (var (id, _) in players)
                SetGold(id, balances[(int)id - 1]);
            game.Store(SavedBudgetProbe.JournalKey, "");
            Session = Reload();
        }

        internal int Gold(long actor) => game.Gold(players[actor]);
        internal void SetGold(long actor, int value) => game.SetPersonalGold(players[actor], value);
        private string? Journal() => game.Load(SavedBudgetProbe.JournalKey) is { Length: > 0 } raw ? raw : null;

        internal BudgetSession Reload()
        {
            var book = BudgetSession.Load(game.FarmIdentity, Journal(), players.Keys);
            if (book.LastCheckpoint != Guid.Empty && !BudgetSession.VerifyWalletProof(path, game.FarmIdentity, book))
                throw new InvalidOperationException("Incomplete fixture checkpoint.");
            return new BudgetSession(book, game.FarmIdentity,
                () => path, Gold, SetGold, Journal, value => game.Store(SavedBudgetProbe.JournalKey, value ?? ""),
                id => ModEntry.BudgetHousemate(homes, id), () => true, _ => true);
        }

        internal Guid Invite(long actor, ChangeKind kind)
        {
            var id = Guid.NewGuid();
            Session.Propose(actor, id, kind);
            return id;
        }

        internal void Save()
        {
            Session.PrepareNight();
            WriteSave();
            Check(Session.CompleteNight() == DiskState.Written);
        }

        internal void WriteSave(long staleFarmer = 0, bool omitJournal = false)
        {
            XElement Farmer(long id, string name) => new(name, new XElement("UniqueMultiplayerID", id),
                new XElement("money", id == staleFarmer ? Gold(id) + 500 : Gold(id)));
            var host = Farmer(1, "player");
            if (!omitJournal && Journal() is { } journal)
                host.Add(new XElement("modData", new XElement("SerializableDictionaryOfStringString",
                    new XElement("item", new XElement("key", new XElement("string", SavedBudgetProbe.JournalKey)),
                        new XElement("value", new XElement("string", journal))))));
            new XElement("SaveGame", host, new XElement("uniqueIDForThisGame", game.FarmIdentity),
                new XElement("farmhands", players.Keys.Where(id => id != 1).Select(id => Farmer(id, "Farmer"))))
                .Save(path);
        }

        public void Dispose() => File.Delete(path);
    }
}
