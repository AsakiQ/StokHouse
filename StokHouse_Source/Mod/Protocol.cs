using StokHouse.Core;
using StardewModdingAPI;

namespace StokHouse;

internal sealed record Command(Guid Operation, string Action, long Player = 0, Guid Request = default, HouseRules? Rules = null);
internal sealed record StateFrame(long Revision, HouseholdData Data, int Capacity, bool Fault);
internal sealed record Notice(string Code, long Player = 0);
internal sealed record UpgradeResult(Guid Request, string Code);
internal sealed class ModConfig
{
    public SButton MenuKey { get; set; } = SButton.F8;
    public int Capacity { get; set; } = 2;
}
