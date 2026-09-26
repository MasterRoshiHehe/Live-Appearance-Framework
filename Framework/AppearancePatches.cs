using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Characters;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// A prefix/postfix on <see cref="NPC.ChooseAppearance"/> so that no caller can break an NPC's running animation.
    ///
    /// Other code re-picks appearances at any moment: vanilla on location changes, APF when a conversation starts or a
    /// CarryOver morning ends, BETAS' UpdateAppearance, other mods. Vanilla's method then (a) may load a sheet that
    /// lacks the frames of the animation she's playing, so the game resets her to frame 0 (standing, facing down), and
    /// (b) always resets the sprite size, which breaks wider/taller animation frames. With this patch, while an NPC
    /// animates, the new textures are still applied, but:
    /// <list type="bullet">
    ///   <item>if the new sheet lacks her animation frames, the fallback sheet is kept until the animation ends
    ///   (the outfit then follows normally);</item>
    ///   <item>her current frame, sprite size and source rectangle are kept.</item>
    /// </list>
    /// NPCs that aren't animating are untouched (the prefix only reads three fields).
    /// </summary>
    internal static class AppearancePatches
    {
        private static IMonitor Monitor;
        private static Func<ModConfig> GetConfig;
        private static RefreshEngine Engine;
        private static bool LoggedError;

        /// <summary>What the sprite looked like before ChooseAppearance.</summary>
        internal sealed class SpriteSnapshot
        {
            public AnimatedSprite Sprite;
            public int Frame;
            public int Width;
            public int Height;
            public int TempHeight;
            public bool IgnoreUpdates;
            public Rectangle SourceRect;
            public bool SpecialSize;
        }

        public static void Apply(Harmony harmony, IMonitor monitor, Func<ModConfig> getConfig, RefreshEngine engine)
        {
            Monitor = monitor;
            GetConfig = getConfig;
            Engine = engine;

            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.ChooseAppearance)),
                prefix: new HarmonyMethod(typeof(AppearancePatches), nameof(Before_ChooseAppearance)),
                postfix: new HarmonyMethod(typeof(AppearancePatches), nameof(After_ChooseAppearance))
            );
        }

        private static void Before_ChooseAppearance(NPC __instance, out SpriteSnapshot __state)
        {
            __state = null;
            try
            {
                ModConfig config = GetConfig?.Invoke();
                AnimatedSprite sprite = __instance?.Sprite;
                if (config == null || !config.Enabled || !config.AnimationFrameFallback || sprite == null || !Context.IsWorldReady)
                    return;

                // Only NPCs in the middle of an animation.
                if (!__instance.doingEndOfRouteAnimation.Value && sprite.CurrentAnimation == null)
                    return;

                CharacterData data = __instance.GetData();
                int width = sprite.SpriteWidth;
                int height = SafeAppearance.GetBaseHeight(sprite);
                __state = new SpriteSnapshot
                {
                    Sprite = sprite,
                    Frame = sprite.currentFrame,
                    Width = width,
                    Height = height,
                    TempHeight = sprite.tempSpriteHeight,
                    IgnoreUpdates = sprite.ignoreSourceRectUpdates,
                    SourceRect = sprite.sourceRect,
                    SpecialSize = sprite.ignoreSourceRectUpdates || sprite.tempSpriteHeight != -1
                        || (data != null && (width != data.Size.X || height != data.Size.Y))
                };
            }
            catch (Exception ex)
            {
                LogError(ex);
                __state = null;
            }
        }

        private static void After_ChooseAppearance(NPC __instance, SpriteSnapshot __state)
        {
            if (__state == null)
                return;

            try
            {
                Engine?.AfterChooseAppearance(__instance, __state);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void LogError(Exception ex)
        {
            if (LoggedError)
                return;
            LoggedError = true;
            Monitor?.Log($"Couldn't protect an NPC animation from an appearance change (logged once): {ex}", LogLevel.Trace);
        }
    }
}
