using System.Collections.Immutable;

namespace StokHouse.FamilyBudget.Spec;

internal sealed record NativeShopQuote(PostingKind Kind, string Shop, string Item, int Currency,
    int UnitPrice, int Quantity, long OfferRevision);
internal sealed record ShopOrder(Guid Id, Guid Account, long BuyerOrSeller, NativeShopQuote Quote);

// The adapter obtains this from a verified native effect, not directly from a peer command.
internal sealed record ShopOutcome(ShopOrder Order, PostingState State);

internal static class ShopParticipant
{
    // Store the receipt on the same native root as the local effects, in the same tick.
    // The native adapter must avoid side effects that cannot be rolled back by this edit.
    internal static ShopOutcome Apply(FamilyShop confirmed, ShopOrder order, NativeShopQuote quote,
        Func<Guid, ShopOutcome?> readReceipt, Action<Guid, ShopOutcome?> writeReceipt,
        Func<PaymentEdit?> planNativeEffect)
    {
        if (readReceipt(order.Id) is { } seen)
            return seen.Order == order ? seen : throw new InvalidOperationException("A different action used this local receipt ID.");

        if (!confirmed.Orders.TryGetValue(order.Id, out var recorded) || recorded != order ||
            !confirmed.Book.Postings.TryGetValue(order.Id, out var posting) || posting.State != PostingState.Held)
            throw new InvalidOperationException("There is no current host authorization for this shop action.");

        var effect = confirmed.CanApply(order, quote) ? planNativeEffect() : null;
        var outcome = new ShopOutcome(order, effect is null ? PostingState.Reversed : PostingState.Applied);
        effect ??= new PaymentEdit();
        effect.Add(() => writeReceipt(order.Id, outcome), () => writeReceipt(order.Id, null));
        effect.Commit();
        return outcome;
    }
}

internal sealed record FamilyShop(BudgetBook Book, ImmutableDictionary<Guid, ShopOrder> Orders)
{
    internal static FamilyShop Open(BudgetBook book) => new(book, ImmutableDictionary<Guid, ShopOrder>.Empty);

    internal (FamilyShop Next, ShopOrder Order) Authorize(Guid id, long actor, NativeShopQuote verifiedQuote)
    {
        if (Orders.TryGetValue(id, out var recorded))
        {
            if (recorded.BuyerOrSeller != actor || recorded.Quote != verifiedQuote)
                throw new InvalidOperationException("The shop action has different contents from its original grant.");
            return (this, recorded);
        }

        var account = Book.ForPlayer(actor) ?? throw new InvalidOperationException("The shopper has no family budget.");
        if (Book.Postings.ContainsKey(id))
            throw new InvalidOperationException("This ID is already used for another family transaction.");
        if (verifiedQuote.Currency != 0 || verifiedQuote.UnitPrice <= 0 || verifiedQuote.Quantity <= 0 ||
            string.IsNullOrWhiteSpace(verifiedQuote.Shop) || string.IsNullOrWhiteSpace(verifiedQuote.Item) ||
            verifiedQuote.Kind is not (PostingKind.Purchase or PostingKind.Sale))
            throw new InvalidOperationException("The shop action has no valid gold quote.");
        long price = (long)verifiedQuote.UnitPrice * verifiedQuote.Quantity;
        if (price > int.MaxValue)
            throw new InvalidOperationException("The shop amount is outside the game's currency range.");
        int delta = verifiedQuote.Kind == PostingKind.Purchase ? -(int)price : (int)price;
        var posting = Book.Post(id, account.Id, actor, delta, verifiedQuote.Kind);
        var order = new ShopOrder(id, account.Id, actor, verifiedQuote);
        return (this with { Book = posting, Orders = Orders.Add(id, order) }, order);
    }

    // Call before entering the original shop mutation, after the host grant arrives. The
    // caller must re-read the offer and inventory on the local game thread without yielding.
    internal bool CanApply(ShopOrder order, NativeShopQuote currentQuote) =>
        Orders.TryGetValue(order.Id, out var recorded) && recorded == order && recorded.Quote == currentQuote &&
        Book.Postings.TryGetValue(order.Id, out var posting) && posting.State == PostingState.Held;

    internal FamilyShop Settle(ShopOutcome outcome)
    {
        var order = outcome.Order;
        if (!Orders.TryGetValue(order.Id, out var recorded) || recorded != order)
            throw new InvalidOperationException("The verified shop outcome refers to a different grant.");
        var next = Book.Resolve(new ActionReceipt(order.Id, order.BuyerOrSeller, order.Account, outcome.State));
        return next == Book ? this : this with { Book = next };
    }
}
