using System.Collections.Immutable;

namespace StokHouse.FamilyBudget;

internal enum ChangeKind { Merge, Split }
internal enum PostingKind { Purchase, Sale, Shipping, Reward, Construction }
internal enum PostingState { Held, Applied, Reversed }

internal sealed record Pair(long First, long Second)
{
    internal static Pair Create(long first, long second)
    {
        if (first == second || first == 0 || second == 0)
            throw new ArgumentException("Two distinct residents are required.");
        return new Pair(Math.Min(first, second), Math.Max(first, second));
    }

    internal bool Contains(long player) => player == First || player == Second;
}

internal sealed record SplitQuote(int First, int Second)
{
    internal static SplitQuote Half(int balance) => new(balance / 2 + balance % 2, balance / 2);
}

internal sealed record Budget(Guid Id, Pair Pair, int Balance);
internal sealed record Proposal(Guid Id, Pair Pair, long Sender, int Day, ChangeKind Kind,
    SplitQuote? Quote = null, bool Accepted = false);
internal sealed record Posting(Guid Id, Guid Budget, long Actor, int Amount, PostingKind Kind, PostingState State);
internal sealed record Checkpoint(Guid Id, int Day, Guid SourceRevision, BudgetBook Next);
internal sealed record SaveReceipt(Guid Farm, Guid Checkpoint, int Day);
internal sealed record ActionReceipt(Guid Operation, long Actor, Guid Budget, PostingState Outcome);

internal sealed record BudgetBook(Guid Farm, int Day, Guid Revision, Guid LastCheckpoint, ImmutableDictionary<long, int> Personal,
    ImmutableDictionary<Guid, Budget> Accounts, ImmutableDictionary<Guid, Proposal> Proposals,
    ImmutableDictionary<Guid, Posting> Postings, ImmutableHashSet<Guid> UsedProposals,
    ImmutableDictionary<long, int>? SavedWallets = null, ImmutableDictionary<Guid, ShopOrder>? ShopOrders = null)
{
    internal static BudgetBook Open(Guid farm, int day, IReadOnlyDictionary<long, int> balances)
    {
        if (farm == Guid.Empty || day < 1 || balances.Any(entry => entry.Key == 0 || entry.Value < 0))
            throw new ArgumentException("Invalid farm, date, or personal balance.");
        return new(farm, day, Guid.NewGuid(), Guid.Empty, balances.ToImmutableDictionary(), ImmutableDictionary<Guid, Budget>.Empty,
            ImmutableDictionary<Guid, Proposal>.Empty, ImmutableDictionary<Guid, Posting>.Empty, ImmutableHashSet<Guid>.Empty);
    }

    internal Budget? ForPlayer(long player) => Accounts.Values.SingleOrDefault(account => account.Pair.Contains(player));

    internal BudgetBook WithPlayers(IEnumerable<long> players)
    {
        var personal = Personal.AddRange(players.Where(id => id != 0 && !Personal.ContainsKey(id)).Distinct()
            .Select(id => new KeyValuePair<long, int>(id, 0)));
        return personal.Count == Personal.Count ? this : this with { Personal = personal, Revision = Guid.NewGuid() };
    }

    internal void Validate()
    {
        if (Day < 1 || Revision == Guid.Empty || Personal is null || Accounts is null || Proposals is null ||
            Postings is null || UsedProposals is null || Personal.Any(pair => pair.Key == 0 || pair.Value < 0) ||
            SavedWallets is { } wallets && wallets.Any(pair => !Personal.ContainsKey(pair.Key) || pair.Value < 0))
            throw new InvalidDataException("Invalid family budget header.");
        var members = new HashSet<long>();
        foreach (var (id, account) in Accounts)
        {
            if (id == Guid.Empty || account is null || account.Id != id || account.Balance < 0 ||
                !ValidPair(account.Pair) || !members.Add(account.Pair.First) || !members.Add(account.Pair.Second) ||
                Personal[account.Pair.First] != 0 || Personal[account.Pair.Second] != 0)
                throw new InvalidDataException("Invalid family account.");
        }
        members.Clear();
        foreach (var (id, offer) in Proposals)
        {
            if (id == Guid.Empty || offer is null || offer.Id != id || !UsedProposals.Contains(id) ||
                !ValidPair(offer.Pair) || !offer.Pair.Contains(offer.Sender) || offer.Day < 1 || offer.Day > Day ||
                !Enum.IsDefined(offer.Kind) || !members.Add(offer.Pair.First) || !members.Add(offer.Pair.Second) ||
                (offer.Kind == ChangeKind.Split ? offer.Quote is not { First: >= 0, Second: >= 0 } ||
                    ForPlayer(offer.Pair.First)?.Pair != offer.Pair : offer.Quote is not null ||
                    ForPlayer(offer.Pair.First) is not null || ForPlayer(offer.Pair.Second) is not null))
                throw new InvalidDataException("Invalid family budget proposal.");
        }
        foreach (var (id, post) in Postings)
        {
            if (id == Guid.Empty || post is null || post.Id != id || post.Budget == Guid.Empty || post.Actor == 0 ||
                post.Amount is 0 or int.MinValue || !Enum.IsDefined(post.Kind) || !Enum.IsDefined(post.State) ||
                (post.Amount < 0) != (post.Kind is PostingKind.Purchase or PostingKind.Construction) ||
                post.State == PostingState.Held && (!Accounts.TryGetValue(post.Budget, out var account) || !account.Pair.Contains(post.Actor)))
                throw new InvalidDataException("Invalid family budget posting.");
        }
        foreach (var account in Accounts.Values)
        {
            var held = Postings.Values.Where(post => post.Budget == account.Id && post.State == PostingState.Held);
            if (held.Where(post => post.Amount < 0).Sum(post => -(long)post.Amount) > account.Balance ||
                held.Where(post => post.Amount > 0).Sum(post => (long)post.Amount) + account.Balance > int.MaxValue)
                throw new InvalidDataException("Invalid family budget holds.");
        }

        bool ValidPair(Pair? pair) => pair is not null && pair.First < pair.Second &&
            Personal.ContainsKey(pair.First) && Personal.ContainsKey(pair.Second);
    }

    internal BudgetBook ObservePersonal(long player, int balance)
    {
        if (balance < 0 || !Personal.ContainsKey(player) || ForPlayer(player) is not null)
            throw new InvalidOperationException("Expected a personal wallet.");
        return this with { Revision = Guid.NewGuid(), Personal = Personal.SetItem(player, balance) };
    }

    internal int Available(Guid account, long actor)
    {
        var budget = GetAccount(account, actor);
        return budget.Balance - Postings.Values
            .Where(post => post.Budget == account && post.State == PostingState.Held && post.Amount < 0)
            .Sum(post => -post.Amount);
    }

    internal BudgetBook Propose(Guid id, Pair pair, long actor, ChangeKind kind, bool shareHome, bool separateWallets)
    {
        if (id == Guid.Empty || UsedProposals.Contains(id) || !shareHome || !separateWallets ||
            !Enum.IsDefined(kind) || !pair.Contains(actor) || pair.First >= pair.Second ||
            !Personal.ContainsKey(pair.First) || !Personal.ContainsKey(pair.Second) ||
            Proposals.Values.Any(offer => offer.Pair.Contains(pair.First) || offer.Pair.Contains(pair.Second)))
            throw new InvalidOperationException("The residents cannot request this budget change.");

        var first = ForPlayer(pair.First);
        var second = ForPlayer(pair.Second);
        if (kind == ChangeKind.Merge && (first is not null || second is not null) ||
            kind == ChangeKind.Split && (first is null || first.Pair != pair || first != second))
            throw new InvalidOperationException("The residents' accounts do not match the requested change.");
        var quote = kind == ChangeKind.Split ? SplitQuote.Half(first!.Balance) : null;
        return this with
        {
            Revision = Guid.NewGuid(),
            Proposals = Proposals.Add(id, new Proposal(id, pair, actor, Day, kind, quote)),
            UsedProposals = UsedProposals.Add(id)
        };
    }

    internal BudgetBook Answer(Guid id, long actor, bool accept)
    {
        if (!Proposals.TryGetValue(id, out var offer) || !offer.Pair.Contains(actor) || actor == offer.Sender)
            throw new InvalidOperationException("Only the other resident can answer.");
        if (offer.Accepted)
            return this;
        return this with
        {
            Revision = Guid.NewGuid(),
            Proposals = accept ? Proposals.SetItem(id, offer with { Accepted = true }) : Proposals.Remove(id)
        };
    }

    internal BudgetBook Cancel(Guid id, long actor)
    {
        if (!Proposals.TryGetValue(id, out var offer) || !offer.Pair.Contains(actor))
            throw new InvalidOperationException("Only the residents can cancel.");
        return this with { Revision = Guid.NewGuid(), Proposals = Proposals.Remove(id) };
    }

    internal BudgetBook Post(Guid id, Guid account, long actor, int amount, PostingKind kind)
    {
        var requested = new Posting(id, account, actor, amount, kind, PostingState.Held);
        if (Postings.TryGetValue(id, out var old))
        {
            if (old with { State = PostingState.Held } != requested)
                throw new InvalidOperationException("Transaction ID already refers to a different action.");
            return this;
        }

        var budget = GetAccount(account, actor);
        bool isSpend = kind is PostingKind.Purchase or PostingKind.Construction;
        if (id == Guid.Empty || !Enum.IsDefined(kind) || amount == 0 || (amount < 0) != isSpend)
            throw new InvalidOperationException("Invalid transaction.");
        if (isSpend && -(long)amount > Available(account, actor))
            throw new InvalidOperationException("Insufficient available funds.");

        if (!isSpend)
        {
            long pending = Postings.Values
                .Where(post => post.Budget == account && post.State == PostingState.Held && post.Amount > 0)
                .Sum(post => (long)post.Amount);
            if (budget.Balance + pending + amount > int.MaxValue)
                throw new InvalidOperationException("The account cannot hold this credit.");
        }

        return this with { Revision = Guid.NewGuid(), Postings = Postings.Add(id, requested) };
    }

    // The game adapter must establish the outcome from an authenticated, durable action receipt.
    internal BudgetBook Resolve(ActionReceipt receipt)
    {
        if (!Postings.TryGetValue(receipt.Operation, out var post) || post.Actor != receipt.Actor ||
            post.Budget != receipt.Budget ||
            receipt.Outcome is not (PostingState.Applied or PostingState.Reversed))
            throw new InvalidOperationException("The action outcome cannot be matched to a transaction.");
        if (post.State != PostingState.Held)
        {
            if (post.State != receipt.Outcome)
                throw new InvalidOperationException("The outcome conflicts with an existing receipt.");
            return this;
        }

        var budget = GetAccount(post.Budget, post.Actor);
        return this with
        {
            Revision = Guid.NewGuid(),
            Postings = Postings.SetItem(post.Id, post with { State = receipt.Outcome }),
            Accounts = receipt.Outcome == PostingState.Applied
                ? Accounts.SetItem(budget.Id, budget with { Balance = checked(budget.Balance + post.Amount) })
                : Accounts
        };
    }

    internal bool MayLeave(long actor) =>
        ForPlayer(actor) is null && !Proposals.Values.Any(offer => offer.Pair.Contains(actor));

    internal Checkpoint PrepareNight(Guid checkpoint, Func<Pair, bool> stillShareHome,
        Func<Pair, bool> economyIdle, bool separateWallets)
    {
        if (checkpoint == Guid.Empty)
            throw new ArgumentException("A checkpoint ID is required.", nameof(checkpoint));

        var next = this with { Day = checked(Day + 1), Revision = Guid.NewGuid(), LastCheckpoint = checkpoint };
        foreach (var offer in Proposals.Values)
        {
            if (!offer.Accepted || !stillShareHome(offer.Pair))
            {
                next = next with { Proposals = next.Proposals.Remove(offer.Id) };
                continue;
            }
            if (!separateWallets || !economyIdle(offer.Pair))
                continue;

            var pair = offer.Pair;
            if (offer.Kind == ChangeKind.Merge)
            {
                long combined = (long)next.Personal[pair.First] + next.Personal[pair.Second];
                if (combined > int.MaxValue)
                    continue;
                next = next with
                {
                    Personal = next.Personal.SetItem(pair.First, 0).SetItem(pair.Second, 0),
                    Accounts = next.Accounts.Add(offer.Id, new Budget(offer.Id, pair, (int)combined)),
                    Proposals = next.Proposals.Remove(offer.Id)
                };
            }
            else if (next.ForPlayer(pair.First) is { } budget &&
                !next.Postings.Values.Any(post => post.Budget == budget.Id && post.State == PostingState.Held))
            {
                var shares = SplitQuote.Half(budget.Balance);
                if (shares != offer.Quote)
                {
                    // An exact quote changed before the save. A fresh proposal needs both residents again.
                    next = next with { Proposals = next.Proposals.Remove(offer.Id) };
                    continue;
                }
                next = next with
                {
                    Personal = next.Personal.SetItem(pair.First, shares.First).SetItem(pair.Second, shares.Second),
                    Accounts = next.Accounts.Remove(budget.Id),
                    Proposals = next.Proposals.Remove(offer.Id)
                };
            }
        }
        return new Checkpoint(checkpoint, next.Day, Revision, next);
    }

    internal BudgetBook ConfirmSave(Checkpoint prepared, SaveReceipt receipt)
    {
        if (Farm != receipt.Farm || prepared.Next.Farm != Farm ||
            prepared.Id != receipt.Checkpoint || prepared.Next.LastCheckpoint != receipt.Checkpoint ||
            prepared.Day != receipt.Day || prepared.Next.Day != receipt.Day)
            throw new InvalidOperationException("The save receipt names another farm or checkpoint.");
        if (receipt.Day == Day && LastCheckpoint == receipt.Checkpoint)
            return this;
        if (receipt.Day != Day + 1 || prepared.SourceRevision != Revision)
            throw new InvalidOperationException("The prepared checkpoint does not follow this saved state.");
        return prepared.Next;
    }

    private Budget GetAccount(Guid id, long actor) =>
        Accounts.TryGetValue(id, out var account) && account.Pair.Contains(actor)
            ? account : throw new InvalidOperationException("This player cannot access that family budget.");
}
