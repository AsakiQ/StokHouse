using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xna.Framework;

namespace StokHouse;

internal sealed record Home(long Owner, string Name, object Interior, object? Cabin)
{
    public bool IsCabin => Cabin is not null;
}

internal sealed class GameApi
{
    internal readonly Type Game;
    internal readonly Type Farmer;
    internal readonly Type Location;
    internal readonly Type FarmHouse;
    internal readonly Type Cabin;
    internal readonly Type Building;
    internal readonly Type Tile;
    internal readonly Type Response;
    internal readonly Type Furniture;
    internal readonly Type Bed;
    internal readonly Type Wallpaper;

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
    private readonly ConstructorInfo createResponse;
    private readonly MethodInfo hud;
    private readonly MethodInfo upgrade;
    private readonly MethodInfo construction;

    private readonly MethodInfo readMailbox;
    private readonly MethodInfo containsItem;
    private readonly MethodInfo getModData;
    private readonly MethodInfo setModData;

    internal string CharacterLookup => GameReflection.Signature(getCharacter);

    internal GameApi(Func<string, Type>? resolveType = null)
    {
        resolveType ??= GameReflection.Type;
        Game = resolveType("StardewValley.Game1");
        Farmer = resolveType("StardewValley.Farmer");
        Location = resolveType("StardewValley.GameLocation");
        FarmHouse = resolveType("StardewValley.Locations.FarmHouse");
        Cabin = resolveType("StardewValley.Locations.Cabin");
        Building = resolveType("StardewValley.Buildings.Building");
        Tile = resolveType("xTile.Dimensions.Location");
        Response = resolveType("StardewValley.Response");
        Furniture = resolveType("StardewValley.Objects.Furniture");
        Bed = resolveType("StardewValley.Objects.BedFurniture");
        Wallpaper = resolveType("StardewValley.Objects.Wallpaper");
        Type npc = resolveType("StardewValley.NPC");

        getFarm = GameReflection.Method(Game, "getFarm", BindingFlags.Static);
        getAllFarmers = GameReflection.Method(Game, "getAllFarmers", BindingFlags.Static);
        getOnlineFarmers = GameReflection.Method(Game, "getOnlineFarmers", BindingFlags.Static);
        getCharacter = GameReflection.Method(Game, "getCharacterFromName", BindingFlags.Static,
            typeof(string), typeof(bool), typeof(bool));
        getBuildingInterior = GameReflection.Method(Building, "GetIndoors", BindingFlags.Instance);
        getMainHouse = GameReflection.Method(resolveType("StardewValley.Utility"), "getHomeOfFarmer",
            BindingFlags.Static, Farmer);
        getMainMailbox = GameReflection.Method(resolveType("StardewValley.Farm"), "GetMainMailboxPosition",
            BindingFlags.Instance);
        getCabinMailbox = GameReflection.Method(Building, "getMailboxPosition", BindingFlags.Instance);
        getEntry = GameReflection.Method(FarmHouse, "getEntryLocation", BindingFlags.Instance);
        warp = GameReflection.Method(Game, "warpFarmer", BindingFlags.Static,
            typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool));
        Type afterQuestion = Location.GetNestedType("afterQuestionBehavior", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new TypeLoadException($"Stok House could not bind {Location.FullName}.afterQuestionBehavior.");
        GameReflection.Method(afterQuestion, "Invoke", BindingFlags.Instance, Farmer, typeof(string));
        createResponse = Response.GetConstructor(BindingFlags.Public | BindingFlags.Instance | BindingFlags.ExactBinding,
            binder: null, new[] { typeof(string), typeof(string) }, modifiers: null)
            ?? throw new MissingMethodException($"Could not bind {Response.FullName}(System.String, System.String).");
        question = GameReflection.Method(Location, "createQuestionDialogue", BindingFlags.Instance,
            typeof(string), Response.MakeArrayType(), afterQuestion, npc);
        hud = GameReflection.Method(Game, "showGlobalMessage", BindingFlags.Static, typeof(string));
        upgrade = GameReflection.Method(Location, "houseUpgradeAccept", BindingFlags.Instance);
        construction = GameReflection.Method(Game, "IsThereABuildingUnderConstruction", BindingFlags.Static, typeof(string));
        readMailbox = GameReflection.Method(Location, "mailbox", BindingFlags.Instance);

        Type inventory = GameReflection.MemberType(Farmer, "Items");
        containsItem = GameReflection.Method(inventory, "ContainsId", BindingFlags.Instance, typeof(string), typeof(int));
        Type modData = GameReflection.MemberType(Farmer, "modData");
        getModData = GameReflection.Method(modData, "TryGetValue", BindingFlags.Instance, typeof(string), typeof(string).MakeByRefType());
        setModData = GameReflection.Method(modData, "set_Item", BindingFlags.Instance, typeof(string), typeof(string));
    }

    internal static object? Read(object target, string name) => GameReflection.Read(target, name);

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
        object? shop = ReferenceEquals(farmer, Player) ? Current : Read(farmer, "currentLocation");
        if (shop is null || LocationName(shop) != "ScienceHouse")
            return false;
        var robin = ((IEnumerable)Read(shop, "characters")!).Cast<object>()
            .FirstOrDefault(npc => Equals(Read(npc, "Name"), "Robin"));
        return robin is not null && Vector2.Distance((Vector2)Read(robin, "Tile")!, FarmerTile(farmer)) <= 5;
    }

    internal string? UpgradeBlock(object farmer, int expectedLevel)
    {
        if (expectedLevel is < 0 or > 2)
            return "upgrade-max";
        if (Level(farmer) != expectedLevel)
            return "upgrade-quote-changed";
        if (UpgradeDays(farmer) >= 0 || Farmers().Any(f => UpgradeDays(f) > 0))
            return "upgrade-in-progress";
        if ((bool)construction.Invoke(null, new object[] { "Robin" })!)
            return "robin-busy";
        return null;
    }

    internal string StartNativeUpgrade(int level)
    {
        object player = Player;
        if (UpgradeBlock(player, level) is { } blocked)
            return blocked;
        if (Robin is null)
            return "robin-unavailable";

        // Check before calling Robin so the mod can give the initiator a useful result.
        var (price, item, quantity) = Price(level);
        if ((int)Read(player, "Money")! < price)
            return "funds";
        if (item is not null)
        {
            object items = Read(player, "Items")!;
            if (!(bool)containsItem.Invoke(items, new object[] { item, quantity })!)
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
        return (bool)getModData.Invoke(data, values)! ? (string?)values[1] : null;
    }

    internal void Store(string key, string value)
    {
        object data = Read(Master, "modData")!;
        setModData.Invoke(data, new object[] { key, value });
    }

    internal void OpenMailbox(object location) => readMailbox.Invoke(location, null);

    internal void Hud(string message) => hud.Invoke(null, new object[] { message });

    internal static string ResponseKey(object response) => (string)Read(response, "responseKey")!;

    internal Array WithSharedUpgrade(Array source, string text)
    {
        var choices = source.Cast<object>().ToList();
        if (choices.Any(response => ResponseKey(response) == RobinDialogue.Choice))
            return source;

        object shared = createResponse.Invoke(new[] { RobinDialogue.Choice, text });
        int upgrade = choices.FindIndex(response => ResponseKey(response) == "Upgrade");
        if (upgrade >= 0)
            choices[upgrade] = shared;
        else
        {
            int leave = choices.FindIndex(response => ResponseKey(response) == "Leave");
            choices.Insert(leave < 0 ? choices.Count : leave, shared);
        }
        var copy = Array.CreateInstance(Response, choices.Count);
        choices.CopyTo((object[])copy, 0);
        return copy;
    }

    internal void Ask(string title, IReadOnlyList<(string Key, string Label)> choices, Action<string> choose)
    {
        Array answers = Array.CreateInstance(Response, choices.Count);
        for (int i = 0; i < choices.Count; i++)
            answers.SetValue(createResponse.Invoke(new[] { choices[i].Key, choices[i].Label }), i);
        Type callback = question.GetParameters()[2].ParameterType;
        var player = Expression.Parameter(Farmer, "player");
        var answer = Expression.Parameter(typeof(string), "answer");
        var body = Expression.Invoke(Expression.Constant(choose), answer);
        Delegate handler = Expression.Lambda(callback, body, player, answer).Compile();
        question.Invoke(Current, new object?[] { title, answers, handler, null });
    }
}
