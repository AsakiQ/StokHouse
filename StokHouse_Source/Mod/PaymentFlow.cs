using System.Text.Json;
using StardewModdingAPI;
using StokHouse.Core;

namespace StokHouse;

public sealed partial class ModEntry
{
    private const string OrderKey = "StokHouse.CoopHousing/upgrade-order";
    private const string MaterialsKey = "StokHouse.CoopHousing/upgrade-materials";
    private const string ConstructionKey = "StokHouse.CoopHousing/upgrade-construction";
    private string? paymentJson;
    private UpgradePayment? paymentCache;
    private Guid refundNotice;
    private bool paymentFault;

    private void ClearPayments()
    {
        paymentJson = null;
        paymentCache = null;
        refundNotice = Guid.Empty;
        paymentFault = false;
    }

    private bool IsPayingForUpgrade(long player) => CurrentPayment() is { Active: true } order && order.Request.Guest == player;

    private UpgradePayment? CurrentPayment()
    {
        var json = game!.Load(OrderKey);
        if (json == paymentJson)
            return paymentCache;
        paymentJson = json;
        if (string.IsNullOrEmpty(json))
            return paymentCache = null;

        try
        {
            var order = json.Length <= 8192 ? JsonSerializer.Deserialize<UpgradePayment>(json) : null;
            if (order is null || order.Request is not { } request || request.Id == Guid.Empty ||
                request.Guest == 0 || request.Owner == 0 || request.Guest == request.Owner ||
                request.Level is < 0 or > 2 || !Enum.IsDefined(order.Phase) || order.RecentOrders?.Length > 64)
                throw new InvalidDataException("The saved upgrade order is invalid.");
            return paymentCache = order;
        }
        catch (Exception ex)
        {
            paymentFault = true;
            Monitor.Log($"Stok House could not read the saved upgrade order: {ex.Message}", LogLevel.Error);
            return paymentCache = null;
        }
    }

    private void BeginPayment(UpgradeRequest request, bool ownerApproved)
    {
        var previous = CurrentPayment();
        string? blocked = paymentFault ? "upgrade-recovery" : previous?.Active == true ? "upgrade-pending" : null;
        var payer = game!.FindFarmer(request.Guest);
        var owner = game.FindFarmer(request.Owner);
        if (blocked is null)
        {
            if (payer is null || owner is null || !IsOnline(request.Guest) || !IsOnline(request.Owner))
                blocked = "upgrade-disconnected";
            else if (!HasPurchaseApproval(ownerApproved, households.Rules(request.Owner).Upgrade, GameApi.Separate(owner)))
                blocked = "upgrade-approval-required";
            else
                blocked = game.PaymentResources(payer, request.Level);
        }
        if (blocked is not null)
        {
            households.CompleteUpgrade(request.Id);
            RejectUpgrade(request.Guest, blocked);
            return;
        }
        var order = new UpgradePayment(request, ownerApproved, GameApi.Separate(owner!), RecentOrders: previous?.Archive());
        try
        {
            StorePayment(order);
            LogRobin($"authorized order={request.Id} payer={request.Guest} owner={request.Owner} separateWallet={order.SeparateWallet}");
        }
        catch (Exception ex)
        {
            households.CompleteUpgrade(request.Id);
            Monitor.Log($"Stok House could not record order {request.Id}: {ex}", LogLevel.Error);
            RejectUpgrade(request.Guest, "upgrade-error");
        }
    }

    private void StorePayment(UpgradePayment order) => game!.Store(OrderKey, JsonSerializer.Serialize(order));

    private bool PaymentWalletHeld() => CurrentPayment()?.GoldHeld == true;

    private void CancelPayment(long actor, Guid request)
    {
        if (CurrentPayment() is not { Active: true, Paused: false } order || order.Request.Guest != actor || order.Request.Id != request)
        {
            Notify(actor, "expired");
            return;
        }
        var cancelled = order.Cancel("upgrade-cancelled");
        if (cancelled == order)
            return;
        StorePayment(cancelled);
        LogRobin($"cancellation requested order={request} payer={actor}");
        Notify(actor, "upgrade-cancelling");
    }

    private void CancelPaymentOnDisconnect(long player)
    {
        if (Running && CurrentPayment() is { Active: true } order &&
            (player == order.Request.Guest || player == order.Request.Owner))
        {
            LogRobin($"participant disconnected player={player} order={order.Request.Id} phase={order.Phase}");
            var stopped = order.Cancel("upgrade-disconnected");
            if (stopped != order && !order.Paused)
                StorePayment(stopped);
        }
    }

    private void TickPayments()
    {
        var order = CurrentPayment();
        if (paymentFault || order is not { Active: true, Paused: false })
            return;

        try
        {
            if (Context.IsMainPlayer)
                order = AdvanceHostPayment(order);
            if (order.Active && !order.Paused && Context.CanPlayerMove && !(bool)GameApi.Read(game!.Game, "eventUp")!)
                AdvanceLocalPayment(order);
        }
        catch (PaymentRecoveryException ex)
        {
            paymentFault = true;
            if (Context.IsMainPlayer)
            {
                try { StorePayment((CurrentPayment() ?? order) with { Paused = true }); }
                catch (Exception journalFailure) { Monitor.Log($"Stok House could not persist the payment pause: {journalFailure}", LogLevel.Error); }
            }
            Monitor.Log($"Stok House paused order {order.Request.Id}: {ex}", LogLevel.Error);
            Tell("upgrade-recovery");
        }
        catch (Exception ex)
        {
            Monitor.Log($"Stok House rolled back the payment step for {order.Request.Id}: {ex.GetBaseException().Message}", LogLevel.Warn);
            if (Context.IsMainPlayer)
            {
                var stopped = order.Cancel("upgrade-error");
                if (stopped != order)
                    TryRecordPaymentFailure(() => StorePayment(stopped));
            }
            else if ((order.Phase == PaymentPhase.Collect && order.Request.Guest == game!.LocalId) ||
                     (order.Phase == PaymentPhase.Schedule && order.Request.Owner == game!.LocalId))
            {
                string key = order.Phase == PaymentPhase.Collect ? MaterialsKey : ConstructionKey;
                if (ReadReceipt(game.Player, key, order.Request.Id) is null)
                    TryRecordPaymentFailure(() => WriteReceipt(key, order, ReceiptKind.Refused, "upgrade-error"));
            }
        }
    }

    private void TryRecordPaymentFailure(Action record)
    {
        try { record(); }
        catch (Exception ex) { Monitor.Log($"Stok House could not record the payment failure: {ex}", LogLevel.Error); }
    }

    private string? PaymentInvalid(UpgradePayment order, object? payer, object? owner)
    {
        var request = order.Request;
        if (request.Expires <= DateTimeOffset.UtcNow)
            return "expired";
        if (payer is null || owner is null || !IsOnline(request.Guest) || !IsOnline(request.Owner))
            return "upgrade-disconnected";
        if (!SameVersion(request.Guest) || !SameVersion(request.Owner))
            return "peer-missing";
        if (households.Home(request.Guest) != request.Owner)
            return "upgrade-quote-changed";
        var policy = households.Rules(request.Owner).Upgrade;
        if (policy == UpgradePolicy.Disabled)
            return "upgrade-disabled";
        if (GameApi.Separate(game!.Master) != order.SeparateWallet || game.WalletChangePending)
            return "upgrade-wallet-change";
        if (!HasPurchaseApproval(order.OwnerApproved, policy, order.SeparateWallet))
            return "upgrade-approval-required";
        if (game.UpgradeBlock(owner, request.Level) is { } blocked)
            return blocked;
        return order.Phase == PaymentPhase.Collect && game.Gold(payer) < GameApi.Price(request.Level).Gold
            ? "funds" : null;
    }

    private UpgradePayment AdvanceHostPayment(UpgradePayment order)
    {
        var request = order.Request;
        // getAllFarmers returns the saved farmhand copy once its live network root is gone.
        var payer = game!.FindFarmer(request.Guest);
        var owner = game.FindFarmer(request.Owner);
        var payerReceipt = ReadReceipt(payer, MaterialsKey, request.Id);
        var ownerReceipt = ReadReceipt(owner, ConstructionKey, request.Id);
        var next = order.Advance(payerReceipt, ownerReceipt,
            payer is not null && game.IsSavedOffline(payer), owner is not null && game.IsSavedOffline(owner),
            order.Phase is PaymentPhase.Collect or PaymentPhase.Schedule ? PaymentInvalid(order, payer, owner) : null);
        if (next is null)
            return order;

        var edit = new PaymentEdit();
        if (next.GoldChange != 0)
        {
            if (payer is null || GameApi.Separate(game.Master) != order.SeparateWallet)
                return order; // leave the claim in the journal if the original wallet cannot be reached
            game.AddWalletEdit(edit, payer, next.GoldChange);
        }
        game.AddDataEdit(edit, game.Master, OrderKey, JsonSerializer.Serialize(next.Payment));
        edit.Commit();
        LogRobin($"order={request.Id} {order.Phase} -> {next.Payment.Phase} goldChange={next.GoldChange} reason={next.Payment.Reason}");
        if (next.Payment.Paused)
        {
            Notify(request.Guest, "upgrade-recovery");
            Notify(request.Owner, "upgrade-recovery");
        }
        if (!next.Payment.Active)
        {
            households.CompleteUpgrade(request.Id);
            string code = next.Payment.Phase == PaymentPhase.Completed ? "upgrade-started" : next.Payment.Reason ?? "upgrade-cancelled";
            Notify(request.Guest, code);
            if (IsOnline(request.Owner))
                Notify(request.Owner, code);
        }
        return next.Payment;
    }

    private void AdvanceLocalPayment(UpgradePayment order)
    {
        var request = order.Request;
        object local = game!.Player;
        var price = GameApi.Price(request.Level);
        if (request.Guest == game.LocalId && order.Phase is PaymentPhase.Collect or PaymentPhase.Refund)
        {
            var receipt = ReadReceipt(local, MaterialsKey, request.Id);
            if (order.Phase == PaymentPhase.Collect && receipt is null)
            {
                if (request.Expires <= DateTimeOffset.UtcNow)
                {
                    WriteReceipt(MaterialsKey, order, ReceiptKind.Refused, "expired");
                    return;
                }
                var edit = price.Item is null ? new PaymentEdit() : game.PlanMaterials(local, price.Item, -price.Quantity);
                if (edit is null)
                    WriteReceipt(MaterialsKey, order, ReceiptKind.Refused, "materials");
                else
                    WriteReceipt(MaterialsKey, order, ReceiptKind.MaterialsHeld, edit: edit);
            }
            else if (order.Phase == PaymentPhase.Refund && receipt?.Kind is null or ReceiptKind.MaterialsHeld)
            {
                var edit = receipt is not null && price.Item is not null
                    ? game.PlanMaterials(local, price.Item, price.Quantity) : new PaymentEdit();
                if (edit is not null)
                    WriteReceipt(MaterialsKey, order, ReceiptKind.Returned, edit: edit);
                else if (refundNotice != request.Id)
                {
                    refundNotice = request.Id;
                    Tell("upgrade-refund-space");
                }
            }
            return;
        }

        if (request.Owner != game.LocalId || order.Phase is not (PaymentPhase.Schedule or PaymentPhase.StopScheduling) ||
            ReadReceipt(local, ConstructionKey, request.Id) is not null)
            return;
        if (order.Phase == PaymentPhase.StopScheduling)
        {
            WriteReceipt(ConstructionKey, order, ReceiptKind.Stopped);
            return;
        }

        string? blocked = PaymentInvalid(order, game.FindFarmer(request.Guest), local);
        if (blocked is not null)
            WriteReceipt(ConstructionKey, order, ReceiptKind.Refused, blocked);
        else
            WriteReceipt(ConstructionKey, order, ReceiptKind.Started, edit: game.PlanConstruction(local));
    }

    private PaymentReceipt? ReadReceipt(object? player, string key, Guid orderId)
    {
        if (player is null || game!.Load(player, key) is not { Length: > 0 } json)
            return null;
        var receipt = JsonSerializer.Deserialize<PaymentReceipt>(json);
        if (receipt?.Id != orderId)
            return null;
        if (!Enum.IsDefined(receipt.Kind))
            throw new InvalidDataException($"Invalid payment receipt for {orderId}.");
        return receipt;
    }

    private void WriteReceipt(string key, UpgradePayment order, ReceiptKind kind, string? reason = null, PaymentEdit? edit = null)
    {
        edit ??= new PaymentEdit();
        game!.AddDataEdit(edit, game.Player, key, JsonSerializer.Serialize(new PaymentReceipt(order.Request.Id, kind, reason)));
        try
        {
            edit.Commit();
        }
        catch (PaymentRecoveryException)
        {
            // The host must not infer success or refund from an uncertain player snapshot.
            TryRecordPaymentFailure(() => game.Store(game.Player, key,
                JsonSerializer.Serialize(new PaymentReceipt(order.Request.Id, ReceiptKind.NeedsReview))));
            throw;
        }
        LogRobin($"order={order.Request.Id} player={game.LocalId} receipt={kind} reason={reason}");
    }
}
