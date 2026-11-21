using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace StokHouse;

public sealed partial class ModEntry
{
    internal static class GamePatches
    {
        internal enum PatchKind { Prefix, Postfix, Transpiler }

        private static ModEntry mod = null!;
        private static GameApi game = null!;

        internal static int Apply(Harmony harmony, GameApi api, ModEntry owner)
        {
            mod = owner;
            game = api;

            // Resolve every target before changing any game method.
            var patches = Resolve(api);
            foreach (var (original, handler, kind) in patches)
            {
                var patch = new HarmonyMethod(handler);
                try
                {
                    harmony.Patch(original, prefix: kind == PatchKind.Prefix ? patch : null,
                        postfix: kind == PatchKind.Postfix ? patch : null,
                        transpiler: kind == PatchKind.Transpiler ? patch : null);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not apply {handler.Name} to {GameReflection.Signature(original)}.", ex);
                }
            }
            return patches.Count;
        }

        internal static IReadOnlyList<(MethodInfo Original, MethodInfo Handler, PatchKind Kind)> Resolve(GameApi api)
        {
            var patches = new List<(MethodInfo, MethodInfo, PatchKind)>();
            Patch(api.Building, "draw", DrawCabin, false, typeof(SpriteBatch));
            Patch(api.Building, "drawBackground", DrawCabin, false, typeof(SpriteBatch));
            Patch(api.Building, "drawShadow", DrawCabin, false, typeof(SpriteBatch), typeof(int), typeof(int));
            Patch(api.Building, "drawInConstruction", DrawCabin, false, typeof(SpriteBatch));
            Patch(api.Building, "isTilePassable", CabinPassable, false, typeof(Vector2));
            Patch(api.Building, "intersects", CabinDoesNotIntersect, false, typeof(Rectangle));
            Patch(api.Building, "doAction", CabinDoesNotIntersect, false, typeof(Vector2), api.Farmer);
            Patch(api.Building, "isActionableTile", CabinDoesNotIntersect, false, typeof(int), typeof(int), api.Farmer);
            Patch(api.Building, "doesTileHaveProperty", CabinTileProperty, false,
                typeof(int), typeof(int), typeof(string), typeof(string), typeof(string).MakeByRefType());
            Patch(api.Location, "destroyStructure", ProtectDemolition, false, api.Building);
            Patch(api.Farmer, "getMailboxPosition", MailIndicator, postfix: true);
            Patch(api.Location, "performAction", MailAction, false, typeof(string[]), api.Farmer, api.Tile);
            Type choices = api.Response.MakeArrayType();
            PatchQuestionRenderer(typeof(string), choices);
            PatchQuestionRenderer(typeof(string), choices, typeof(int));
            Patch(api.Location, "answerDialogueAction", RobinAnswer, false, typeof(string), typeof(string[]));
            Patch(api.Furniture, "canBeRemoved", FurnitureRemove, false, api.Farmer);
            Patch(api.Bed, "canBeRemoved", FurnitureRemove, false, api.Farmer);
            Patch(api.Bed, "CanModifyBed", BedPermission, false, api.Farmer);
            Patch(api.Furniture, "placementAction", FurniturePlace, false,
                api.Location, typeof(int), typeof(int), api.Farmer);
            Patch(api.Wallpaper, "placementAction", FurniturePlace, false,
                api.Location, typeof(int), typeof(int), api.Farmer);
            foreach (string method in new[] { "MergeWallets", "SeparateWallets" })
            {
                var original = GameReflection.Method(api.ManorHouse, method, BindingFlags.Static | BindingFlags.DeclaredOnly);
                patches.Add((original, ((Func<bool>)WalletTransition).Method, PatchKind.Prefix));
            }

            return patches;

            void Patch(Type type, string method, Delegate handler, bool postfix, params Type[] parameters)
            {
                var original = GameReflection.Method(type, method,
                    BindingFlags.Instance | BindingFlags.DeclaredOnly, parameters);
                patches.Add((original, handler.Method, postfix ? PatchKind.Postfix : PatchKind.Prefix));
            }

            void PatchQuestionRenderer(params Type[] parameters)
            {
                var original = GameReflection.Method(api.Game, "drawObjectQuestionDialogue",
                    BindingFlags.Static | BindingFlags.DeclaredOnly, parameters);
                patches.Add((original, ((Func<IEnumerable<CodeInstruction>, MethodBase, IEnumerable<CodeInstruction>>)RobinChoiceArgument).Method,
                    PatchKind.Transpiler));
            }
        }

        private static bool WalletTransition()
        {
            if (!mod.active || !Context.IsWorldReady || !Context.IsMainPlayer || !mod.PaymentWalletHeld())
                return true;
            // Keep the same wallet reachable until the held gold is settled. Vanilla retries next night.
            mod.LogRobin("wallet conversion deferred while an upgrade holds gold");
            return false;
        }

        private static bool Hidden(object building) => mod.Running &&
            game.CabinOwner(building) is var id && id != 0 && mod.households.IsGuest(id);

        private static bool DrawCabin(object __instance) => !Hidden(__instance);

        private static bool CabinPassable(object __instance, ref bool __result)
        {
            if (!Hidden(__instance))
                return true;
            __result = true;
            return false;
        }

        private static bool CabinDoesNotIntersect(object __instance, ref bool __result)
        {
            if (!Hidden(__instance))
                return true;
            __result = false;
            return false;
        }

        private static bool CabinTileProperty(object __instance, string __2, ref bool __result) =>
            __2 == "NoSpawn" || CabinDoesNotIntersect(__instance, ref __result);

        private static bool ProtectDemolition(object __0, ref bool __result)
        {
            if (!mod.Running || !mod.households.ProtectCabin(game.CabinOwner(__0)))
                return true;
            mod.Tell("protect-cabin");
            __result = false;
            return false;
        }

        private static void MailIndicator(object __instance, ref Point __result)
        {
            if (mod.Running && GameApi.Id(__instance) == game.LocalId && mod.households.IsGuest(game.LocalId) &&
                mod.Homes.TryGetValue(mod.households.Home(game.LocalId), out var home))
                __result = game.Mailbox(home);
        }

        private static bool MailAction(object __instance, string[] __0, object __1, object __2, ref bool __result)
        {
            if (!mod.Running || __0 is null || __0.Length == 0 || __0[0] != "Mailbox" ||
                GameApi.Id(__1) != game.LocalId || !ReferenceEquals(__instance, game.Farm))
                return true;
            long local = game.LocalId;
            Point clicked = GameApi.TilePoint(__2);
            long owner = mod.households.Home(local);
            if (mod.Homes.TryGetValue(owner, out var home) && clicked == game.Mailbox(home) &&
                mod.households.IsGuest(local))
            {
                game.OpenMailbox(__instance);
                __result = true;
                return false;
            }

            if (mod.Homes.Values.Any(other => other.Owner != owner && clicked == game.Mailbox(other)))
            {
                mod.Tell("other-mail");
                __result = true;
                return false;
            }
            return true;
        }

        private static IEnumerable<CodeInstruction> RobinChoiceArgument(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            // Harmony 2.2.2 doesn't write a replacement reference from __args back to this by-value argument.
            var edit = ((Func<Array, Array>)RobinChoices).Method;
            foreach (var instruction in ReplaceChoiceArgument(__originalMethod, edit))
                yield return instruction;
            foreach (var instruction in instructions)
                yield return instruction;
        }

        internal static IEnumerable<CodeInstruction> ReplaceChoiceArgument(MethodBase renderer, MethodInfo edit)
        {
            yield return new(OpCodes.Ldarg_1);
            yield return new(OpCodes.Call, edit);
            yield return new(OpCodes.Castclass, renderer.GetParameters()[1].ParameterType);
            yield return new(OpCodes.Starg_S, (byte)1);
        }

        private static Array RobinChoices(Array choices)
        {
            if (!mod.Running)
                return choices;
            try
            {
                if (game.Current is not { } location || !mod.households.IsGuest(game.LocalId) ||
                    !Equals(GameApi.Read(location, "lastQuestionKey"), "carpenter"))
                    return choices;
                var keys = choices.Cast<object>().Select(GameApi.ResponseKey).ToArray();
                if (!keys.Contains("Shop") || !keys.Contains("Construct"))
                    return choices;

                var quote = mod.GetRobinQuote(location, out string? refusal);
                mod.LogRobin($"menu [{string.Join(", ", keys)}] player={game.LocalId} " +
                    (quote is not null ? $"shared owner={quote.Owner} level={quote.Level}" : $"vanilla reason={refusal}"));
                return quote is null ? choices : game.WithSharedUpgrade(choices, mod.T("robin-choice"));
            }
            catch (Exception ex)
            {
                mod.Monitor.Log($"Stok House left Robin's menu unchanged after an error: {ex}", LogLevel.Error);
                return choices;
            }
        }

        private static bool RobinAnswer(object __instance, string __0, ref bool __result)
        {
            if (!RobinDialogue.IsUpgradeAnswer(__0))
                return true;

            RobinQuote? quote = null;
            string? refusal;
            try
            {
                quote = mod.GetRobinQuote(__instance, out refusal);
            }
            catch (Exception ex)
            {
                refusal = "upgrade-error";
                mod.Monitor.Log($"Stok House could not check Robin's answer {__0}: {ex}", LogLevel.Error);
            }

            bool handled = RobinDialogue.TryHandle(__0, refusal,
                () => mod.dialogs.Enqueue(() => mod.ShowRobinQuote(quote!)), code => mod.Tell(code));
            mod.LogRobin($"answer {__0}; route={(handled ? quote is null ? "feedback" : "shared quote" : "vanilla")}; " +
                $"reason={refusal ?? "available"}");
            if (handled)
                __result = true;
            return !handled;
        }

        private static bool CanDecorate(object location, object farmer, out bool delegated)
        {
            delegated = false;
            if (!mod.Running || !game.FarmHouse.IsInstanceOfType(location) || GameApi.Id(farmer) != game.LocalId)
                return true;
            long owner = (long)GameApi.Read(location, "OwnerId")!;
            if (owner == 0 || owner == game.LocalId || !mod.households.Guests(owner).Any())
                return true;
            delegated = mod.households.Home(game.LocalId) == owner;
            return delegated && mod.households.Rules(owner).Decorate;
        }

        private static bool FurniturePlace(object __0, object? __3, ref bool __result)
        {
            if (CanDecorate(__0, __3 ?? game.Player, out _))
                return true;
            mod.Tell("decor-denied");
            __result = false;
            return false;
        }

        private static bool FurnitureRemove(object? __0, ref bool __result)
        {
            if (!mod.Running || CanDecorate(game.Current, __0 ?? game.Player, out _))
                return true;
            __result = false;
            return false;
        }

        private static bool BedPermission(object? __0, ref bool __result)
        {
            if (!mod.Running || __0 is null)
                return true;
            bool allowed = CanDecorate(game.Current, __0, out bool delegated);
            if (allowed && !delegated)
                return true;
            __result = allowed;
            return false;
        }
    }

    private sealed record RobinQuote(long Owner, int Level);

    private RobinQuote? GetRobinQuote(object location, out string? refusal)
    {
        if (!Running)
            refusal = fault ? "state-fault" : "not-ready";
        else if (!households.IsGuest(game!.LocalId))
            refusal = "upgrade-unavailable";
        else if (GameApi.LocationName(location) != "ScienceHouse" || !game.IsAtRobin(game.Player))
            refusal = "robin-unavailable";
        else
        {
            long id = households.Home(game.LocalId);
            if (paymentFault || CurrentPayment()?.Paused == true)
                refusal = "upgrade-recovery";
            else if (households.Rules(id).Upgrade == Core.UpgradePolicy.Disabled)
                refusal = "upgrade-disabled";
            else if (!IsOnline(id) || !SameVersion(id))
                refusal = "upgrade-owner-offline";
            else if (!AllPeersReady())
                refusal = "peer-missing";
            else if (!Homes.TryGetValue(id, out var home) || GameApi.Read(home.Interior, "owner") is not { } owner)
                refusal = "upgrade-unavailable";
            else
            {
                int level = GameApi.Level(owner);
                refusal = game.UpgradeBlock(owner, level);
                if (refusal is null)
                    return new RobinQuote(id, level);
            }
        }
        return null;
    }

    private void ShowRobinQuote(RobinQuote selected)
    {
        try
        {
            var quote = GetRobinQuote(game!.Current, out string? refusal);
            if (quote != selected)
            {
                string code = refusal ?? "upgrade-quote-changed";
                LogRobin($"quote refused owner={selected.Owner} level={selected.Level} reason={code}");
                Tell(code);
                return;
            }
            string text = Helper.Translation.Get("robin-quote", new
            {
                player = game.Name(selected.Owner), price = Quote(selected.Level)
            }).ToString();
            LogRobin($"quote displayed owner={selected.Owner} level={selected.Level}");
            Confirm(text, yes =>
            {
                LogRobin($"quote answered owner={selected.Owner} level={selected.Level} accepted={yes}");
                if (yes)
                    Submit("upgrade", player: selected.Owner, upgradeLevel: selected.Level);
            });
        }
        catch (Exception ex)
        {
            Monitor.Log($"Stok House could not open Robin's quote for owner={selected.Owner}: {ex}", LogLevel.Error);
            Tell("upgrade-error");
        }
    }

    private void LogRobin(string message) => Monitor.Log($"Robin: {message}", LogLevel.Trace);
}
