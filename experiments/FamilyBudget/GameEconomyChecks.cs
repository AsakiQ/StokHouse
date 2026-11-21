using System.Text.Json;
using StokHouse.Core;

namespace StokHouse.FamilyBudget.Spec;

internal static class GameEconomyChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("Stok House construction holds family gold until the named owner's native receipt", () =>
        {
            var book = Family(20_000);
            var order = Request(0, owner: 2, guest: 1);
            var held = FamilyConstruction.Authorize(book, order, ownerApproved: true);
            Check(held.Book.Personal[1] == 0 && held.Book.Personal[2] == 0 && held.Book.Personal[7] == 450);
            Check(held.Book.Available(held.Account, 2) == 10_000);
            Reject(() => held.Book.Post(Guid.NewGuid(), held.Account, 2, -10_001, PostingKind.Purchase));

            var scheduled = held.Advance(new(order.Id, ReceiptKind.MaterialsHeld), null, false, false, null);
            var stale = scheduled.Advance(null, new(Guid.NewGuid(), ReceiptKind.Started), false, false, null);
            Check(stale == scheduled && scheduled.Book.ForPlayer(1)!.Balance == 20_000);
            var confirmed = scheduled.Advance(null, new(order.Id, ReceiptKind.Started), false, false, null);
            Check(confirmed.Order.Phase == PaymentPhase.Completed && confirmed.Book.Available(held.Account, 1) == 10_000);
            Check(confirmed.Book.ForPlayer(1)!.Balance == 10_000);
            Check(confirmed.Advance(null, new(order.Id, ReceiptKind.Started), false, false, null) == confirmed);
        });

        test("unauthorized households, insufficient family gold and duplicate construction IDs cannot start", () =>
        {
            var book = Family(70_000);
            Reject(() => FamilyConstruction.Authorize(book, Request(0, owner: 7, guest: 1), true));
            Reject(() => FamilyConstruction.Authorize(book, Request(2, owner: 2, guest: 1), true));
            var request = Request(1, owner: 2, guest: 1);
            Reject(() => FamilyConstruction.Authorize(book, request, false));
            var order = FamilyConstruction.Authorize(book, request, true);
            Check(order.Book.Available(order.Account, 1) == 5_000);
            Reject(() => FamilyConstruction.Authorize(order.Book, request, true));
        });

        test("cancelled family construction releases its exact reservation after the refund receipt", () =>
        {
            var book = Family(15_000);
            var request = Request(0, owner: 1, guest: 2);
            var held = FamilyConstruction.Authorize(book, request, true);
            var scheduled = held.Advance(new(request.Id, ReceiptKind.MaterialsHeld), null, false, false, null);
            var cancelling = scheduled.Cancel("upgrade-disconnected");
            Check(cancelling.Advance(null, null, true, false, null) == cancelling);
            var waiting = cancelling.Advance(new(request.Id, ReceiptKind.MaterialsHeld), null, true, true, null);
            Check(waiting.Order.Phase == PaymentPhase.Refund && waiting.Book.Available(held.Account, 2) == 5_000);
            var refunded = waiting.Advance(new(request.Id, ReceiptKind.Returned), null, false, true, null);
            Check(refunded.Order.Phase == PaymentPhase.Cancelled && refunded.Book.Available(held.Account, 1) == 15_000);
            Check(refunded.Advance(new(request.Id, ReceiptKind.Returned), null, false, true, null) == refunded);
        });

        test("restoring the construction journal and simultaneous store credit preserves the exact household balance", () =>
        {
            var book = Family(123_456);
            var request = Request(2, owner: 1, guest: 2);
            var held = FamilyConstruction.Authorize(book, request, true);
            var scheduled = held.Advance(new(request.Id, ReceiptKind.MaterialsHeld), null, false, false, null);
            var updated = scheduled.Book.Post(Guid.NewGuid(), held.Account, 1, 123, PostingKind.Sale);
            var sale = updated.Postings.Values.Single(post => post.Kind == PostingKind.Sale);
            updated = updated.Resolve(new(sale.Id, 1, held.Account, PostingState.Applied));
            var reloaded = JsonSerializer.Deserialize<FamilyConstruction>(JsonSerializer.Serialize(scheduled.RefreshBook(updated)))!;
            var confirmed = reloaded.Cancel("upgrade-disconnected").Advance(null,
                new(request.Id, ReceiptKind.Started), true, true, null);
            Check(confirmed.Order.Phase == PaymentPhase.Completed && confirmed.Book.Available(held.Account, 1) == 23_579);
        });

        test("two gold purchases on the same family balance receive only one native grant", () =>
        {
            var shop = FamilyShop.Open(Family(15_000));
            var first = new NativeShopQuote(PostingKind.Purchase, "SeedShop", "(O)472", 0, 1000, 10, 9);
            var (held, grant) = shop.Authorize(Guid.NewGuid(), 2, first);
            Check(held.CanApply(grant, first) && held.Book.Available(grant.Account, 1) == 5_000);
            Reject(() => held.Authorize(Guid.NewGuid(), 1, first));
            Reject(() => held.Authorize(grant.Id, 2, first with { Item = "(O)473" }));
            var charged = held.Settle(new(grant, PostingState.Applied));
            Check(!charged.CanApply(grant, first) && charged.Book.Available(grant.Account, 1) == 5_000);
            Check(charged.Settle(new(grant, PostingState.Applied)) == charged);
        });

        test("deferred shop purchases re-check the offer before accepting an applied result", () =>
        {
            var shop = FamilyShop.Open(Family(15_000));
            var quote = new NativeShopQuote(PostingKind.Purchase, "SeedShop", "(O)472", 0, 100, 10, 9);
            var (held, grant) = shop.Authorize(Guid.NewGuid(), 2, quote);
            Check(!held.CanApply(grant, quote with { OfferRevision = 10 }));
            Check(!held.CanApply(grant, quote with { Quantity = 5 }));
            Reject(() => held.Settle(new(grant with { Quote = quote with { OfferRevision = 10 } }, PostingState.Applied)));
            var reversed = held.Settle(new(grant, PostingState.Reversed));
            Check(reversed.Book.Available(grant.Account, 1) == 15_000 && !reversed.CanApply(grant, quote));
        });

        test("shop sales keep provisional credits unavailable and settle once after the inventory action", () =>
        {
            var shop = FamilyShop.Open(Family(15_000));
            var quote = new NativeShopQuote(PostingKind.Sale, "FishShop", "(O)136", 0, 73, 5, 12);
            var (held, order) = shop.Authorize(Guid.NewGuid(), 1, quote);
            Check(held.Book.Available(order.Account, 2) == 15_000);
            var reloaded = JsonSerializer.Deserialize<FamilyShop>(JsonSerializer.Serialize(held))!;
            var credited = reloaded.Settle(new(order, PostingState.Applied));
            Check(credited.Book.Available(order.Account, 2) == 15_365);
            Check(credited.Settle(new(order, PostingState.Applied)) == credited);
            Reject(() => credited.Authorize(Guid.NewGuid(), 2, quote with { Currency = 4 }));
        });

        test("a repeated shop grant before the host sees the receipt never gives the item twice", () =>
        {
            var shop = FamilyShop.Open(Family(15_000));
            var quote = new NativeShopQuote(PostingKind.Purchase, "SeedShop", "(O)472", 0, 100, 2, 7);
            var (authorized, order) = shop.Authorize(Guid.NewGuid(), 2, quote);
            int units = 0;
            ShopOutcome? saved = null;
            var first = ShopParticipant.Apply(authorized, order, quote, _ => saved, (_, receipt) => saved = receipt, Plan);
            var repeat = ShopParticipant.Apply(authorized, order, quote, _ => saved, (_, receipt) => saved = receipt, Plan);
            Check(first == repeat && first.State == PostingState.Applied && units == 2);
            Check(authorized.Settle(first).Book.Available(order.Account, 1) == 14_800);

            PaymentEdit Plan()
            {
                int before = units;
                var edit = new PaymentEdit();
                edit.Add(() => units += 2, () => units = before);
                return edit;
            }
        });

        test("failure to persist a shop receipt rolls back the native inventory edit", () =>
        {
            var shop = FamilyShop.Open(Family(15_000));
            var quote = new NativeShopQuote(PostingKind.Sale, "FishShop", "(O)136", 0, 7, 5, 7);
            var (authorized, order) = shop.Authorize(Guid.NewGuid(), 2, quote);
            int units = 5;
            ShopOutcome? saved = null;
            var effect = new PaymentEdit();
            effect.Add(() => units -= 5, () => units = 5);
            Reject(() => ShopParticipant.Apply(authorized, order, quote, _ => saved,
                (_, receipt) =>
                {
                    saved = receipt;
                    if (receipt?.State == PostingState.Applied)
                        throw new InvalidOperationException("The receipt write failed after storing the value.");
                }, () => effect));
            Check(units == 5 && saved?.State == PostingState.Reversed && authorized.Book.Available(order.Account, 1) == 15_000);
        });

        test("the online and offline shipping paths share one persisted claim per farmer per day", () =>
        {
            var book = Family(15_000);
            var night = new ShippingClaim(book.Farm, book.Day, 1, 725);
            var other = night with { Player = 2, Gold = 300 };
            var delivered = FamilyShipping.RecordClearedBin(FamilyShipping.RecordClearedBin(book, night), other);
            var reloaded = JsonSerializer.Deserialize<BudgetBook>(JsonSerializer.Serialize(delivered))!;
            Check(FamilyShipping.RecordClearedBin(reloaded, night) == reloaded);
            Check(FamilyShipping.RecordClearedBin(reloaded, other) == reloaded);
            Check(reloaded.ForPlayer(1)!.Balance == 16_025);
            Reject(() => FamilyShipping.RecordClearedBin(reloaded, night with { Gold = 921 }));
            Check(FamilyShipping.RecordClearedBin(reloaded, night with { Player = 7 }) == reloaded);
        });
    }

    private static UpgradeRequest Request(int level, long owner, long guest) =>
        new(Guid.NewGuid(), owner, guest, level, DateTimeOffset.UtcNow.AddMinutes(3));

    private static BudgetBook Family(int balance)
    {
        var book = BudgetBook.Open(Guid.NewGuid(), 1, new Dictionary<long, int> { [1] = balance, [2] = 0, [7] = 450 });
        var invite = Guid.NewGuid();
        book = book.Propose(invite, Pair.Create(1, 2), 1, ChangeKind.Merge, true, true).Answer(invite, 2, true);
        var night = book.PrepareNight(Guid.NewGuid(), _ => true, _ => true, true);
        return book.ConfirmSave(night, new(book.Farm, night.Id, night.Day));
    }

    private static void Check(bool result)
    {
        if (!result)
            throw new InvalidOperationException("Economy fixture assertion failed.");
    }

    private static void Reject(Action run)
    {
        try { run(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the economic operation to fail.");
    }
}
