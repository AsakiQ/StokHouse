// Only the signatures used by Stok House. These types do not implement the game.
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StardewValley
{
    public static class Game1
    {
        public static readonly string version = "1.6.15";
        public static readonly GameLocation currentLocation = new();
        public static readonly Farmer MasterPlayer = new();
        public static Farmer player => MasterPlayer;
        public static bool RobinBusy { get; set; }
        public static Response[] LastChoices { get; private set; } = Array.Empty<Response>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void drawObjectQuestionDialogue(string message, Response[] responses) => LastChoices = responses;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void drawObjectQuestionDialogue(string message, Response[] responses, int width) => LastChoices = responses;

        public static Farm getFarm() => new();
        public static T? getFarm<T>() where T : Farm => null;
        public static IEnumerable<Farmer> getAllFarmers() => new[] { MasterPlayer };
        public static IEnumerable<Farmer> getOnlineFarmers() => Array.Empty<Farmer>();
        public static NPC getCharacterFromName(string name, bool mustBeVillager = true, bool includeEventActors = false) => new(name);
        public static T? getCharacterFromName<T>(string name, bool mustBeVillager = true, bool includeEventActors = false) where T : NPC => null;
        public static void warpFarmer(string location, int x, int y, int direction, bool structure) { }
        public static void showGlobalMessage(string text) { }
        public static bool IsThereABuildingUnderConstruction(string builder) => RobinBusy;
    }

    public class Character
    {
        public Dictionary<string, string> modData { get; } = new();
    }

    public class Farmer : Character
    {
        public TestInventory Items { get; } = new();
        public long UniqueMultiplayerID { get; set; } = 1;
        public int HouseUpgradeLevel { get; set; }
        public Counter daysUntilHouseUpgrade { get; } = new() { Value = -1 };
        public Flag changeWalletTypeTonight { get; } = new();
        public TestTeam team { get; set; } = new();
        public bool useSeparateWallets => team.Separate;
        public Point getMailboxPosition() => Point.Zero;
        public bool isActive() => true;
    }

    public class Counter
    {
        public int Value { get; set; }
    }

    public class Flag
    {
        public bool Value { get; set; }
    }

    public class TestTeam
    {
        public bool Separate { get; set; }
        public Counter Shared { get; } = new();
        private readonly Dictionary<long, Counter> individual = new();
        public Counter GetMoney(Farmer payer)
        {
            if (!Separate)
                return Shared;
            if (!individual.TryGetValue(payer.UniqueMultiplayerID, out var wallet))
                individual[payer.UniqueMultiplayerID] = wallet = new Counter();
            return wallet;
        }
    }

    public class TestInventory
    {
        private readonly Item?[] items = new Item?[3];
        public int Count => items.Length;
        public Item? this[int index] { get => items[index]; set => items[index] = value; }
    }

    public interface ISalable { }

    public class Item : ISalable
    {
        public string QualifiedItemId { get; set; } = "";
        public int Stack { get; set; }
        public int maximumStackSize() => 999;
        public bool canStackWith(ISalable other) => other is Item item && item.QualifiedItemId == QualifiedItemId;
    }

    public static class ItemRegistry
    {
        public static Item Create(string id, int count, int quality, bool allowNull) => new() { QualifiedItemId = id, Stack = count };
    }

    public class NPC
    {
        public NPC(string name) => Name = name;
        public string Name { get; }
    }

    public class Response
    {
        public Response(string key, string text) { responseKey = key; responseText = text; }
        public string responseKey;
        public string responseText;
    }

    public class GameLocation
    {
        public delegate void afterQuestionBehavior(Farmer farmer, string answer);
        public afterQuestionBehavior? callback;
        public string? lastQuestionKey;
        public string? LastNativeAnswer;
        public bool destroyStructure(Building building) => true;
        public bool performAction(string action, Farmer who, xTile.Dimensions.Location tile) => false;
        public bool performAction(string[] action, Farmer who, xTile.Dimensions.Location tile) => true;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void createQuestionDialogue(string title, Response[] options, string dialogKey)
        {
            lastQuestionKey = dialogKey;
            Game1.drawObjectQuestionDialogue(title, options);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void createQuestionDialogue(string title, Response[] options, string dialogKey, object action)
        {
            lastQuestionKey = dialogKey;
            Game1.drawObjectQuestionDialogue(title, options, 1200);
        }
        public void createQuestionDialogue(string title, Response[] options, afterQuestionBehavior onReply, NPC? who) => callback = onReply;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool answerDialogueAction(string answer, string[] arguments)
        {
            LastNativeAnswer = answer;
            return true;
        }
        public void mailbox() { }


    }

    public class Farm : GameLocation
    {
        public Point GetMainMailboxPosition() => Point.Zero;
    }

    public static class Utility
    {
        public static FarmHouse getHomeOfFarmer(Farmer farmer) => new();
    }
}

namespace StardewValley.Buildings
{
    public class Building
    {
        public void draw(SpriteBatch batch) { }
        public void drawBackground(SpriteBatch batch) { }
        public void drawShadow(SpriteBatch batch, int x, int y) { }
        public void drawInConstruction(SpriteBatch batch) { }
        public bool isTilePassable(Vector2 position) => true;
        public bool intersects(Rectangle rectangle) => true;
        public bool doAction(Vector2 tile, Farmer farmer) => true;
        public bool isActionableTile(int x, int y, Farmer farmer) => true;
        public bool doesTileHaveProperty(int x, int y, string name, string layer, ref string result) => true;
        public GameLocation GetIndoors() => new();
        public Point getMailboxPosition() => Point.Zero;
    }
}

namespace StardewValley.Locations
{
    public class FarmHouse : GameLocation
    {
        public Point getEntryLocation() => Point.Zero;
    }

    public class Cabin : FarmHouse { }

    public class ManorHouse : GameLocation
    {
        public static void MergeWallets() { }
        public static void SeparateWallets() { }
    }
}

namespace StardewValley.Objects
{
    public class Furniture
    {
        public virtual bool canBeRemoved(Farmer farmer) => true;
        public bool placementAction(GameLocation location, int x, int y, Farmer farmer) => true;
    }

    public class BedFurniture : Furniture
    {
        public override bool canBeRemoved(Farmer farmer) => false;
        public bool CanModifyBed(Farmer farmer) => true;
    }

    public class Wallpaper
    {
        public bool placementAction(GameLocation location, int x, int y, Farmer farmer) => true;
    }
}

namespace xTile.Dimensions
{
    public readonly struct Location { }
}
