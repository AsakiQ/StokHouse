using System.Text.Json;
using System.Xml.Linq;
using StardewValley;
using StardewValley.Internal;
using StardewValley.Menus;
using StokHouse.FamilyBudget;
using Object = StardewValley.Object;

namespace StokHouse;

internal static class ShopIntegrationTests
{
    internal static void Run(Action<string, Action> test)
    {
        if (!ModEntry.BudgetBuildEnabled)
            return;

        test("native shop quotes require current gold price, inventory capacity, date and buyer location", () =>
        {
            using var world = new World();
            var quote = world.Quote(5);
            Check(quote.Quantity == 5 && quote.UnitPrice == 20);
            var fresh = world.Shop.HostQuote(Game1.player, quote);
            Check(fresh == quote);
            var changed = world.Stock;
            changed.Price = 21;
            Check(world.Shop.HostQuote(Game1.player, quote) != quote);
            Check(world.Shop.HostQuote(Game1.player, quote with { OfferRevision = quote.OfferRevision - 1 }) is null);
            Check(world.Shop.HostQuote(Game1.player, quote with { Quantity = 1000 }) is null);
            Check(world.Shop.HostQuote(new Farmer { currentLocation = new GameLocation { NameOrUniqueName = "Farm" } }, quote) is null);
        });

        test("special stock, recipes, barter, other currencies and limited stock are left to vanilla", () =>
        {
            using var world = new World();
            world.Item.IsRecipe = true;
            Check(world.Shop.LocalQuote(world.Menu, world.Item, null, 1) is null);
            world.Item.IsRecipe = false;
            world.Stock.TradeItem = "(O)388";
            Check(world.Shop.LocalQuote(world.Menu, world.Item, null, 1) is null);
            world.Stock.TradeItem = null;
            world.Stock.Stock = 2;
            Check(world.Shop.LocalQuote(world.Menu, world.Item, null, 1) is null);
            world.Stock.Stock = int.MaxValue;
            world.Menu.currency = 4;
            Check(world.Shop.LocalQuote(world.Menu, world.Item, null, 1) is null);
            world.Menu.currency = 0;
            world.Menu.buyBackItems.Add(world.Item);
            Check(world.Shop.LocalQuote(world.Menu, world.Item, null, 1) is null);
        });

        test("an empty cursor adds one durable purchase and is immune to replay before settlement", () =>
        {
            using var world = new World();
            var (held, order) = FamilyShop.Open(Ready(300)).Authorize(Guid.NewGuid(), 1, world.Quote(3));
            var one = world.Apply(held, order);
            var two = world.Apply(held, order);
            Check(one == two && one.State == PostingState.Applied && world.Count() == 3 && world.Menu.heldItem is null);
            var paid = held.Settle(one);
            Check(paid.Book.Available(order.Account, 1) == 240 && paid.Settle(two) == paid);
        });

        test("a compatible existing cursor stack is capped and deposited once with only the new units charged", () =>
        {
            using var world = new World();
            world.Menu.heldItem = new Object { QualifiedItemId = world.Item.QualifiedItemId, Stack = 997 };
            var quote = world.Quote(8);
            Check(quote.Quantity == 2);
            var (held, order) = FamilyShop.Open(Ready(300)).Authorize(Guid.NewGuid(), 1, quote);
            var paid = held.Settle(world.Apply(held, order));
            Check(world.Count() == 999 && world.Menu.heldItem is null && paid.Book.Available(order.Account, 1) == 260);
        });

        test("a cursor already in inventory and a full inventory cannot grant any item", () =>
        {
            using var world = new World();
            var existing = new Object { QualifiedItemId = world.Item.QualifiedItemId, Stack = 2 };
            world.Menu.heldItem = existing;
            Game1.player.Items[0] = existing;
            Check(world.Shop.PlanDelivery(world.Menu, world.Item, existing, 1) is null);
            world.Menu.heldItem = null;
            for (int i = 0; i < Game1.player.Items.Count; i++)
                Game1.player.Items[i] = new Object { QualifiedItemId = "(O)388", Stack = 999 };
            Check(world.Shop.PlanDelivery(world.Menu, world.Item, null, 1) is null);
            Check(world.Count() == 0);
        });

        test("a simultaneous second buyer cannot overdraw the shared pool and other budgets stay independent", () =>
        {
            using var world = new World();
            var quote = world.Quote(3);
            var book = Ready(100);
            var (held, order) = FamilyShop.Open(book).Authorize(Guid.NewGuid(), 1, quote);
            Reject(() => held.Authorize(Guid.NewGuid(), 2, quote));
            Reject(() => held.Authorize(Guid.NewGuid(), 7, quote));
            var (third, secondOrder) = held.Authorize(Guid.NewGuid(), 4, quote);
            Check(third.Book.Available(order.Account, 2) == 40 && third.Book.Available(secondOrder.Account, 3) == 140);
            Check(third.Book.ForPlayer(6)?.Balance == 300 && third.Book.Personal[7] == 700);
        });

        test("shop holds and inventory receipts survive reload without double delivery or debit", () =>
        {
            using var world = new World();
            var (held, order) = FamilyShop.Open(Ready(200)).Authorize(Guid.NewGuid(), 1, world.Quote(3));
            var recovered = FamilyShop.Open(BudgetSession.Load(world.Api.FarmIdentity,
                JsonSerializer.Serialize(held.Book), Enumerable.Range(1, 7).Select(x => (long)x)));
            var delivered = world.Apply(recovered, order);
            Check(world.Count() == 3);
            var afterDisconnect = world.Apply(recovered, order);
            var paid = recovered.Settle(afterDisconnect);
            Check(delivered == afterDisconnect && world.Count() == 3 && paid.Book.Available(order.Account, 2) == 140);
            Check(paid.Settle(delivered) == paid);
        });

        test("missing delivery after reconnect reverses the hold only after recording a real refusal", () =>
        {
            using var world = new World();
            var (held, order) = FamilyShop.Open(Ready(200)).Authorize(Guid.NewGuid(), 1, world.Quote(3));
            Check(held.Book.Available(order.Account, 2) == 140);
            var undone = ShopParticipant.Apply(held, order, order.Quote with { Quantity = 0 }, world.Receipt,
                world.Receipt, () => throw new InvalidOperationException("Must not deliver after closing a shop."));
            Check(undone.State == PostingState.Reversed && world.Count() == 0 &&
                held.Settle(undone).Book.Available(order.Account, 2) == 200);
        });

        test("a changed native offer cannot consume a reservation or deliver an item", () =>
        {
            using var world = new World();
            var (held, order) = FamilyShop.Open(Ready(200)).Authorize(Guid.NewGuid(), 1, world.Quote(3));
            world.Stock.Price++;
            var quote = world.Quote(3);
            var verified = ShopParticipant.Apply(held, order, quote, world.Receipt, world.Receipt,
                () => throw new InvalidOperationException("A stale quote attempted delivery."));
            Check(verified.State == PostingState.Reversed && world.Count() == 0 &&
                held.Settle(verified).Book.Available(order.Account, 2) == 200);
        });

        test("a saved debit requires the same receipt on the correct farmer", () =>
        {
            using var world = new World();
            var (held, order) = FamilyShop.Open(Ready(200)).Authorize(Guid.NewGuid(), 1, world.Quote(3));
            var delivered = world.Apply(held, order);
            var paid = held.Settle(delivered).Book;
            var path = Path.GetTempFileName();
            try
            {
                XElement Player(long id, string name, bool hasReceipt) => new(name,
                    new XElement("UniqueMultiplayerID", id), new XElement("money", 0),
                    hasReceipt ? new XElement("modData", new XElement("item",
                        new XElement("key", new XElement("string", SavedBudgetProbe.ShopReceiptKey(order.Id))),
                        new XElement("value", new XElement("string", JsonSerializer.Serialize(delivered))))) : null);
                void Save(bool rightPlayer) => new XElement("SaveGame", Player(1, "player", rightPlayer),
                    new XElement("farmhands", Player(2, "Farmer", !rightPlayer)),
                    new XElement("uniqueIDForThisGame", world.Api.FarmIdentity)).Save(path);

                Save(false);
                Check(!BudgetSession.VerifyWalletProof(path, world.Api.FarmIdentity, paid));
                Save(true);
                Check(BudgetSession.VerifyWalletProof(path, world.Api.FarmIdentity, paid));
            }
            finally { File.Delete(path); }
        });
    }

    private static BudgetBook Ready(int initial)
    {
        var book = BudgetSession.Load(Game1.uniqueIDForThisGame, null, Enumerable.Range(1, 7).Select(x => (long)x));
        foreach (var (first, gold) in new[] { (1L, initial), (3L, 200), (5L, 300) })
        {
            var id = Guid.NewGuid();
            book = book.ObservePersonal(first, gold).Propose(id, Pair.Create(first, first + 1), first, ChangeKind.Merge,
                true, true).Answer(id, first + 1, true);
        }
        book = book.ObservePersonal(7, 700);
        var night = book.PrepareNight(Guid.NewGuid(), _ => true, _ => true, true);
        return book.ConfirmSave(night, new SaveReceipt(book.Farm, night.Id, night.Day)) with
        {
            SavedWallets = System.Collections.Immutable.ImmutableDictionary<long, int>.Empty
        };
    }

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Shop integration assertion failed.");
    }

    private static void Reject(Action call)
    {
        try { call(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("An invalid shop purchase was authorized.");
    }

    private sealed class World : IDisposable
    {
        internal readonly GameApi Api = new();
        internal readonly ShopMenu Menu = new();
        internal readonly Object Item = new() { QualifiedItemId = "(O)472", Stack = 1 };
        internal readonly ItemStockInformation Stock = new();
        internal GameShop Shop => Api.Shop!;
        private readonly Dictionary<Guid, ShopOutcome> receipts = new();

        internal World()
        {
            Menu.itemPriceAndStock.Add(Item, Stock);
            Menu.forSale.Add(Item);
            ShopBuilder.Catalog = new() { [Item.getOne()] = Stock };
            Game1.activeClickableMenu = Menu;
            for (int i = 0; i < Game1.player.Items.Count; i++)
                Game1.player.Items[i] = null;
        }

        internal int Count() => Enumerable.Range(0, Game1.player.Items.Count).Select(i => Game1.player.Items[i])
            .Where(item => item?.QualifiedItemId == Item.QualifiedItemId).Sum(item => item!.Stack);
        internal ShopOutcome? Receipt(Guid id) => receipts.GetValueOrDefault(id);
        internal void Receipt(Guid id, ShopOutcome? receipt)
        {
            if (receipt is not null)
                receipts[id] = receipt;
        }
        internal NativeShopQuote Quote(int units) => Shop.LocalQuote(Menu, Item, Menu.heldItem, units)!;
        internal ShopOutcome Apply(FamilyShop session, ShopOrder order) =>
            ShopParticipant.Apply(session, order, Quote(order.Quote.Quantity), Receipt, Receipt,
                () => Shop.PlanDelivery(Menu, Item, Menu.heldItem, order.Quote.Quantity));

        public void Dispose()
        {
            Game1.activeClickableMenu = null;
            for (int i = 0; i < Game1.player.Items.Count; i++)
                Game1.player.Items[i] = null;
        }
    }
}
