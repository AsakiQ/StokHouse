using System.Reflection;

namespace StokHouse;

internal sealed partial class GameApi
{
    private readonly MethodInfo inventoryGet;
    private readonly MethodInfo? oneItem;

    internal static int StackSize(object item) => (int)Read(item, "Stack")!;
    internal int StackLimit(object item) => (int)maxStack.Invoke(item, null)!;
    internal bool StacksWith(object written, object sample) => (bool)canStack.Invoke(written, new[] { sample })!;

    internal PaymentEdit? PlanShopDeposit(object player, object template, int total, object? cursor)
    {
        object inventory = Read(player, "Items")!;
        int count = Math.Min((int)Read(player, "MaxItems")!, (int)Read(inventory, "Count")!);
        var slots = Enumerable.Range(0, count).Select(slot => inventoryGet.Invoke(inventory, new object[] { slot })).ToArray();
        if (cursor is not null && slots.Any(item => ReferenceEquals(item, cursor)))
            return null;
        if (total <= 0 || StackLimit(template) <= 0)
            return null;

        int remaining = total;
        var edit = new PaymentEdit();
        foreach (var index in Enumerable.Range(0, count).OrderBy(index => slots[index] is null))
        {
            if (remaining == 0)
                break;
            int slot = index;
            if (slots[slot] is { } current)
            {
                if (!StacksWith(current, template))
                    continue;
                int old = StackSize(current);
                int added = Math.Min(remaining, Math.Max(0, StackLimit(current) - old));
                if (added == 0)
                    continue;
                edit.Add(() => WriteStack(current, old + added), () => WriteStack(current, old));
                remaining -= added;
            }
            else
            {
                object stack = oneItem!.Invoke(template, null)!;
                int added = Math.Min(remaining, StackLimit(stack));
                WriteStack(stack, added);
                edit.Add(() => SetSlot(inventory, slot, stack), () => SetSlot(inventory, slot, null));
                remaining -= added;
            }
        }
        return remaining == 0 ? edit : null;
    }
    private readonly MethodInfo inventorySet;
    private readonly MethodInfo setStack;
    private readonly MethodInfo maxStack;
    private readonly MethodInfo canStack;
    private readonly MethodInfo createItem;
    private readonly MethodInfo getWallet;
    private readonly MethodInfo setIntValue;
    private readonly MethodInfo setUpgradeDays;

    private static int IntValue(object field) => (int)Read(field, "Value")!;

    private object Wallet(object payer) => getWallet.Invoke(Read(Master, "team"), new[] { payer })!;

    internal ulong FarmIdentity => (ulong)Read(Game, "uniqueIDForThisGame")!;

    internal void SetPersonalGold(object farmer, int value)
    {
        if (!Separate(Master) || value < 0)
            throw new InvalidOperationException("The family budget requires a personal game wallet.");
        SetInt(Wallet(farmer), value, setIntValue);
    }

    internal int Gold(object payer) => IntValue(Wallet(payer));

    internal bool WalletChangePending => (bool)Read(Read(Master, "changeWalletTypeTonight")!, "Value")!;

    internal string? PaymentResources(object payer, int level, int? available = null)
    {
        var (gold, item, count) = Price(level);
        if ((available ?? Gold(payer)) < gold)
            return "funds";
        return item is null || PlanMaterials(payer, item, -count) is not null ? null : "materials";
    }

    internal void AddWalletEdit(PaymentEdit edit, object payer, int change)
    {
        var wallet = Wallet(payer);
        int before = IntValue(wallet);
        int after = checked(before + change);
        if (after < 0)
            throw new InvalidOperationException("The payer's wallet no longer covers the order.");
        edit.Add(() => SetInt(wallet, after, setIntValue), () => SetInt(wallet, before, setIntValue));
    }

    internal void AddDataEdit(PaymentEdit edit, object farmer, string key, string value)
    {
        string before = Load(farmer, key) ?? "";
        edit.Add(() => Store(farmer, key, value), () => Store(farmer, key, before));
    }

    internal PaymentEdit? PlanMaterials(object payer, string id, int change)
    {
        object inventory = Read(payer, "Items")!;
        int count = (int)Read(inventory, "Count")!;
        object template = Material(id, 1);
        int remaining = Math.Abs(change);
        var edit = new PaymentEdit();

        for (int i = 0; i < count && remaining > 0; i++)
        {
            int slot = i;
            object? item = inventoryGet.Invoke(inventory, new object[] { slot });
            if (item is null)
            {
                if (change < 0)
                    continue;
                int added = Math.Min(remaining, (int)maxStack.Invoke(template, null)!);
                object stack = Material(id, added);
                edit.Add(() => SetSlot(inventory, slot, stack), () => SetSlot(inventory, slot, null));
                remaining -= added;
                continue;
            }
            if (!Equals(Read(item, "QualifiedItemId"), id) ||
                !(bool)canStack.Invoke(item, new[] { template })!)
                continue;

            int before = (int)Read(item, "Stack")!;
            int after = change < 0
                ? Math.Max(0, before - remaining)
                : before + Math.Min(remaining, Math.Max(0, (int)maxStack.Invoke(item, null)! - before));
            if (after == before)
                continue;
            if (after == 0)
                edit.Add(() => SetSlot(inventory, slot, null), () => SetSlot(inventory, slot, item));
            else
                edit.Add(() => WriteStack(item, after), () => WriteStack(item, before));
            remaining -= Math.Abs(after - before);
        }
        return remaining == 0 ? edit : null;
    }

    internal PaymentEdit PlanConstruction(object owner)
    {
        object days = Read(owner, "daysUntilHouseUpgrade")!;
        int before = UpgradeDays(owner);
        if (before >= 0)
            throw new InvalidOperationException("The owner already has a house upgrade.");
        var edit = new PaymentEdit();
        // Same timer that houseUpgradeAccept sets. Farmer.dayupdate advances the owner's native upgrade.
        edit.Add(() => SetInt(days, 3, setUpgradeDays), () => SetInt(days, before, setUpgradeDays));
        return edit;
    }

    private object Material(string id, int amount) => createItem.Invoke(null, new object[] { id, amount, 0, false })!;

    private void WriteStack(object item, int value)
    {
        setStack.Invoke(item, new object[] { value });
        if ((int)Read(item, "Stack")! != value)
            throw new InvalidOperationException("The game rejected a material stack change.");
    }

    private void SetSlot(object inventory, int slot, object? item)
    {
        inventorySet.Invoke(inventory, new object?[] { slot, item });
        if (!ReferenceEquals(inventoryGet.Invoke(inventory, new object[] { slot }), item))
            throw new InvalidOperationException("The game rejected a material slot change.");
    }

    private static void SetInt(object field, int value, MethodInfo setter)
    {
        setter.Invoke(field, new object[] { value });
        if (IntValue(field) != value)
            throw new InvalidOperationException("The game rejected a payment field change.");
    }
}
