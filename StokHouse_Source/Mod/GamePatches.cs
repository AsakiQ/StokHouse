using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace StokHouse;

public sealed partial class ModEntry
{
    internal static class GamePatches
    {
        private static ModEntry mod = null!;
        private static GameApi game = null!;

        internal static int Apply(Harmony harmony, GameApi api, ModEntry owner)
        {
            mod = owner;
            game = api;

            // Resolve every target before changing any game method.
            var patches = Resolve(api);
            foreach (var (original, handler, postfix) in patches)
            {
                var patch = new HarmonyMethod(handler);
                try
                {
                    harmony.Patch(original, prefix: postfix ? null : patch, postfix: postfix ? patch : null);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not apply {handler.Name} to {GameReflection.Signature(original)}.", ex);
                }
            }
            return patches.Count;
        }

        internal static IReadOnlyList<(MethodInfo Original, MethodInfo Handler, bool Postfix)> Resolve(GameApi api)
        {
            var patches = new List<(MethodInfo, MethodInfo, bool)>();
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
            Patch(api.Location, "createQuestionDialogue", RobinChoices, false,
                typeof(string), api.Response.MakeArrayType(), typeof(string));
            Patch(api.Location, "answerDialogueAction", RobinAnswer, false, typeof(string), typeof(string[]));
            Patch(api.Furniture, "canBeRemoved", FurnitureRemove, false, api.Farmer);
            Patch(api.Bed, "canBeRemoved", FurnitureRemove, false, api.Farmer);
            Patch(api.Bed, "CanModifyBed", BedPermission, false, api.Farmer);
            Patch(api.Furniture, "placementAction", FurniturePlace, false,
                api.Location, typeof(int), typeof(int), api.Farmer);
            Patch(api.Wallpaper, "placementAction", FurniturePlace, false,
                api.Location, typeof(int), typeof(int), api.Farmer);

            return patches;

            void Patch(Type type, string method, Delegate handler, bool postfix, params Type[] parameters)
            {
                var original = GameReflection.Method(type, method,
                    BindingFlags.Instance | BindingFlags.DeclaredOnly, parameters);
                patches.Add((original, handler.Method, postfix));
            }
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

        private static bool RobinChoices(object[] __args)
        {
            if (!mod.Running || (string)__args[2] != "carpenter" || !mod.households.IsGuest(game.LocalId))
                return true;
            __args[1] = game.AppendResponse((Array)__args[1], "StokHouseUpgrade", mod.T("robin-choice"), "Upgrade", "Renovate");
            return true;
        }

        private static bool RobinAnswer(string __0, ref bool __result)
        {
            if (!mod.Running || !mod.households.IsGuest(game.LocalId))
                return true;
            if (__0 is not ("carpenter_StokHouseUpgrade" or "carpenter_Upgrade" or "carpenter_Renovate" or "upgrade_Yes"))
                return true;
            if (__0 == "carpenter_StokHouseUpgrade")
                mod.dialogs.Enqueue(() => mod.ShowRobinQuote());
            __result = true;
            return false;
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

    private void ShowRobinQuote()
    {
        if (!Running || !game!.IsAtRobin(game.Player) ||
            !Homes.TryGetValue(households.Home(game.LocalId), out var home) ||
            GameApi.Read(home.Interior, "owner") is not { } owner ||
            GameApi.Level(owner) is not (0 or 1 or 2))
        {
            Tell("upgrade-unavailable");
            return;
        }
        if (households.Rules(home.Owner).Upgrade == Core.UpgradePolicy.Disabled)
        {
            Tell("upgrade-disabled");
            return;
        }
        string quote = Helper.Translation.Get("robin-quote", new
        {
            player = game.Name(home.Owner), price = Quote(GameApi.Level(owner))
        }).ToString();
        Confirm(quote, yes => { if (yes) Submit("upgrade"); });
    }
}
