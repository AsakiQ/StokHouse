using System.Text.Json;

namespace StokHouse.FamilyBudget.Spec;

internal enum DiskState { Unknown, NotWritten, Written }

internal sealed class NightBudgetStore : INightBudgetStore
{
    private readonly Func<long, int> getWallet;
    private readonly Action<long, int> setWallet;
    private readonly Func<string?> readSnapshot;
    private readonly Action<string?> writeSnapshot;
    private readonly Func<Guid, DiskState> diskState;
    private NightWrite? pending;
    private string? previous;
    private bool integrityKnown = true;

    internal NightBudgetStore(Func<long, int> getWallet, Action<long, int> setWallet,
        Func<string?> readSnapshot, Action<string?> writeSnapshot, Func<Guid, DiskState> diskState)
    {
        this.getWallet = getWallet;
        this.setWallet = setWallet;
        this.readSnapshot = readSnapshot;
        this.writeSnapshot = writeSnapshot;
        this.diskState = diskState;
    }

    public int ReadPersonal(long player) => getWallet(player);

    public void Stage(NightWrite write)
    {
        if (pending is not null || write.Replacement.Keys.Any(player => !write.Expected.ContainsKey(player)) ||
            write.Expected.Any(entry => getWallet(entry.Key) != entry.Value))
            throw new InvalidOperationException("The native economy changed before staging.");

        string snapshot = JsonSerializer.Serialize(write.Checkpoint.Next);
        previous = readSnapshot();
        pending = write;
        var touched = new List<long>();
        bool propertyTouched = false;
        try
        {
            foreach (var (player, balance) in write.Replacement)
            {
                touched.Add(player);
                WriteWallet(player, balance);
            }
            propertyTouched = true;
            WriteSnapshot(snapshot);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (propertyTouched)
                TryRestore(() => WriteSnapshot(previous), failures);
            foreach (long player in touched.AsEnumerable().Reverse())
                TryRestore(() => WriteWallet(player, write.Expected[player]), failures);
            if (failures.Count > 1)
            {
                integrityKnown = false;
                throw new AggregateException("The engine rejected a restore. Hold the checkpoint for manual recovery.", failures);
            }

            Clear();
            throw;
        }
    }

    public bool TryDiscardBeforeWrite(Guid checkpoint)
    {
        if (!integrityKnown)
            return false;
        if (pending is null)
            return true; // Stage failed synchronously and already restored all changes.
        if (pending.Checkpoint.Id != checkpoint || diskState(checkpoint) != DiskState.NotWritten)
            return false;
        if (pending.Replacement.Any(entry => getWallet(entry.Key) != entry.Value) ||
            readSnapshot() != JsonSerializer.Serialize(pending.Checkpoint.Next))
            return false;

        var failures = new List<Exception>();
        TryRestore(() => WriteSnapshot(previous), failures);
        foreach (long player in pending.Replacement.Keys)
            TryRestore(() => WriteWallet(player, pending.Expected[player]), failures);
        if (failures.Count > 0)
        {
            integrityKnown = false;
            throw new AggregateException("The engine could not restore the failed checkpoint.", failures);
        }

        Clear();
        return true;
    }

    internal SaveReceipt Receipt(Guid checkpoint)
    {
        if (!integrityKnown || pending is not { } write || write.Checkpoint.Id != checkpoint ||
            diskState(checkpoint) != DiskState.Written)
            throw new InvalidOperationException("The native store cannot prove this checkpoint was saved.");
        Clear();
        var saved = write.Checkpoint;
        return new SaveReceipt(saved.Next.Farm, saved.Id, saved.Day);
    }

    private void WriteWallet(long player, int value)
    {
        setWallet(player, value);
        if (getWallet(player) != value)
            throw new InvalidOperationException($"The engine rejected a wallet write for {player}.");
    }

    private void WriteSnapshot(string? value)
    {
        writeSnapshot(value);
        if (readSnapshot() != value)
            throw new InvalidOperationException("The engine rejected a budget journal write.");
    }

    private void Clear()
    {
        pending = null;
        previous = null;
    }

    private static void TryRestore(Action restore, List<Exception> errors)
    {
        try { restore(); }
        catch (Exception ex) { errors.Add(ex); }
    }
}
