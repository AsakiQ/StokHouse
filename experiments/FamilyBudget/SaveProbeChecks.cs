namespace StokHouse.FamilyBudget.Spec;

internal static class SaveProbeChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("the completed primary save must contain the exact staged checkpoint on the master farmer", () =>
        {
            const string staged = "{&quot;day&quot;:2}";
            string player = Dictionary(staged);
            Verify(World(123, player), "{\"day\":2}", previous: null, DiskState.Written);
            Verify(World(123, player), "{\"day\":2}", previous: null, DiskState.Unknown, saveEnded: false);
            Verify(World(124, player), "{\"day\":2}", previous: null, DiskState.Unknown);
            Verify(World(123, "") + player, "{\"day\":2}", previous: null, DiskState.Unknown);
            Verify(World(123, "", "<farmhands><Farmer>" + player + "</Farmer></farmhands>"),
                "{\"day\":2}", previous: null, DiskState.NotWritten);
        });

        test("a prior committed snapshot proves non-write while foreign or damaged snapshots stay unresolved", () =>
        {
            Verify(World(123, Dictionary("old")), "new", "old", DiskState.NotWritten);
            Verify(World(123, Dictionary("third")), "new", "old", DiskState.Unknown);
            Verify(World(123, Dictionary("old")).Replace("</SaveGame>", ""), "new", "old", DiskState.Unknown);
            Verify(World(123, "<modData><Unknown><value>old</value></Unknown></modData>"),
                "new", "old", DiskState.Unknown);
            Verify(World(123, Dictionary("new") + Dictionary("old")), "new", "old", DiskState.Unknown);
            Verify(World(123, ""), "new", "old", DiskState.Unknown);
            Verify(World(123, ""), "new", previous: null, DiskState.NotWritten);
        });

        test("the host does not publish a split merely because the save event arrived", () =>
        {
            string path = Path.GetTempFileName();
            try
            {
                var wallets = new Dictionary<long, int> { [1] = 10_000, [2] = 5_000 };
                string? journal = null;
                string? prior = null;
                var store = new NightBudgetStore(id => wallets[id], (id, value) => wallets[id] = value, () => journal,
                    value => journal = value, _ => SavedBudgetProbe.Inspect(path, 123, journal!, prior, saveEnded: true));
                var host = new BudgetHost(BudgetBook.Open(Guid.NewGuid(), 1, wallets), store,
                    id => id == 1 ? 2 : 1, () => true, _ => true);
                var proposal = Guid.NewGuid();
                host.Propose(1, proposal, ChangeKind.Merge);
                host.Answer(2, proposal, true);
                var merge = host.BeginSave(Guid.NewGuid());
                WriteCurrent();
                host.Saved(store.Receipt(merge.Checkpoint.Id));

                prior = journal;
                proposal = Guid.NewGuid();
                host.Propose(2, proposal, ChangeKind.Split);
                host.Answer(1, proposal, true);
                var split = host.BeginSave(Guid.NewGuid());
                if (host.View(1).Available != 15_000 || !host.NightInProgress)
                    throw new InvalidOperationException("The split took effect before the disk write.");
                if (SavedBudgetProbe.Inspect(path, 123, journal!, prior, saveEnded: true) != DiskState.NotWritten)
                    throw new InvalidOperationException("The preceding file is not proof of the split.");
                WriteCurrent();
                host.Saved(store.Receipt(split.Checkpoint.Id));
                if (host.View(1).Available is not null || wallets[1] != 7_500 || wallets[2] != 7_500)
                    throw new InvalidOperationException("The confirmed split did not apply the expected balances.");

                void WriteCurrent() => File.WriteAllText(path,
                    World(123, Dictionary(System.Security.SecurityElement.Escape(journal)!)));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static string World(ulong id, string host, string extra = "") =>
        $"<SaveGame><player>{host}</player><uniqueIDForThisGame>{id}</uniqueIDForThisGame>{extra}</SaveGame>";

    private static string Dictionary(string value) =>
        "<modData><SerializableDictionaryOfStringString><item><key><string>" + SavedBudgetProbe.JournalKey +
        "</string></key><value><string>" + value + "</string></value></item></SerializableDictionaryOfStringString></modData>";

    private static void Verify(string xml, string staged, string? previous, DiskState expected, bool saveEnded = true)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, xml);
            var actual = SavedBudgetProbe.Inspect(path, 123, staged, previous, saveEnded);
            if (actual != expected)
                throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
