using StokHouse.Core;

namespace StokHouse;

internal enum PaymentPhase { Collect, Schedule, StopScheduling, Refund, Completed, Cancelled }
internal enum ReceiptKind { MaterialsHeld, Started, Refused, Stopped, Returned, NeedsReview }
internal sealed record PaymentReceipt(Guid Id, ReceiptKind Kind, string? Reason = null);
internal sealed record UpgradePayment(UpgradeRequest Request, bool OwnerApproved, bool SeparateWallet,
    PaymentPhase Phase = PaymentPhase.Collect, string? Reason = null, Guid[]? RecentOrders = null, bool Paused = false)
{
    internal bool Active => Phase is not (PaymentPhase.Completed or PaymentPhase.Cancelled);
    internal bool HasSeen(Guid id) => Request.Id == id || RecentOrders?.Contains(id) == true;

    internal Guid[] Archive() => (RecentOrders ?? Array.Empty<Guid>()).Append(Request.Id).TakeLast(64).ToArray();

    internal bool GoldHeld => Phase is PaymentPhase.Schedule or PaymentPhase.StopScheduling;

    internal UpgradePayment Cancel(string reason) => Phase switch
    {
        PaymentPhase.Collect => this with { Phase = PaymentPhase.Refund, Reason = reason },
        PaymentPhase.Schedule => this with { Phase = PaymentPhase.StopScheduling, Reason = reason },
        _ => this
    };

    internal PaymentTransition? Advance(PaymentReceipt? payer, PaymentReceipt? owner,
        bool payerSavedOffline, bool ownerSavedOffline, string? invalid)
    {
        if (payer?.Id != Request.Id)
            payer = null;
        if (owner?.Id != Request.Id)
            owner = null;
        if (Paused)
            return null;
        if (payer?.Kind == ReceiptKind.NeedsReview || owner?.Kind == ReceiptKind.NeedsReview)
            return new(this with { Paused = true });

        int price = UpgradeCosts.ForLevel(Request.Level).Gold;
        return Phase switch
        {
            PaymentPhase.Collect when invalid is not null || payer?.Kind == ReceiptKind.Refused =>
                new(Cancel(invalid ?? payer?.Reason ?? "upgrade-error")),
            PaymentPhase.Collect when payer?.Kind == ReceiptKind.MaterialsHeld =>
                new(this with { Phase = PaymentPhase.Schedule }, -price),

            // Observe a native start before considering cancellation or a lost connection.
            PaymentPhase.Schedule or PaymentPhase.StopScheduling when owner?.Kind == ReceiptKind.Started =>
                new(this with { Phase = PaymentPhase.Completed, Reason = null }),
            PaymentPhase.Schedule or PaymentPhase.StopScheduling when owner?.Kind is ReceiptKind.Stopped or ReceiptKind.Refused ||
                ownerSavedOffline && owner is null =>
                new(this with { Phase = PaymentPhase.Refund, Reason = Reason ?? owner?.Reason ?? "upgrade-disconnected" }, price),
            PaymentPhase.Schedule when invalid is not null =>
                new(Cancel(invalid)),

            PaymentPhase.Refund when payer?.Kind is ReceiptKind.Returned or ReceiptKind.Refused ||
                payerSavedOffline && payer is null =>
                new(this with { Phase = PaymentPhase.Cancelled }),
            _ => null
        };
    }
}

internal sealed record PaymentTransition(UpgradePayment Payment, int GoldChange = 0);

// All edits in a step run on the game thread before networking or the save loop can serialize them.
internal sealed class PaymentEdit
{
    private readonly List<(Action Apply, Action Undo)> edits = new();

    internal void Add(Action apply, Action undo) => edits.Add((apply, undo));

    internal void Commit()
    {
        int attempted = 0;
        try
        {
            foreach (var (apply, _) in edits)
            {
                attempted++;
                apply();
            }
        }
        catch (Exception failure)
        {
            List<Exception> errors = new() { failure };
            for (int i = attempted - 1; i >= 0; i--)
            {
                try { edits[i].Undo(); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            if (errors.Count > 1)
                throw new PaymentRecoveryException(errors);
            throw;
        }
    }
}

internal sealed class PaymentRecoveryException : AggregateException
{
    internal PaymentRecoveryException(IEnumerable<Exception> errors)
        : base("The game rejected a payment rollback. Stop processing this order.", errors) { }
}
