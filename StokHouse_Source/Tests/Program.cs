using StokHouse.Core;
using System.Text.Json;

var time = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
var roster = new HashSet<long> { 101, 102, 103, 104, 105 };
const long host = 101;
int passed = 0;

Test("five farmers in three homes", () =>
{
    var state = New();
    Move(state, 101, 102);
    Move(state, 103, 104);
    Eq(3, roster.Select(state.Home).Distinct().Count());
    Eq(101L, state.Home(102));
    Eq(103L, state.Home(104));
    Eq(105L, state.Home(105));
    Eq(true, state.ProtectCabin(102));
    Eq(true, state.ProtectCabin(103));
    Eq(false, state.ProtectCabin(105));
});

Test("only the recipient can accept and replay is ignored", () =>
{
    var state = New();
    var request = Invite(state, 101, 102);
    Eq("expired", Resolve(state, request, 105));
    Eq("moved", Resolve(state, request, 102));
    Eq("expired", Resolve(state, request, 102));
});

Test("host cannot move out of the main farmhouse", () =>
{
    var state = New();
    Eq("unavailable", state.Invite(103, host, host, roster, 2, time, out _).Code);
    Eq("already-home", state.Leave(host, roster).Code);
});

Test("expiry, rejection and cancellation do not move a character", () =>
{
    var state = New();
    var first = Invite(state, 101, 102);
    Eq("expired", state.CancelInvite(first.Id, 104, time, out _).Code);
    Eq("cancelled", state.CancelInvite(first.Id, 101, time, out _).Code);
    Eq("expired", Resolve(state, first, 102));
    var next = Invite(state, 101, 102);
    Eq("declined", state.ResolveInvite(next.Id, 102, false, host, roster, 2, time, out _).Code);
    var last = Invite(state, 101, 102);
    Eq("expired", state.ResolveInvite(last.Id, 102, true, host, roster, 2, time.AddMinutes(4), out _).Code);
    Eq(102L, state.Home(102));
});

Test("conflicting requests are resolved against current housing", () =>
{
    var state = New();
    var first = Invite(state, 102, 104);
    var second = Invite(state, 101, 102);
    Eq("moved", Resolve(state, first, 104));
    Eq("unavailable", Resolve(state, second, 102));
    Eq("full", state.Invite(102, 105, host, roster, 2, time, out _).Code);
    Eq("sent", state.Invite(102, 105, host, roster, 3, time, out _).Code);
});

Test("guest can change homes and return to the original cabin", () =>
{
    var state = New();
    Move(state, 101, 102);
    Move(state, 103, 102);
    Eq(103L, state.Home(102));
    Eq("left", state.Leave(102, roster).Code);
    Eq(102L, state.Home(102));
    Eq(false, state.ProtectCabin(102));
});

Test("owned rules govern upgrade and survive serialization", () =>
{
    var state = New();
    Move(state, 101, 102);
    Eq("unavailable", state.SetRules(102, new HouseRules(false), roster).Code);
    Eq("rules-saved", state.SetRules(101, new HouseRules(false, UpgradePolicy.Disabled), roster).Code);
    Eq("upgrade-disabled", state.RequestUpgrade(102, 0, time, out _).Code);
    var wire = JsonSerializer.Serialize(state.Export());
    var restored = new Households(JsonSerializer.Deserialize<HouseholdData>(wire)!);
    restored.ValidateHomes(roster, host);
    Eq(101L, restored.Home(102));
    Eq(new HouseRules(false, UpgradePolicy.Disabled), restored.Rules(101));
});

Test("upgrade grants are limited to the owner and expire", () =>
{
    var state = New();
    Move(state, 101, 102);
    Move(state, 103, 104);
    Eq("upgrade-sent", state.RequestUpgrade(102, 0, time, out var order).Code);
    Eq("upgrade-pending", state.RequestUpgrade(104, 0, time, out _).Code);
    Eq(false, state.OwnsUpgrade(order!.Id, 102, time, out _));
    Eq(true, state.OwnsUpgrade(order.Id, 101, time, out _));
    Eq(false, state.OwnsUpgrade(order.Id, 101, time.AddMinutes(4), out _));
    Eq("upgrade-sent", state.RequestUpgrade(104, 1, time.AddMinutes(4), out _).Code);
});

Test("disconnect closes pending requests", () =>
{
    var state = New();
    var invite = Invite(state, 101, 102);
    state.ForgetRequests(101);
    Eq("expired", Resolve(state, invite, 102));
});

Test("wrong schema, dependency chains and missing cabins are rejected", () =>
{
    Throws(() => new Households(new HouseholdData(2, new(), new())));
    Throws(() => new Households(new HouseholdData(1, new() { [102] = 103, [103] = 101 }, new())));
    var state = New();
    Move(state, 101, 102);
    Throws(() => state.ValidateHomes(new HashSet<long> { 101, 103 }, host));
});

Console.WriteLine($"Passed: {passed}");
return;

Households New() => new(HouseholdData.Empty());

Invitation Invite(Households state, long owner, long guest)
{
    Eq("sent", state.Invite(owner, guest, host, roster, 2, time, out var item).Code);
    return item!;
}

string Resolve(Households state, Invitation invitation, long guest) =>
    state.ResolveInvite(invitation.Id, guest, true, host, roster, 2, time, out _).Code;

void Move(Households state, long owner, long guest) => Eq("moved", Resolve(state, Invite(state, owner, guest), guest));

void Test(string name, Action action)
{
    action();
    Console.WriteLine($"PASS {name}");
    passed++;
}

static void Eq<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected InvalidDataException.");
}
