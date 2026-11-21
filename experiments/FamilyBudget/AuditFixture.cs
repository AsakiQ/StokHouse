namespace StardewValley;

internal sealed class Farmer
{
    public int Money { get; set; }
}

internal sealed class FarmerTeam
{
    public int money;
}

internal static class AuditFixture
{
    internal static void Credit(Farmer who, FarmerTeam team, int source)
    {
        int amount = source switch { 0 => 30, 1 => 50, 2 => 5, 3 => 20, _ => -25 };
        who.Money += amount;
        team.money += Math.Abs(amount);
    }
}
