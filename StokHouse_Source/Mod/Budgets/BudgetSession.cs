using System.Text.Json;

namespace StokHouse.FamilyBudget;

internal sealed class BudgetSession
{
    private readonly NightBudgetStore store;
    private readonly BudgetHost host;
    private readonly Func<string?> readJournal;
    private readonly Func<string?> saveFile;
    private readonly ulong farmIdentity;
    private string? previousDiskSnapshot;
    private string? previousFileFingerprint;
    private NightWrite? pending;
    private bool saveEnded;

    internal BudgetSession(BudgetBook book, ulong farmIdentity, Func<string?> saveFile,
        Func<long, int> getWallet, Action<long, int> setWallet, Func<string?> readJournal, Action<string?> writeJournal,
        Func<long, long?> partner, Func<bool> separate, Func<Pair, bool> idle)
    {
        this.farmIdentity = farmIdentity;
        this.saveFile = saveFile;
        this.readJournal = readJournal;
        previousDiskSnapshot = readJournal();
        store = new NightBudgetStore(getWallet, setWallet, readJournal, writeJournal, CheckDisk);
        host = new BudgetHost(book, store, partner, separate, idle, collectRemainingGold: true);
    }

    internal BudgetBook Book => host.Current;
    internal bool Saving => host.NightInProgress;
    internal BudgetView View(long player) => host.View(player);
    internal bool MayLeave(long player) => host.MayLeave(player);
    internal void Propose(long actor, Guid id, ChangeKind kind) => host.Propose(actor, id, kind);
    internal void Answer(long actor, Guid id, bool accept) => host.Answer(actor, id, accept);
    internal void Cancel(long actor, Guid id) => host.Cancel(actor, id);
    internal void Adopt(BudgetBook book) => host.ReplaceTransactionBook(book);

    internal NightWrite PrepareNight()
    {
        saveEnded = false;
        previousFileFingerprint = saveFile() is { } path ? SavedBudgetProbe.Fingerprint(path) : null;
        var id = Guid.NewGuid();
        pending = host.BeginSave(id);
        return pending;
    }

    internal DiskState CompleteNight()
    {
        if (pending is null)
            return DiskState.Unknown;
        saveEnded = true;
        var id = pending.Checkpoint.Id;
        var outcome = CheckDisk(id);
        if (outcome == DiskState.Written)
        {
            host.Saved(store.Receipt(id));
            previousDiskSnapshot = readJournal();
            pending = null;
        }
        else if (outcome == DiskState.NotWritten && host.DiscardFailedSave())
            pending = null;
        else
            outcome = DiskState.Unknown;
        return outcome;
    }

    private DiskState CheckDisk(Guid checkpoint)
    {
        if (pending?.Checkpoint.Id != checkpoint || saveFile() is not { } path ||
            readJournal() is not { Length: > 0 } staged)
            return DiskState.Unknown;
        // Read the primary data file. The menu metadata is not the world-save authority.
        var state = SavedBudgetProbe.Inspect(path, farmIdentity, staged, previousDiskSnapshot, saveEnded);
        if (state == DiskState.Written && !VerifyWalletProof(path, farmIdentity, pending.Checkpoint.Next))
            return DiskState.Unknown;
        if (state == DiskState.NotWritten && (previousFileFingerprint is null ||
            previousFileFingerprint != SavedBudgetProbe.Fingerprint(path)))
            return DiskState.Unknown;
        return state;
    }

    internal static bool VerifyWalletProof(string? path, ulong farmIdentity, BudgetBook book)
    {
        if (book.SavedWallets is not { } wallets)
            return false;
        var shop = FamilyShop.Open(book).Orders.Values.Where(order => book.Postings[order.Id].State != PostingState.Held)
            .Select(order => new ShopOutcome(order, book.Postings[order.Id].State)).ToArray();
        return wallets.Count == 0 && shop.Length == 0 ||
            path is not null && SavedBudgetProbe.HasWallets(path, farmIdentity, wallets, shop);
    }

    internal static BudgetBook Load(ulong farmIdentity, string? raw, IEnumerable<long> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        // The native farm ID binds this record to one world, even if modData is copied.
        var bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, farmIdentity);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x53746f6b42756467UL);
        var farm = new Guid(bytes);
        var book = raw is { Length: > 0 }
            ? JsonSerializer.Deserialize<BudgetBook>(raw) ?? throw new InvalidDataException("Missing family budget.")
            : BudgetBook.Open(farm, 1, new Dictionary<long, int>());
        if (book.Farm != farm)
            throw new InvalidDataException("Family budget belongs to another farm.");
        book.Validate();
        FamilyShop.Open(book).Validate();
        return book.WithPlayers(players);
    }
}
