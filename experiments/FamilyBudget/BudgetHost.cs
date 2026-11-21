using System.Collections.Immutable;

namespace StokHouse.FamilyBudget.Spec;

internal sealed record BudgetView(Guid? Account, int? Available, Proposal? Proposal);
internal sealed record NightWrite(Checkpoint Checkpoint, ImmutableDictionary<long, int> Expected,
    ImmutableDictionary<long, int> Replacement);

// The game adapter must stage these wallet writes and the checkpoint into the same native save.
// TryDiscardBeforeWrite may return true only if it can prove the save was never committed.
internal interface INightBudgetStore
{
    int ReadPersonal(long player);
    void Stage(NightWrite write);
    bool TryDiscardBeforeWrite(Guid checkpoint);
}

// Runs on the host game thread. The caller supplies the authenticated multiplayer sender,
// never a farmer ID supplied in a command body.
internal sealed class BudgetHost
{
    private readonly INightBudgetStore store;
    private readonly Func<long, long?> residentPartner;
    private readonly Func<bool> separateWallets;
    private readonly Func<Pair, bool> otherPaymentsIdle;
    private NightWrite? staging;

    internal BudgetBook Current { get; private set; }
    internal bool NightInProgress => staging is not null;

    internal BudgetHost(BudgetBook snapshot, INightBudgetStore store, Func<long, long?> residentPartner,
        Func<bool> separateWallets, Func<Pair, bool> otherPaymentsIdle)
    {
        Current = snapshot;
        this.store = store;
        this.residentPartner = residentPartner;
        this.separateWallets = separateWallets;
        this.otherPaymentsIdle = otherPaymentsIdle;
    }

    internal BudgetView View(long sender)
    {
        var account = Current.ForPlayer(sender);
        var offer = Current.Proposals.Values.SingleOrDefault(proposal => proposal.Pair.Contains(sender));
        return new BudgetView(account?.Id, account is null ? null : Current.Available(account.Id, sender), offer);
    }

    internal void Propose(long sender, Guid id, ChangeKind kind)
    {
        RequireOpen();
        if (residentPartner(sender) is not long partner || residentPartner(partner) != sender)
            throw new InvalidOperationException("The sender and recipient must be current housemates.");
        Current = Current.Propose(id, Pair.Create(sender, partner), sender, kind, true, separateWallets());
    }

    internal void Answer(long sender, Guid id, bool accept)
    {
        RequireOpen();
        if (Current.Proposals.TryGetValue(id, out var offer) && accept && !CurrentPair(offer.Pair))
            throw new InvalidOperationException("The household changed after the proposal.");
        Current = Current.Answer(id, sender, accept);
    }

    internal void Cancel(long sender, Guid id)
    {
        RequireOpen();
        Current = Current.Cancel(id, sender);
    }

    internal bool MayLeave(long sender) => !NightInProgress && Current.MayLeave(sender);

    internal Posting Hold(Guid operation, Guid account, long sender, int amount, PostingKind kind)
    {
        RequireOpen();
        Current = Current.Post(operation, account, sender, amount, kind);
        return Current.Postings[operation];
    }

    internal void Apply(ActionReceipt verifiedReceipt)
    {
        RequireOpen();
        Current = Current.Resolve(verifiedReceipt);
    }

    internal NightWrite BeginSave(Guid id)
    {
        RequireOpen();
        var observed = Current;
        // Capture only the consenting residents' latest vanilla wallets. Accounts which remain
        // personal (including other families and single players) need no rewrite.
        var expected = ImmutableDictionary.CreateBuilder<long, int>();
        foreach (var proposal in Current.Proposals.Values.Where(
            offer => offer.Accepted && separateWallets() && CurrentPair(offer.Pair) && otherPaymentsIdle(offer.Pair)))
        {
            foreach (long resident in new[] { proposal.Pair.First, proposal.Pair.Second })
            {
                int value = store.ReadPersonal(resident);
                if (proposal.Kind == ChangeKind.Merge)
                    observed = observed.ObservePersonal(resident, value);
                else if (value != 0)
                    throw new InvalidOperationException("A family member has an unexpected personal balance.");
                expected.Add(resident, value);
            }
        }

        var checkpoint = observed.PrepareNight(id, CurrentPair, otherPaymentsIdle, separateWallets());
        var changes = ImmutableDictionary.CreateBuilder<long, int>();
        foreach (var (resident, value) in expected)
        {
            int replacement = checkpoint.Next.Personal[resident];
            if (value != replacement)
                changes.Add(resident, replacement);
        }
        var pending = new NightWrite(checkpoint, expected.ToImmutable(), changes.ToImmutable());

        // Stage must roll back a synchronous failure. Subsequent writes are frozen until
        // Saved or confirmed discard, including in the window after the engine captures disk data.
        Current = observed;
        staging = pending;
        try
        {
            store.Stage(pending);
        }
        catch
        {
            DiscardFailedSave();
            throw;
        }
        return pending;
    }

    internal void Saved(SaveReceipt receipt)
    {
        if (staging is null)
        {
            if (receipt.Farm == Current.Farm && receipt.Day == Current.Day && receipt.Checkpoint == Current.LastCheckpoint)
                return;
            throw new InvalidOperationException("No matching save is staged.");
        }
        Current = Current.ConfirmSave(staging.Checkpoint, receipt);
        staging = null;
    }

    internal bool DiscardFailedSave()
    {
        if (staging is null)
            return true;
        if (!store.TryDiscardBeforeWrite(staging.Checkpoint.Id))
            return false;
        staging = null;
        return true;
    }

    private bool CurrentPair(Pair pair) =>
        residentPartner(pair.First) == pair.Second && residentPartner(pair.Second) == pair.First;

    private void RequireOpen()
    {
        if (NightInProgress)
            throw new InvalidOperationException("A night save is already staged. Its outcome must be confirmed first.");
    }
}
