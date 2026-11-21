namespace StokHouse.Core;

public enum UpgradePolicy { Confirm, JointWallet, Disabled }
public sealed record HouseRules(bool Decorate = true, UpgradePolicy Upgrade = UpgradePolicy.Confirm);
public sealed record Invitation(Guid Id, long Owner, long Guest, DateTimeOffset Expires);
public sealed record UpgradeRequest(Guid Id, long Owner, long Guest, int Level, DateTimeOffset Expires);
public sealed record Outcome(string Code, bool Changed = false);
public sealed record HouseholdData(int Schema, Dictionary<long, long> Moves, Dictionary<long, HouseRules> Rules)
{
    public static HouseholdData Empty() => new(1, new(), new());
}

public sealed class Households
{
    public const int MaximumFarmers = 8;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(3);

    private readonly Dictionary<long, long> moves;
    private readonly Dictionary<long, HouseRules> rules;
    private readonly Dictionary<Guid, Invitation> invitations = new();
    private readonly Dictionary<Guid, UpgradeRequest> upgrades = new();

    public Households(HouseholdData data)
    {
        if (data.Schema != 1 || data.Moves is null || data.Rules is null ||
            data.Moves.Count > MaximumFarmers || data.Rules.Count > MaximumFarmers ||
            data.Moves.Any(pair => pair.Key == 0 || pair.Value == 0 || pair.Key == pair.Value ||
                                data.Moves.ContainsKey(pair.Value)) ||
            data.Rules.Any(pair => pair.Key == 0 || pair.Value is null || !Enum.IsDefined(pair.Value.Upgrade)))
            throw new InvalidDataException("Invalid Stok House household data.");
        moves = new(data.Moves);
        rules = new(data.Rules);
    }

    public HouseholdData Export() => new(1, new(moves), new(rules));
    public long Home(long farmer) => moves.GetValueOrDefault(farmer, farmer);
    public bool IsGuest(long farmer) => moves.ContainsKey(farmer);
    public HouseRules Rules(long owner) => rules.GetValueOrDefault(owner) ?? new();
    public IEnumerable<long> Guests(long owner) => moves.Where(pair => pair.Value == owner).Select(pair => pair.Key);
    public bool ProtectCabin(long owner) => IsGuest(owner) || Guests(owner).Any();

    public void ValidateHomes(IReadOnlySet<long> homes, long host)
    {
        if (IsGuest(host) || moves.Any(pair => !homes.Contains(pair.Key) || !homes.Contains(pair.Value)))
            throw new InvalidDataException("A Stok House household refers to a missing player cabin or the host has moved.");
    }

    public Outcome Invite(long owner, long guest, long host, IReadOnlySet<long> homes, int capacity,
        DateTimeOffset now, out Invitation? invitation)
    {
        invitation = null;
        Expire(now);
        if (!homes.Contains(owner) || !homes.Contains(guest) || owner == guest || guest == host ||
            IsGuest(owner) || Home(guest) == owner || Guests(guest).Any())
            return new("unavailable");
        if (Guests(owner).Count() + 1 >= capacity)
            return new("full");
        if (invitations.Values.Any(item => item.Owner == owner || item.Guest == guest))
            return new("pending");
        invitation = new(Guid.NewGuid(), owner, guest, now + RequestLifetime);
        invitations.Add(invitation.Id, invitation);
        return new("sent");
    }

    public Outcome ResolveInvite(Guid id, long actor, bool accept, long host, IReadOnlySet<long> homes,
        int capacity, DateTimeOffset now, out Invitation? resolved)
    {
        resolved = null;
        Expire(now);
        if (!invitations.TryGetValue(id, out var pending) || pending.Guest != actor)
            return new("expired");
        invitations.Remove(id);
        resolved = pending;
        if (!accept)
            return new("declined");
        if (!homes.Contains(pending.Owner) || !homes.Contains(actor) || actor == host ||
            IsGuest(pending.Owner) || Home(actor) == pending.Owner || Guests(actor).Any())
            return new("unavailable");
        if (Guests(pending.Owner).Count() + 1 >= capacity)
            return new("full");
        moves[actor] = pending.Owner;
        ForgetRequests(actor);
        return new("moved", true);
    }

    public Outcome CancelInvite(Guid id, long actor, DateTimeOffset now, out Invitation? resolved)
    {
        resolved = null;
        Expire(now);
        if (!invitations.TryGetValue(id, out var pending) || pending.Owner != actor)
            return new("expired");
        invitations.Remove(id);
        resolved = pending;
        return new("cancelled");
    }

    public Outcome Leave(long actor, IReadOnlySet<long> homes)
    {
        if (!homes.Contains(actor))
            return new("unavailable");
        if (!moves.Remove(actor))
            return new("already-home");
        ForgetRequests(actor);
        return new("left", true);
    }

    public Outcome SetRules(long actor, HouseRules update, IReadOnlySet<long> homes)
    {
        if (!homes.Contains(actor) || IsGuest(actor) || !Enum.IsDefined(update.Upgrade))
            return new("unavailable");
        if (Rules(actor) == update)
            return new("rules-saved");
        rules[actor] = update;
        foreach (var id in upgrades.Values.Where(item => item.Owner == actor).Select(item => item.Id).ToArray())
            upgrades.Remove(id);
        return new("rules-saved", true);
    }

    public Outcome RequestUpgrade(long guest, int level, DateTimeOffset now, out UpgradeRequest? request)
    {
        request = null;
        Expire(now);
        if (!IsGuest(guest) || level is < 0 or > 2)
            return new("unavailable");
        long owner = Home(guest);
        if (Rules(owner).Upgrade == UpgradePolicy.Disabled)
            return new("upgrade-disabled");
        if (upgrades.Count != 0)
            return new("upgrade-pending");
        request = new(Guid.NewGuid(), owner, guest, level, now + RequestLifetime);
        upgrades.Add(request.Id, request);
        return new("upgrade-sent");
    }

    public bool OwnsUpgrade(Guid id, long actor, DateTimeOffset now, out UpgradeRequest? request)
    {
        Expire(now);
        return upgrades.TryGetValue(id, out request) && request.Owner == actor;
    }

    public void CompleteUpgrade(Guid id) => upgrades.Remove(id);

    public void ForgetRequests(long player)
    {
        foreach (var id in invitations.Values.Where(item => item.Owner == player || item.Guest == player).Select(item => item.Id).ToArray())
            invitations.Remove(id);
        foreach (var id in upgrades.Values.Where(item => item.Owner == player || item.Guest == player).Select(item => item.Id).ToArray())
            upgrades.Remove(id);
    }

    public void Expire(DateTimeOffset now)
    {
        foreach (var id in invitations.Values.Where(item => item.Expires <= now).Select(item => item.Id).ToArray())
            invitations.Remove(id);
        foreach (var id in upgrades.Values.Where(item => item.Expires <= now).Select(item => item.Id).ToArray())
            upgrades.Remove(id);
    }
}
