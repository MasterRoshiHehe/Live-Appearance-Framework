using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Makes position queries see the event actor, not the overworld NPC, while an event actor picks its outfit.
    ///
    /// An event actor is a separate NPC object with the same name. Queries that look an NPC up by name
    /// (BETAS <c>NPC_NEAR_AREA</c>/<c>NPC_NEAR_NPC</c>, Positional Audio <c>NPC_POSITION</c>...) find the overworld NPC,
    /// who is still wherever her schedule put her. For example, an outfit for "Haley is in her room" stays true for the
    /// actor during an event in the Forest, because the real Haley is still in her room.
    ///
    /// While an event actor runs <see cref="NPC.ChooseAppearance"/>, this makes the overworld NPC report the actor's
    /// tile if both are in the same map, or a tile outside every map if not. Only the NPC's local position caches
    /// (plain private fields that recompute when she moves) are changed, never her synced position or location, and
    /// they're reset right after. Nothing is sent to other players and nothing is saved.
    /// </summary>
    internal static class EventActorPositions
    {
        /// <summary>A tile no map has.</summary>
        private static readonly Point Nowhere = new(-10000, -10000);

        private static readonly AccessTools.FieldRef<Character, Point> CachedTilePoint = AccessTools.FieldRefAccess<Character, Point>("cachedTilePoint");
        private static readonly AccessTools.FieldRef<Character, Vector2> CachedTile = AccessTools.FieldRefAccess<Character, Vector2>("cachedTile");
        private static readonly AccessTools.FieldRef<Character, Point> CachedStandingPixel = AccessTools.FieldRefAccess<Character, Point>("cachedStandingPixel");
        private static readonly AccessTools.FieldRef<Character, Vector2> TilePointFor = AccessTools.FieldRefAccess<Character, Vector2>("pixelPositionForCachedTilePoint");
        private static readonly AccessTools.FieldRef<Character, Vector2> TileFor = AccessTools.FieldRefAccess<Character, Vector2>("pixelPositionForCachedTile");
        private static readonly AccessTools.FieldRef<Character, Vector2> StandingPixelFor = AccessTools.FieldRefAccess<Character, Vector2>("pixelPositionForCachedStandingPixel");

        /// <summary>The value the game uses to mark a cache as stale.</summary>
        private static readonly Vector2 Stale = new(-2.1474836E+09f);

        /// <summary>What the prefix changed, undone by the finalizer.</summary>
        internal sealed class State
        {
            public NPC Twin;
            public NPC PreviousActor;
            public string PreviousEventId;
            public bool SetActor;
        }

        /// <summary>The event actor whose outfit is being chosen right now (inside <see cref="NPC.ChooseAppearance"/>), or null.</summary>
        public static NPC DressingActor { get; private set; }

        /// <summary>The ID of the event that actor belongs to (the current event when its outfit is chosen).</summary>
        public static string DressingEventId { get; private set; }

        private static IMonitor Monitor;
        private static Func<ModConfig> GetConfig;
        private static bool LoggedError;

        public static void Apply(Harmony harmony, IMonitor monitor, Func<ModConfig> getConfig)
        {
            Monitor = monitor;
            GetConfig = getConfig;

            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.ChooseAppearance)),
                prefix: new HarmonyMethod(typeof(EventActorPositions), nameof(Before_ChooseAppearance)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(EventActorPositions), nameof(Finally_ChooseAppearance))
            );
        }

        private static void Before_ChooseAppearance(NPC __instance, out State __state)
        {
            __state = null;
            try
            {
                if (__instance == null || !__instance.EventActor || !Context.IsWorldReady)
                    return;

                // Always: tell the EVENT_ACTOR query who is being dressed (restored by the finalizer, nesting-safe).
                __state = new State { PreviousActor = DressingActor, PreviousEventId = DressingEventId, SetActor = true };
                DressingActor = __instance;
                DressingEventId = Game1.CurrentEvent?.id;

                ModConfig config = GetConfig?.Invoke();
                if (config == null || !config.Enabled || !config.EventActorsUseOwnPosition)
                    return;

                NPC twin = Game1.getCharacterFromName(__instance.Name, mustBeVillager: false);
                if (twin == null || ReferenceEquals(twin, __instance) || twin.EventActor)
                    return;

                bool sameMap = twin.currentLocation != null && ReferenceEquals(twin.currentLocation, __instance.currentLocation);
                Point tile = sameMap ? __instance.TilePoint : Nowhere;
                Point pixel = sameMap ? __instance.StandingPixel : new Point(Nowhere.X * Game1.tileSize, Nowhere.Y * Game1.tileSize);
                Vector2 position = twin.Position;

                CachedTilePoint(twin) = tile;
                CachedTile(twin) = new Vector2(tile.X, tile.Y);
                CachedStandingPixel(twin) = pixel;
                TilePointFor(twin) = position;
                TileFor(twin) = position;
                StandingPixelFor(twin) = position;
                __state.Twin = twin;
            }
            catch (Exception ex)
            {
                LogError(ex);
                if (__state?.Twin != null)
                    Reset(__state.Twin);
                if (__state != null)
                    __state.Twin = null;
            }
        }

        private static Exception Finally_ChooseAppearance(Exception __exception, State __state)
        {
            if (__state != null)
            {
                try
                {
                    if (__state.Twin != null)
                        Reset(__state.Twin);
                    if (__state.SetActor)
                    {
                        DressingActor = __state.PreviousActor;
                        DressingEventId = __state.PreviousEventId;
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            }
            return __exception;
        }

        /// <summary>Mark the caches stale so the game recomputes them from her real position on next read.</summary>
        private static void Reset(NPC twin)
        {
            TilePointFor(twin) = Stale;
            TileFor(twin) = Stale;
            StandingPixelFor(twin) = Stale;
        }

        private static void LogError(Exception ex)
        {
            if (LoggedError)
                return;
            LoggedError = true;
            Monitor?.Log($"Couldn't redirect position queries to an event actor (logged once): {ex}", LogLevel.Trace);
        }
    }
}
