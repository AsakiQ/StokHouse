using System.Collections.Immutable;

namespace StokHouse.FamilyBudget;

internal sealed record NativeShopQuote(PostingKind Kind, string Shop, string Item, int Currency,
    int UnitPrice, int Quantity, long OfferRevision, string Entry = "", int Quality = 0);
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
        if (effect is null)
        {
            var declined = new ShopOutcome(order, PostingState.Reversed);
            writeReceipt(order.Id, declined);
            return declined;
        }

        // A crash or failed rollback leaves a durable uncertainty marker rather than
        // a missing receipt which could be interpreted as a free refund on reconnect.
        var prepared = new ShopOutcome(order, PostingState.Held);
        writeReceipt(order.Id, prepared);
        var outcome = new ShopOutcome(order, PostingState.Applied);
        effect.Add(() => writeReceipt(order.Id, outcome), () => writeReceipt(order.Id, prepared));
        try
        {
            effect.Commit();
            return outcome;
        }
        catch (PaymentRecoveryException)
        {
            try { writeReceipt(order.Id, prepared); }
            catch (Exception) { /* Preserve the original recovery error. */ }
            throw;
        }
        catch (Exception)
        {
            writeReceipt(order.Id, new ShopOutcome(order, PostingState.Reversed));
            throw;
        }
    }
}

internal sealed record FamilyShop(BudgetBook Book)
{
    internal ImmutableDictionary<Guid, ShopOrder> Orders => Book.ShopOrders ?? ImmutableDictionary<Guid, ShopOrder>.Empty;
    internal static FamilyShop Open(BudgetBook book) => new(book);

    internal (FamilyShop Next, ShopOrder Order) Authorize(Guid id, long actor, NativeShopQuote verifiedQuote)
    {
        if (Orders.TryGetValue(id, out var recorded))
        {
            if (recorded.BuyerOrSeller != actor || recorded.Quote != verifiedQuote)
                throw new InvalidOperationException("The shop action has different contents from its original grant.");
            return (this, recorded);
        }

        var account = Book.ForPlayer(actor) ?? throw new InvalidOperationException("The shopper has no family budget.");
        if (Pending(actor) is not null || Book.Postings.ContainsKey(id))
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
        return (this with { Book = posting with { ShopOrders = Orders.Add(id, order) } }, order);
    }

    // Call before entering the original shop mutation, after the host grant arrives. The
    // caller must re-read the offer and inventory on the local game thread without yielding.
    internal bool CanApply(ShopOrder order, NativeShopQuote currentQuote) =>
        Orders.TryGetValue(order.Id, out var recorded) && recorded == order && recorded.Quote == currentQuote &&
        Book.Postings.TryGetValue(order.Id, out var posting) && posting.State == PostingState.Held;

    internal ShopOrder? Pending(long buyer) => Orders.Values.SingleOrDefault(order => order.BuyerOrSeller == buyer &&
        Book.Postings[order.Id].State == PostingState.Held);

    internal void Validate()
    {
        var pending = new HashSet<long>();
        foreach (var (id, order) in Orders)
        {
            if (order is null || order.Id != id || order.Quote is not { Currency: 0, UnitPrice: > 0, Quantity: > 0 } quote ||
                quote.Kind is not (PostingKind.Purchase or PostingKind.Sale) || !Book.Postings.TryGetValue(id, out var post) ||
                post.Budget != order.Account || post.Actor != order.BuyerOrSeller || post.Kind != quote.Kind ||
                post.Amount != (long)quote.UnitPrice * quote.Quantity * (quote.Kind == PostingKind.Purchase ? -1 : 1) ||
                post.State == PostingState.Held && !pending.Add(post.Actor))
                throw new InvalidDataException("The saved shop order disagrees with its posting.");
        }
        if (Book.Postings.Values.Any(post => post.State == PostingState.Held && post.Kind == PostingKind.Purchase &&
            !Orders.ContainsKey(post.Id)))
            throw new InvalidDataException("A saved shop hold has no shop order.");
    }

    internal FamilyShop Settle(ShopOutcome outcome)
    {
        var order = outcome.Order;
        if (!Orders.TryGetValue(order.Id, out var recorded) || recorded != order)
            throw new InvalidOperationException("The verified shop outcome refers to a different grant.");
        var next = Book.Resolve(new ActionReceipt(order.Id, order.BuyerOrSeller, order.Account, outcome.State));
        return next == Book ? this : this with { Book = next };
    }
}
