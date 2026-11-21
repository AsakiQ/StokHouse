using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace StokHouse;

internal sealed record Home(long Owner, string Name, object Interior, object? Cabin)
{
    public bool IsCabin => Cabin is not null;
}

internal sealed class GameApi
{
    private const BindingFlags Member = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private static readonly Dictionary<(Type, string), MemberInfo> Members = new();

    internal static Type Type(string name) =>
        AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);

    internal static MethodInfo Method(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    internal static object? Read(object target, string name)
    {
        Type type = target as Type ?? target.GetType();
        if (!Members.TryGetValue((type, name), out var member))
        {
            member = (MemberInfo?)type.GetProperty(name, Member) ?? type.GetField(name, Member)
                ?? throw new MissingMemberException(type.FullName, name);
            Members.Add((type, name), member);
        }
        object? instance = target is Type ? null : target;
        return member is PropertyInfo property ? property.GetValue(instance) : ((FieldInfo)member).GetValue(instance);
    }

    internal readonly Type Game = Type("StardewValley.Game1");
    internal readonly Type Farmer = Type("StardewValley.Farmer");
    internal readonly Type Location = Type("StardewValley.GameLocation");
    internal readonly Type FarmHouse = Type("StardewValley.Locations.FarmHouse");
    internal readonly Type Cabin = Type("StardewValley.Locations.Cabin");
    internal readonly Type Building = Type("StardewValley.Buildings.Building");
    internal readonly Type Tile = Type("xTile.Dimensions.Location");
    internal readonly Type Response = Type("StardewValley.Response");
    internal readonly Type Npc = Type("StardewValley.NPC");
    internal readonly Type Furniture = Type("StardewValley.Objects.Furniture");
    internal readonly Type Bed = Type("StardewValley.Objects.BedFurniture");
    internal readonly Type Wallpaper = Type("StardewValley.Objects.Wallpaper");
    internal readonly Type SpriteBatch = Type("Microsoft.Xna.Framework.Graphics.SpriteBatch");

    private readonly MethodInfo getFarm;
    private readonly MethodInfo getAllFarmers;
    private readonly MethodInfo getOnlineFarmers;
    private readonly MethodInfo getCharacter;
    private readonly MethodInfo getBuildingInterior;
    private readonly MethodInfo getMainHouse;
    private readonly MethodInfo getMainMailbox;
    private readonly MethodInfo getCabinMailbox;
    private readonly MethodInfo getEntry;
    private readonly MethodInfo warp;
    private readonly MethodInfo question;
    private readonly MethodInfo hud;
    private readonly MethodInfo upgrade;
    private readonly MethodInfo construction;

    internal GameApi()
    {
        getFarm = Method(Game, "getFarm");
        getAllFarmers = Method(Game, "getAllFarmers");
        getOnlineFarmers = Method(Game, "getOnlineFarmers");
        getCharacter = Method(Game, "getCharacterFromName", typeof(string), typeof(bool), typeof(bool));
        getBuildingInterior = Method(Building, "GetIndoors");
        getMainHouse = Method(Type("StardewValley.Utility"), "getHomeOfFarmer", Farmer);
        getMainMailbox = Method(Type("StardewValley.Farm"), "GetMainMailboxPosition");
        getCabinMailbox = Method(Building, "getMailboxPosition");
        getEntry = Method(FarmHouse, "getEntryLocation");
        warp = Method(Game, "warpFarmer", typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool));
        Type afterQuestion = AccessTools.Inner(Location, "afterQuestionBehavior")
            ?? throw new TypeLoadException("GameLocation.afterQuestionBehavior");
        question = Method(Location, "createQuestionDialogue", typeof(string), Response.MakeArrayType(), afterQuestion, Npc);
        hud = Method(Game, "showGlobalMessage", typeof(string));
        upgrade = Method(Location, "houseUpgradeAccept");
        construction = Method(Game, "IsThereABuildingUnderConstruction", typeof(string));
    }

    internal string Version => (string)Read(Game, "version")!;
    internal object Player => Read(Game, "player")!;
    internal object Master => Read(Game, "MasterPlayer")!;
    internal object Current => Read(Game, "currentLocation")!;
    internal object Farm => getFarm.Invoke(null, null)!;
    internal long LocalId => Id(Player);
    internal long HostId => Id(Master);
    internal static long Id(object farmer) => (long)Read(farmer, "UniqueMultiplayerID")!;
    internal static string LocationName(object location) => (string)Read(location, "NameOrUniqueName")!;
    internal static int Level(object farmer) => (int)Read(farmer, "HouseUpgradeLevel")!;
    internal static bool Separate(object farmer) => (bool)Read(farmer, "useSeparateWallets")!;
    internal static int UpgradeDays(object farmer) => (int)Read(Read(farmer, "daysUntilHouseUpgrade")!, "Value")!;
    internal static Vector2 FarmerTile(object farmer) => (Vector2)Read(farmer, "Tile")!;
    internal static bool IsUnclaimed(object farmer) => (bool)Read(farmer, "isUnclaimedFarmhand")!;
    internal object? Robin => getCharacter.Invoke(null, new object[] { "Robin", true, false });

    internal IEnumerable<object> Online() => ((IEnumerable)getOnlineFarmers.Invoke(null, null)!).Cast<object>();
    internal IEnumerable<object> Farmers() => ((IEnumerable)getAllFarmers.Invoke(null, null)!).Cast<object>();
    internal object? FindFarmer(long id) => Farmers().FirstOrDefault(farmer => Id(farmer) == id);
    internal string Name(long id) => FindFarmer(id) is { } farmer ? (string)Read(farmer, "Name")! : id.ToString();

    internal Dictionary<long, Home> Homes()
    {
        var result = new Dictionary<long, Home>();
        var main = getMainHouse.Invoke(null, new[] { Master })!;
        result.Add(HostId, new Home(HostId, LocationName(main), main, null));
        foreach (var building in ((IEnumerable)Read(Farm, "buildings")!).Cast<object>())
        {
            if (!(bool)Read(building, "isCabin")! || getBuildingInterior.Invoke(building, null) is not { } interior ||
                !Cabin.IsInstanceOfType(interior) || Read(interior, "owner") is not { } owner || IsUnclaimed(owner))
                continue;
            long id = Id(owner);
            if (!result.TryAdd(id, new Home(id, LocationName(interior), interior, building)))
                throw new InvalidDataException("Two Stardew cabins have the same owner.");
        }
        return result;
    }

    internal long CabinOwner(object building)
    {
        if (!(bool)Read(building, "isCabin")! || getBuildingInterior.Invoke(building, null) is not { } inside ||
            Read(inside, "owner") is not { } owner || IsUnclaimed(owner))
            return 0;
        return Id(owner);
    }

    internal Point Mailbox(Home home) => home.Cabin is { } cabin
        ? (Point)getCabinMailbox.Invoke(cabin, null)!
        : (Point)getMainMailbox.Invoke(Farm, null)!;

    internal static Point TilePoint(object tile) => new((int)Read(tile, "X")!, (int)Read(tile, "Y")!);

    internal bool WarpHome(Home home)
    {
        if (LocationName(Current) == home.Name)
            return true;
        var entry = (Point)getEntry.Invoke(home.Interior, null)!;
        warp.Invoke(null, new object[] { home.Name, entry.X, entry.Y, 0, home.IsCabin });
        return false;
    }

    internal bool IsAtRobin(object farmer)
    {
        object? robin = Robin;
        return robin is not null && Read(robin, "currentLocation") is { } shop &&
               LocationName(shop) == "ScienceHouse" &&
               Read(farmer, "currentLocation") is { } here && LocationName(here) == "ScienceHouse" &&
               Vector2.Distance((Vector2)Read(robin, "Tile")!, FarmerTile(farmer)) <= 5;
    }

    internal bool CanStartUpgrade(object farmer, int expectedLevel)
    {
        if (Level(farmer) != expectedLevel || expectedLevel is < 0 or > 2 || UpgradeDays(farmer) >= 0 ||
            (bool)construction.Invoke(null, new object[] { "Robin" })! || Farmers().Any(f => UpgradeDays(f) > 0))
            return false;
        return true;
    }

    internal string StartNativeUpgrade(int level)
    {
        object player = Player;
        if (!CanStartUpgrade(player, level) || Robin is null)
            return "upgrade-unavailable";

        // Check before calling Robin so the mod can give the initiator a useful result.
        var (price, item, quantity) = Price(level);
        if ((int)Read(player, "Money")! < price)
            return "funds";
        if (item is not null)
        {
            object items = Read(player, "Items")!;
            var contains = Method(items.GetType(), "ContainsId", typeof(string), typeof(int));
            if (!(bool)contains.Invoke(items, new object[] { item, quantity })!)
                return "materials";
        }

        upgrade.Invoke(Current, null);
        return UpgradeDays(player) > 0 ? "upgrade-started" : "upgrade-unavailable";
    }

    internal static (int Gold, string? Item, int Quantity) Price(int level) => level switch
    {
        0 => (10_000, "(O)388", 450),
        1 => (65_000, "(O)709", 100),
        2 => (100_000, null, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    internal string? Load(string key)
    {
        object data = Read(Master, "modData")!;
        var values = new object?[] { key, null };
        var method = Method(data.GetType(), "TryGetValue", typeof(string), typeof(string).MakeByRefType());
        return (bool)method.Invoke(data, values)! ? (string?)values[1] : null;
    }

    internal void Store(string key, string value)
    {
        object data = Read(Master, "modData")!;
        Method(data.GetType(), "set_Item", typeof(string), typeof(string)).Invoke(data, new object[] { key, value });
    }

    internal void Hud(string message) => hud.Invoke(null, new object[] { message });

    internal Array AppendResponse(Array source, string key, string text, params string[] remove)
    {
        object[] kept = source.Cast<object>().Where(response => !remove.Contains((string)Read(response, "responseKey")!)).ToArray();
        Array copy = Array.CreateInstance(Response, kept.Length + 1);
        int index = 0;
        bool inserted = false;
        foreach (object response in kept)
        {
            if (!inserted && (string)Read(response, "responseKey")! == "Leave")
            {
                copy.SetValue(Activator.CreateInstance(Response, key, text), index++);
                inserted = true;
            }
            copy.SetValue(response, index++);
        }
        if (!inserted)
            copy.SetValue(Activator.CreateInstance(Response, key, text), index);
        return copy;
    }

    internal void Ask(string title, IReadOnlyList<(string Key, string Label)> choices, Action<string> choose)
    {
        Array answers = Array.CreateInstance(Response, choices.Count);
        for (int i = 0; i < choices.Count; i++)
            answers.SetValue(Activator.CreateInstance(Response, choices[i].Key, choices[i].Label), i);
        Type callback = question.GetParameters()[2].ParameterType;
        var player = Expression.Parameter(Farmer, "player");
        var answer = Expression.Parameter(typeof(string), "answer");
        var body = Expression.Invoke(Expression.Constant(choose), answer);
        Delegate handler = Expression.Lambda(callback, body, player, answer).Compile();
        question.Invoke(Current, new object?[] { title, answers, handler, null });
    }
}
