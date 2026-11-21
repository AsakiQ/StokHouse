using System.Text.Json;
using StokHouse.FamilyBudget.Spec;

if (args.Length != 0)
{
    if (args is ["--contract" or "--verify-contract", { } reference])
    {
        var contract = MoneyAudit.Describe(reference);
        Console.WriteLine($"Assembly {contract.Version}, reference assembly: {contract.ReferenceOnly}.");
        if (args[0] == "--verify-contract")
            MoneyAudit.VerifyGameContract(contract);
        foreach (string signature in contract.Signatures)
            Console.WriteLine(signature);
        return;
    }
    if (args is not ["--audit" or "--verify-audit", { } path])
        throw new ArgumentException("Usage: --audit|--verify-audit|--contract|--verify-contract <path to Stardew Valley.dll>");
    var entries = MoneyAudit.Read(path);
    if (args[0] == "--verify-audit")
    {
        MoneyAudit.VerifyGameContract(MoneyAudit.Describe(path));
        MoneyAudit.VerifyMoneyPaths(entries);
    }

    if (entries.Count == 0)
        throw new InvalidDataException("No known Stardew Valley money paths were found in this assembly.");
    Console.WriteLine("Caller\tIL offset\tMoney member");
    foreach (var site in entries.OrderBy(site => site.Caller, StringComparer.Ordinal).ThenBy(site => site.Offset))
        Console.WriteLine($"{site.Caller}\t{site.Offset:x4}\t{site.Target}");
    Console.WriteLine($"{entries.Count} monetary IL references.");
    return;
}

var pair = Pair.Create(1, 2);
int passed = 0;

Test("only a housemate can consent and the group activates after the matching saved checkpoint", () =>
{
    var initial = Open();
    var id = Guid.NewGuid();
    var proposed = initial.Propose(id, pair, 1, ChangeKind.Merge, true, true);
    Reject(() => proposed.Answer(id, 1, true));
    Reject(() => proposed.Answer(id, 3, true));
    var accepted = proposed.Answer(id, 2, true);
    var prepared = Prepare(accepted);
    Check(accepted.ForPlayer(1) is null && accepted.Personal[1] == 10_000 && accepted.Personal[2] == 5_000);
    Reject(() => accepted.ConfirmSave(prepared, new(accepted.Farm, Guid.NewGuid(), prepared.Day)));
    var active = Commit(accepted, prepared);
    Check(active.Available(id, 1) == 15_000 && active.Available(id, 2) == 15_000);
    Check(active.Personal[1] == 0 && active.Personal[2] == 0);
    Check(Commit(active, prepared) == active);
});

Test("merge uses the latest personal balances without affecting others", () =>
{
    var proposed = Invite(Open(), pair, ChangeKind.Merge);
    var edited = proposed.ObservePersonal(2, 7_530);
    var active = Overnight(edited);
    Check(active.ForPlayer(1)!.Balance == 17_530 && active.Personal[3] == 3_000);
    Check(Total(edited) == Total(active));
});

Test("global wallet, unrelated residents, duplicate proposals and conflicting pairs are rejected", () =>
{
    var book = Open();
    var id = Guid.NewGuid();
    Reject(() => book.Propose(id, pair, 1, ChangeKind.Merge, true, false));
    Reject(() => book.Propose(id, pair, 1, ChangeKind.Merge, false, true));
    var proposed = book.Propose(id, pair, 1, ChangeKind.Merge, true, true);
    Reject(() => proposed.Propose(Guid.NewGuid(), Pair.Create(2, 3), 3, ChangeKind.Merge, true, true));
    Reject(() => proposed.Cancel(id, 3));
    var cancelled = proposed.Cancel(id, 2);
    Reject(() => cancelled.Propose(id, pair, 1, ChangeKind.Merge, true, true));
});

Test("decline, timeout, same-day cancellation and stale nightly checkpoint preserve gold", () =>
{
    var initial = Open();
    var id = Guid.NewGuid();
    var offer = initial.Propose(id, pair, 1, ChangeKind.Merge, true, true);
    Check(Overnight(offer).Accounts.Count == 0);
    Check(Overnight(offer.Answer(id, 2, false)).Accounts.Count == 0);
    var accepted = offer.Answer(id, 2, true);
    var checkpoint = Prepare(accepted);
    var cancelled = accepted.Cancel(id, 1);
    Reject(() => Commit(cancelled, checkpoint));
    Check(Total(Overnight(cancelled)) == Total(initial));
});

Test("three family wallets and a solo player are independent", () =>
{
    var book = Open();
    var second = Pair.Create(3, 4);
    var third = Pair.Create(5, 6);
    book = Overnight(Invite(Invite(Invite(book, pair, ChangeKind.Merge), second, ChangeKind.Merge), third, ChangeKind.Merge));
    Check(book.Accounts.Count == 3 && book.ForPlayer(7) is null && book.Personal[7] == 700);
    var firstId = book.ForPlayer(1)!.Id;
    var secondId = book.ForPlayer(3)!.Id;
    var thirdId = book.ForPlayer(5)!.Id;
    Reject(() => book.Available(firstId, 3));
    Reject(() => book.Post(Guid.NewGuid(), firstId, 7, -1, PostingKind.Purchase));
    book = Execute(book, firstId, 2, -14_000, PostingKind.Purchase);
    Check(book.Available(firstId, 1) == 1_000 && book.Available(secondId, 4) == 3_400 && book.Available(thirdId, 6) == 1_100);
});

Test("serialized reservations prevent two clients spending the same balance", () =>
{
    var book = Active();
    var id = book.ForPlayer(1)!.Id;
    var purchase = Guid.NewGuid();
    var held = book.Post(purchase, id, 1, -10_000, PostingKind.Purchase);
    Check(held.Available(id, 2) == 5_000);
    Reject(() => held.Post(Guid.NewGuid(), id, 2, -10_000, PostingKind.Purchase));
    Reject(() => held.Post(Guid.NewGuid(), id, 2, int.MinValue, PostingKind.Purchase));
    var spent = held.Resolve(new(purchase, 1, id, PostingState.Applied));
    Check(spent.Available(id, 2) == 5_000 && spent.ForPlayer(1)!.Balance == 5_000);
});

Test("a repeated transaction and receipt have no side effects and conflicting payloads fail", () =>
{
    var book = Active();
    var id = book.ForPlayer(1)!.Id;
    var purchase = Guid.NewGuid();
    var held = book.Post(purchase, id, 1, -1_000, PostingKind.Purchase);
    Check(held.Post(purchase, id, 1, -1_000, PostingKind.Purchase) == held);
    Reject(() => held.Post(purchase, id, 2, -1_000, PostingKind.Purchase));
    Reject(() => held.Post(purchase, id, 1, -2_000, PostingKind.Purchase));
    Reject(() => held.Resolve(new(purchase, 2, id, PostingState.Applied)));
    var receipt = new ActionReceipt(purchase, 1, id, PostingState.Applied);
    var spent = held.Resolve(receipt);
    Check(spent.Resolve(receipt) == spent);
    Reject(() => spent.Resolve(receipt with { Outcome = PostingState.Reversed }));
});

Test("shop sales, overnight shipping and quest rewards post once each", () =>
{
    var book = Active();
    var account = book.ForPlayer(1)!.Id;
    var sale = Guid.NewGuid();
    var pending = book.Post(sale, account, 2, 500, PostingKind.Sale);
    Check(pending.Available(account, 1) == 15_000);
    book = pending.Resolve(new(sale, 2, account, PostingState.Applied));
    book = Execute(book, account, 1, 1_000, PostingKind.Shipping);
    book = Execute(book, account, 2, 200, PostingKind.Reward);
    Check(book.Available(account, 1) == 16_700 && book.Available(account, 2) == 16_700);
    Check(book.Post(sale, account, 2, 500, PostingKind.Sale) == book);
});

Test("disconnected construction holds gold across host restart until a recorded outcome", () =>
{
    var book = Active();
    var account = book.ForPlayer(1)!.Id;
    var purchase = Guid.NewGuid();
    var held = Copy(book.Post(purchase, account, 2, -10_000, PostingKind.Construction));
    Check(held.Available(account, 1) == 5_000);
    Check(!held.MayLeave(2));
    var returned = held.Resolve(new(purchase, 2, account, PostingState.Reversed));
    Check(returned.Available(account, 2) == 15_000);
    Check(returned.Resolve(new(purchase, 2, account, PostingState.Reversed)) == returned);
});

Test("split quotes exact amounts and handles an odd gold without loss", () =>
{
    var book = Active();
    var account = book.ForPlayer(1)!.Id;
    book = Execute(book, account, 1, 1, PostingKind.Reward);
    var offer = Invite(book, pair, ChangeKind.Split);
    Check(offer.Proposals.Values.Single().Quote == new SplitQuote(7_501, 7_500));
    var prepared = Prepare(offer);
    Check(!offer.MayLeave(1) && !offer.MayLeave(2));
    var split = Commit(offer, prepared);
    Check(split.ForPlayer(1) is null && split.Personal[1] == 7_501 && split.Personal[2] == 7_500);
    Check(split.MayLeave(2) && Total(book) == Total(split));
});

Test("a changed balance requires a new split quote and fresh consent", () =>
{
    var offered = Invite(Active(), pair, ChangeKind.Split);
    var account = offered.ForPlayer(1)!.Id;
    var changed = Execute(offered, account, 2, 100, PostingKind.Reward);
    var next = Overnight(changed);
    Check(next.ForPlayer(1)!.Balance == 15_100 && next.Proposals.Count == 0 && !next.MayLeave(2));
    Check(Overnight(Invite(next, pair, ChangeKind.Split)).Personal[2] == 7_550);
});

Test("a held transaction suspends splitting and consumes nothing until resolved", () =>
{
    var offered = Invite(Active(), pair, ChangeKind.Split);
    var account = offered.ForPlayer(1)!.Id;
    var purchase = Guid.NewGuid();
    var held = offered.Post(purchase, account, 2, -100, PostingKind.Purchase);
    var next = Overnight(held);
    Check(next.ForPlayer(1) is not null && next.Proposals.Count == 1 && !next.MayLeave(2));
    var resolved = next.Resolve(new(purchase, 2, account, PostingState.Reversed));
    Check(Overnight(resolved).Personal[2] == 7_500);
});

Test("a change to residents or unavailable economy does not activate a proposed merge", () =>
{
    var proposed = Invite(Open(), pair, ChangeKind.Merge);
    var pending = Commit(proposed, proposed.PrepareNight(Guid.NewGuid(), _ => true, _ => false, true));
    Check(pending.Proposals.Count == 1 && pending.Accounts.Count == 0);
    var moved = Commit(proposed, proposed.PrepareNight(Guid.NewGuid(), _ => false, _ => true, true));
    Check(moved.Proposals.Count == 0 && moved.Accounts.Count == 0 && Total(proposed) == Total(moved));
});

Test("a persisted checkpoint belongs to one save and can be restored without recomputing", () =>
{
    var start = Invite(Open(), pair, ChangeKind.Merge);
    var checkpoint = Copy(Prepare(start));
    var loadedPrior = Copy(start);
    var next = Commit(loadedPrior, checkpoint);
    var loadedResult = Copy(next);
    Check(Commit(loadedResult, checkpoint) == loadedResult && loadedResult.ForPlayer(2)!.Balance == 15_000);
    Reject(() => Open().ConfirmSave(checkpoint, new(next.Farm, checkpoint.Id, next.Day)));
    Reject(() => Commit(loadedResult, Prepare(loadedPrior)));
});

Test("overflow cannot silently discard contributions or receipts", () =>
{
    var initial = Open().ObservePersonal(1, int.MaxValue);
    var proposed = Invite(initial, pair, ChangeKind.Merge);
    var deferred = Overnight(proposed);
    Check(deferred.Accounts.Count == 0 && Total(deferred) == Total(initial));
    var full = Overnight(proposed.ObservePersonal(2, 0));
    Reject(() => full.Post(Guid.NewGuid(), full.ForPlayer(1)!.Id, 1, 1, PostingKind.Reward));
});

Test("a long stream of duplicated expenses, credits and snapshots reconciles exactly", () =>
{
    var rng = new Random(420);
    var book = Active();
    var account = book.ForPlayer(1)!.Id;
    long expected = Total(book);
    for (int batch = 0; batch < 70; batch++)
    {
        var receipts = new List<ActionReceipt>();
        for (int entry = 0; entry < 10; entry++)
        {
            long actor = rng.Next(1, 3);
            int amount = rng.Next(1, 250);
            bool deposit = rng.Next(2) == 0;
            if (!deposit && amount > book.Available(account, actor))
                continue;
            var id = Guid.NewGuid();
            var kind = deposit ? PostingKind.Sale : PostingKind.Purchase;
            int delta = deposit ? amount : -amount;
            book = book.Post(id, account, actor, delta, kind);
            Check(book.Post(id, account, actor, delta, kind) == book);
            var outcome = rng.Next(4) == 0 ? PostingState.Reversed : PostingState.Applied;
            receipts.Add(new(id, actor, account, outcome));
            if (outcome == PostingState.Applied)
                expected += delta;
            Check(book.Available(account, actor) >= 0);
        }
        book = Copy(book);
        foreach (var receipt in receipts.AsEnumerable().Reverse())
        {
            book = book.Resolve(receipt);
            Check(book.Resolve(receipt) == book);
        }
        Check(Total(book) == expected && book.Available(account, 1) == book.Available(account, 2));
    }
});

Test("the engine audit indexes direct calls and fields without loading the scanned assembly", () =>
{
    string fixture = typeof(StardewValley.AuditFixture).Assembly.Location;
    var contract = MoneyAudit.Describe(fixture);
    Check(!contract.ReferenceOnly && contract.Signatures.Contains("instance System.Int32 StardewValley.Farmer.get_Money()"));
    var paths = MoneyAudit.Read(fixture)
        .Where(site => site.Caller == "StardewValley.AuditFixture.Credit")
        .Select(site => site.Target).ToHashSet();
    Check(paths.SetEquals(new[] { "StardewValley.Farmer.get_Money", "StardewValley.Farmer.set_Money", "StardewValley.FarmerTeam.money" }));
});

HostChecks.Run(Test);
GameEconomyChecks.Run(Test);
SaveProbeChecks.Run(Test);
Console.WriteLine($"Family budget model, save coordination and audit checks passed: {passed}.");

void Test(string name, Action run)
{
    run();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static void Check(bool result)
{
    if (!result)
        throw new InvalidOperationException("Assertion failed.");
}

static void Reject(Action run)
{
    try { run(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected the operation to be rejected.");
}

static BudgetBook Open() => BudgetBook.Open(Guid.NewGuid(), 1, new Dictionary<long, int>
{
    [1] = 10_000, [2] = 5_000, [3] = 3_000, [4] = 400, [5] = 500, [6] = 600, [7] = 700
});

BudgetBook Active() => Overnight(Invite(Open(), pair, ChangeKind.Merge));

static BudgetBook Invite(BudgetBook book, Pair residents, ChangeKind kind)
{
    var id = Guid.NewGuid();
    return book.Propose(id, residents, residents.First, kind, true, true).Answer(id, residents.Second, true);
}

static Checkpoint Prepare(BudgetBook book) => book.PrepareNight(Guid.NewGuid(), _ => true, _ => true, true);

static BudgetBook Commit(BudgetBook book, Checkpoint checkpoint) =>
    book.ConfirmSave(checkpoint, new(book.Farm, checkpoint.Id, checkpoint.Day));

static BudgetBook Overnight(BudgetBook book) => Commit(book, Prepare(book));

static BudgetBook Execute(BudgetBook book, Guid account, long actor, int amount, PostingKind kind)
{
    var id = Guid.NewGuid();
    return book.Post(id, account, actor, amount, kind).Resolve(new(id, actor, account, PostingState.Applied));
}

static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

static long Total(BudgetBook book) =>
    book.Personal.Values.Sum(value => (long)value) + book.Accounts.Values.Sum(account => (long)account.Balance);
