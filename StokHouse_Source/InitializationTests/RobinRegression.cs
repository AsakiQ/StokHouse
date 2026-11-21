using System.Reflection;
using HarmonyLib;
using StardewValley;
using StokHouse.Core;

namespace StokHouse;

internal static class RobinRegression
{
    private static readonly string[] Carpenter = { "carpenter" };
    private static GameApi game = null!;
    private static string? refusal;
    private static Action? deferredQuote;
    private static int Quotes { get; set; }
    private static string? Notice { get; set; }

    internal static void Run(Action<string, Action> test)
    {
        game = new GameApi();
        var location = new GameLocation();
        test("the old Harmony __args write loses the new choice and closes the dialogue without an action", () =>
        {
            WithPatches("legacy", harmony =>
            {
                var question = typeof(GameLocation).GetMethod(nameof(GameLocation.createQuestionDialogue),
                    new[] { typeof(string), typeof(Response[]), typeof(string) })!;
                harmony.Patch(question, prefix: new HarmonyMethod(((Func<object[], bool>)LegacyChoices).Method));
                harmony.Patch(AnswerMethod(), prefix: new HarmonyMethod(((AnswerPatch)LegacyAnswer).Method));

                location.createQuestionDialogue("Robin", Options(), "carpenter");
                Eq("Upgrade", Game1.LastChoices[1].responseKey);
                Eq(true, location.answerDialogueAction("carpenter_" + Game1.LastChoices[1].responseKey, Carpenter));
                Eq<string?>(null, location.LastNativeAnswer);
                Eq(0, Quotes);
                Eq<Action?>(null, deferredQuote);
            });
        });

        test("the exact choice argument is replaced on both renderer paths and opens a deferred quote", () =>
        {
            WithPatches("renderer", harmony =>
            {
                PatchRendererAndAnswer(harmony);
                for (int path = 0; path < 2; path++)
                {
                    if (path == 0)
                        location.createQuestionDialogue("Robin", Options(), "carpenter");
                    else
                        location.createQuestionDialogue("Robin", Options(), "carpenter", new object());
                    Eq(RobinDialogue.Choice, Game1.LastChoices[1].responseKey);
                    Eq("Shared house", Game1.LastChoices[1].responseText);
                    Eq("Renovate", Game1.LastChoices[2].responseKey);
                    Eq(true, location.answerDialogueAction("carpenter_" + Game1.LastChoices[1].responseKey, Carpenter));
                    Eq(path, Quotes);
                    deferredQuote!();
                }
                Eq(2, Quotes);
                Eq<string?>(null, location.LastNativeAnswer);
            });
        });

        test("an available legacy upgrade answer also opens the quote after the dialogue closes", () =>
        {
            WithPatches("legacy-answer", harmony =>
            {
                PatchRendererAndAnswer(harmony);
                Eq(true, location.answerDialogueAction("carpenter_Upgrade", Carpenter));
                Eq(0, Quotes);
                deferredQuote!();
                Eq(1, Quotes);
                Eq<string?>(null, location.LastNativeAnswer);
            });
        });

        test("owners and guests without a shared upgrade keep all vanilla carpenter actions", () =>
        {
            WithPatches("fallback", harmony =>
            {
                PatchRendererAndAnswer(harmony);
                refusal = "upgrade-disabled";
                location.createQuestionDialogue("Robin", Options(), "carpenter");
                Eq("Upgrade", Game1.LastChoices[1].responseKey);
                foreach (string answer in new[] { "carpenter_Upgrade", "carpenter_Renovate", "upgrade_Yes", "carpenter_Shop", "carpenter_Construct" })
                {
                    Eq(true, location.answerDialogueAction(answer, Carpenter));
                    Eq(answer, location.LastNativeAnswer);
                }
                Eq<Action?>(null, deferredQuote);

                // Renovation and a vanilla purchase confirmation still belong to the base game for eligible guests.
                refusal = null;
                foreach (string answer in new[] { "carpenter_Renovate", "upgrade_Yes" })
                {
                    location.answerDialogueAction(answer, Carpenter);
                    Eq(answer, location.LastNativeAnswer);
                }
            });
        });

        test("an already displayed shared choice shows a refusal if the owner becomes unavailable", () =>
        {
            WithPatches("feedback", harmony =>
            {
                PatchRendererAndAnswer(harmony);
                refusal = "upgrade-owner-offline";
                Eq(true, location.answerDialogueAction(RobinDialogue.SharedAnswer, Carpenter));
                Eq("upgrade-owner-offline", Notice);
                Eq<Action?>(null, deferredQuote);
            });
        });

        test("the shared choice is added once even when the technical cabin is already fully upgraded", () =>
        {
            var responses = (Response[])game.WithSharedUpgrade(
                new[] { new Response("Shop", ""), new Response("Construct", ""), new Response("Leave", "") }, "Shared house");
            Eq("Shop,Construct,StokHouseUpgrade,Leave", string.Join(",", responses.Select(x => x.responseKey)));
            Eq(true, ReferenceEquals(responses, game.WithSharedUpgrade(responses, "Shared house")));
        });

        test("owner confirmation applies to confirm mode and separate wallets", () =>
        {
            Eq(true, ModEntry.RequiresOwnerApproval(UpgradePolicy.Confirm, false));
            Eq(true, ModEntry.RequiresOwnerApproval(UpgradePolicy.Confirm, true));
            Eq(false, ModEntry.RequiresOwnerApproval(UpgradePolicy.JointWallet, false));
            Eq(true, ModEntry.RequiresOwnerApproval(UpgradePolicy.JointWallet, true));
        });

        test("automatic grants cannot charge after switching to separate wallets or owner confirmation", () =>
        {
            Eq(true, ModEntry.HasPurchaseApproval(false, UpgradePolicy.JointWallet, false));
            Eq(false, ModEntry.HasPurchaseApproval(false, UpgradePolicy.JointWallet, true));
            Eq(false, ModEntry.HasPurchaseApproval(false, UpgradePolicy.Confirm, false));
            Eq(true, ModEntry.HasPurchaseApproval(true, UpgradePolicy.Confirm, true));
            Eq(false, ModEntry.HasPurchaseApproval(true, UpgradePolicy.Disabled, false));
        });

    }

    private static Response[] Options() => new[] { new Response("Shop", ""), new Response("Upgrade", "Cabin"),
        new Response("Renovate", ""), new Response("Construct", ""), new Response("Leave", "") };

    private static MethodInfo AnswerMethod() => typeof(GameLocation).GetMethod(nameof(GameLocation.answerDialogueAction))!;

    private static void WithPatches(string scenario, Action<Harmony> run)
    {
        Quotes = 0;
        Notice = refusal = null;
        deferredQuote = null;
        var harmony = new Harmony("StokHouse.Tests.Robin." + scenario);
        try { run(harmony); }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void PatchRendererAndAnswer(Harmony harmony)
    {
        var transpiler = new HarmonyMethod(((Func<IEnumerable<CodeInstruction>, MethodBase, IEnumerable<CodeInstruction>>)Renderer).Method);
        harmony.Patch(typeof(Game1).GetMethod(nameof(Game1.drawObjectQuestionDialogue),
            new[] { typeof(string), typeof(Response[]) })!, transpiler: transpiler);
        harmony.Patch(typeof(Game1).GetMethod(nameof(Game1.drawObjectQuestionDialogue),
            new[] { typeof(string), typeof(Response[]), typeof(int) })!, transpiler: transpiler);
        harmony.Patch(AnswerMethod(), prefix: new HarmonyMethod(((AnswerPatch)RouteAnswer).Method));
    }

    private static IEnumerable<CodeInstruction> Renderer(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
        ModEntry.GamePatches.ReplaceChoiceArgument(__originalMethod, ((Func<Array, Array>)Edit).Method).Concat(instructions);

    private static Array Edit(Array choices) => refusal is null ? game.WithSharedUpgrade(choices, "Shared house") : choices;

    private delegate bool AnswerPatch(string __0, ref bool __result);

    private static bool RouteAnswer(string __0, ref bool __result)
    {
        if (!RobinDialogue.TryHandle(__0, refusal, () => deferredQuote = () => Quotes++, reason => Notice = reason))
            return true;
        __result = true;
        return false;
    }

    private static bool LegacyChoices(object[] __args)
    {
        __args[1] = game.WithSharedUpgrade((Array)__args[1], "Shared house");
        return true;
    }

    private static bool LegacyAnswer(string __0, ref bool __result)
    {
        if (__0 == RobinDialogue.SharedAnswer)
            deferredQuote = () => Quotes++;
        __result = true;
        return false;
    }

    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
