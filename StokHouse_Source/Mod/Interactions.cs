using StokHouse.Core;

namespace StokHouse;

public sealed partial class ModEntry
{
    private string T(string key) => Helper.Translation.Get(key).ToString();

    private void Ask(string title, IEnumerable<(string Key, string Label)> choices, Action<string> select)
    {
        game!.Ask(title, choices.Append(("back", T("back"))).ToArray(), answer =>
        {
            if (answer != "back")
                dialogs.Enqueue(() => { if (Running) select(answer); });
        });
    }

    private void Confirm(string title, Action<bool> select)
    {
        game!.Ask(title, new[] { ("yes", T("yes")), ("no", T("no")) }, answer =>
            dialogs.Enqueue(() => { if (Running) select(answer == "yes"); }));
    }

    private void ShowMenu()
    {
        var id = game!.LocalId;
        if (!Homes.TryGetValue(households.Home(id), out var home))
        {
            Tell("not-ready");
            return;
        }
        var residents = new[] { home.Owner }.Concat(households.Guests(home.Owner)).Select(game.Name);
        string title = Helper.Translation.Get("menu", new
        {
            owner = game.Name(home.Owner), residents = string.Join(", ", residents), capacity
        }).ToString();
        var choices = new List<(string, string)>();
        if (home.Owner == id)
        {
            choices.Add(("invite", T("invite")));
            choices.Add(("rights", T("rights")));
        }
        else
            choices.Add(("leave", T("leave")));
        if (inbox.Count > 0)
            choices.Add(("inbox", T("inbox")));
        if (outbox.Count > 0)
            choices.Add(("outbox", T("outbox")));
        if (offers.Count > 0)
            choices.Add(("offers", T("offers")));
        var payment = CurrentPayment();
        if (payment is { Paused: false } && payment.Request.Guest == id && payment.Phase is PaymentPhase.Collect or PaymentPhase.Schedule)
            choices.Add(("cancel-upgrade", T("upgrade-cancel")));
        if (id == game.HostId)
            choices.Add(("reset", T("reset")));

        Ask(title, choices, answer =>
        {
            switch (answer)
            {
                case "invite": ShowPlayers(); break;
                case "rights": ShowRights(); break;
                case "leave": Confirm(T("leave-confirm"), yes => { if (yes) Submit("leave"); }); break;
                case "inbox":
                    if (inbox.Values.OrderBy(item => item.Expires).FirstOrDefault() is { } invite)
                        ShowInvite(invite);
                    break;
                case "outbox": ShowOutgoing(); break;
                case "offers":
                    if (offers.Values.OrderBy(item => item.Expires).FirstOrDefault() is { } offer)
                        ShowUpgrade(offer);
                    break;
                case "cancel-upgrade":
                    Confirm(T("upgrade-cancel-confirm"), yes =>
                    {
                        if (yes)
                            Submit("upgrade-cancel", request: payment!.Request.Id);
                    });
                    break;
                case "reset": Confirm(T("reset-confirm"), yes => { if (yes) Submit("reset"); }); break;
            }
        });
    }

    private void ShowPlayers()
    {
        long id = game!.LocalId;
        var available = game.Online()
            .Select(GameApi.Id)
            .Where(player => player != id && player != game.HostId && SameVersion(player) &&
                             Homes.ContainsKey(player) && households.Home(player) != id && !households.Guests(player).Any())
            .OrderBy(game.Name).ToArray();
        if (available.Length == 0)
        {
            Tell("no-players");
            return;
        }
        Ask(T("pick-player"), available.Select(player => (player.ToString(), game.Name(player))), answer =>
        {
            if (long.TryParse(answer, out long player) && available.Contains(player))
                Submit("invite", player);
        });
    }

    private void ShowInvite(Invitation item)
    {
        if (item.Expires <= DateTimeOffset.UtcNow || !inbox.ContainsKey(item.Id))
            return;
        string text = Helper.Translation.Get("invitation", new { player = game!.Name(item.Owner) }).ToString();
        Ask(text, new[] { ("accept", T("accept")), ("decline", T("decline")) }, answer =>
        {
            if (inbox.Remove(item.Id))
                Submit(answer, request: item.Id);
        });
    }

    private void ShowOutgoing()
    {
        if (outbox.Values.FirstOrDefault() is not { } item)
            return;
        string text = Helper.Translation.Get("outgoing", new { player = game!.Name(item.Guest) }).ToString();
        Confirm(text, cancel =>
        {
            if (cancel && outbox.Remove(item.Id))
                Submit("cancel", request: item.Id);
        });
    }

    private void ShowRights()
    {
        var rules = households.Rules(game!.LocalId);
        var choices = new[]
        {
            ("decor", T(rules.Decorate ? "decor-disable" : "decor-enable")),
            ("confirm", T("mode-confirm")),
            ("joint", T("mode-joint")),
            ("disabled", T("mode-disabled"))
        };
        Ask(T("rights-title") + " " + T(rules.Upgrade switch
        {
            UpgradePolicy.Confirm => "mode-confirm",
            UpgradePolicy.JointWallet => "mode-joint",
            _ => "mode-disabled"
        }), choices, answer =>
        {
            switch (answer)
            {
                case "decor": Submit("rights", rules: rules with { Decorate = !rules.Decorate }); break;
                case "confirm": Submit("rights", rules: rules with { Upgrade = UpgradePolicy.Confirm }); break;
                case "disabled": Submit("rights", rules: rules with { Upgrade = UpgradePolicy.Disabled }); break;
                case "joint":
                    Confirm(T("joint-confirm"), accept =>
                    {
                        if (accept)
                            Submit("rights", rules: rules with { Upgrade = UpgradePolicy.JointWallet });
                    });
                    break;
            }
        });
    }

    private void RequestUpgrade(long actor, long quotedOwner, int? quotedLevel, Guid operation, DateTimeOffset now)
    {
        long house = households.Home(actor);
        LogRobin($"request guest={actor} house={house} quotedOwner={quotedOwner} level={quotedLevel}");
        var previous = CurrentPayment();
        if (paymentFault || previous?.Paused == true)
            RejectUpgrade(actor, "upgrade-recovery");
        else if (previous?.HasSeen(operation) == true)
            LogRobin($"ignored replayed order {operation}");
        else if (previous?.Active == true)
            RejectUpgrade(actor, "upgrade-pending");
        else if (!households.IsGuest(actor) || quotedOwner != house || quotedLevel is not (0 or 1 or 2))
            RejectUpgrade(actor, "upgrade-quote-changed");
        else if (households.Rules(house).Upgrade == UpgradePolicy.Disabled)
            RejectUpgrade(actor, "upgrade-disabled");
        else if (!AllPeersReady())
            RejectUpgrade(actor, "peer-missing");
        else if (!IsOnline(house) || !SameVersion(house))
            RejectUpgrade(actor, "upgrade-owner-offline");
        else if (!Homes.TryGetValue(house, out var home) || GameApi.Read(home.Interior, "owner") is not { } owner)
            RejectUpgrade(actor, "upgrade-unavailable");
        else if (game!.FindFarmer(actor) is not { } guest || !game.IsAtRobin(guest))
            RejectUpgrade(actor, "robin-unavailable");
        else if (game.UpgradeBlock(owner, quotedLevel.Value) is { } blocked)
            RejectUpgrade(actor, blocked);
        else if (game.WalletChangePending)
            RejectUpgrade(actor, "upgrade-wallet-change");
        else if (game.PaymentResources(guest, quotedLevel.Value) is { } resourceError)
            RejectUpgrade(actor, resourceError);
        else
        {
            Outcome result = households.RequestUpgrade(actor, quotedLevel.Value, now, out var request, operation);
            if (request is null)
            {
                RejectUpgrade(actor, result.Code);
                return;
            }
            Notify(actor, result.Code);
            var policy = households.Rules(house).Upgrade;
            bool separate = GameApi.Separate(owner);
            bool needsApproval = RequiresOwnerApproval(policy, separate);
            LogRobin($"request {request.Id} policy={policy} separateWallets={separate} ownerApproval={needsApproval}");
            if (needsApproval)
                Deliver(house, request, "upgrade", ReceiveUpgrade);
            else
                DecideUpgrade(house, request.Id, true, now, ownerApproved: false);
        }
    }

    internal static bool RequiresOwnerApproval(UpgradePolicy policy, bool separateWallets) =>
        policy != UpgradePolicy.JointWallet || separateWallets;

    internal static bool HasPurchaseApproval(bool ownerApproved, UpgradePolicy policy, bool separateWallets) =>
        policy != UpgradePolicy.Disabled && (ownerApproved || !RequiresOwnerApproval(policy, separateWallets));

    private void RejectUpgrade(long player, string code)
    {
        LogRobin($"request refused player={player} reason={code}");
        Notify(player, code);
    }

    private void DecideUpgrade(long owner, Guid id, bool accept, DateTimeOffset now, bool ownerApproved = true)
    {
        if (!households.OwnsUpgrade(id, owner, now, out var request) || request is null || CurrentPayment()?.HasSeen(id) == true)
        {
            LogRobin($"decision ignored request={id} owner={owner}; missing, expired or already granted");
            Notify(owner, "expired");
            return;
        }
        if (!accept)
        {
            households.CompleteUpgrade(id);
            LogRobin($"request {id} declined by owner {owner}");
            Notify(request.Guest, "upgrade-declined");
            return;
        }

        string? blocked;
        if (!AllPeersReady())
            blocked = "peer-missing";
        else if (!IsOnline(request.Guest) || households.Home(request.Guest) != owner)
            blocked = "upgrade-quote-changed";
        else if (households.Rules(owner).Upgrade == UpgradePolicy.Disabled)
            blocked = "upgrade-disabled";
        else if (game!.FindFarmer(owner) is not { } farmer || !IsOnline(owner))
            blocked = "upgrade-owner-offline";
        else
            blocked = game.UpgradeBlock(farmer, request.Level);
        if (blocked is null && game!.WalletChangePending)
            blocked = "upgrade-wallet-change";

        if (blocked is not null)
        {
            households.CompleteUpgrade(id);
            RejectUpgrade(request.Guest, blocked);
            Notify(owner, blocked);
            return;
        }
        BeginPayment(request, ownerApproved);
    }

    private void ReceiveUpgrade(UpgradeRequest item)
    {
        if (item.Owner != game!.LocalId)
            return;
        offers[item.Id] = item;
        dialogs.Enqueue(() => { if (offers.ContainsKey(item.Id)) ShowUpgrade(item); });
    }

    private string Quote(int level)
    {
        var (gold, item, quantity) = GameApi.Price(level);
        return item is null ? $"{gold:N0} G" : $"{gold:N0} G + {quantity} {T(item == "(O)388" ? "wood" : "hardwood")}";
    }

    private void ShowUpgrade(UpgradeRequest item)
    {
        if (!offers.ContainsKey(item.Id) || item.Expires <= DateTimeOffset.UtcNow)
            return;
        var text = Helper.Translation.Get("upgrade-confirm", new
        {
            player = game!.Name(item.Guest), price = Quote(item.Level)
        }).ToString();
        Ask(text, new[] { ("accept", T("accept")), ("decline", T("decline")) }, reply =>
        {
            if (offers.Remove(item.Id))
                Submit(reply == "accept" ? "upgrade-accept" : "upgrade-decline", request: item.Id);
        });
    }
}
