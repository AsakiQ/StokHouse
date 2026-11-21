using System.Text.Json;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StokHouse.Core;

namespace StokHouse;

public sealed partial class ModEntry : Mod
{
    private const string StorageKey = "StokHouse.CoopHousing/households";
    private const string Protocol = "stok-house/1/";
    private static readonly TimeSpan ReplayLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<(long Player, Guid Id), DateTimeOffset> seen = new();
    private readonly Dictionary<Guid, Invitation> inbox = new();
    private readonly Dictionary<Guid, Invitation> outbox = new();
    private readonly Dictionary<Guid, UpgradeRequest> offers = new();
    private readonly Dictionary<Guid, UpgradeRequest> granted = new();
    private readonly Dictionary<Guid, (string Code, DateTimeOffset Expires)> completedLocally = new();
    private readonly Queue<Action> dialogs = new();
    private GameApi? game;
    private ModConfig config = new();
    private Households households = new(HouseholdData.Empty());
    private bool active;
    private bool ready;
    private bool fault;
    private bool relocate;
    private bool wasDayStarted;
    private long revision;
    private long? localHome;
    private int capacity;
    private int ticksBeforeSync;
    private int ticksBeforeWarp;

    public override void Entry(IModHelper helper)
    {
        config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.GameLaunched += OnLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => Clear();
        helper.Events.GameLoop.DayStarted += (_, _) => { wasDayStarted = true; };
        helper.Events.GameLoop.UpdateTicked += Tick;
        helper.Events.Input.ButtonPressed += OnButton;
        helper.Events.Multiplayer.PeerDisconnected += OnDisconnect;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        helper.ConsoleCommands.Add("stok_house_status", "Show the status of Stok House.", (_, _) =>
            Monitor.Log($"Stok House. Runtime: {active}, save ready: {ready}, storage fault: {fault}, revision: {revision}.", LogLevel.Info));
    }

    private void OnLaunched(object? sender, GameLaunchedEventArgs args)
    {
        var harmony = new Harmony(ModManifest.UniqueID);
        try
        {
            game = new GameApi();
            if (game.Version != "1.6.15")
                throw new NotSupportedException($"Stok House alpha targets Stardew Valley 1.6.15; detected {game.Version}.");
            GamePatches.Apply(harmony, game, this);
            active = true;
            Monitor.Log("Stok House hooks bound to Stardew Valley 1.6.15.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(ModManifest.UniqueID);
            Monitor.Log($"Stok House disabled before changing the world: {ex}", LogLevel.Error);
        }
    }

    private void Clear()
    {
        ready = fault = relocate = wasDayStarted = false;
        revision = 0;
        localHome = null;
        ticksBeforeSync = ticksBeforeWarp = 0;
        capacity = Math.Clamp(config.Capacity, 1, Households.MaximumFarmers);
        households = new Households(HouseholdData.Empty());
        seen.Clear();
        inbox.Clear();
        outbox.Clear();
        offers.Clear();
        granted.Clear();
        completedLocally.Clear();
        dialogs.Clear();
        cachedHomes.Clear();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs args)
    {
        Clear();
        if (!active || Context.ScreenId != 0)
            return;
        if (!Context.IsMainPlayer)
        {
            RequestSync();
            return;
        }

        try
        {
            string? raw = game!.Load(StorageKey);
            if (raw is not null)
            {
                if (raw.Length > 12_288)
                    throw new InvalidDataException("Household data exceeds the maximum size.");
                households = new Households(JsonSerializer.Deserialize<HouseholdData>(raw)
                    ?? throw new InvalidDataException("Missing household data."));
            }
            households.ValidateHomes(Homes.Keys.ToHashSet(), game.HostId);
            ready = true;
        }
        catch (Exception ex)
        {
            fault = true;
            Monitor.Log($"Stok House data was left unchanged. Restore a known-good save. {ex}", LogLevel.Error);
        }
    }

    private void Tick(object? sender, UpdateTickedEventArgs args)
    {
        if (!active || !Context.IsWorldReady || Context.ScreenId != 0)
            return;

        if (!Context.IsMainPlayer && !ready && !fault && ++ticksBeforeSync >= 120)
            RequestSync();

        if (args.IsMultipleOf(60))
        {
            RefreshHomes();
            var now = DateTimeOffset.UtcNow;
            households.Expire(now);
            RemoveExpired(inbox, item => item.Expires, now);
            RemoveExpired(outbox, item => item.Expires, now);
            RemoveExpired(offers, item => item.Expires, now);
            RemoveExpired(granted, item => item.Expires, now);
            RemoveExpired(completedLocally, item => item.Expires, now);
            foreach (var id in seen.Where(item => item.Value < now).Select(item => item.Key).ToArray())
                seen.Remove(id);
        }

        if (!Running || !Context.CanPlayerMove || (bool)GameApi.Read(game!.Game, "eventUp")!)
            return;

        if (wasDayStarted)
        {
            wasDayStarted = false;
            if (households.IsGuest(game.LocalId) && Homes.TryGetValue(game.LocalId, out var original))
                relocate |= GameApi.LocationName(game.Current) == original.Name;
        }

        if (households.IsGuest(game.LocalId) && Homes.TryGetValue(game.LocalId, out var technical) &&
            GameApi.LocationName(game.Current) == technical.Name)
            relocate = true;

        if (ticksBeforeWarp > 0)
            ticksBeforeWarp--;
        if (relocate && ticksBeforeWarp == 0 && Homes.TryGetValue(households.Home(game.LocalId), out var home))
        {
            relocate = !game.WarpHome(home);
            ticksBeforeWarp = 60;
            if (relocate)
                return;
        }

        if (dialogs.TryDequeue(out var show))
            show();
    }

    private static void RemoveExpired<T>(Dictionary<Guid, T> requests, Func<T, DateTimeOffset> expiry, DateTimeOffset now)
    {
        foreach (var id in requests.Where(item => expiry(item.Value) <= now).Select(item => item.Key).ToArray())
            requests.Remove(id);
    }

    private bool Running => active && ready && !fault && Context.IsWorldReady && Context.ScreenId == 0;
    private readonly Dictionary<long, Home> cachedHomes = new();
    private IReadOnlyDictionary<long, Home> Homes
    {
        get
        {
            if (cachedHomes.Count == 0)
                RefreshHomes();
            return cachedHomes;
        }
    }

    private void RefreshHomes()
    {
        try
        {
            var current = game!.Homes();
            cachedHomes.Clear();
            foreach (var (id, home) in current)
                cachedHomes.Add(id, home);
        }
        catch (Exception ex)
        {
            if (!fault)
                Monitor.Log($"Stok House cannot resolve the farm's homes: {ex}", LogLevel.Error);
            fault = true;
        }
    }

    private void OnButton(object? sender, ButtonPressedEventArgs args)
    {
        if (!active || !Context.CanPlayerMove || Context.ScreenId != 0 || args.Button != config.MenuKey)
            return;
        Helper.Input.Suppress(args.Button);
        if (Running)
            ShowMenu();
        else
            Tell(fault ? "state-fault" : "not-ready");
    }

    private void OnDisconnect(object? sender, PeerDisconnectedEventArgs args)
    {
        if (Context.IsMainPlayer)
            households.ForgetRequests(args.Peer.PlayerID);
    }

    private bool SameVersion(long player) => player == game!.LocalId ||
        Helper.Multiplayer.GetConnectedPlayer(player)?.GetMod(ModManifest.UniqueID)?.Version
            .Equals(ModManifest.Version) == true;

    private bool IsOnline(long player) => game!.Online().Any(farmer => GameApi.Id(farmer) == player);
    private bool AllPeersReady() => Helper.Multiplayer.GetConnectedPlayers().All(peer => SameVersion(peer.PlayerID));

    private void RequestSync()
    {
        ticksBeforeSync = 0;
        if (game is not null && Context.IsWorldReady)
            Send(new Command(Guid.NewGuid(), "sync"), "command", game.HostId);
    }

    private void Send<T>(T payload, string type, long? target = null) where T : notnull =>
        Helper.Multiplayer.SendMessage(payload, Protocol + type, new[] { ModManifest.UniqueID },
            target.HasValue ? new[] { target.Value } : null);

    private void Submit(string action, long player = 0, Guid request = default, HouseRules? rules = null)
    {
        var command = new Command(Guid.NewGuid(), action, player, request, rules);
        if (Context.IsMainPlayer)
            HandleCommand(game!.LocalId, command);
        else
            Send(command, "command", game!.HostId);
    }

    private void OnMessage(object? sender, ModMessageReceivedEventArgs args)
    {
        if (!active || !Context.IsWorldReady || Context.ScreenId != 0 || args.FromModID != ModManifest.UniqueID ||
            !args.Type.StartsWith(Protocol, StringComparison.Ordinal))
            return;

        var type = args.Type[Protocol.Length..];
        try
        {
            if (Context.IsMainPlayer)
            {
                if (SameVersion(args.FromPlayerID) && type == "command")
                    HandleCommand(args.FromPlayerID, args.ReadAs<Command>());
                else if (SameVersion(args.FromPlayerID) && type == "upgrade-result")
                    OnUpgradeResult(args.FromPlayerID, args.ReadAs<UpgradeResult>());
                return;
            }

            if (args.FromPlayerID != game!.HostId)
                return;
            switch (type)
            {
                case "state": ApplyFrame(args.ReadAs<StateFrame>()); break;
                case "notice": ReceiveNotice(args.ReadAs<Notice>()); break;
                case "invitation": ReceiveInvitation(args.ReadAs<Invitation>()); break;
                case "invitation-out": RememberOutgoing(args.ReadAs<Invitation>()); break;
                case "upgrade": ReceiveUpgrade(args.ReadAs<UpgradeRequest>()); break;
                case "upgrade-run": ReceiveGrant(args.ReadAs<UpgradeRequest>()); break;
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Stok House rejected a {type} message. {ex.GetBaseException().Message}", LogLevel.Warn);
        }
    }

    private StateFrame Frame() => new(revision, households.Export(), capacity, fault);

    private void ApplyFrame(StateFrame frame)
    {
        if ((ready && frame.Revision <= revision && !frame.Fault) || frame.Capacity is < 1 or > Households.MaximumFarmers)
            return;
        var next = new Households(frame.Data);
        revision = frame.Revision;
        capacity = frame.Capacity;
        fault |= frame.Fault;
        ready = true;
        households = next;

        long id = game!.LocalId;
        long destination = next.Home(id);
        relocate |= localHome.HasValue ? destination != localHome.Value : destination != id;
        localHome = destination;
    }

    private bool Commit(HouseholdData before)
    {
        try
        {
            game!.Store(StorageKey, JsonSerializer.Serialize(households.Export()));
        }
        catch (Exception ex)
        {
            households = new Households(before);
            fault = true;
            Monitor.Log($"Stok House blocked subsequent changes after a save error: {ex}", LogLevel.Error);
            Send(Frame(), "state");
            return false;
        }
        revision++;
        long id = game.LocalId;
        long destination = households.Home(id);
        relocate |= localHome.HasValue && destination != localHome.Value;
        localHome = destination;
        Send(Frame(), "state");
        return true;
    }

    private void HandleCommand(long actor, Command command)
    {
        if (command.Action == "sync")
        {
            if (ready || fault)
                Send(Frame(), "state", actor);
            return;
        }
        if (!Running || !IsOnline(actor))
            return;
        if (command.Operation == Guid.Empty || seen.ContainsKey((actor, command.Operation)))
            return;
        var now = DateTimeOffset.UtcNow;
        if (seen.Keys.Count(key => key.Player == actor) >= 64)
        {
            Notify(actor, "rate-limit");
            return;
        }
        seen.Add((actor, command.Operation), now + ReplayLifetime);
        RefreshHomes();
        if (fault)
            return;
        HouseholdData before = households.Export();
        var homes = Homes.Keys.ToHashSet();
        Outcome result;
        switch (command.Action)
        {
            case "invite":
                if (!AllPeersReady() || !IsOnline(command.Player) || !SameVersion(command.Player))
                {
                    Notify(actor, "peer-missing");
                    return;
                }
                result = households.Invite(actor, command.Player, game!.HostId, homes, capacity, now, out var invite);
                if (invite is not null)
                {
                    Deliver(invite.Guest, invite, "invitation", ReceiveInvitation);
                    Deliver(invite.Owner, invite, "invitation-out", RememberOutgoing);
                }
                break;
            case "accept":
            case "decline":
                if (command.Action == "accept" && granted.Values.Any(item => item.Guest == actor))
                {
                    Notify(actor, "upgrade-pending");
                    return;
                }
                if (command.Action == "accept" && !AllPeersReady())
                {
                    Notify(actor, "peer-missing");
                    return;
                }
                result = households.ResolveInvite(command.Request, actor, command.Action == "accept", game!.HostId,
                    homes, capacity, now, out var resolved);
                if (result.Changed && !Commit(before))
                    result = new("state-fault");
                if (resolved is not null)
                    Notify(resolved.Owner, result.Code, actor);
                Notify(actor, result.Code);
                return;
            case "cancel":
                result = households.CancelInvite(command.Request, actor, now, out var cancelled);
                if (cancelled is not null)
                    Notify(cancelled.Guest, "cancelled", actor);
                break;
            case "leave":
                if (granted.Values.Any(item => item.Guest == actor))
                {
                    Notify(actor, "upgrade-pending");
                    return;
                }
                result = households.Leave(actor, homes);
                break;
            case "rights":
                result = command.Rules is null ? new("unavailable") : households.SetRules(actor, command.Rules, homes);
                break;
            case "reset":
                if (actor != game!.HostId)
                    return;
                if (granted.Count != 0)
                {
                    Notify(actor, "upgrade-pending");
                    return;
                }
                if (before.Moves.Keys.Any(id => !IsOnline(id) || !SameVersion(id)))
                {
                    Notify(actor, "reset-online");
                    return;
                }
                households = new Households(HouseholdData.Empty());
                result = new("reset-done", true);
                break;
            case "upgrade":
                RequestUpgrade(actor, now);
                return;
            case "upgrade-accept":
            case "upgrade-decline":
                DecideUpgrade(actor, command.Request, command.Action == "upgrade-accept", now);
                return;
            default:
                return;
        }
        if (result.Changed && !Commit(before))
            result = new("state-fault");
        Notify(actor, result.Code);
    }

    private void Deliver<T>(long player, T item, string type, Action<T> local) where T : notnull
    {
        if (player == game!.LocalId)
            local(item);
        else
            Send(item, type, player);
    }

    private void Notify(long id, string code, long player = 0) =>
        Deliver(id, new Notice(code, player), "notice", ReceiveNotice);

    private void ReceiveNotice(Notice notice)
    {
        foreach (var pending in outbox.Values.Where(item => item.Guest == notice.Player).ToArray())
        {
            if (notice.Code is "moved" or "declined" or "unavailable" or "full")
                outbox.Remove(pending.Id);
        }
        foreach (var pending in inbox.Values.Where(item => item.Owner == notice.Player).ToArray())
        {
            if (notice.Code == "cancelled")
                inbox.Remove(pending.Id);
        }
        Tell(notice.Code, notice.Player);
    }
    private void Tell(string code, long player = 0) => game!.Hud(Helper.Translation.Get(code, new
    {
        player = player == 0 ? "" : game!.Name(player)
    }).ToString());

    private void ReceiveInvitation(Invitation invitation)
    {
        if (invitation.Guest != game!.LocalId)
            return;
        inbox[invitation.Id] = invitation;
        dialogs.Enqueue(() => { if (inbox.ContainsKey(invitation.Id)) ShowInvite(invitation); });
    }

    private void RememberOutgoing(Invitation invitation)
    {
        if (invitation.Owner == game!.LocalId)
            outbox[invitation.Id] = invitation;
    }
}
