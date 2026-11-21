using System.Text.Json;

namespace StokHouse.FamilyBudget.Spec;

internal static class HostChecks
{
    private static readonly Dictionary<long, long> Partners = new() { [1] = 2, [2] = 1, [3] = 4, [4] = 3 };

    internal static void Run(Action<string, Action> test)
    {
        test("host scopes invitations, views and commands to the authenticated household", () =>
        {
            var host = Start(out _);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            host.Propose(1, first, ChangeKind.Merge);
            host.Propose(3, second, ChangeKind.Merge);
            Check(host.View(1).Proposal?.Id == first && host.View(2).Proposal?.Id == first);
            Check(host.View(3).Proposal?.Id == second && host.View(7).Proposal is null);
            Reject(() => host.Answer(4, first, true));
            Reject(() => host.Cancel(7, first));
            Reject(() => host.Propose(7, Guid.NewGuid(), ChangeKind.Merge));
            host.Answer(2, first, true);
            Check(host.View(2).Proposal?.Accepted == true);
        });

        test("night stages balances for both consenting pairs and publishes only after the matching save", () =>
        {
            var host = Start(out var store);
            var first = Consent(host, 1, 2, ChangeKind.Merge);
            var second = Consent(host, 3, 4, ChangeKind.Merge);
            store.Wallets[2] += 1_000;
            var staged = host.BeginSave(Guid.NewGuid());
            Check(staged.Expected.Count == 4 && staged.Replacement.Count == 4);
            Check(store.Wallets[7] == 700);
            Check(host.View(1).Available is null && host.View(3).Available is null && host.NightInProgress);
            Reject(() => host.Cancel(1, first));
            Reject(() => host.Saved(new(host.Current.Farm, Guid.NewGuid(), staged.Checkpoint.Day)));
            var receipt = store.Commit();
            host.Saved(receipt);
            host.Saved(receipt);
            Check(host.View(1) == new BudgetView(first, 16_000, null) && host.View(2) == host.View(1));
            Check(host.View(3) == new BudgetView(second, 3_400, null) && host.View(4) == host.View(3));
            Check(host.View(7) == new BudgetView(null, null, null) && !host.NightInProgress);
        });

        test("proven failure restores the native wallets and keeps the accepted request for a retry", () =>
        {
            var host = Start(out var store);
            var proposal = Consent(host, 1, 2, ChangeKind.Merge);
            host.BeginSave(Guid.NewGuid());
            Check(store.Wallets[1] == 0);
            Check(host.DiscardFailedSave());
            Check(store.Wallets[1] == 10_000 && store.Wallets[2] == 5_000);
            Check(host.View(2).Proposal?.Id == proposal && host.View(2).Available is null);
            host.BeginSave(Guid.NewGuid());
            host.Saved(store.Commit());
            Check(host.View(2).Available == 15_000);
        });

        test("unknown write outcome freezes new commands and host restart loads the committed checkpoint exactly once", () =>
        {
            var host = Start(out var store);
            var proposal = Consent(host, 1, 2, ChangeKind.Merge);
            host.BeginSave(Guid.NewGuid());
            var receipt = store.Commit();
            Check(!host.DiscardFailedSave() && host.NightInProgress);
            Reject(() => host.BeginSave(Guid.NewGuid()));
            Reject(() => host.Propose(3, Guid.NewGuid(), ChangeKind.Merge));

            var restored = Restore(store);
            restored.Saved(receipt);
            Check(restored.View(1) == new BudgetView(proposal, 15_000, null) && store.Wallets[1] == 0);
            Check(restored.View(7).Available is null && store.Wallets[7] == 700);
        });

        test("a known synchronous save failure releases the freeze and preserves amounts", () =>
        {
            var host = Start(out var store);
            Consent(host, 1, 2, ChangeKind.Merge);
            store.FailStage = true;
            Reject(() => host.BeginSave(Guid.NewGuid()));
            Check(!host.NightInProgress && store.Wallets[1] == 10_000 && store.Wallets[2] == 5_000);
            Check(host.View(1).Proposal?.Accepted == true);
        });

        test("host defers merge during another payment and blocks departure through mutual splitting", () =>
        {
            bool purchaseBusy = true;
            var host = Start(out var store, idle: _ => !purchaseBusy);
            Consent(host, 1, 2, ChangeKind.Merge);
            host.BeginSave(Guid.NewGuid());
            host.Saved(store.Commit());
            Check(host.View(2).Available is null && host.View(2).Proposal?.Accepted == true);
            purchaseBusy = false;
            host.BeginSave(Guid.NewGuid());
            host.Saved(store.Commit());
            Check(!host.MayLeave(2));
            var split = Consent(host, 2, 1, ChangeKind.Split);
            Check(host.View(1).Proposal?.Quote == new SplitQuote(7_500, 7_500));
            host.BeginSave(Guid.NewGuid());
            Check(!host.MayLeave(1));
            host.Saved(store.Commit());
            Check(host.MayLeave(1) && host.MayLeave(2) && host.View(2).Proposal?.Id != split);
            Check(store.Wallets[1] == 7_500 && store.Wallets[2] == 7_500);
        });

        test("native store rolls back both wallets and the journal if the journal setter throws", () =>
        {
            var fixture = MakeNative();
            Consent(fixture.Host, 1, 2, ChangeKind.Merge);
            fixture.FailNextWrite();
            Reject(() => fixture.Host.BeginSave(Guid.NewGuid()));
            Check(!fixture.Host.NightInProgress && fixture.ReadJournal() is null);
            Check(fixture.Wallets[1] == 10_000 && fixture.Wallets[2] == 5_000);

            var write = fixture.Host.BeginSave(Guid.NewGuid());
            Reject(() => fixture.Store.Receipt(write.Checkpoint.Id));
            Check(!fixture.Host.DiscardFailedSave() && fixture.Host.NightInProgress);
            fixture.ConfirmDisk(DiskState.Written);
            fixture.Host.Saved(fixture.Store.Receipt(write.Checkpoint.Id));
            Check(fixture.Host.View(1).Available == 15_000 && fixture.Host.View(2) == fixture.Host.View(1));
        });

        test("native store restores prior balances only with evidence of an unwritten save", () =>
        {
            var fixture = MakeNative();
            Consent(fixture.Host, 1, 2, ChangeKind.Merge);
            var write = fixture.Host.BeginSave(Guid.NewGuid());
            Check(fixture.Wallets[2] == 0 && fixture.ReadJournal() is not null);
            Check(!fixture.Host.DiscardFailedSave() && fixture.Wallets[2] == 0);
            fixture.ConfirmDisk(DiskState.NotWritten);
            Check(fixture.Host.DiscardFailedSave() && fixture.Wallets[2] == 5_000 && fixture.ReadJournal() is null);
            Reject(() => fixture.Store.Receipt(write.Checkpoint.Id));
        });

        test("unexpected native credits to a family wallet prevent an unsafe split", () =>
        {
            var host = Start(out var store);
            Consent(host, 1, 2, ChangeKind.Merge);
            host.BeginSave(Guid.NewGuid());
            host.Saved(store.Commit());
            Consent(host, 1, 2, ChangeKind.Split);
            store.Wallets[2] = 230;
            Reject(() => host.BeginSave(Guid.NewGuid()));
            Check(host.View(1).Available == 15_000 && store.Wallets[2] == 230 && !host.NightInProgress);
        });
    }

    private sealed record NativeFixture(BudgetHost Host, NightBudgetStore Store, Dictionary<long, int> Wallets,
        Action FailNextWrite, Func<string?> ReadJournal, Action<DiskState> ConfirmDisk);

    private static NativeFixture MakeNative()
    {
        var wallets = new Dictionary<long, int> { [1] = 10_000, [2] = 5_000 };
        string? journal = null;
        DiskState state = DiskState.Unknown;
        bool fail = false;
        var store = new NightBudgetStore(id => wallets[id], (id, balance) => wallets[id] = balance, () => journal,
            value =>
            {
                journal = value;
                if (fail)
                {
                    fail = false;
                    throw new InvalidOperationException("Fixture journal setter failed after writing.");
                }
            }, _ => state);
        var host = new BudgetHost(BudgetBook.Open(Guid.NewGuid(), 1, wallets), store, Partner, () => true, _ => true);
        return new NativeFixture(host, store, wallets, () => fail = true, () => journal, value => state = value);
    }

    private static BudgetHost Start(out TestStore store, Func<Pair, bool>? idle = null)
    {
        var balances = new Dictionary<long, int> { [1] = 10_000, [2] = 5_000, [3] = 3_000, [4] = 400, [7] = 700 };
        store = new TestStore(balances);
        return new BudgetHost(BudgetBook.Open(Guid.NewGuid(), 1, balances), store, Partner, () => true, idle ?? (_ => true));
    }

    private static BudgetHost Restore(TestStore store) =>
        new(JsonSerializer.Deserialize<BudgetBook>(store.Durable!)!, store, Partner, () => true, _ => true);

    private static long? Partner(long sender) => Partners.TryGetValue(sender, out long partner) ? partner : null;

    private static Guid Consent(BudgetHost host, long sender, long recipient, ChangeKind kind)
    {
        var id = Guid.NewGuid();
        host.Propose(sender, id, kind);
        host.Answer(recipient, id, true);
        return id;
    }

    private static void Check(bool result)
    {
        if (!result)
            throw new InvalidOperationException("Host fixture assertion failed.");
    }

    private static void Reject(Action tryCall)
    {
        try { tryCall(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the host to reject this operation.");
    }

    private sealed class TestStore : INightBudgetStore
    {
        internal readonly Dictionary<long, int> Wallets;
        private NightWrite? pending;
        private Guid? committed;
        internal string? Durable { get; private set; }
        internal bool FailStage;

        internal TestStore(Dictionary<long, int> balances) => Wallets = balances;

        public int ReadPersonal(long player) => Wallets[player];

        public void Stage(NightWrite write)
        {
            if (pending is not null || write.Expected.Any(entry => Wallets[entry.Key] != entry.Value))
                throw new InvalidOperationException("Fixture storage revision changed.");
            pending = write;
            foreach (var (farmer, replacement) in write.Replacement)
                Wallets[farmer] = replacement;
            if (FailStage)
                throw new InvalidOperationException("Fixture forced storage failure after staging.");
        }

        internal SaveReceipt Commit()
        {
            var checkpoint = pending!.Checkpoint;
            Durable = JsonSerializer.Serialize(checkpoint.Next);
            pending = null;
            committed = checkpoint.Id;
            return new SaveReceipt(checkpoint.Next.Farm, checkpoint.Id, checkpoint.Day);
        }

        public bool TryDiscardBeforeWrite(Guid checkpoint)
        {
            if (committed == checkpoint)
                return false;
            if (pending?.Checkpoint.Id == checkpoint)
            {
                foreach (var (farmer, original) in pending.Expected)
                    Wallets[farmer] = original;
                pending = null;
            }
            return true;
        }
    }
}
