using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Keeps NPC animations and outfit sheets compatible.
    ///
    /// Mods like Harem Valley add animation frames only to the NPC's default sheet (e.g. frames 196+ on
    /// Characters/Haley). Outfit sheets from other mods are often smaller (e.g. 64×416 = frames 0–51). If the NPC uses
    /// a frame her sheet doesn't have, vanilla's AnimatedSprite.UpdateSourceRect silently resets currentFrame to 0 and
    /// she just stands there. This class:
    /// <list type="number">
    ///   <item><b>Frame check before a swap:</b> LAF only switches her to a sheet that has every frame she's using right
    ///   now (current frame, the queued animation frames, and her schedule animation). Otherwise the swap waits until
    ///   she's back on frames the new sheet has.</item>
    ///   <item><b>Fallback:</b> if her current sheet already lacks her schedule animation's frames, it shows the winter
    ///   sheet (in winter) or the default sheet until the animation is over.</item>
    ///   <item><b>Orphaned loops:</b> if she walks away while a schedule animation's loop is still set on her sprite
    ///   (e.g. a companion mod recruited her mid-scene), the loop overrides her walking frames every tick. The loop is
    ///   stopped, like vanilla does when a player talks to a moving NPC.</item>
    /// </list>
    /// Cost: a few integer compares per NPC in the player's location per tick; reflection only while one animates.
    /// </summary>
    internal sealed class AnimationFrameGuard
    {
        /// <summary>Ticks without a position change before an NPC counts as standing still again.</summary>
        private const int MovingGraceTicks = 15;

        /// <summary>Ticks of walking with the schedule loop still set before it counts as orphaned (arriving at the spot is 1–2).</summary>
        private const int OrphanMoveTicks = 12;

        /// <summary>Ticks an NPC stands on her animation spot, flagged as animating but with nothing playing, before the loop is restarted.</summary>
        private const int ResumeAfterTicks = 45;

        /// <summary>Vanilla behaviors whose startRouteBehavior does extra setup (sheet size, temporary sprites, attire): never restarted by LAF.</summary>
        private static readonly HashSet<string> SpecialBehaviors = new(StringComparer.OrdinalIgnoreCase)
        {
            "abigail_videogames", "dick_fish", "clint_hammer", "birdie_fish", "change_beach", "change_normal", "penny_dishes"
        };

        private readonly IReflectionHelper Reflection;
        private readonly IMonitor Monitor;
        private readonly HashSet<string> Logged = new(StringComparer.Ordinal);

        public AnimationFrameGuard(IReflectionHelper reflection, IMonitor monitor)
        {
            this.Reflection = reflection;
            this.Monitor = monitor;
        }

        public void Reset()
        {
            this.Logged.Clear();
        }

        /*********
        ** Movement
        *********/
        /// <summary>Record the NPC's position for this tick. Call once per tick for NPCs in the player's location.</summary>
        public void TrackMovement(NPC npc, NpcState state)
        {
            Vector2 position = npc.Position;
            if (!state.MoveTracked || position != state.LastPosition)
            {
                if (state.MoveTracked)
                    state.LastMovedTick = Game1.ticks;
                state.LastPosition = position;
                state.MoveTracked = true;
            }
        }

        /// <summary>
        /// Whether the NPC moved recently. Uses the position, not movement flags or the pathfinding controller:
        /// companion mods often set the position directly, and farmhands have no controller.
        /// </summary>
        public static bool MovedRecently(NpcState state)
        {
            return state.MoveTracked && Game1.ticks - state.LastMovedTick < MovingGraceTicks;
        }

        /*********
        ** Frames
        *********/
        /// <summary>Whether the NPC is really playing her schedule (end-of-route) animation: flagged, and standing still.</summary>
        public static bool IsPlayingRouteAnimation(NPC npc, NpcState state)
        {
            return npc.doingEndOfRouteAnimation.Value
                && !string.IsNullOrEmpty(npc.endOfRouteBehaviorName.Value)
                && !MovedRecently(state)
                && IsOnRouteSpot(npc, state);
        }

        /// <summary>
        /// Whether the NPC stands where her schedule animation started. The game's "doing end-of-route animation" flag
        /// can stay on after another mod takes her away (a companion standing still somewhere else isn't in the scene).
        /// </summary>
        public static bool IsOnRouteSpot(NPC npc, NpcState state)
        {
            return !state.RouteAnchored
                || (npc.TilePoint == state.RouteTile && npc.currentLocation?.NameOrUniqueName == state.RouteLocation);
        }

        /// <summary>Remember where the NPC's schedule animation plays (first time it's seen running with her standing still).</summary>
        public void UpdateRouteAnchor(NPC npc, NpcState state)
        {
            string behavior = npc.endOfRouteBehaviorName.Value;
            if (!npc.doingEndOfRouteAnimation.Value || string.IsNullOrEmpty(behavior))
            {
                state.RouteAnchored = false;
                state.StillNoAnimTicks = 0;
                return;
            }

            if (MovedRecently(state) || npc.Sprite?.CurrentAnimation == null)
                return;

            if (!state.RouteAnchored || state.RouteBehavior != behavior)
            {
                state.RouteAnchored = true;
                state.RouteBehavior = behavior;
                state.RouteTile = npc.TilePoint;
                state.RouteLocation = npc.currentLocation?.NameOrUniqueName;
            }
        }

        /// <summary>
        /// The highest frame the NPC uses right now: her current frame, every frame queued in the sprite's animation, and
        /// (while she plays it) every frame of her schedule animation. -1 if unknown.
        /// </summary>
        public int GetMaxFrameInUse(NPC npc, NpcState state)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite == null)
                return -1;

            bool playing = IsPlayingRouteAnimation(npc, state);
            int max = MaxOfCurrentAnimation(sprite);

            // The current frame only matters while something animates. When nothing does, it's a leftover (vanilla
            // restores a pre-animation frame that can itself be an animation frame) that walking/facing resets anyway.
            if (max >= 0 || playing)
                max = Math.Max(max, sprite.currentFrame);
            if (playing)
                max = Math.Max(max, this.GetMaxRouteFrame(npc, state));
            return max;
        }

        /// <summary>The highest frame of the NPC's schedule animation (intro, loop, outro), or -1.</summary>
        private int GetMaxRouteFrame(NPC npc, NpcState state)
        {
            string behavior = npc.endOfRouteBehaviorName.Value;
            if (string.IsNullOrEmpty(behavior))
                return -1;
            if (state.GuardBehavior == behavior)
                return state.GuardMaxFrame;

            int max = -1;
            if (this.TryGetRouteFrames(npc, out HashSet<int> frames))
            {
                foreach (int frame in frames)
                    max = Math.Max(max, frame);
                state.GuardBehavior = behavior;
                state.GuardMaxFrame = max;
            }
            return max;
        }

        /// <summary>The frames vanilla loaded from Data/animationDescriptions for the NPC's current schedule behavior.</summary>
        private bool TryGetRouteFrames(NPC npc, out HashSet<int> frames)
        {
            frames = new HashSet<int>();
            try
            {
                string behavior = npc.endOfRouteBehaviorName.Value;
                string loaded = this.Reflection.GetField<string>(npc, "loadedEndOfRouteBehavior").GetValue();
                if (string.IsNullOrEmpty(loaded) || (behavior != null && behavior.Length > 0 && !string.Equals(loaded, behavior, StringComparison.Ordinal)))
                    return false;

                foreach (string field in new[] { "routeEndIntro", "routeEndAnimation", "routeEndOutro" })
                {
                    int[] values = this.Reflection.GetField<int[]>(npc, field).GetValue();
                    if (values != null)
                        frames.UnionWith(values);
                }
                return frames.Count > 0;
            }
            catch (Exception ex)
            {
                this.LogOnce("reflection", $"Couldn't read {npc.Name}'s animation frames: {ex.Message}", LogLevel.Trace);
                return false;
            }
        }

        private static int MaxOfCurrentAnimation(AnimatedSprite sprite)
        {
            int max = -1;
            List<FarmerSprite.AnimationFrame> animation = sprite?.CurrentAnimation;
            if (animation != null)
            {
                foreach (FarmerSprite.AnimationFrame frame in animation)
                    max = Math.Max(max, frame.frame);
            }
            return max;
        }

        /// <summary>Whether a texture holds frame <paramref name="maxFrame"/> at the sprite's current frame size.</summary>
        public static bool CanShow(Texture2D texture, AnimatedSprite sprite, int maxFrame)
        {
            if (maxFrame < 0 || texture == null || sprite == null)
                return true;

            int width = sprite.SpriteWidth;
            int height = SafeAppearance.GetBaseHeight(sprite);
            if (width <= 0 || height <= 0)
                return true;

            int perRow = texture.Width / width;
            int rows = texture.Height / height;
            return perRow > 0 && maxFrame < perRow * rows;
        }

        /// <summary>Whether switching the NPC to this sprite asset now keeps every frame she uses right now.</summary>
        public bool AssetCanShow(NPC npc, NpcState state, string assetName)
        {
            int maxFrame = this.GetMaxFrameInUse(npc, state);
            if (maxFrame < 0)
                return true;

            Texture2D texture = TryLoad(assetName);
            return texture == null || CanShow(texture, npc.Sprite, maxFrame);
        }

        /*********
        ** Fallback
        *********/
        /// <summary>
        /// If the NPC plays a schedule animation her current sheet can't show, switch to a sheet that can.
        /// Returns true if a fallback sheet is (still) in use.
        /// </summary>
        public bool ApplyFallback(NPC npc, NpcState state)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite?.spriteTexture == null || !IsPlayingRouteAnimation(npc, state))
                return state.FallbackActive;

            // The current frame is useless here (vanilla already reset it to 0), so use the animation's own frames.
            int maxFrame = Math.Max(this.GetMaxRouteFrame(npc, state), MaxOfCurrentAnimation(sprite));
            if (maxFrame < 0 || CanShow(sprite.spriteTexture, sprite, maxFrame))
                return state.FallbackActive;

            if (this.TryUseFallbackSheet(npc, state, maxFrame))
            {
                RestoreAnimationFrame(sprite);
                return true;
            }
            return state.FallbackActive;
        }

        /// <summary>Show the first candidate sheet that holds <paramref name="maxFrame"/>. Returns whether one was loaded.</summary>
        public bool TryUseFallbackSheet(NPC npc, NpcState state, int maxFrame)
        {
            AnimatedSprite sprite = npc.Sprite;
            string animation = npc.endOfRouteBehaviorName.Value ?? "(animation)";
            foreach (string candidate in GetCandidates(npc))
            {
                Texture2D texture = TryLoad(candidate);
                if (texture == null || !CanShow(texture, sprite, maxFrame))
                    continue;

                string from = AppearanceResolver.GetSpriteAsset(npc);
                if (!npc.TryLoadSprites(candidate, out string error))
                {
                    this.LogOnce($"fail:{npc.Name}:{candidate}", $"{npc.Name}: couldn't load '{candidate}' for '{animation}': {error}", LogLevel.Trace);
                    return false;
                }

                state.FallbackActive = true;
                this.LogOnce(
                    $"use:{npc.Name}:{animation}:{from}",
                    $"{npc.Name}: '{from}' has no frame {maxFrame} for '{animation}', showing '{candidate}' until it ends.",
                    LogLevel.Trace);
                return true;
            }

            this.LogOnce(
                $"none:{npc.Name}:{animation}",
                $"{npc.Name}: no sheet has frame {maxFrame} for '{animation}' (current: '{AppearanceResolver.GetSpriteAsset(npc)}'). The game will show frame 0.",
                LogLevel.Trace);
            return false;
        }

        /// <summary>
        /// Put the sprite back on the frame its running animation is at. When a sheet without the frame was loaded,
        /// vanilla reset currentFrame to 0 (standing, facing down), and while a menu is open nothing advances it again.
        /// </summary>
        public static void RestoreAnimationFrame(AnimatedSprite sprite)
        {
            List<FarmerSprite.AnimationFrame> animation = sprite?.CurrentAnimation;
            if (animation == null || animation.Count == 0)
                return;

            int index = sprite.currentAnimationIndex;
            if (index < 0 || index >= animation.Count)
                return;

            sprite.currentFrame = animation[index].frame;
            sprite.UpdateSourceRect();
        }

        /// <summary>Whether a fallback sheet is no longer needed: nothing animates on the sprite, and she isn't (really) in her schedule animation.</summary>
        public static bool FallbackFinished(NPC npc, NpcState state)
        {
            return state.FallbackActive
                && npc.Sprite?.CurrentAnimation == null
                && !IsPlayingRouteAnimation(npc, state);
        }

        /*********
        ** Orphaned schedule animations
        *********/
        /// <summary>
        /// If the NPC walks away while her sprite still loops her schedule animation (another mod took her out of the
        /// scene without ending it, e.g. a companion recruiter), end the scene the way vanilla does at the end of the
        /// outro (<c>routeEndAnimationFinished</c>: clears the flag and freezeMotion, restores the frame and size). Only
        /// the middle loop counts; intro/outro frames are left to vanilla (the NPC can't walk during those anyway).
        /// </summary>
        public bool EndOrphanedLoop(NPC npc, NpcState state)
        {
            AnimatedSprite sprite = npc.Sprite;
            List<FarmerSprite.AnimationFrame> animation = sprite?.CurrentAnimation;
            if (animation == null || animation.Count == 0 || !sprite.loop)
            {
                state.OrphanMoveTicks = 0;
                return false;
            }
            if (Game1.ticks != state.LastMovedTick)
            {
                // Standing still again (a nudge, not walking): start counting from zero next time.
                if (Game1.ticks - state.LastMovedTick > 5)
                    state.OrphanMoveTicks = 0;
                return false;
            }

            // Arriving at the animation spot moves her for a tick or two while the intro starts: wait for real walking.
            if (++state.OrphanMoveTicks < OrphanMoveTicks)
                return false;
            state.OrphanMoveTicks = 0;

            int[] middle = this.GetRouteFrameArray(npc, "routeEndAnimation");
            if (middle == null || middle.Length == 0)
                return false;
            var loopFrames = new HashSet<int>(middle);
            foreach (FarmerSprite.AnimationFrame frame in animation)
            {
                if (!loopFrames.Contains(frame.frame))
                    return false; // some other animation (another mod's), not ours to touch
            }

            string behavior = npc.endOfRouteBehaviorName.Value;
            try
            {
                this.Reflection.GetMethod(npc, "routeEndAnimationFinished").Invoke(new object[] { null });
            }
            catch (Exception ex)
            {
                sprite.StopAnimation();
                this.LogOnce("finish-reflection", $"Couldn't end {npc.Name}'s animation the vanilla way, stopped it instead: {ex.Message}", LogLevel.Trace);
            }
            state.RouteAnchored = false;
            this.LogOnce(
                $"orphan:{npc.Name}:{behavior}:{Game1.dayOfMonth}",
                $"{npc.Name} walked away while her schedule animation '{behavior}' still looped (another mod took her out of it). Ended it the vanilla way so she walks normally.",
                LogLevel.Trace);
            return true;
        }

        /// <summary>
        /// If the NPC stands on her animation spot, still flagged as doing her schedule animation, but nothing plays
        /// (another mod replaced the loop with its own animation, e.g. a kiss, and didn't restart it), restart the
        /// loop the way vanilla builds it. Waits a moment so other mods' short animations can finish.
        /// </summary>
        public bool ResumeInterruptedLoop(NPC npc, NpcState state, bool talking)
        {
            AnimatedSprite sprite = npc.Sprite;
            string behavior = npc.endOfRouteBehaviorName.Value;
            if (sprite == null || talking || !state.RouteAnchored || sprite.CurrentAnimation != null
                || !IsPlayingRouteAnimation(npc, state) || SpecialBehaviors.Contains(behavior ?? "") || (behavior ?? "").StartsWith("square_"))
            {
                state.StillNoAnimTicks = 0;
                return false;
            }

            if (++state.StillNoAnimTicks < ResumeAfterTicks)
                return false;
            state.StillNoAnimTicks = 0;

            int[] middle = this.GetRouteFrameArray(npc, "routeEndAnimation");
            if (middle == null || middle.Length == 0)
                return false;

            // Same as vanilla's doMiddleAnimation, minus startRouteBehavior (messages/effects already happened once).
            sprite.ClearAnimation();
            foreach (int frame in middle)
                sprite.AddFrame(new FarmerSprite.AnimationFrame(frame, 100, 0, secondaryArm: false, flip: false));
            sprite.loop = true;

            this.LogOnce(
                $"resume:{npc.Name}:{behavior}:{Game1.timeOfDay}",
                $"{npc.Name} stood on her spot for '{behavior}' with nothing playing (another mod interrupted it). Restarted the loop.",
                LogLevel.Trace);
            return true;
        }

        /*********
        ** Event actors
        *********/
        /// <summary>
        /// Event actors are separate NPC objects that start with the outfit sheet the NPC wears. If an event command
        /// shows frames that sheet doesn't have (e.g. Harem Valley event frames that only exist on the default sheet),
        /// the game shows frame 0 instead. This switches the actor to a sheet that has the frames. The actor is
        /// discarded when the event ends, so the switch is never undone mid-event (later commands may rely on it too).
        /// </summary>
        /// <returns>Whether the actor's sheet was switched.</returns>
        public bool EnsureEventActorFrames(NPC actor, int maxFrame)
        {
            AnimatedSprite sprite = actor?.Sprite;
            if (sprite?.spriteTexture == null || maxFrame < 0 || CanShow(sprite.spriteTexture, sprite, maxFrame))
                return false;

            string from = AppearanceResolver.GetSpriteAsset(actor);
            foreach (string candidate in GetCandidates(actor))
            {
                Texture2D texture = TryLoad(candidate);
                if (texture == null || !CanShow(texture, sprite, maxFrame))
                    continue;

                sprite.LoadTexture(candidate, Game1.IsMasterGame);
                this.LogOnce(
                    $"event:{Game1.CurrentEvent?.id}:{actor.Name}:{candidate}",
                    $"Event '{Game1.CurrentEvent?.id}': {actor.Name}'s sheet '{from}' has no frame {maxFrame}, using '{candidate}' for the rest of the event.",
                    LogLevel.Trace);
                return true;
            }

            this.LogOnce(
                $"event-none:{Game1.CurrentEvent?.id}:{actor.Name}:{maxFrame}",
                $"Event '{Game1.CurrentEvent?.id}': no sheet has frame {maxFrame} for {actor.Name} (current: '{from}'). The game will show frame 0.",
                LogLevel.Trace);
            return false;
        }

        /// <summary>Per-tick safety net for event actors animated by commands LAF doesn't patch (other mods' commands).</summary>
        public void GuardEventActors(Event @event)
        {
            if (@event?.actors == null)
                return;

            foreach (NPC actor in @event.actors)
            {
                AnimatedSprite sprite = actor?.Sprite;
                if (sprite?.CurrentAnimation == null)
                    continue;

                int maxFrame = MaxOfCurrentAnimation(sprite);
                if (this.EnsureEventActorFrames(actor, maxFrame))
                    RestoreAnimationFrame(sprite);
            }
        }

        /// <summary>After the NPC's sheet changed while nothing animates: if her frame isn't on the new sheet, show her standing in her facing direction instead of frame 0.</summary>
        public static void NormalizeIdleFrame(NPC npc, int frameBefore)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite?.spriteTexture == null || sprite.CurrentAnimation != null)
                return;
            if (!CanShow(sprite.spriteTexture, sprite, frameBefore))
                sprite.faceDirection(npc.FacingDirection);
        }

        private int[] GetRouteFrameArray(NPC npc, string field)
        {
            try
            {
                return this.Reflection.GetField<int[]>(npc, field).GetValue();
            }
            catch
            {
                return null;
            }
        }

        /*********
        ** Helpers
        *********/
        /// <summary>Sheets that usually carry animation frames added by other mods: the winter sheet in winter, then the default sheet.</summary>
        private static IEnumerable<string> GetCandidates(NPC npc)
        {
            string textureName = npc.getTextureName();
            if (npc.currentLocation?.GetSeason() == Season.Winter)
                yield return $"Characters/{textureName}_Winter";
            yield return $"Characters/{textureName}";
        }

        private static Texture2D TryLoad(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return null;
            try
            {
                return Game1.content.DoesAssetExist<Texture2D>(assetName)
                    ? Game1.content.Load<Texture2D>(assetName)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private void LogOnce(string key, string message, LogLevel level)
        {
            if (this.Logged.Add(key))
                this.Monitor.Log(message, level);
        }
    }
}
