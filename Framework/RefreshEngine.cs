using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>The result of one refresh attempt.</summary>
    internal enum RefreshOutcome
    {
        /// <summary>LAF doesn't handle this NPC right now (no location, overridden textures, opted out...).</summary>
        Skipped,

        /// <summary>Not safe yet (event, dialogue, animation): queued and retried.</summary>
        Deferred,

        /// <summary>The chosen entry didn't change.</summary>
        Unchanged,

        /// <summary>LAF's record was updated; the NPC already had the right textures.</summary>
        Synced,

        /// <summary>The appearance was changed.</summary>
        Applied
    }

    /// <summary>LAF's record of one NPC instance. Shared by all screens (keyed by the NPC object).</summary>
    internal sealed class NpcState
    {
        /// <summary>Whether <see cref="AppliedId"/> is trustworthy.</summary>
        public bool Known;

        /// <summary>The Appearance entry ID LAF last applied or confirmed (null = default textures).</summary>
        public string AppliedId;

        /// <summary><see cref="NPC.LastAppearanceId"/> right after LAF's last apply. If it differs later, something else re-picked.</summary>
        public string SeenVanillaId;

        /// <summary>The NPC's location at LAF's last apply. Vanilla re-picks on location change.</summary>
        public string AppliedLocation;

        // Tile / animation watch record.
        public bool WatchInitialized;
        public string WatchLocation;
        public Point WatchTile;
        public string WatchAnimation;

        // Animation frame guard (see AnimationFrameGuard).
        /// <summary>A fallback sheet is shown because her outfit sheet lacks the frames of her current animation.</summary>
        public bool FallbackActive;
        public string GuardBehavior;
        public int GuardMaxFrame = -1;

        // Movement tracking (position based; see AnimationFrameGuard).
        public bool MoveTracked;
        public Vector2 LastPosition;
        public int LastMovedTick = -1000;
        public int OrphanMoveTicks;

        // Where her schedule animation plays (see AnimationFrameGuard.UpdateRouteAnchor).
        public bool RouteAnchored;
        public string RouteBehavior;
        public string RouteLocation;
        public Point RouteTile;
        public int StillNoAnimTicks;

        // Per-tick cache for the NPC_APPEARANCE query.
        public int ExpectedTick = -1;
        public string ExpectedLocation;
        public string ExpectedId;
    }

    /// <summary>
    /// Decides when to re-check NPC appearances and applies the result safely.
    /// Runs on every client and every split-screen screen, for the NPCs in that screen's location: the textures are
    /// local, and every client reaches the same answer from the same synced inputs.
    /// </summary>
    internal sealed class RefreshEngine
    {
        /*********
        ** Per-screen state
        *********/
        private sealed class ScreenState
        {
            public int LastTime = -1;
            public int ClockChangedTick = -1;
            public bool WarpPending;
            public string RefreshAllTrigger;
            public bool EventWasUp;
            public string TalkingTo;
            public int ScanTimer;
            public int SeenDataVersion;
            public int SeenScheduleVersion;
            public readonly List<(NPC Npc, string Trigger, int NotBefore)> Pending = new();
            public readonly HashSet<NPC> ProcessedThisTick = new(ReferenceEqualityComparer.Instance);
            public int ProcessedTick = -1;
        }

        private readonly PerScreen<ScreenState> Screens = new(() => new ScreenState());
        private ConditionalWeakTable<NPC, NpcState> States = new();

        private readonly IMonitor Monitor;
        private readonly Func<ModConfig> GetConfig;
        private readonly DynamicNpcRegistry Registry;
        private readonly PositionalAudioBridge PositionalAudio;
        private readonly AnimationFrameGuard Guard;
        private readonly ScheduleTracker Schedules;

        /// <summary>Incremented when Data/Characters changes; each screen re-checks its location when it sees a new value.</summary>
        private int DataVersion;

        public event EventHandler<IAppearanceChangedEventArgs> AppearanceChanged;

        private ModConfig Config => this.GetConfig();

        /// <summary>Idle poses (set after construction; null if unavailable).</summary>
        internal IdlePoseManager Poses { get; set; }

        public RefreshEngine(IMonitor monitor, Func<ModConfig> getConfig, DynamicNpcRegistry registry, PositionalAudioBridge positionalAudio, AnimationFrameGuard guard, ScheduleTracker schedules)
        {
            this.Guard = guard;
            this.Schedules = schedules;
            this.Monitor = monitor;
            this.GetConfig = getConfig;
            this.Registry = registry;
            this.PositionalAudio = positionalAudio;
        }

        /*********
        ** Lifecycle
        *********/
        /// <summary>Forget everything (save loaded, returned to title).</summary>
        public void Reset()
        {
            this.States = new ConditionalWeakTable<NPC, NpcState>();
            this.Screens.ResetAllScreens();
            this.Registry.MarkDirty();
            this.PositionalAudio.Reset();
            this.Guard.Reset();
            this.Schedules.Reset();
        }

        public void OnDayStarted()
        {
            // Vanilla re-picked every NPC overnight: forget LAF's records, then re-sync the current location quietly.
            this.States = new ConditionalWeakTable<NPC, NpcState>();
            this.Registry.MarkDirty();
            ScreenState screen = this.Screens.Value;
            screen.Pending.Clear();
            screen.LastTime = Game1.timeOfDay;
            screen.ClockChangedTick = -1;
            screen.RefreshAllTrigger = "DayStarted";
        }

        public void OnWarped()
        {
            // Vanilla doesn't re-pick NPCs who were already standing in the new location, so check them.
            this.Screens.Value.WarpPending = true;
        }

        public void OnCharacterDataChanged()
        {
            this.Registry.MarkDirty();
            this.DataVersion++;
        }

        /// <summary>Re-check every dynamic NPC in this screen's location on the next tick.</summary>
        public void RequestLocationRefresh(string trigger)
        {
            this.Screens.Value.RefreshAllTrigger = trigger;
        }

        /*********
        ** Tick
        *********/
        public void OnUpdateTicked()
        {
            ModConfig config = this.Config;
            if (!Context.IsWorldReady || !config.Enabled)
                return;

            ScreenState screen = this.Screens.Value;
            this.Registry.EnsureFresh(config);

            // Events and festivals: event actors are separate NPCs with their own sprites. Wait, then re-check.
            if (IsEventUp())
            {
                screen.EventWasUp = true;
                if (config.AnimationFrameFallback)
                    this.Guard.GuardEventActors(Game1.CurrentEvent);
                return;
            }
            if (screen.EventWasUp)
            {
                screen.EventWasUp = false;
                screen.RefreshAllTrigger = "EventEnded";
            }

            this.TrackDialogue(screen);

            // Every tick (cheap: only NPCs playing a schedule animation), so a missing frame never shows for long.
            this.GuardAnimations(Game1.currentLocation, config);

            // Clock: detected here instead of in TimeChanged, and handled one tick later, so Content Patcher's
            // "OnTimeChange" edits of Data/Characters are already applied.
            if (Game1.timeOfDay != screen.LastTime)
            {
                if (screen.LastTime != -1)
                    screen.ClockChangedTick = Game1.ticks;
                screen.LastTime = Game1.timeOfDay;
            }
            if (screen.ClockChangedTick >= 0 && Game1.ticks > screen.ClockChangedTick)
            {
                screen.ClockChangedTick = -1;
                if (config.RefreshOnClock)
                    this.RefreshLocation(Game1.currentLocation, "Clock");
            }

            if (screen.SeenDataVersion != this.DataVersion)
            {
                screen.SeenDataVersion = this.DataVersion;
                this.RefreshLocation(Game1.currentLocation, "DataChanged");
            }

            if (screen.WarpPending)
            {
                screen.WarpPending = false;
                this.RefreshLocation(Game1.currentLocation, "Warp");
            }

            if (screen.RefreshAllTrigger != null)
            {
                string trigger = screen.RefreshAllTrigger;
                screen.RefreshAllTrigger = null;
                this.RefreshLocation(Game1.currentLocation, trigger);
            }

            if (--screen.ScanTimer <= 0)
            {
                screen.ScanTimer = Math.Max(1, config.ScanInterval);
                this.CheckSchedules(screen);
                this.ProcessPending(screen);
                this.ScanWatchedNpcs(Game1.currentLocation, config);
            }

            this.PositionalAudio.Flush();
        }

        /// <summary>Remember who this screen's player is talking to. When the box closes, re-check that NPC a little later (after APF gives her real portrait back).</summary>
        private void TrackDialogue(ScreenState screen)
        {
            string speaker = GetCurrentSpeaker();
            if (speaker == screen.TalkingTo)
                return;

            string previous = screen.TalkingTo;
            screen.TalkingTo = speaker;

            if (previous != null)
            {
                NPC npc = Game1.getCharacterFromName(previous);
                if (npc != null && this.Registry.IsDynamic(npc.Name))
                    this.AddPending(screen, npc, "DialogueClosed", Game1.ticks + 2);
            }
        }

        private static string GetCurrentSpeaker()
        {
            return (Game1.activeClickableMenu as DialogueBox)?.characterDialogue?.speaker?.Name;
        }

        private static bool IsEventUp()
        {
            return Game1.eventUp || Game1.CurrentEvent != null;
        }

        /*********
        ** Triggers
        *********/
        /// <summary>Re-check every dynamic NPC in a location.</summary>
        public void RefreshLocation(GameLocation location, string trigger, bool force = false)
        {
            if (location == null)
                return;

            foreach (NPC npc in location.characters.ToList())
            {
                if (npc != null && this.Registry.IsDynamic(npc.Name))
                    this.Refresh(npc, trigger, force);
            }
        }

        /// <summary>
        /// Schedules can be replaced after an outfit was picked (Ginger Island days, save load, schedule edits reloaded
        /// mid-day, other mods). When that happens, re-check NPCs in this screen's location whose conditions use schedule
        /// queries. Stop changes themselves need nothing extra: stops start at clock times, which the clock refresh covers.
        /// </summary>
        private void CheckSchedules(ScreenState screen)
        {
            if (!this.Registry.AnyScheduleSensitive || !ScheduleTracker.CanRead)
                return;

            this.Schedules.DetectChanges();
            if (screen.SeenScheduleVersion == this.Schedules.Version)
                return;
            screen.SeenScheduleVersion = this.Schedules.Version;

            GameLocation location = Game1.currentLocation;
            if (location == null)
                return;
            foreach (NPC npc in location.characters.ToList())
            {
                if (npc != null && this.Registry.TryGet(npc.Name, out DynamicNpcInfo info) && info.ScheduleSensitive)
                    this.Refresh(npc, "ScheduleChanged");
            }
        }

        /// <summary>Look for tile / animation changes of watched NPCs in the location.</summary>
        private void ScanWatchedNpcs(GameLocation location, ModConfig config)
        {
            if (location == null || (!config.RefreshOnTile && !config.RefreshOnAnimation))
                return;

            foreach (NPC npc in location.characters.ToList())
            {
                if (npc == null || !this.Registry.TryGet(npc.Name, out DynamicNpcInfo info))
                    continue;

                bool watchTile = config.RefreshOnTile && info.PositionSensitive;
                bool watchAnimation = config.RefreshOnAnimation && info.AnimationSensitive;
                if (!watchTile && !watchAnimation)
                    continue;

                NpcState state = this.States.GetOrCreateValue(npc);
                string locationName = npc.currentLocation?.NameOrUniqueName;
                Point tile = npc.TilePoint;
                string animation = npc.doingEndOfRouteAnimation.Value ? npc.endOfRouteBehaviorName.Value : null;

                if (!state.WatchInitialized || state.WatchLocation != locationName)
                {
                    // First look, or she changed location (vanilla re-picks then): just record.
                    state.WatchInitialized = true;
                    state.WatchLocation = locationName;
                    state.WatchTile = tile;
                    state.WatchAnimation = animation;
                    continue;
                }

                bool tileChanged = state.WatchTile != tile;
                bool animationChanged = state.WatchAnimation != animation;
                state.WatchTile = tile;
                state.WatchAnimation = animation;

                if (watchAnimation && animationChanged)
                    this.Refresh(npc, "Animation");
                else if (watchTile && tileChanged)
                    this.Refresh(npc, "Tile");
            }
        }

        /// <summary>
        /// Keep schedule animations visible when the outfit sheet lacks their frames (see <see cref="AnimationFrameGuard"/>),
        /// and put the Appearance entry back once such an animation is over.
        /// </summary>
        private void GuardAnimations(GameLocation location, ModConfig config)
        {
            if (location == null || !config.AnimationFrameFallback)
                return;

            List<NPC> finished = null;
            foreach (NPC npc in location.characters)
            {
                if (npc == null || !npc.IsVillager || npc.SimpleNonVillagerNPC || npc.Sprite == null)
                    continue;

                // Idle poses are LAF's own animation: the pose manager looks after them.
                if (this.Poses?.IsPosing(npc) == true)
                    continue;

                // Only NPCs that animate, or that LAF is already helping, need a record.
                bool relevant = npc.doingEndOfRouteAnimation.Value || npc.Sprite.CurrentAnimation != null;
                NpcState state;
                if (!relevant && !(this.States.TryGetValue(npc, out state) && (state.FallbackActive || state.MoveTracked)))
                    continue;
                state = this.States.GetOrCreateValue(npc);

                this.Guard.TrackMovement(npc, state);
                this.Guard.UpdateRouteAnchor(npc, state);
                if (config.RepairInterruptedAnimations)
                {
                    this.Guard.EndOrphanedLoop(npc, state);
                    this.Guard.ResumeInterruptedLoop(npc, state, this.IsTalkingTo(npc));
                }

                if (AnimationFrameGuard.FallbackFinished(npc, state))
                {
                    state.FallbackActive = false;
                    state.GuardBehavior = null;
                    state.GuardMaxFrame = -1;
                    (finished ??= new List<NPC>()).Add(npc);
                    continue;
                }

                // Fallback sheets only for NPCs whose look comes from Appearance entries (that's where sheets without the frames come from).
                if (CanHandle(npc, out _) && npc.GetData()?.Appearance is { Count: > 0 })
                    this.Guard.ApplyFallback(npc, state);

                if (!relevant && !state.FallbackActive)
                    state.MoveTracked = false; // stop tracking once she's back to normal
            }

            if (finished != null)
            {
                foreach (NPC npc in finished)
                    this.Refresh(npc, "AnimationEnded", force: true);
            }
        }

        /// <summary>
        /// Called after any <see cref="NPC.ChooseAppearance"/> on an animating NPC (see <see cref="AppearancePatches"/>):
        /// keep her animation intact whatever sheet was chosen.
        /// </summary>
        internal void AfterChooseAppearance(NPC npc, AppearancePatches.SpriteSnapshot before)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite == null || !ReferenceEquals(sprite, before.Sprite))
                return;

            // 1. Frame size / source rect (vanilla resets them on every call).
            if (before.SpecialSize)
            {
                if (sprite.SpriteWidth != before.Width)
                    sprite.SpriteWidth = before.Width;
                if (SafeAppearance.GetBaseHeight(sprite) != before.Height)
                    sprite.SpriteHeight = before.Height;
                sprite.tempSpriteHeight = before.TempHeight;
                sprite.ignoreSourceRectUpdates = before.IgnoreUpdates;
            }

            // Idle pose: keep it if the new sheet has its frames (back on the frame it was at), else she stands.
            if (this.Poses?.IsPosing(npc) == true)
            {
                if (this.Poses.AfterSheetChanged(npc))
                    AnimationFrameGuard.RestoreAnimationFrame(sprite);
                return;
            }

            // 2. The newly chosen sheet must hold every frame she's using, else keep a sheet that does until it ends.
            //    (-1 = nothing really animates: e.g. the game's flag is stale because another mod led her away.)
            NpcState state = this.States.GetOrCreateValue(npc);
            int inUse = this.Guard.GetMaxFrameInUse(npc, state);
            if (inUse >= 0 && !Game1.eventUp && !npc.spriteOverridden)
            {
                int maxFrame = Math.Max(before.Frame, inUse);
                if (!AnimationFrameGuard.CanShow(sprite.spriteTexture, sprite, maxFrame))
                    this.Guard.TryUseFallbackSheet(npc, state, maxFrame);
            }

            // 3. The frame she was on (vanilla reset it to 0 if the chosen sheet lacked it).
            if (inUse < 0 && !AnimationFrameGuard.CanShow(sprite.spriteTexture, sprite, before.Frame))
            {
                AnimationFrameGuard.NormalizeIdleFrame(npc, before.Frame); // leftover frame the new sheet lacks: stand normally
                return;
            }
            sprite.currentFrame = before.Frame;
            if (before.SpecialSize)
                sprite.sourceRect = before.SourceRect;
            else
                sprite.UpdateSourceRect();
        }

        public bool IsFallbackActive(NPC npc) => npc != null && this.States.TryGetValue(npc, out NpcState state) && state.FallbackActive;

        /// <summary>For laf_why: animation-related state.</summary>
        public string DescribeAnimation(NPC npc)
        {
            if (npc == null || !this.States.TryGetValue(npc, out NpcState state))
                return null;
            string spot = state.RouteAnchored ? $"spot {state.RouteLocation} ({state.RouteTile.X}, {state.RouteTile.Y}), {(AnimationFrameGuard.IsOnRouteSpot(npc, state) ? "on it" : "away from it")}" : "no spot recorded";
            return $"Animation: flag {(npc.doingEndOfRouteAnimation.Value ? "on" : "off")}, {(AnimationFrameGuard.IsPlayingRouteAnimation(npc, state) ? "playing" : "not playing")}, {spot}, "
                + $"{(npc.Sprite?.CurrentAnimation != null ? $"{npc.Sprite.CurrentAnimation.Count} queued frame(s)" : "nothing queued")}, current frame {npc.Sprite?.currentFrame}, highest frame in use {this.Guard.GetMaxFrameInUse(npc, state)}"
                + $"{(AnimationFrameGuard.MovedRecently(state) ? ", moving" : "")}.";
        }

        private void ProcessPending(ScreenState screen)
        {
            if (screen.Pending.Count == 0)
                return;

            var pending = screen.Pending.ToList();
            screen.Pending.Clear();
            foreach (var entry in pending)
            {
                if (Game1.ticks < entry.NotBefore)
                {
                    screen.Pending.Add(entry);
                    continue;
                }
                if (entry.Npc?.currentLocation == null)
                    continue;

                // A deferred NPC re-adds itself.
                this.Refresh(entry.Npc, entry.Trigger);
            }
        }

        /// <summary>Re-check an NPC next tick (e.g. her idle pose changed, for conditions using NPC_IN_POSE). No-op for NPCs that can't change.</summary>
        public void QueueRefresh(NPC npc, string trigger)
        {
            if (npc != null && Context.IsWorldReady && this.Registry.IsDynamic(npc.Name))
                this.AddPending(this.Screens.Value, npc, trigger, Game1.ticks + 1);
        }

        private void AddPending(ScreenState screen, NPC npc, string trigger, int notBefore = 0)
        {
            for (int i = 0; i < screen.Pending.Count; i++)
            {
                if (ReferenceEquals(screen.Pending[i].Npc, npc))
                {
                    screen.Pending[i] = (npc, trigger, Math.Max(notBefore, screen.Pending[i].NotBefore));
                    return;
                }
            }
            screen.Pending.Add((npc, trigger, notBefore));
        }

        /*********
        ** Core
        *********/
        /// <summary>Re-check one NPC and apply the result if it changed.</summary>
        /// <param name="npc">The NPC.</param>
        /// <param name="trigger">What caused the check (for logs and the API event).</param>
        /// <param name="force">Re-apply even if LAF thinks nothing changed (still respects the guards).</param>
        public RefreshOutcome Refresh(NPC npc, string trigger, bool force = false)
        {
            if (!Context.IsWorldReady || !this.Config.Enabled)
                return RefreshOutcome.Skipped;

            if (!CanHandle(npc, out string skipReason))
            {
                this.Verbose($"{npc?.Name ?? "(null)"}: skipped ({skipReason}, trigger: {trigger}).");
                return RefreshOutcome.Skipped;
            }

            ScreenState screen = this.Screens.Value;

            // Several triggers in one tick → check once (unless forced).
            if (screen.ProcessedTick != Game1.ticks)
            {
                screen.ProcessedTick = Game1.ticks;
                screen.ProcessedThisTick.Clear();
            }
            if (!force && !screen.ProcessedThisTick.Add(npc))
                return RefreshOutcome.Unchanged;

            if (IsEventUp())
            {
                this.AddPending(screen, npc, "EventEnded");
                return RefreshOutcome.Deferred;
            }

            if (this.IsTalkingTo(npc))
            {
                this.AddPending(screen, npc, "DialogueClosed");
                return RefreshOutcome.Deferred;
            }

            NpcState state = this.States.GetOrCreateValue(npc);
            string location = npc.currentLocation.NameOrUniqueName;

            // Something else re-picked since LAF's last apply (vanilla on location change, another mod): don't trust the record.
            if (state.Known && (state.AppliedLocation != location || state.SeenVanillaId != npc.LastAppearanceId))
                state.Known = false;

            ResolveResult result = AppearanceResolver.Resolve(npc);
            if (result == null)
                return RefreshOutcome.Skipped;

            string winnerId = result.WinnerId;
            if (!force && state.Known && winnerId == state.AppliedId)
                return RefreshOutcome.Unchanged;

            // Not known yet, and she already wears it: just remember (no event, no call).
            if (!force && !state.Known && AppearanceResolver.TexturesMatch(npc, result))
            {
                this.Record(state, npc, winnerId, location);
                return RefreshOutcome.Synced;
            }

            // The fallback sheet outlived its animation while nobody watched her location: forget it.
            if (AnimationFrameGuard.FallbackFinished(npc, state))
            {
                state.FallbackActive = false;
                state.GuardBehavior = null;
                state.GuardMaxFrame = -1;
            }

            // Idle pose: if it doesn't fit the new outfit, it ends first (its outro plays on the current sheet); the
            // outfit changes once she stands.
            if (this.Poses != null && this.Poses.PrepareForSheetChange(npc, AppearanceResolver.GetExpectedAssets(result).Sprite))
            {
                this.Verbose($"{npc.Name}: switch to '{result.WinnerId ?? "(default)"}' waits (idle pose ends first, trigger: {trigger}).");
                this.AddPending(screen, npc, "PoseEnded");
                return RefreshOutcome.Deferred;
            }

            // Frame check: only switch to a sheet that has every frame she uses right now (current frame, queued
            // animation, schedule animation), and never away from a fallback sheet that shows her animation. Otherwise
            // the change waits (retried every scan) until she's back on frames the new sheet has.
            if (this.Config.AnimationFrameFallback
                && (state.FallbackActive || !this.Guard.AssetCanShow(npc, state, AppearanceResolver.GetExpectedAssets(result).Sprite)))
            {
                this.Verbose($"{npc.Name}: switch to '{result.WinnerId ?? "(default)"}' waits ({(state.FallbackActive ? "fallback sheet shows her animation" : $"new sheet lacks frame {this.Guard.GetMaxFrameInUse(npc, state)}")}, trigger: {trigger}).");
                this.AddPending(screen, npc, "AnimationEnded");
                return RefreshOutcome.Deferred;
            }

            // Optional: don't swap the sheet in the middle of a schedule animation.
            if (!force && this.Config.DeferDuringAnimations && npc.doingEndOfRouteAnimation.Value
                && !(this.Registry.TryGet(npc.Name, out DynamicNpcInfo info) && info.AnimationSensitive))
            {
                this.AddPending(screen, npc, "AnimationEnded");
                return RefreshOutcome.Deferred;
            }

            string oldId = state.Known ? state.AppliedId : npc.LastAppearanceId;
            string oldPortrait = AppearanceResolver.GetLoadedPortrait(npc)?.Name;
            string oldSprite = AppearanceResolver.GetSpriteAsset(npc);

            int frameBefore = npc.Sprite?.currentFrame ?? 0;
            try
            {
                SafeAppearance.Choose(npc);
                AnimationFrameGuard.NormalizeIdleFrame(npc, frameBefore);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't change {npc.Name}'s appearance: {ex}", LogLevel.Error);
                this.Record(state, npc, winnerId, location); // don't retry every tick
                return RefreshOutcome.Skipped;
            }

            // Recorded even if a texture failed to load, so vanilla's warning is logged once per change, not every 10 minutes.
            this.Record(state, npc, winnerId, location);

            string newPortrait = AppearanceResolver.GetLoadedPortrait(npc)?.Name;
            string newSprite = AppearanceResolver.GetSpriteAsset(npc);
            this.Log(
                $"{npc.Name}: '{oldId ?? "(default)"}' → '{winnerId ?? "(default)"}' (trigger: {trigger}{(trigger == "Clock" ? $" {Game1.timeOfDay}" : "")}). "
                + $"Sprite {Short(oldSprite)} → {Short(newSprite)}, portrait {Short(oldPortrait)} → {Short(newPortrait)}.");

            if (oldId != winnerId)
            {
                this.RaiseAppearanceChanged(npc, oldId, winnerId, trigger);
                if (this.Config.RefreshPositionalAudio)
                    this.PositionalAudio.NotifyAppearanceChanged();
            }

            return RefreshOutcome.Applied;
        }

        /// <summary>Call <see cref="NPC.ChooseAppearance"/> safely for another mod, and keep LAF's record in sync.</summary>
        public void SafeChooseAppearance(NPC npc)
        {
            if (npc == null)
                return;

            SafeAppearance.Choose(npc);

            if (npc.currentLocation != null)
            {
                NpcState state = this.States.GetOrCreateValue(npc);
                ResolveResult result = AppearanceResolver.Resolve(npc);
                if (result != null)
                    this.Record(state, npc, result.WinnerId, npc.currentLocation.NameOrUniqueName);
            }
        }

        private void Record(NpcState state, NPC npc, string winnerId, string location)
        {
            state.Known = true;
            state.AppliedId = winnerId;
            state.SeenVanillaId = npc.LastAppearanceId;
            state.AppliedLocation = location;
        }

        /// <summary>Whether LAF can handle this NPC at all.</summary>
        public static bool CanHandle(NPC npc, out string reason)
        {
            if (npc == null)
                reason = "no NPC";
            else if (!npc.IsVillager || npc.SimpleNonVillagerNPC)
                reason = "not a villager";
            else if (npc.currentLocation == null)
                reason = "not in a location";
            else if (!npc.AllowDynamicAppearance)
                reason = "AllowDynamicAppearance is off";
            else if (npc.portraitOverridden || npc.spriteOverridden)
                reason = "textures overridden by an event or mod";
            else
            {
                reason = null;
                return true;
            }
            return false;
        }

        /// <summary>Whether any local player is talking to this NPC right now.</summary>
        public bool IsTalkingTo(NPC npc)
        {
            if (GetCurrentSpeaker() == npc.Name)
                return true;

            foreach (var pair in this.Screens.GetActiveValues())
            {
                if (pair.Value.TalkingTo == npc.Name)
                    return true;
            }
            return false;
        }

        /*********
        ** Queries
        *********/
        /// <summary>The entry the NPC wears according to LAF, or the game's last pick if LAF hasn't handled her.</summary>
        public string GetAppliedId(NPC npc)
        {
            if (npc == null)
                return null;
            return this.States.TryGetValue(npc, out NpcState state) && state.Known ? state.AppliedId : npc.LastAppearanceId;
        }

        /// <summary>The entry the game would pick right now (dry run, cached for the current tick).</summary>
        public string GetExpectedId(NPC npc)
        {
            if (npc?.currentLocation == null)
                return null;

            NpcState state = this.States.GetOrCreateValue(npc);
            string location = npc.currentLocation.NameOrUniqueName;
            if (state.ExpectedTick == Game1.ticks && state.ExpectedLocation == location)
                return state.ExpectedId;

            string id = AppearanceResolver.Resolve(npc)?.WinnerId;
            state.ExpectedTick = Game1.ticks;
            state.ExpectedLocation = location;
            state.ExpectedId = id;
            return id;
        }

        /// <summary>For laf_why.</summary>
        public string DescribeState(NPC npc)
        {
            if (npc != null && this.States.TryGetValue(npc, out NpcState state) && state.Known)
                return $"LAF record: '{state.AppliedId ?? "(default)"}' (applied in {state.AppliedLocation})";
            return "LAF record: none yet";
        }

        public bool HasPending(NPC npc, out string trigger)
        {
            foreach (var entry in this.Screens.Value.Pending)
            {
                if (ReferenceEquals(entry.Npc, npc))
                {
                    trigger = entry.Trigger;
                    return true;
                }
            }
            trigger = null;
            return false;
        }

        /*********
        ** Helpers
        *********/
        private void RaiseAppearanceChanged(NPC npc, string oldId, string newId, string trigger)
        {
            var handlers = this.AppearanceChanged;
            if (handlers == null)
                return;

            var args = new AppearanceChangedEventArgs(npc, oldId, newId, trigger);
            foreach (EventHandler<IAppearanceChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"A mod's AppearanceChanged handler failed: {ex}", LogLevel.Error);
                }
            }
        }

        private static string Short(string asset) => string.IsNullOrEmpty(asset) ? "(none)" : asset.Replace('\\', '/');

        private void Log(string message)
        {
            this.Monitor.Log(message, this.Config.DebugLogging ? LogLevel.Debug : LogLevel.Trace);
        }

        private void Verbose(string message)
        {
            if (this.Config.DebugLogging)
                this.Monitor.Log(message, LogLevel.Trace);
        }
    }

    internal sealed class AppearanceChangedEventArgs : EventArgs, IAppearanceChangedEventArgs
    {
        public NPC Npc { get; }
        public string OldAppearanceId { get; }
        public string NewAppearanceId { get; }
        public string Trigger { get; }

        public AppearanceChangedEventArgs(NPC npc, string oldId, string newId, string trigger)
        {
            this.Npc = npc;
            this.OldAppearanceId = oldId;
            this.NewAppearanceId = newId;
            this.Trigger = trigger;
        }
    }
}
