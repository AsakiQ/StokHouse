using System.Text.Json;
using StardewValley;
using StokHouse.Core;

namespace StokHouse;

internal static class PaymentRegression
{
    private const string Wood = "(O)388";
    private const string Hardwood = "(O)709";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    internal static void Run(Action<string, Action> test)
    {
        test("separate wallets charge the guest and start the host's construction", () =>
        {
            var world = new World(separate: true, ownerIsHost: true);
            world.RunOrder();
            Eq(5_000, world.Game.Gold(world.Payer));
            Eq(40_000, world.Game.Gold(world.Owner));
            Eq(150, Count(world.Payer));
            Eq(800, Count(world.Owner));
            Eq(3, GameApi.UpgradeDays(world.Owner));
            Eq(-1, GameApi.UpgradeDays(world.Payer));
            Eq(true, ReferenceEquals(Game1.player, Game1.MasterPlayer));
        });

        test("joint wallet, guest materials and a different farmhand's cabin stay separate", () =>
        {
            var world = new World(separate: false, ownerIsHost: false);
            world.RunOrder();
            Eq(5_000, world.Game.Gold(world.Payer));
            Eq(5_000, world.Game.Gold(world.Owner));
            Eq(150, Count(world.Payer));
            Eq(800, Count(world.Owner));
            Eq(3, GameApi.UpgradeDays(world.Owner));
            Eq(-1, GameApi.UpgradeDays(Game1.MasterPlayer));
        });

        test("hardwood second upgrade and gold-only cellar use their own costs", () =>
        {
            var second = new World(true, true, level: 1);
            second.Payer.Items[0] = ItemRegistry.Create(Hardwood, 120, 0, false);
            second.Payer.Items[1] = null;
            Eq<string?>(null, second.Game.PaymentResources(second.Payer, 1));
            second.RunOrder();
            Eq(5_000, second.Game.Gold(second.Payer));
            Eq(20, Count(second.Payer));

            var third = new World(true, false, level: 2);
            third.RunOrder();
            Eq(5_000, third.Game.Gold(third.Payer));
            Eq(600, Count(third.Payer));
            Eq(800, Count(third.Owner));
        });

        test("resources are checked on the payer and shortages change neither player", () =>
        {
            var world = new World(true, true);
            world.Payer.team.GetMoney(world.Payer).Value = 9_999;
            Eq("funds", world.Game.PaymentResources(world.Payer, 0));
            world.Payer.team.GetMoney(world.Payer).Value = 10_000;
            world.Payer.Items[1] = null;
            Eq("materials", world.Game.PaymentResources(world.Payer, 0));
            Eq<PaymentEdit?>(null, world.Game.PlanMaterials(world.Payer, Wood, -450));
            Eq(200, Count(world.Payer));
            Eq(800, Count(world.Owner));
        });

        test("two simultaneous orders share one request slot and duplicate IDs are archived", () =>
        {
            var houses = new Households(new HouseholdData(1, new() { [2] = 1, [4] = 3 }, new()));
            var id = Guid.NewGuid();
            Eq("upgrade-sent", houses.RequestUpgrade(2, 0, Now, out var first, id).Code);
            Eq(id, first!.Id);
            Eq("upgrade-pending", houses.RequestUpgrade(4, 0, Now, out _).Code);
            var closed = new UpgradePayment(first, true, true, PaymentPhase.Cancelled);
            var next = new UpgradePayment(first with { Id = Guid.NewGuid() }, true, true, RecentOrders: closed.Archive());
            Eq(true, next.HasSeen(id));
            Eq(false, next.HasSeen(Guid.NewGuid()));
        });

        test("duplicate snapshots and late receipts cannot take gold or start the order twice", () =>
        {
            var world = new World(true, true);
            world.TakeMaterials();
            Eq(-10_000, world.Step()!.GoldChange);
            Eq<PaymentTransition?>(null, world.Step());
            world.Start();
            Eq(PaymentPhase.Completed, world.Step()!.Payment.Phase);
            Eq<PaymentTransition?>(null, world.Step());
            Eq(5_000, world.Game.Gold(world.Payer));
            Eq(150, Count(world.Payer));
            Eq(3, GameApi.UpgradeDays(world.Owner));
        });

        test("cancelling before debit never touches wallet or materials", () =>
        {
            var world = new World(true, true);
            world.Order = world.Order.Cancel("upgrade-cancelled");
            world.ReturnMaterials();
            Eq(PaymentPhase.Cancelled, world.Step()!.Payment.Phase);
            Eq(15_000, world.Game.Gold(world.Payer));
            Eq(600, Count(world.Payer));
        });

        test("payer disconnect before scheduling keeps credits until the recorded material refund", () =>
        {
            var world = new World(true, false);
            world.TakeMaterials();
            world.Step();
            Eq(PaymentPhase.StopScheduling, world.Step(invalid: "upgrade-disconnected")!.Payment.Phase);
            Eq<PaymentTransition?>(null, world.Step(payerSavedOffline: true));
            world.OwnerReceipt = world.Receipt(ReceiptKind.Stopped);
            Eq(10_000, world.Step(payerSavedOffline: true)!.GoldChange);
            Eq<PaymentTransition?>(null, world.Step(payerSavedOffline: true));
            Eq(15_000, world.Game.Gold(world.Payer));
            Eq(150, Count(world.Payer));
            world.ReturnMaterials();
            Eq(PaymentPhase.Cancelled, world.Step()!.Payment.Phase);
            Eq(600, Count(world.Payer));
            Eq<PaymentTransition?>(null, world.Step());
            Eq(-1, GameApi.UpgradeDays(world.Owner));
        });

        test("stable offline owner snapshot permits a refund when no start was recorded", () =>
        {
            var world = new World(false, false);
            world.TakeMaterials();
            world.Step();
            Eq(10_000, world.Step(ownerSavedOffline: true, invalid: "upgrade-disconnected")!.GoldChange);
            world.ReturnMaterials();
            world.Step();
            Eq(15_000, world.Game.Gold(world.Payer));
            Eq(600, Count(world.Payer));
        });

        test("a start captured in a saved owner snapshot wins the cancellation race", () =>
        {
            var world = new World(true, false);
            world.TakeMaterials();
            world.Step();
            world.Order = world.Order.Cancel("upgrade-disconnected");
            world.Start();
            var settlement = world.Step(ownerSavedOffline: true)!;
            Eq(PaymentPhase.Completed, settlement.Payment.Phase);
            Eq(0, settlement.GoldChange);
            Eq(150, Count(world.Payer));
        });

        test("save reload after escrow resumes the order without re-debiting", () =>
        {
            var world = new World(true, true);
            world.TakeMaterials();
            world.Step();
            string journal = JsonSerializer.Serialize(world.Order);
            string payer = JsonSerializer.Serialize(world.PayerReceipt);
            world.Order = JsonSerializer.Deserialize<UpgradePayment>(journal)!;
            world.PayerReceipt = JsonSerializer.Deserialize<PaymentReceipt>(payer);
            Eq<PaymentTransition?>(null, world.Step());
            world.Start();
            world.OwnerReceipt = JsonSerializer.Deserialize<PaymentReceipt>(JsonSerializer.Serialize(world.OwnerReceipt));
            Eq(PaymentPhase.Completed, world.Step()!.Payment.Phase);
            Eq(5_000, world.Game.Gold(world.Payer));
            Eq(150, Count(world.Payer));
        });

        test("insufficient refund space changes no slots and the claim is payable after space is freed", () =>
        {
            var world = new World(true, true);
            world.TakeMaterials();
            world.Order = world.Order.Cancel("upgrade-cancelled");
            world.Payer.Items[0] = ItemRegistry.Create("(O)390", 20, 0, false);
            world.Payer.Items[2] = ItemRegistry.Create("(O)390", 20, 0, false);
            world.Payer.Items[1]!.Stack = 990;
            Eq<PaymentEdit?>(null, world.Game.PlanMaterials(world.Payer, Wood, +450));
            Eq(990, world.Payer.Items[1]!.Stack);
            world.Payer.Items[2] = null;
            world.ReturnMaterials();
            Eq(PaymentPhase.Cancelled, world.Step()!.Payment.Phase);
            Eq(1_440, Count(world.Payer, Wood));
        });

        test("failing the inventory receipt rolls back every edited slot and leaves exact stacks", () =>
        {
            var world = new World(true, true);
            var original = world.Payer.Items[0];
            var edits = world.Game.PlanMaterials(world.Payer, Wood, -450)!;
            edits.Add(() => throw new InvalidOperationException("receipt write failed"), () => { });
            Throws<InvalidOperationException>(edits.Commit);
            Eq(true, ReferenceEquals(original, world.Payer.Items[0]));
            Eq(200, world.Payer.Items[0]!.Stack);
            Eq(400, world.Payer.Items[1]!.Stack);
            Eq(15_000, world.Game.Gold(world.Payer));
        });

        test("failing the host journal write rolls back gold before retry", () =>
        {
            var world = new World(true, true);
            var edit = new PaymentEdit();
            world.Game.AddWalletEdit(edit, world.Payer, -10_000);
            edit.Add(() => throw new InvalidOperationException("journal write failed"), () => { });
            Throws<InvalidOperationException>(edit.Commit);
            Eq(15_000, world.Game.Gold(world.Payer));
            world.TakeMaterials();
            world.Step();
            Eq(5_000, world.Game.Gold(world.Payer));
        });

        test("unknown outcome is marked for review rather than paying or refunding again", () =>
        {
            var world = new World(true, true);
            world.TakeMaterials();
            world.Step();
            world.OwnerReceipt = world.Receipt(ReceiptKind.NeedsReview);
            Eq(true, world.Step()!.Payment.Paused);
            world.Order = JsonSerializer.Deserialize<UpgradePayment>(JsonSerializer.Serialize(world.Order))!;
            Eq<PaymentTransition?>(null, world.Step(ownerSavedOffline: true));
            Eq(5_000, world.Game.Gold(world.Payer));

            var edit = new PaymentEdit();
            edit.Add(() => throw new InvalidOperationException("partial setter"), () => throw new InvalidOperationException("failed undo"));
            Throws<PaymentRecoveryException>(edit.Commit);
        });

        test("old receipts cannot authorize another payment", () =>
        {
            var world = new World(true, true);
            world.PayerReceipt = new PaymentReceipt(Guid.NewGuid(), ReceiptKind.MaterialsHeld);
            Eq<PaymentTransition?>(null, world.Step());
            Eq(15_000, world.Game.Gold(world.Payer));
        });
    }

    private static int Count(Farmer farmer, string? id = null) => Enumerable.Range(0, farmer.Items.Count)
        .Select(i => farmer.Items[i]).Where(item => item is not null && (id is null || item.QualifiedItemId == id))
        .Sum(item => item!.Stack);

    private sealed class World
    {
        internal GameApi Game { get; } = new();
        internal Farmer Payer { get; }
        internal Farmer Owner { get; }
        internal UpgradePayment Order { get; set; }
        internal PaymentReceipt? PayerReceipt { get; set; }
        internal PaymentReceipt? OwnerReceipt { get; set; }

        internal World(bool separate, bool ownerIsHost, int level = 0)
        {
            var team = new TestTeam { Separate = separate };
            Game1.MasterPlayer.team = team;
            Game1.MasterPlayer.daysUntilHouseUpgrade.Value = -1;
            Payer = new Farmer { UniqueMultiplayerID = 2, team = team };
            Owner = ownerIsHost ? Game1.MasterPlayer : new Farmer { UniqueMultiplayerID = 3, team = team };
            Owner.HouseUpgradeLevel = level;
            team.GetMoney(Owner).Value = 40_000;
            team.GetMoney(Payer).Value = GameApi.Price(level).Gold + 5_000;
            Payer.Items[0] = ItemRegistry.Create(Wood, 200, 0, false);
            Payer.Items[1] = ItemRegistry.Create(Wood, 400, 0, false);
            Owner.Items[0] = ItemRegistry.Create(Wood, 800, 0, false);
            Order = new UpgradePayment(new UpgradeRequest(Guid.NewGuid(), Owner.UniqueMultiplayerID,
                Payer.UniqueMultiplayerID, level, Now.AddMinutes(3)), true, separate);
        }

        internal PaymentReceipt Receipt(ReceiptKind kind) => new(Order.Request.Id, kind);

        internal PaymentTransition? Step(bool payerSavedOffline = false, bool ownerSavedOffline = false, string? invalid = null)
        {
            var next = Order.Advance(PayerReceipt, OwnerReceipt, payerSavedOffline, ownerSavedOffline, invalid);
            if (next is null)
                return null;
            var edit = new PaymentEdit();
            if (next.GoldChange != 0)
                Game.AddWalletEdit(edit, Payer, next.GoldChange);
            Game.AddDataEdit(edit, Game1.MasterPlayer, "test-order", JsonSerializer.Serialize(next.Payment));
            edit.Commit();
            Order = next.Payment;
            return next;
        }

        internal void TakeMaterials()
        {
            var price = GameApi.Price(Order.Request.Level);
            var edit = price.Item is null ? new PaymentEdit() : Game.PlanMaterials(Payer, price.Item, -price.Quantity)!;
            var receipt = Receipt(ReceiptKind.MaterialsHeld);
            Game.AddDataEdit(edit, Payer, "test-materials", JsonSerializer.Serialize(receipt));
            edit.Commit();
            PayerReceipt = receipt;
        }

        internal void ReturnMaterials()
        {
            var price = GameApi.Price(Order.Request.Level);
            var edit = price.Item is not null && PayerReceipt?.Kind == ReceiptKind.MaterialsHeld
                ? Game.PlanMaterials(Payer, price.Item, price.Quantity)! : new PaymentEdit();
            var receipt = Receipt(ReceiptKind.Returned);
            Game.AddDataEdit(edit, Payer, "test-materials", JsonSerializer.Serialize(receipt));
            edit.Commit();
            PayerReceipt = receipt;
        }

        internal void Start()
        {
            var edit = Game.PlanConstruction(Owner);
            var receipt = Receipt(ReceiptKind.Started);
            Game.AddDataEdit(edit, Owner, "test-construction", JsonSerializer.Serialize(receipt));
            edit.Commit();
            OwnerReceipt = receipt;
        }

        internal void RunOrder()
        {
            TakeMaterials();
            Eq(PaymentPhase.Schedule, Step()!.Payment.Phase);
            Start();
            Eq(PaymentPhase.Completed, Step()!.Payment.Phase);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
