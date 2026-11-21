using System.Text.Json;
using StardewModdingAPI;
using StokHouse.FamilyBudget;

namespace StokHouse;

internal sealed record ShopRequest(Guid Id, NativeShopQuote Quote);
internal sealed record ShopResult(Guid Id, string Notice);

public sealed partial class ModEntry
{
    private sealed record LocalShopRequest(ShopRequest Request, object Menu, object Offer, object? Cursor, int CursorStack);
    private LocalShopRequest? localPurchase;
    private ShopOrder? incomingShopGrant;
    private bool shopDeliveryFault;

    private void ClearShop()
    {
        localPurchase = null;
        incomingShopGrant = null;
        shopDeliveryFault = false;
    }

    private int? ShopBalance()
    {
        if (!Running || !BudgetOptIn)
            return null;
        if (Context.IsMainPlayer)
            return budgetSession?.View(game!.LocalId).Available;
        return localBudget?.View.Available;
    }

    private bool HandleShopClick(object menu, object item, object? held, int amount)
    {
        bool awaitingAccount = Running && BudgetOptIn && !Context.IsMainPlayer && localBudget is null;
        if (ShopBalance() is null && !awaitingAccount || game!.Shop is not { } shop ||
            shop.LocalQuote(menu, item, held, amount) is not { } quote)
            return false; // Special and ordinary personal offers stay with the native method.
        if (awaitingAccount)
        {
            Tell("budget-shop-disabled");
            return true;
        }

        if (shopDeliveryFault || budgetFault || localPurchase is not null)
        {
            Tell("budget-shop-wait");
            return true;
        }
        if (quote.Quantity <= 0 || shop.PlanDelivery(menu, item, held, quote.Quantity) is null)
        {
            Tell("budget-shop-space");
            return true;
        }
        var request = new ShopRequest(Guid.NewGuid(), quote);
        localPurchase = new LocalShopRequest(request, menu, item, held, held is null ? 0 : GameApi.StackSize(held));
        if (Context.IsMainPlayer)
        {
            ProcessShopRequest(game.LocalId, request);
            ApplyShopGrant();
            SettleShopReceipts();
        }
        else
            Send(request, "shop-request", game.HostId);
        return true;
    }

    private void SendShopResult(long buyer, Guid id, string reason) =>
        Deliver(buyer, new ShopResult(id, reason), "shop-result", ReceiveShopResult);

    private void ReceiveShopResult(ShopResult result)
    {
        if (localPurchase?.Request.Id != result.Id)
            return;
        localPurchase = null;
        if (incomingShopGrant?.Id == result.Id)
            incomingShopGrant = null;
        Tell(result.Notice);
    }

    private void ProcessShopRequest(long buyer, ShopRequest request)
    {
        if (!Running || !BudgetOptIn || budgetFault || budgetSession is not { Saving: false } session ||
            !BudgetPeer(buyer) || !IsOnline(buyer) || request.Id == Guid.Empty || request.Quote is not { } quote)
        {
            SendShopResult(buyer, request.Id, "budget-shop-disabled");
            return;
        }
        try
        {
            var shop = FamilyShop.Open(session.Book);
            if (shop.Orders.TryGetValue(request.Id, out var old))
            {
                if (old.BuyerOrSeller != buyer || old.Quote != quote)
                    return;
                var state = session.Book.Postings[old.Id].State;
                if (state != PostingState.Held)
                    SendShopResult(buyer, old.Id, state == PostingState.Applied ? "budget-shop-bought" : "budget-shop-declined");
                else
                    DeliverShopGrant(buyer, old);
                return;
            }
            if (shop.Pending(buyer) is not null || session.Book.ForPlayer(buyer) is not { } account ||
                !BudgetSeparate() || BudgetPartner(buyer) is not long partner || !account.Pair.Contains(partner))
            {
                SendShopResult(buyer, request.Id, "budget-shop-wait");
                return;
            }
            if (game!.FindFarmer(buyer) is not { } farmer || game.Shop!.HostQuote(farmer, quote) != quote)
            {
                SendShopResult(buyer, request.Id, "budget-shop-price");
                return;
            }
            if ((long)quote.UnitPrice * quote.Quantity > session.Book.Available(account.Id, buyer))
            {
                SendShopResult(buyer, request.Id, "budget-shop-funds");
                return;
            }
            var (held, order) = shop.Authorize(request.Id, buyer, quote);
            StoreShopBook(held.Book);
            BudgetLog($"shop reserve id={order.Id} buyer={buyer} shop={quote.Shop} offer={quote.Entry} units={quote.Quantity} unitPrice={quote.UnitPrice}");
            DeliverShopGrant(buyer, order);
        }
        catch (Exception ex)
        {
            if (ex is PaymentRecoveryException)
                budgetFault = true;
            BudgetLog($"shop denied id={request.Id} buyer={buyer}: {ex.GetBaseException().Message}", LogLevel.Warn);
            SendShopResult(buyer, request.Id, "budget-shop-disabled");
        }
    }

    private void DeliverShopGrant(long buyer, ShopOrder order) =>
        Deliver(buyer, order, "shop-grant", ReceiveShopGrant);

    private void ResendShopGrant(long buyer)
    {
        if (budgetSession is { Saving: false } session && FamilyShop.Open(session.Book).Pending(buyer) is { } order)
            DeliverShopGrant(buyer, order);
    }

    private void ReceiveShopGrant(ShopOrder order)
    {
        if (!BudgetOptIn || order.BuyerOrSeller != game!.LocalId ||
            localPurchase is { } purchase && purchase.Request.Id != order.Id)
            return;
        incomingShopGrant = order;
        ApplyShopGrant();
    }

    private void ApplyShopGrant()
    {
        if (shopDeliveryFault || incomingShopGrant is not { } order || game!.Shop is not { } shop)
            return;
        try
        {
            var book = Context.IsMainPlayer ? budgetSession?.Book :
                game.Load(SavedBudgetProbe.JournalKey) is { Length: > 0 and <= 2_000_000 } raw
                    ? JsonSerializer.Deserialize<BudgetBook>(raw) : null;
            if (book is null || book.ShopOrders?.GetValueOrDefault(order.Id) != order ||
                !book.Postings.TryGetValue(order.Id, out var posting))
                return; // Replication can arrive after the targeted grant.
            if (posting.State != PostingState.Held)
            {
                incomingShopGrant = null;
                return;
            }
            if (Context.IsMainPlayer ? budgetFault || budgetSession is { Saving: true } :
                localBudget is not { Ready: true } frame || frame.View.Account != order.Account)
                return;

            var local = localPurchase;
            var current = local?.Request.Id == order.Id && shop.IsOpen(local.Menu) &&
                (local.Cursor is null || GameApi.StackSize(local.Cursor) == local.CursorStack)
                ? shop.LocalQuote(local.Menu, local.Offer, local.Cursor, order.Quote.Quantity) : null;
            var outcome = ShopParticipant.Apply(FamilyShop.Open(book), order, current ?? order.Quote with { Quantity = 0 },
                id => ReadShopOutcome(game.Player, id), (id, value) =>
                    game.Store(game.Player, SavedBudgetProbe.ShopReceiptKey(id), value is null ? "" : JsonSerializer.Serialize(value)),
                () => shop.PlanDelivery(local!.Menu, local.Offer, local.Cursor, order.Quote.Quantity));
            incomingShopGrant = null;
            BudgetLog($"shop delivery id={order.Id} buyer={order.BuyerOrSeller} outcome={outcome.State}", LogLevel.Debug);
            if (outcome.State == PostingState.Held)
            {
                shopDeliveryFault = true;
                Tell("budget-shop-recovery");
            }
        }
        catch (Exception ex)
        {
            shopDeliveryFault |= ex is PaymentRecoveryException;
            incomingShopGrant = null;
            BudgetLog($"shop delivery id={order.Id} fault={shopDeliveryFault}: {ex}", LogLevel.Warn);
            Tell(shopDeliveryFault ? "budget-shop-recovery" : "budget-shop-declined");
        }
    }

    private void SettleShopReceipts()
    {
        if (!Context.IsMainPlayer || budgetFault || budgetSession is not { Saving: false } session)
            return;
        var open = FamilyShop.Open(session.Book);
        foreach (var order in open.Orders.Values.Where(order => session.Book.Postings[order.Id].State == PostingState.Held).ToArray())
        {
            try
            {
                if (game!.FindFarmer(order.BuyerOrSeller) is not { } farmer ||
                    ReadShopOutcome(farmer, order.Id) is not { State: PostingState.Applied or PostingState.Reversed } receipt)
                    continue; // Disconnection or an uncertain receipt alone never authorizes a refund.
                var completed = FamilyShop.Open(session.Book).Settle(receipt);
                StoreShopBook(completed.Book);
                BudgetLog($"shop settle id={order.Id} buyer={order.BuyerOrSeller} result={receipt.State}");
                if (IsOnline(order.BuyerOrSeller))
                    SendShopResult(order.BuyerOrSeller, order.Id,
                        receipt.State == PostingState.Applied ? "budget-shop-bought" : "budget-shop-declined");
            }
            catch (Exception ex)
            {
                budgetFault = true;
                BudgetLog($"shop settlement frozen id={order.Id}: {ex}", LogLevel.Error);
                return;
            }
        }
    }

    private void StoreShopBook(BudgetBook next)
    {
        var edit = new PaymentEdit();
        AppendBudgetUpdate(edit, next);
        edit.Commit();
        AdoptBudgetUpdate(next);
    }

    private ShopOutcome? ReadShopOutcome(object farmer, Guid id)
    {
        string? raw = game!.Load(farmer, SavedBudgetProbe.ShopReceiptKey(id));
        if (string.IsNullOrEmpty(raw))
            return null;
        var outcome = raw.Length <= 4096 ? JsonSerializer.Deserialize<ShopOutcome>(raw) : null;
        if (outcome?.Order.Id != id || !Enum.IsDefined(outcome.State))
            throw new InvalidDataException($"Invalid saved shop receipt {id}.");
        return outcome;
    }
}
