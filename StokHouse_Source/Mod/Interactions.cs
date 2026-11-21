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

    private void RequestUpgrade(long actor, DateTimeOffset now)
    {
        if (granted.Count != 0)
        {
            Notify(actor, "upgrade-pending");
            return;
        }
        long house = households.Home(actor);
        if (!AllPeersReady() || !IsOnline(house) || !SameVersion(house) ||
            !Homes.TryGetValue(house, out var home) || GameApi.Read(home.Interior, "owner") is not { } owner ||
            game!.FindFarmer(actor) is not { } guest || !game.IsAtRobin(guest))
        {
            Notify(actor, "robin-unavailable");
            return;
        }
        int level = GameApi.Level(owner);
        if (!game.CanStartUpgrade(owner, level))
        {
            Notify(actor, "upgrade-unavailable");
            return;
        }
        Outcome result = households.RequestUpgrade(actor, level, now, out var request);
        Notify(actor, result.Code);
        if (request is null)
            return;
        if (households.Rules(house).Upgrade == UpgradePolicy.JointWallet && !GameApi.Separate(owner))
            DecideUpgrade(house, request.Id, true, now);
        else
            Deliver(house, request, "upgrade", ReceiveUpgrade);
    }

    private void DecideUpgrade(long owner, Guid id, bool accept, DateTimeOffset now)
    {
        if (!households.OwnsUpgrade(id, owner, now, out var request) || request is null || granted.ContainsKey(id))
        {
            Notify(owner, "expired");
            return;
        }
        if (!accept)
        {
            households.CompleteUpgrade(id);
            Notify(request.Guest, "upgrade-declined");
            return;
        }
        if (!AllPeersReady() || !IsOnline(request.Guest) ||
            households.Home(request.Guest) != owner || households.Rules(owner).Upgrade == UpgradePolicy.Disabled ||
            game!.FindFarmer(owner) is not { } farmer || !game.CanStartUpgrade(farmer, request.Level))
        {
            households.CompleteUpgrade(id);
            Notify(request.Guest, "upgrade-unavailable");
            Notify(owner, "upgrade-unavailable");
            return;
        }
        granted[id] = request;
        Deliver(owner, request, "upgrade-run", ReceiveGrant);
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

    private void ReceiveGrant(UpgradeRequest item)
    {
        if (item.Owner != game!.LocalId || item.Level is < 0 or > 2)
            return;
        dialogs.Enqueue(() =>
        {
            if (!completedLocally.TryGetValue(item.Id, out var previous))
            {
                string code = "upgrade-unavailable";
                try
                {
                    if (DateTimeOffset.UtcNow < item.Expires && households.Home(item.Guest) == item.Owner &&
                        households.Rules(item.Owner).Upgrade != UpgradePolicy.Disabled)
                        code = game.StartNativeUpgrade(item.Level);
                }
                catch (Exception ex)
                {
                    code = GameApi.UpgradeDays(game.Player) > 0 ? "upgrade-started" : "upgrade-error";
                    Monitor.Log($"Stok House could not complete a native upgrade request. {ex}", StardewModdingAPI.LogLevel.Error);
                }
                previous = (code, item.Expires);
                completedLocally[item.Id] = previous;
            }
            var result = new UpgradeResult(item.Id, previous.Code);
            if (StardewModdingAPI.Context.IsMainPlayer)
                OnUpgradeResult(item.Owner, result);
            else
                Send(result, "upgrade-result", game.HostId);
            if (previous.Code != "upgrade-started")
                Tell(previous.Code);
        });
    }

    private void OnUpgradeResult(long actor, UpgradeResult result)
    {
        if (!granted.TryGetValue(result.Request, out var request) || request.Owner != actor ||
            result.Code is not ("upgrade-started" or "upgrade-unavailable" or "upgrade-error" or "funds" or "materials"))
            return;
        granted.Remove(result.Request);
        households.CompleteUpgrade(result.Request);
        Notify(request.Guest, result.Code);
    }
}
