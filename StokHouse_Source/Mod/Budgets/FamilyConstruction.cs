using StokHouse.Core;

namespace StokHouse.FamilyBudget;

// This snapshot must be committed in one host write alongside the next native order phase.
// No gold goes through the farmer's own wallet while the family account funds the order.
internal sealed record FamilyConstruction(BudgetBook Book, Guid Account, UpgradePayment Order)
{
    internal static void VerifySavedOrder(BudgetBook book, UpgradePayment? order)
    {
        var holds = book.Postings.Values.Where(post => post.Kind == PostingKind.Construction &&
            post.State == PostingState.Held).ToArray();
        if (order is { Active: true, FamilyAccount: Guid account })
        {
            if (holds.Length != 1)
                throw new InvalidDataException("There is no unique hold for the saved family upgrade.");
            new FamilyConstruction(book, account, order).ValidateReservation(book);
        }
        else if (holds.Length != 0)
            throw new InvalidDataException("A family construction hold has no active game order.");
    }

    internal static FamilyConstruction Authorize(BudgetBook book, UpgradeRequest request, bool ownerApproved)
    {
        if (!ownerApproved)
            throw new InvalidOperationException("The owner has not approved this separate-wallet order.");
        var account = book.ForPlayer(request.Guest);
        if (account is null || !account.Pair.Contains(request.Owner) || request.Owner == request.Guest)
            throw new InvalidOperationException("This order does not belong to the paying household.");
        if (book.Postings.ContainsKey(request.Id))
            throw new InvalidOperationException("Resume the original order instead of creating a second one.");
        int gold = UpgradeCosts.ForLevel(request.Level).Gold;
        var held = book.Post(request.Id, account.Id, request.Guest, -gold, PostingKind.Construction);
        return new FamilyConstruction(held, account.Id, new UpgradePayment(request, ownerApproved, SeparateWallet: true, FamilyAccount: account.Id));
    }

    // Purchases and earnings may have been posted while the native order was in flight.
    internal FamilyConstruction RefreshBook(BudgetBook book)
    {
        ValidateReservation(book);
        return this with { Book = book };
    }

    internal FamilyConstruction Cancel(string reason) =>
        Order.Paused ? this : this with { Order = Order.Cancel(reason) };

    internal FamilyConstruction Advance(PaymentReceipt? payer, PaymentReceipt? owner, bool payerSavedOffline,
        bool ownerSavedOffline, string? invalid)
    {
        if (!Order.Active || Order.Paused)
            return this;
        ValidateReservation(Book);
        var transition = Order.Advance(payer, owner, payerSavedOffline, ownerSavedOffline, invalid);
        if (transition is null)
            return this;

        var next = transition.Payment;
        var book = next.Phase switch
        {
            PaymentPhase.Completed => Resolve(PostingState.Applied),
            PaymentPhase.Cancelled => Resolve(PostingState.Reversed),
            _ => Book
        };
        return this with { Book = book, Order = next };
    }

    private BudgetBook Resolve(PostingState outcome) =>
        Book.Resolve(new ActionReceipt(Order.Request.Id, Order.Request.Guest, Account, outcome));

    private void ValidateReservation(BudgetBook book)
    {
        PostingState expected = Order.Phase switch
        {
            PaymentPhase.Completed => PostingState.Applied,
            PaymentPhase.Cancelled => PostingState.Reversed,
            _ => PostingState.Held
        };
        if (Order.FamilyAccount != Account || book.Farm != Book.Farm || !book.Postings.TryGetValue(Order.Request.Id, out var reservation) ||
            reservation.Budget != Account || reservation.Actor != Order.Request.Guest ||
            reservation.Kind != PostingKind.Construction || reservation.State != expected ||
            reservation.Amount != -UpgradeCosts.ForLevel(Order.Request.Level).Gold)
            throw new InvalidOperationException("The native order and saved family reservation do not match.");
    }
}
