using System.Collections;
using System.Reflection;
using StokHouse.FamilyBudget;

namespace StokHouse;

internal sealed class GameShop
{
    private const int MaxUnits = 999;
    private readonly GameApi game;
    private readonly Type ordinaryObject;
    private readonly Type clickable;
    private readonly MethodInfo buildStock;
    private readonly MethodInfo dataShops;
    internal readonly MethodInfo Purchase;
    internal readonly MethodInfo LeftClick;
    internal readonly MethodInfo RightClick;
    internal readonly MethodInfo CurrencyAmount;
    private readonly FieldInfo cursor;
    private readonly MethodInfo containsPoint;

    internal GameShop(GameApi game, Func<string, Type> resolve)
    {
        this.game = game;
        Type shop = resolve("StardewValley.Menus.ShopMenu");
        Type salable = resolve("StardewValley.ISalable");
        ordinaryObject = resolve("StardewValley.Object");
        clickable = resolve("StardewValley.Menus.ClickableComponent");
        Purchase = GameReflection.Method(shop, "tryToPurchaseItem", BindingFlags.Instance | BindingFlags.DeclaredOnly,
            salable, salable, typeof(int), typeof(int), typeof(int));
        LeftClick = GameReflection.Method(shop, "receiveLeftClick", BindingFlags.Instance | BindingFlags.DeclaredOnly,
            typeof(int), typeof(int), typeof(bool));
        RightClick = GameReflection.Method(shop, "receiveRightClick", BindingFlags.Instance | BindingFlags.DeclaredOnly,
            typeof(int), typeof(int), typeof(bool));
        CurrencyAmount = GameReflection.Method(shop, "getPlayerCurrencyAmount", BindingFlags.Static, game.Farmer, typeof(int));
        buildStock = GameReflection.Method(resolve("StardewValley.Internal.ShopBuilder"), "GetShopStock", BindingFlags.Static, typeof(string));
        dataShops = GameReflection.Method(resolve("StardewValley.DataLoader"), "Shops", BindingFlags.Static,
            resolve("StardewValley.LocalizedContentManager"));
        cursor = shop.GetField("heldItem") ?? throw new MissingFieldException(shop.FullName, "heldItem");
        containsPoint = GameReflection.Method(clickable, "containsPoint", BindingFlags.Instance, typeof(int), typeof(int));
    }

    internal object? Cursor(object menu) => cursor.GetValue(menu);
    internal bool IsOpen(object menu) => ReferenceEquals(GameApi.Read(game.Game, "activeClickableMenu"), menu);
    private int Day => (int)GameApi.Read(GameApi.Read(game.Game, "Date")!, "TotalDays")!;

    internal object? OfferAt(object menu, int x, int y)
    {
        var buttons = (IList)GameApi.Read(menu, "forSaleButtons")!;
        var sale = (IList)GameApi.Read(menu, "forSale")!;
        var start = (int)GameApi.Read(menu, "currentItemIndex")!;
        for (int i = 0; i < buttons.Count && i + start < sale.Count; i++)
        {
            if (buttons[i] is { } button && clickable.IsInstanceOfType(button) &&
                (bool)containsPoint.Invoke(button, new object[] { x, y })!)
                return sale[i + start];
        }
        return null;
    }

    internal NativeShopQuote? LocalQuote(object menu, object offer, object? held, int requested)
    {
        if (!ReferenceEquals(Cursor(menu), held) || (int)GameApi.Read(menu, "currency")! != 0 ||
            (bool)GameApi.Read(menu, "readOnly")! || (bool)GameApi.Read(menu, "_isStorageShop")! ||
            GameApi.Read(menu, "onPurchase") is not null || GameApi.Read(menu, "canPurchaseCheck") is not null ||
            InBuyback(menu, offer) || GameApi.Read(menu, "ShopId") is not string shop ||
            !InStore(shop, game.Current) || ((IDictionary)GameApi.Read(menu, "itemPriceAndStock")!)[offer] is not { } stock)
            return null;

        var baseQuote = Describe(shop, offer, stock, Day);
        if (baseQuote is null)
            return null;
        int room = game.StackLimit(offer);
        if (held is not null)
        {
            if (held.GetType() != ordinaryObject || !game.StacksWith(held, offer))
                return baseQuote with { Quantity = 0 };
            room = game.StackLimit(held) - GameApi.StackSize(held);
        }
        return baseQuote with { Quantity = Math.Clamp(Math.Min(requested, room), 0, MaxUnits) };
    }

    internal NativeShopQuote? HostQuote(object buyer, NativeShopQuote request)
    {
        if (request.Kind != PostingKind.Purchase || request.OfferRevision != Day || request.Quantity is <= 0 or > MaxUnits ||
            request.Currency != 0 || GameApi.Read(buyer, "currentLocation") is not { } location || !InStore(request.Shop, location))
            return null;
        var shops = (IDictionary)dataShops.Invoke(null, new[] { GameApi.Read(game.Game, "content") })!;
        if (shops[request.Shop] is not { } data || Convert.ToInt32(GameApi.Read(data, "Currency")) != 0)
            return null;
        var stock = (IDictionary)buildStock.Invoke(null, new object[] { request.Shop })!;
        var stable = stock.Keys.Cast<object>()
            .Select(item => (Item: item, Quote: Describe(request.Shop, item, stock[item]!, Day)))
            .Where(entry => entry.Quote is not null && entry.Quote.Item == request.Item &&
                entry.Quote.Entry == request.Entry && entry.Quote.Quality == request.Quality).Take(2).ToArray();
        if (stable.Length != 1 || request.Quantity > game.StackLimit(stable[0].Item))
            return null;
        return stable[0].Quote! with { Quantity = request.Quantity };
    }

    internal PaymentEdit? PlanDelivery(object menu, object offer, object? originalCursor, int quantity)
    {
        if (quantity <= 0 || !ReferenceEquals(Cursor(menu), originalCursor))
            return null;
        object template = originalCursor ?? offer;
        int total = checked(quantity + (originalCursor is null ? 0 : GameApi.StackSize(originalCursor)));
        var edit = game.PlanShopDeposit(game.Player, template, total, originalCursor);
        if (edit is null)
            return null;

        // The empty-cursor and stack paths use the original native stack rules. Close the
        // completed stack into inventory immediately rather than leaving items on an unsaved cursor.
        if (originalCursor is not null)
            edit.Add(() => cursor.SetValue(menu, null), () => cursor.SetValue(menu, originalCursor));
        return edit;
    }

    private NativeShopQuote? Describe(string shop, object item, object stock, int day)
    {
        if (item.GetType() != ordinaryObject || (bool)GameApi.Read(item, "IsRecipe")! ||
            (bool)GameApi.Read(item, "isLostItem")! || GameApi.StackSize(item) != 1 ||
            (int)GameApi.Read(stock, "Stock")! != int.MaxValue || (int)GameApi.Read(stock, "Price")! is not (> 0) ||
            GameApi.Read(stock, "TradeItem") is not null || GameApi.Read(stock, "ItemToSyncStack") is not null ||
            GameApi.Read(stock, "ActionsOnPurchase") is ICollection { Count: > 0 } ||
            GameApi.Read(stock, "SyncedKey") is not string { Length: > 0 } entry ||
            GameApi.Read(item, "QualifiedItemId") is not string id || id is "(O)434" or "(O)858")
            return null;
        return new NativeShopQuote(PostingKind.Purchase, shop, id, 0, (int)GameApi.Read(stock, "Price")!, 1, day, entry,
            (int)GameApi.Read(item, "Quality")!);
    }

    private static bool InBuyback(object menu, object offer) =>
        ((IEnumerable)GameApi.Read(menu, "buyBackItems")!).Cast<object>().Any(item => ReferenceEquals(item, offer));

    private static bool InStore(string id, object location) =>
        id is "SeedShop" or "FishShop" or "Blacksmith" or "Saloon" && GameApi.LocationName(location) == id;
}
