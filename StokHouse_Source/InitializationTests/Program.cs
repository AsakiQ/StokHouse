using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StokHouse;

const BindingFlags instance = BindingFlags.Instance;
const BindingFlags staticMethod = BindingFlags.Static;
const string modId = "StokHouse.Tests.Initialization";
int passed = 0;

// SMAPI resolves dependencies by simple assembly name, including its bundled MonoMod.Common.
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
    new AssemblyName(request.Name).Name == "MonoMod.Common"
        ? Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "MonoMod.Common.dll"))
        : null;

Test("old Harmony lookup reproduces the startup failure", () =>
{
    Throws<AmbiguousMatchException>(() =>
        AccessTools.Method(typeof(Game1), "getCharacterFromName", new[] { typeof(string), typeof(bool), typeof(bool) }));
});

Test("production constructor binds only non-generic methods", () =>
{
    var game = new GameApi();
    Eq("1.6.15", game.Version);
    Eq("static StardewValley.Game1.getCharacterFromName(System.String, System.Boolean, System.Boolean)", game.CharacterLookup);
    Eq("Robin", ((NPC)game.Robin!).Name);
    Eq(typeof(Farm), game.Farm.GetType());
});

Test("production constructor resolves dialogue and inherited save data", () =>
{
    var game = NewApi();
    Eq<string?>(null, game.Load("sample"));
    game.Store("sample", "value");
    Eq("value", game.Load("sample"));
    string? response = null;
    game.Ask("sample", new[] { ("ok", "OK") }, answer => response = answer);
    Game1.currentLocation.callback!(Game1.MasterPlayer, "ok");
    Eq("ok", response);
});

Test("reflection rejects inexact signatures and reports the failing member", () =>
{
    var ex = Throws<MissingMethodException>(() => GameReflection.Method(typeof(Game1), "getCharacterFromName", staticMethod, typeof(object)));
    Eq(true, ex.Message.Contains("StardewValley.Game1.getCharacterFromName(System.Object)", StringComparison.Ordinal));
    Throws<MissingMethodException>(() => GameReflection.Method(typeof(Game1), "getFarm", instance));
    Throws<MissingMemberException>(() => GameReflection.MemberType(typeof(Farmer), "MemberMissing"));
    Eq(typeof(Dictionary<string, string>), GameReflection.MemberType(typeof(Farmer), "modData"));
});

Test("all targeted Harmony patches resolve and install on signature fixtures", () =>
{
    var game = NewApi();
    var patches = ModEntry.GamePatches.Resolve(game);
    Eq(20, patches.Count);
    Eq(20, patches.Select(patch => patch.Original).Distinct().Count());
    var mail = patches.Single(patch => patch.Handler.Name == "MailAction");
    Eq(typeof(string[]), mail.Original.GetParameters()[0].ParameterType);
    var building = patches.Single(patch => patch.Handler.Name == "CabinTileProperty");
    Eq(typeof(string).MakeByRefType(), building.Original.GetParameters()[4].ParameterType);
    var harmony = new Harmony(modId);
    try
    {
        Eq(20, ModEntry.GamePatches.Apply(harmony, game, new ModEntry()));
        Eq(20, harmony.GetPatchedMethods().Count());
        Eq(true, new GameLocation().performAction(new[] { "Mailbox" }, new Farmer(), new xTile.Dimensions.Location()));
        Eq(PointZero(), new Farmer().getMailboxPosition());
    }
    finally
    {
        harmony.UnpatchAll(modId);
    }
});

Test("missing initialization targets fail before any patch is installed", () =>
{
    Type ResolveWithMissingBuilding(string name) => name == "StardewValley.Buildings.Building"
        ? typeof(IncompleteBuilding)
        : FixtureType(name);
    var game = new GameApi(ResolveWithMissingBuilding);
    var harmony = new Harmony(modId);
    var ex = Throws<MissingMethodException>(() => ModEntry.GamePatches.Apply(harmony, game, new ModEntry()));
    Eq(true, ex.Message.Contains("IncompleteBuilding.draw", StringComparison.Ordinal));
    Eq(0, harmony.GetPatchedMethods().Count());
});

RobinRegression.Run(Test);

string temp = Path.Combine(Path.GetTempPath(), $"stok-house-config-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temp);
try
{
    string path = Path.Combine(temp, "config.json");
    Test("fresh config defaults to O", () =>
    {
        var config = LoadConfig(path);
        Eq(SButton.O, config.MenuKey);
        Eq("O", JsonNode.Parse(File.ReadAllText(path))!["MenuKey"]!.GetValue<string>());
    });

    Test("legacy F8 and numeric F8 migrate while every other setting survives", () =>
    {
        foreach (string key in new[] { "\"F8\"", ((int)SButton.F8).ToString() })
        {
            File.WriteAllText(path, "{\"menuKey\":" + key + ",\"Capacity\":4,\"Other\":{\"custom\":[1,true]}}");
            var config = LoadConfig(path);
            Eq(SButton.O, config.MenuKey);
            Eq(4, config.Capacity);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Eq("O", json["menuKey"]!.GetValue<string>());
            Eq(true, json["Other"]!["custom"]![1]!.GetValue<bool>());
        }
    });

    Test("custom keys and subsequent F8 edits are respected", () =>
    {
        File.WriteAllText(path, """{"MenuKey":"K","Capacity":3,"Other":"keep"}""");
        Eq(SButton.K, LoadConfig(path).MenuKey);
        string userEdit = File.ReadAllText(path).Replace("\"K\"", "\"F8\"", StringComparison.Ordinal);
        File.WriteAllText(path, userEdit);
        var config = LoadConfig(path);
        Eq(SButton.F8, config.MenuKey);
        Eq(3, config.Capacity);
        Eq(userEdit, File.ReadAllText(path));
    });

    Test("invalid JSON is left intact and diagnostics report fallback to O", () =>
    {
        const string invalid = "{...";
        File.WriteAllText(path, invalid);
        string? warning = null;
        Eq(SButton.O, ModConfig.Load(path, _ => {}, message => warning = message).MenuKey);
        Eq(true, warning!.Contains("config.json", StringComparison.Ordinal));
        Eq(invalid, File.ReadAllText(path));
    });
}
finally
{
    Directory.Delete(temp, recursive: true);
}

if (args is ["--game-path", var location])
{
    Test("installed Stardew Valley metadata resolves game API and all Harmony targets", () =>
    {
        string folder = Path.GetFullPath(location);
        var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "Stardew Valley.dll"));
        var tileAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "xTile.dll"));
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
        try
        {
            var game = new GameApi(name => (name.StartsWith("xTile.", StringComparison.Ordinal) ? tileAssembly : gameAssembly)
                .GetType(name, throwOnError: true)!);
            Eq(20, ModEntry.GamePatches.Resolve(game).Count);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolveDependency;
        }

        Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (var candidate in new[] { Path.Combine(folder, name.Name + ".dll"),
                         Path.Combine(folder, "smapi-internal", name.Name + ".dll") })
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            return null;
        }
    });
}
else if (args.Length > 0)
{
    throw new ArgumentException("Use --game-path followed by an installed Stardew Valley directory.");
}
Console.WriteLine($"Initialization and configuration checks passed: {passed}.");

static Microsoft.Xna.Framework.Point PointZero() => Microsoft.Xna.Framework.Point.Zero;
static Type FixtureType(string name) => typeof(Game1).Assembly.GetType(name, throwOnError: true)!;
static GameApi NewApi() => new(FixtureType);
static ModConfig LoadConfig(string file) => ModConfig.Load(file, _ => {}, message => throw new InvalidOperationException(message));

void Test(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Eq<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

internal class IncompleteBuilding
{
    public GameLocation GetIndoors() => new();
    public Microsoft.Xna.Framework.Point getMailboxPosition() => new();
}
