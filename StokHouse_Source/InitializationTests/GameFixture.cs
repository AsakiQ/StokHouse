// Only the signatures used by Stok House. These types do not implement the game.
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

        public static Farm getFarm() => new();
        public static T? getFarm<T>() where T : Farm => null;
        public static IEnumerable<Farmer> getAllFarmers() => Array.Empty<Farmer>();
        public static IEnumerable<Farmer> getOnlineFarmers() => Array.Empty<Farmer>();
        public static NPC getCharacterFromName(string name, bool mustBeVillager = true, bool includeEventActors = false) => new(name);
        public static T? getCharacterFromName<T>(string name, bool mustBeVillager = true, bool includeEventActors = false) where T : NPC => null;
        public static void warpFarmer(string location, int x, int y, int direction, bool structure) { }
        public static void showGlobalMessage(string text) { }
        public static bool IsThereABuildingUnderConstruction(string builder) => false;
    }

    public class Character
    {
        public Dictionary<string, string> modData { get; } = new();
    }

    public class Farmer : Character
    {
        public TestInventory Items { get; } = new();
        public Point getMailboxPosition() => Point.Zero;
    }

    public class TestInventory
    {
        public bool ContainsId(string item) => false;
        public bool ContainsId(string item, int quantity) => false;
    }

    public class NPC
    {
        public NPC(string name) => Name = name;
        public string Name { get; }
    }

    public class Response
    {
        public Response(string key, string text) => responseKey = key;
        public string responseKey;
    }

    public class GameLocation
    {
        public delegate void afterQuestionBehavior(Farmer farmer, string answer);
        public afterQuestionBehavior? callback;
        public bool destroyStructure(Building building) => true;
        public bool performAction(string action, Farmer who, xTile.Dimensions.Location tile) => false;
        public bool performAction(string[] action, Farmer who, xTile.Dimensions.Location tile) => true;
        public void createQuestionDialogue(string title, Response[] options, string dialogKey) { }
        public void createQuestionDialogue(string title, Response[] options, afterQuestionBehavior onReply, NPC? who) => callback = onReply;
        public bool answerDialogueAction(string answer, string[] arguments) => true;
        public void mailbox() { }
        private void houseUpgradeAccept() { }
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
