using StokHouse.Core;

namespace StokHouse;

internal sealed record Command(Guid Operation, string Action, long Player = 0, Guid Request = default, HouseRules? Rules = null, int? UpgradeLevel = null);
internal sealed record StateFrame(long Revision, HouseholdData Data, int Capacity, bool Fault);
internal sealed record Notice(string Code, long Player = 0);
