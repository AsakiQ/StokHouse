namespace StokHouse;

internal static class UpgradeCosts
{
    internal static (int Gold, string? Item, int Quantity) ForLevel(int level) => level switch
    {
        0 => (10_000, "(O)388", 450),
        1 => (65_000, "(O)709", 100),
        2 => (100_000, null, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
