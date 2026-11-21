using System.Text.Json;
using StardewModdingAPI;
using StokHouse.FamilyBudget;

namespace StokHouse;

internal sealed record BudgetFrame(long Sequence, long Recipient, bool Ready, long Partner, BudgetView View);

public sealed partial class ModEntry
{
#if FAMILY_BUDGET_PLAYTEST
    internal static bool BudgetBuildEnabled => true;
#else
    internal static bool BudgetBuildEnabled => false;
#endif
    private bool BudgetOptIn => BudgetBuildEnabled && game is not null && config.IsPlaytestFarm(game.FarmIdentity);
    private BudgetSession? budgetSession;
    private BudgetFrame? localBudget;
    private readonly HashSet<long> budgetPeers = new();
    private long budgetSequence;
    private bool budgetFault;
    private bool savedBudgetPresent;

    private void ClearBudgets()
    {
        budgetSession = null;
        localBudget = null;
        budgetPeers.Clear();
        budgetSequence = 0;
        budgetFault = savedBudgetPresent = false;
    }

    private void LoadBudgets()
    {
        try
        {
            string? raw = game!.Load(SavedBudgetProbe.JournalKey);
            if (raw is { Length: > 2_000_000 })
                throw new InvalidDataException("Family journal exceeds the recovery limit.");
            if (string.IsNullOrEmpty(raw) && !BudgetOptIn)
                return;
            ulong farm = game.FarmIdentity;
            if (farm == 0)
                throw new InvalidDataException("The native farm ID is not available.");
            var book = BudgetSession.Load(farm, raw, Homes.Keys);
            savedBudgetPresent = book.Accounts.Count != 0;
            if (book.LastCheckpoint != Guid.Empty && (savedBudgetPresent || BudgetOptIn) && !BudgetSession.VerifyWalletProof(BudgetSavePath(), farm, book))
                throw new InvalidDataException("The loaded family checkpoint disagrees with the saved personal wallets.");
            if (savedBudgetPresent && !BudgetSeparate())
                throw new InvalidDataException("A saved family account requires separate game wallets.");
            FamilyConstruction.VerifySavedOrder(book, CurrentPayment());
            if (!BudgetOptIn)
            {
                if (savedBudgetPresent)
                    BudgetLog("loaded an active playtest journal without playtest enabled; budget house moves and orders are frozen", LogLevel.Warn);
            }
            budgetSession = new BudgetSession(book, farm, BudgetSavePath,
                id => game.Gold(BudgetFarmer(id)), (id, value) => game.SetPersonalGold(BudgetFarmer(id), value),
                () => game.Load(SavedBudgetProbe.JournalKey),
                value => game.Store(SavedBudgetProbe.JournalKey, value ?? ""),
                BudgetPartner, BudgetSeparate, BudgetParticipantsReady);
            if (BudgetOptIn)
                BudgetLog($"loaded day={book.Day} accounts={book.Accounts.Count} proposals={book.Proposals.Count} checkpoint={book.LastCheckpoint}");
        }
        catch (Exception ex)
        {
            budgetFault = true;
            BudgetLog($"cannot load the budget journal; leave the on-disk data intact. {ex}", LogLevel.Error);
        }
    }

    private static string? BudgetSavePath() => Constants.CurrentSavePath is { } dir
        ? Path.Combine(dir, new DirectoryInfo(dir).Name) : null;

    private object BudgetFarmer(long id) => game!.FindFarmer(id) ??
        throw new InvalidOperationException($"Missing farmer wallet for {id}.");

    private bool BudgetSeparate() => GameApi.Separate(game!.Master);

    private bool BudgetPeer(long player) => player == game!.LocalId || budgetPeers.Contains(player);

    private bool BudgetParticipantsReady(Pair pair)
    {
        var order = CurrentPayment();
        bool hasAccount = budgetSession?.Book.ForPlayer(pair.First) is not null;
        return (hasAccount || !game!.WalletChangePending) && IsOnline(pair.First) && IsOnline(pair.Second) &&
            SameVersion(pair.First) && SameVersion(pair.Second) && BudgetPeer(pair.First) && BudgetPeer(pair.Second) &&
            (order is not { Active: true } || !pair.Contains(order.Request.Guest) && !pair.Contains(order.Request.Owner));
    }

    internal static long? BudgetHousemate(Core.Households homes, long player)
    {
        long owner = homes.Home(player);
        var guests = homes.Guests(owner).Take(2).ToArray();
        return guests.Length == 1 ? player == owner ? guests[0] : owner : null;
    }

    private long? BudgetPartner(long player) => BudgetHousemate(households, player);

    private bool BudgetBlocksMove(long player) =>
        budgetFault || budgetSession is { } session && !session.MayLeave(player);

    private bool BudgetBlocksReset() =>
        budgetFault || budgetSession is { } session && (session.Saving || session.Book.Accounts.Count != 0 || session.Book.Proposals.Count != 0);

    private bool BudgetBlocksWalletChange() => budgetFault || savedBudgetPresent || budgetSession is { } session &&
        (session.Saving || session.Book.Accounts.Count != 0 || session.Book.Proposals.Count != 0);

    private void BudgetLog(string message, LogLevel level = LogLevel.Info) =>
        Monitor.Log($"Family budget: {message}", level);

    private void BudgetTick()
    {
        if (!BudgetOptIn || !Context.IsWorldReady || Context.ScreenId != 0)
            return;
        if (Context.IsMainPlayer)
            ResendShopGrant(game!.LocalId);
        else
        {
            Send(new Command(Guid.NewGuid(), "budget-sync"), "command", game!.HostId);
            if (localPurchase is { } purchase)
                Send(purchase.Request, "shop-request", game.HostId);
        }
    }

    private void SendBudgetFrame(long player)
    {
        if (!BudgetOptIn || !BudgetPeer(player) || budgetSession is not { } session)
            return;
        var frame = new BudgetFrame(++budgetSequence, player, !budgetFault && !session.Saving && BudgetSeparate(),
            BudgetPartner(player) ?? 0, session.View(player));
        Deliver(player, frame, "budget-state", ReceiveBudgetFrame);
    }

    private void ReceiveBudgetFrame(BudgetFrame frame)
    {
        if (!BudgetOptIn || frame.Recipient != game!.LocalId || frame.Sequence <= (localBudget?.Sequence ?? 0))
            return;
        bool newOffer = frame.View.Proposal is { Accepted: false } offer && offer.Sender != game.LocalId &&
            offer.Id != localBudget?.View.Proposal?.Id;
        localBudget = frame;
        if (newOffer)
            Tell("budget-new-offer");
    }

    private void RefreshBudgetFrames()
    {
        if (!BudgetOptIn)
            return;
        SendBudgetFrame(game!.LocalId);
        foreach (long peer in budgetPeers.Where(IsOnline))
            SendBudgetFrame(peer);
    }

    private void HandleBudget(long actor, Command command)
    {
        if (!BudgetOptIn || budgetFault || budgetSession is not { } session || session.Saving || !BudgetPeer(actor))
        {
            Notify(actor, "budget-disabled");
            return;
        }
        try
        {
            if (command.Action is "budget-merge" or "budget-split")
            {
                if (BudgetPartner(actor) is not long partner || !BudgetParticipantsReady(Pair.Create(actor, partner)) ||
                    !BudgetSeparate())
                {
                    Notify(actor, "budget-unavailable");
                    return;
                }
                session.Adopt(session.Book.WithPlayers(Homes.Keys));
                session.Propose(actor, command.Operation, command.Action == "budget-merge" ? ChangeKind.Merge : ChangeKind.Split);
            }
            else if (command.Action is "budget-accept" or "budget-decline")
                session.Answer(actor, command.Request, command.Action == "budget-accept");
            else if (command.Action == "budget-cancel")
                session.Cancel(actor, command.Request);
            else
                return;
            BudgetLog($"action={command.Action} sender={actor} request={(command.Request == Guid.Empty ? command.Operation : command.Request)}");
            RefreshBudgetFrames();
        }
        catch (InvalidOperationException ex)
        {
            BudgetLog($"denied sender={actor} action={command.Action}: {ex.Message}", LogLevel.Debug);
            Notify(actor, "budget-unavailable");
            SendBudgetFrame(actor);
        }
    }

    private void PrepareBudgetSave()
    {
        if (!BudgetOptIn || !Running || !Context.IsMainPlayer || budgetFault || budgetSession is not { } session ||
            session.Book.Accounts.Count == 0 && session.Book.Proposals.Count == 0 && session.Book.LastCheckpoint == Guid.Empty)
            return;

        try
        {
            var write = session.PrepareNight();
            BudgetLog($"staged night day={session.Book.Day} checkpoint={write.Checkpoint.Id} walletWrites={write.Replacement.Count}");
            foreach (var (farmer, target) in write.Replacement)
                BudgetLog($"checkpoint={write.Checkpoint.Id} wallet={farmer} from={write.Expected[farmer]} to={target}", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            budgetFault |= session.Saving;
            BudgetLog($"could not stage night; freeze={budgetFault}: {ex}", LogLevel.Error);
        }
    }

    private void CompleteBudgetSave()
    {
        if (!BudgetOptIn || !Context.IsMainPlayer || Context.ScreenId != 0 || budgetSession is not { Saving: true } session)
            return;
        try
        {
            var acceptedSplits = session.Book.Proposals.Values.Where(offer => offer is { Accepted: true, Kind: ChangeKind.Split }).ToArray();
            var outcome = session.CompleteNight();
            budgetFault = outcome == DiskState.Unknown;
            savedBudgetPresent = session.Book.Accounts.Count != 0;
            BudgetLog($"night evidence={outcome} day={session.Book.Day} accounts={session.Book.Accounts.Count} checkpoint={session.Book.LastCheckpoint}",
                budgetFault ? LogLevel.Error : LogLevel.Info);
            RefreshBudgetFrames();
            if (outcome == DiskState.Written)
            {
                foreach (var accepted in acceptedSplits.Where(offer => !session.Book.Proposals.ContainsKey(offer.Id) &&
                    session.Book.ForPlayer(offer.Pair.First) is not null))
                {
                    Notify(accepted.Pair.First, "budget-quote-changed");
                    Notify(accepted.Pair.Second, "budget-quote-changed");
                }
            }
        }
        catch (Exception ex)
        {
            budgetFault = true;
            BudgetLog($"could not reconcile the completed save; budget operations are frozen: {ex}", LogLevel.Error);
        }
    }

    private Guid? UpgradeFamilyAccount(long payer) => budgetSession?.Book.ForPlayer(payer)?.Id;

    private bool BudgetAllowsPayment(UpgradePayment order) => order.FamilyAccount is not Guid account ||
        BudgetOptIn && (Context.IsMainPlayer
            ? !budgetFault && budgetSession is { Saving: false }
            : localBudget is { Ready: true } frame && frame.View.Account == account);

    private string? BudgetUpgradeResources(object payer, long payerId, long owner, int level)
    {
        if (budgetFault)
            return "budget-disabled";
        if (budgetSession?.Book.ForPlayer(payerId) is not { } account)
            return game!.PaymentResources(payer, level);
        if (!BudgetOptIn || !account.Pair.Contains(owner) || budgetSession.Saving ||
            !BudgetPeer(payerId) || !BudgetPeer(owner))
            return "budget-disabled";
        return game!.PaymentResources(payer, level, budgetSession.Book.Available(account.Id, payerId));
    }

    private void AppendBudgetUpdate(PaymentEdit edit, BudgetBook book)
    {
        game!.AddDataEdit(edit, game.Master, SavedBudgetProbe.JournalKey, JsonSerializer.Serialize(book));
    }

    private void AdoptBudgetUpdate(BudgetBook book)
    {
        budgetSession!.Adopt(book);
        RefreshBudgetFrames();
    }

    private void ShowBudgetMenu()
    {
        if (Context.IsMainPlayer)
            SendBudgetFrame(game!.LocalId);
        if (localBudget is not { Ready: true } frame)
        {
            Tell("budget-disabled");
            return;
        }

        var view = frame.View;
        if (frame.Partner == 0 && view.Account is null)
        {
            Tell("budget-no-housemate");
            return;
        }

        string title = view.Available is int balance
            ? Helper.Translation.Get("budget-balance", new { gold = balance.ToString("N0") }).ToString()
            : T("budget-personal");
        if (view.Account is not null)
            title += " " + T("budget-playtest-economy");
        var choices = new List<(string, string)>();
        if (view.Proposal is { } offer)
        {
            var first = offer.Pair.First;
            var second = offer.Pair.Second;
            title += " " + Helper.Translation.Get(offer.Kind == ChangeKind.Merge ? "budget-merge-offer" : "budget-split-offer", new
            {
                player = game!.Name(offer.Sender),
                first = game.Name(first),
                second = game.Name(second),
                firstGold = offer.Quote?.First.ToString("N0"),
                secondGold = offer.Quote?.Second.ToString("N0")
            }).ToString();
            if (offer.Accepted)
                title += " " + T("budget-waits-night");
            if (offer.Sender != game.LocalId && !offer.Accepted)
            {
                choices.Add(("budget-accept", T("accept")));
                choices.Add(("budget-decline", T("decline")));
            }
            else
                choices.Add(("budget-cancel", T("budget-cancel")));
        }
        else
        {
            string action = view.Account is null ? "budget-merge" : "budget-split";
            choices.Add((action, T(action)));
        }
        Ask(title, choices, action => Submit(action, request: view.Proposal?.Id ?? Guid.Empty));
    }

    private void EnableBudgetPlaytest()
    {
        if (!BudgetBuildEnabled || BudgetOptIn)
            return;
        if (!BudgetSeparate())
        {
            Tell("budget-playtest-separate");
            return;
        }

        Confirm(T("budget-playtest-confirm"), yes =>
        {
            if (!yes)
                return;
            bool saved = ModConfig.EnablePlaytest(ConfigPath, game!.FarmIdentity,
                message => Monitor.Log(message, LogLevel.Warn));
            Tell(saved ? "budget-playtest-restart" : "budget-playtest-config-error");
        });
    }
}
