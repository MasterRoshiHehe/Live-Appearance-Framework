using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Plays idle poses: frames of an NPC's current sheet while she stands still somewhere (see <see cref="IdlePoseData"/>),
    /// without editing any schedule, so it works for every schedule source.
    ///
    /// How it plays: the frames are set as the sprite's <see cref="AnimatedSprite.CurrentAnimation"/> (looping). The game
    /// advances them itself on every client (host: <c>NPC.update</c> → <c>animateOnce</c>; farmhands:
    /// <c>updateSlaveAnimation</c>), and while an animation runs she doesn't turn towards a player who talks to her
    /// (<c>faceTowardFarmerForPeriod</c> and <c>AnimatedSprite.faceDirection</c> do nothing then). Intro/transition →
    /// loop switches happen inside the animation (frame end behavior of the last frame), so there's no gap.
    ///
    /// Everything is local: nothing is synced or saved. Schedule-based filters only work on the host for now.
    /// </summary>
    internal sealed class IdlePoseManager
    {
        /*********
        ** State
        *********/
        private enum Phase { None, Intro, Transition, Loop, Outro }

        private sealed class PoseState
        {
            // stillness
            public bool Tracked;
            public int LastSeenTick = -100;
            public Vector2 LastPosition;
            public int StillSinceTick;
            /// <summary>She already stood still when this client started watching her (warp in, save load): pose without the intro.</summary>
            public bool StillWhenFirstSeen;

            // running pose
            public Phase Phase;
            public string PoseId;
            public IdlePoseData Data;
            public int Slot = -1;
            public IdlePoseStage Stage;
            public int Facing;
            public AnimatedSprite Sprite;
            public AnimatedSprite.endOfAnimationBehavior Marker;
            public int SegmentCount;
            public bool OutroDone;

            public int NextEvalTick;
        }

        /// <summary>A pose entry that applies to an NPC right now.</summary>
        internal sealed class PoseMatch
        {
            public string Id;
            public IdlePoseData Data;
            public int Slot;
            public IdlePoseStage Stage;
        }

        private readonly IMonitor Monitor;
        private readonly IGameContentHelper Content;
        private readonly Func<ModConfig> GetConfig;
        private readonly ScheduleTracker Schedules;
        private readonly RefreshEngine Engine;
        private readonly HashSet<string> Logged = new(StringComparer.Ordinal);

        private ConditionalWeakTable<NPC, PoseState> States = new();
        private readonly HashSet<NPC> Posing = new(ReferenceEqualityComparer.Instance);

        private Dictionary<string, List<(string Id, IdlePoseData Data)>> ByNpc;
        private bool DataDirty = true;

        /// <summary>The asset content packs edit.</summary>
        public string AssetName { get; }

        public IdlePoseManager(IMonitor monitor, IGameContentHelper content, Func<ModConfig> getConfig, ScheduleTracker schedules, RefreshEngine engine, string assetName)
        {
            this.Monitor = monitor;
            this.Content = content;
            this.GetConfig = getConfig;
            this.Schedules = schedules;
            this.Engine = engine;
            this.AssetName = assetName;
        }

        /*********
        ** Asset
        *********/
        public void OnAssetRequested(AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(this.AssetName))
                e.LoadFrom(() => new Dictionary<string, IdlePoseData>(), AssetLoadPriority.Exclusive);
        }

        public void OnAssetsInvalidated(IEnumerable<IAssetName> names)
        {
            if (names.Any(name => name.IsEquivalentTo(this.AssetName)))
            {
                this.DataDirty = true;
                foreach (PoseState state in this.Posing.Select(this.GetState))
                    state.NextEvalTick = 0;
            }
        }

        private void EnsureData()
        {
            if (!this.DataDirty && this.ByNpc != null)
                return;
            this.DataDirty = false;

            var byNpc = new Dictionary<string, List<(string, IdlePoseData)>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var data = this.Content.Load<Dictionary<string, IdlePoseData>>(this.AssetName);
                foreach (var pair in data.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.Npc))
                    {
                        this.Monitor.Log($"Idle pose '{pair.Key}' has no Npc and is ignored.", LogLevel.Warn);
                        continue;
                    }
                    if (GetStages(pair.Value).Count == 0)
                    {
                        this.Monitor.Log($"Idle pose '{pair.Key}' has no Frames or Stages with frames and is ignored.", LogLevel.Warn);
                        continue;
                    }
                    if (!byNpc.TryGetValue(pair.Value.Npc, out var list))
                        byNpc[pair.Value.Npc] = list = new List<(string, IdlePoseData)>();
                    list.Add((pair.Key, pair.Value));
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't load {this.AssetName}: {ex}", LogLevel.Error);
            }
            this.ByNpc = byNpc;
        }

        /*********
        ** Lifecycle
        *********/
        public void Reset()
        {
            this.States = new ConditionalWeakTable<NPC, PoseState>();
            this.Posing.Clear();
            this.Logged.Clear();
            this.DataDirty = true;
        }

        /// <summary>End every pose now (day ending, disabled). Poses are never saved.</summary>
        public void StopAll(string reason)
        {
            foreach (NPC npc in this.Posing.ToList())
                this.StopNow(npc, this.GetState(npc), reason);
        }

        /*********
        ** Queries
        *********/
        public bool IsPosing(NPC npc)
        {
            return npc != null && this.States.TryGetValue(npc, out PoseState state) && state.Phase != Phase.None;
        }

        public string GetPoseId(NPC npc) => this.IsPosing(npc) ? this.GetState(npc).PoseId : null;

        public string GetStageId(NPC npc) => this.IsPosing(npc) ? this.GetState(npc).Stage?.Id : null;

        /*********
        ** Tick
        *********/
        public void OnUpdateTicked()
        {
            ModConfig config = this.GetConfig();
            if (!Context.IsWorldReady)
                return;
            if (!config.Enabled || !config.IdlePoses)
            {
                if (this.Posing.Count > 0)
                    this.StopAll("idle poses disabled");
                return;
            }
            if (Game1.eventUp || Game1.CurrentEvent != null)
                return;

            this.EnsureData();

            // Poses of NPCs nobody watches anymore end at once, so no stale animation walks around off-screen.
            if (this.Posing.Count > 0)
            {
                foreach (NPC npc in this.Posing.ToList())
                {
                    PoseState state = this.GetState(npc);
                    if (Game1.ticks - state.LastSeenTick > 10 || npc.currentLocation == null)
                        this.StopNow(npc, state, "nobody watches her location");
                }
            }

            GameLocation location = Game1.currentLocation;
            if (location == null || this.ByNpc.Count == 0)
                return;

            foreach (NPC npc in location.characters)
            {
                if (npc?.Sprite != null && this.ByNpc.ContainsKey(npc.Name))
                    this.Process(npc, config);
            }
        }

        private void Process(NPC npc, ModConfig config)
        {
            PoseState state = this.GetState(npc);
            if (state.LastSeenTick == Game1.ticks)
                return; // split-screen: another screen already handled her this tick
            if (Game1.ticks - state.LastSeenTick > 2)
                state.Tracked = false; // new observation (warp in, save load)
            state.LastSeenTick = Game1.ticks;

            // Stillness (position-based: companion mods set positions directly, farmhands have no controller).
            bool moving = npc.isMoving() || (Context.IsMainPlayer && npc.controller != null);
            Vector2 position = npc.Position;
            if (!state.Tracked)
            {
                state.Tracked = true;
                state.LastPosition = position;
                state.StillSinceTick = Game1.ticks;
                state.StillWhenFirstSeen = !moving && !npc.doingEndOfRouteAnimation.Value;
            }
            else if (position != state.LastPosition || moving)
            {
                state.LastPosition = position;
                state.StillSinceTick = Game1.ticks;
                state.StillWhenFirstSeen = false;
                if (state.Phase != Phase.None)
                {
                    this.StopNow(npc, state, "she moved");
                    return;
                }
            }

            if (state.Phase != Phase.None)
                this.UpdateRunning(npc, state, config);
            else if (Game1.ticks >= state.NextEvalTick)
            {
                state.NextEvalTick = Game1.ticks + Math.Max(1, config.ScanInterval);
                PoseMatch match = this.FindMatch(npc, state, starting: true, sheetAsset: null, reasons: null);
                if (match != null)
                    this.Start(npc, state, match);
            }
        }

        private void UpdateRunning(NPC npc, PoseState state, ModConfig config)
        {
            AnimatedSprite sprite = npc.Sprite;

            // Outro finished: stand normally.
            if (state.Phase == Phase.Outro && (state.OutroDone || sprite.CurrentAnimation == null))
            {
                this.Finish(npc, state);
                return;
            }

            if (npc.doingEndOfRouteAnimation.Value)
            {
                this.StopNow(npc, state, "a schedule animation started");
                return;
            }

            // Someone else's animation took over (e.g. another mod's kiss): let it be. She poses again later.
            List<FarmerSprite.AnimationFrame> animation = sprite.CurrentAnimation;
            if (animation != null && !this.IsOurs(npc, state, animation))
            {
                this.Log(npc, state, "another animation replaced it; it restarts once she stands still again");
                this.Clear(npc, state);
                return;
            }

            // Stopped by the game or a mod (Halt, a new sprite object...): continue the stage without standing up.
            if (animation == null || !ReferenceEquals(sprite, state.Sprite))
            {
                if (state.Phase == Phase.Outro)
                {
                    this.Finish(npc, state);
                    return;
                }
                this.PlaySequence(npc, state, null, state.Stage.Frames, Phase.Loop);
            }

            // Talking with KeepWhenTalkedTo off: stand and face the player; the pose restarts after the dialogue.
            if (!state.Data.KeepWhenTalkedTo && this.Engine.IsTalkingTo(npc))
            {
                this.StopNow(npc, state, "a player talks to her (KeepWhenTalkedTo is off)");
                npc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, opposite: false, useTileCalculations: false);
                state.StillSinceTick = Game1.ticks;
                return;
            }

            if (state.Phase == Phase.Outro || Game1.ticks < state.NextEvalTick)
                return;
            state.NextEvalTick = Game1.ticks + Math.Max(1, config.ScanInterval);

            PoseMatch match = this.FindMatch(npc, state, starting: false, sheetAsset: null, reasons: null);
            if (match == null || match.Id != state.PoseId)
            {
                this.End(npc, state, match == null ? "its conditions no longer apply" : $"pose '{match.Id}' applies now");
                return;
            }

            if (!ReferenceEquals(match.Stage, state.Stage))
            {
                string from = state.Stage?.Id;
                state.Slot = match.Slot;
                state.Stage = match.Stage;
                this.PlaySequence(npc, state, match.Stage.Transition, match.Stage.Frames, Phase.Transition);
                this.Log(npc, state, $"stage '{from}' → '{match.Stage.Id}'");
                this.Engine.QueueRefresh(npc, "Pose");
            }
        }

        /*********
        ** Integration with outfit changes
        *********/
        /// <summary>
        /// Before LAF switches the NPC's outfit: whether the running pose stays valid with the outfit that's about to be
        /// applied (conditions using NPC_APPEARANCE already see it, and the new sheet must have the pose's frames).
        /// If not, the pose ends (with its outro, on the current sheet). Returns true if the outfit change must wait
        /// for the outro.
        /// </summary>
        public bool PrepareForSheetChange(NPC npc, string newSpriteAsset)
        {
            if (!this.IsPosing(npc))
                return false;

            PoseState state = this.GetState(npc);
            if (state.Phase != Phase.Outro)
            {
                PoseMatch match = this.FindMatch(npc, state, starting: false, sheetAsset: newSpriteAsset, reasons: null);
                if (match != null && match.Id == state.PoseId)
                    return false;
                this.End(npc, state, "her outfit is about to change");
            }
            return this.IsPosing(npc);
        }

        /// <summary>After any appearance change while posing: keep the pose if the new sheet has its frames, else stand at once. Returns whether the pose continues.</summary>
        public bool AfterSheetChanged(NPC npc)
        {
            if (!this.IsPosing(npc))
                return false;

            PoseState state = this.GetState(npc);
            state.NextEvalTick = 0; // re-check the conditions this tick
            if (AnimationFrameGuard.CanShow(npc.Sprite?.spriteTexture, npc.Sprite, GetMaxFrame(state.Data)))
                return true;

            this.StopNow(npc, state, $"her new sheet '{AppearanceResolver.GetSpriteAsset(npc)}' lacks its frames");
            return false;
        }

        /*********
        ** Matching
        *********/
        /// <summary>The highest-priority pose entry that applies to the NPC now, or null.</summary>
        /// <param name="starting">Also require the start conditions (standing still long enough, facing).</param>
        /// <param name="sheetAsset">Check frames against this sprite asset instead of the loaded sheet.</param>
        /// <param name="reasons">If set, receives one line per entry explaining the result.</param>
        private PoseMatch FindMatch(NPC npc, PoseState state, bool starting, string sheetAsset, List<string> reasons)
        {
            if (!this.ByNpc.TryGetValue(npc.Name, out var entries))
                return null;

            PoseMatch best = null;
            foreach ((string id, IdlePoseData data) in entries.OrderByDescending(p => p.Data.Priority))
            {
                PoseMatch match = this.TryMatch(npc, state, id, data, starting, sheetAsset, out string reason);
                reasons?.Add($"{id} (priority {data.Priority}): {(match != null ? $"applies, stage '{match.Stage.Id}'" : reason)}");
                if (match != null && best == null)
                {
                    best = match;
                    if (reasons == null)
                        break;
                }
            }
            return best;
        }

        private PoseMatch TryMatch(NPC npc, PoseState state, string id, IdlePoseData data, bool starting, string sheetAsset, out string reason)
        {
            GameLocation location = npc.currentLocation;
            bool thisPose = state.Phase != Phase.None && state.PoseId == id;

            if (location == null)
                return Fail("not in a location", out reason);
            if (npc.IsInvisible || npc.isSleeping.Value)
                return Fail("invisible or sleeping", out reason);
            if (npc.doingEndOfRouteAnimation.Value)
                return Fail($"she plays schedule animation '{npc.endOfRouteBehaviorName.Value}'", out reason);
            if (!thisPose && npc.Sprite.CurrentAnimation != null && !this.IsOurs(npc, state, npc.Sprite.CurrentAnimation))
                return Fail("another animation plays on her sprite", out reason);
            if (!string.IsNullOrWhiteSpace(data.Location) && !string.Equals(location.NameOrUniqueName, data.Location, StringComparison.OrdinalIgnoreCase))
                return Fail($"she's in {location.NameOrUniqueName}, not {data.Location}", out reason);

            Point tile = npc.TilePoint;
            if (!string.IsNullOrWhiteSpace(data.Tile))
            {
                if (!TryParseTile(data.Tile, out Point wanted))
                    return Fail($"Tile '{data.Tile}' isn't \"x y\"", out reason);
                if (tile != wanted)
                    return Fail($"she's on tile {tile.X} {tile.Y}, not {wanted.X} {wanted.Y}", out reason);
            }

            if (data.AtScheduleStop)
            {
                ScheduleInfo info = this.Schedules.Get(npc);
                if (info == null)
                    return Fail(ScheduleTracker.CanRead ? "she has no schedule today" : "schedules are only known to the host", out reason);
                int index = info.GetCurrentStopIndex(Game1.timeOfDay);
                if (index < 0)
                    return Fail("before her first schedule stop", out reason);
                ScheduleStop stop = info.Stops[index];
                if (!string.Equals(stop.Location, location.NameOrUniqueName, StringComparison.OrdinalIgnoreCase) || stop.Tile != tile)
                    return Fail($"not on her current schedule stop ({stop.Time}: {stop.Location} {stop.Tile.X} {stop.Tile.Y})", out reason);
            }

            if (starting)
            {
                if (data.Facing.HasValue && npc.FacingDirection != data.Facing.Value)
                    return Fail($"she faces {npc.FacingDirection}, not {data.Facing.Value}", out reason);
                if (!data.KeepWhenTalkedTo && this.Engine.IsTalkingTo(npc))
                    return Fail("a player talks to her", out reason);
                if (!state.StillWhenFirstSeen)
                {
                    int stillMs = (Game1.ticks - state.StillSinceTick) * 1000 / 60;
                    if (stillMs < data.StillFor)
                        return Fail($"standing still for {stillMs} of {data.StillFor} ms", out reason);
                }
            }

            if (!string.IsNullOrWhiteSpace(data.Condition) && !GameStateQuery.CheckConditions(data.Condition, location))
                return Fail($"condition false: {data.Condition}", out reason);

            // Stage: the first slot whose condition is true, with today's animation for that slot.
            List<IdlePoseStage> slots = GetStages(data);
            List<IdlePoseStage> contents = data.ShuffleStages ? this.Shuffle(npc, id, slots) : slots;
            int slot = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(slots[i].Condition) || GameStateQuery.CheckConditions(slots[i].Condition, location))
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
                return Fail("no stage condition is true", out reason);

            // Every frame of the pose must exist on the sheet (else the game would show frame 0).
            int maxFrame = GetMaxFrame(data);
            var texture = sheetAsset != null ? TryLoadTexture(sheetAsset) : npc.Sprite.spriteTexture;
            if (texture != null && !AnimationFrameGuard.CanShow(texture, npc.Sprite, maxFrame))
            {
                string sheet = sheetAsset ?? AppearanceResolver.GetSpriteAsset(npc);
                this.LogOnce($"frames:{id}:{sheet}", $"Idle pose '{id}' doesn't play for {npc.Name}: her sheet '{sheet}' has no frame {maxFrame}.");
                return Fail($"her sheet '{sheet}' has no frame {maxFrame}", out reason);
            }

            reason = null;
            return new PoseMatch { Id = id, Data = data, Slot = slot, Stage = contents[slot] };
        }

        private static PoseMatch Fail(string why, out string reason)
        {
            reason = why;
            return null;
        }

        /// <summary>The stages of a pose (a pose with only Frames has one stage).</summary>
        private static List<IdlePoseStage> GetStages(IdlePoseData data)
        {
            var stages = new List<IdlePoseStage>();
            if (data.Stages != null)
            {
                for (int i = 0; i < data.Stages.Count; i++)
                {
                    IdlePoseStage stage = data.Stages[i];
                    if (stage?.Frames == null || stage.Frames.Count == 0)
                        continue;
                    if (string.IsNullOrWhiteSpace(stage.Id))
                        stage.Id = $"Stage{i + 1}";
                    stages.Add(stage);
                }
            }
            if (stages.Count == 0 && data.Frames is { Count: > 0 })
                stages.Add(new IdlePoseStage { Id = "Default", Frames = data.Frames });
            return stages;
        }

        /// <summary>Today's order of the stage animations (the same for every player all day).</summary>
        private List<IdlePoseStage> Shuffle(NPC npc, string poseId, List<IdlePoseStage> stages)
        {
            var result = stages.ToList();
            Random random = Utility.CreateDaySaveRandom(Game1.hash.GetDeterministicHashCode($"{npc.Name}/{poseId}"));
            for (int i = result.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (result[i], result[j]) = (result[j], result[i]);
            }
            return result;
        }

        private static int GetMaxFrame(IdlePoseData data)
        {
            int max = -1;
            void Add(List<IdlePoseFrame> frames)
            {
                if (frames == null)
                    return;
                foreach (IdlePoseFrame frame in frames)
                {
                    if (frame != null)
                        max = Math.Max(max, frame.Frame);
                }
            }

            Add(data.Intro);
            Add(data.Outro);
            foreach (IdlePoseStage stage in GetStages(data))
            {
                Add(stage.Transition);
                Add(stage.Frames);
            }
            return max;
        }

        /*********
        ** Playing
        *********/
        private void Start(NPC npc, PoseState state, PoseMatch match)
        {
            state.PoseId = match.Id;
            state.Data = match.Data;
            state.Slot = match.Slot;
            state.Stage = match.Stage;
            state.Facing = npc.FacingDirection;
            state.OutroDone = false;
            this.Posing.Add(npc);

            bool intro = !state.StillWhenFirstSeen && match.Data.Intro is { Count: > 0 };
            this.PlaySequence(npc, state, intro ? match.Data.Intro : null, match.Stage.Frames, intro ? Phase.Intro : Phase.Loop);
            this.Engine.QueueRefresh(npc, "Pose");
            this.Log(npc, state, $"started{(intro ? "" : state.StillWhenFirstSeen ? " (she was already there, no intro)" : "")}, stage '{match.Stage.Id}'");
        }

        /// <summary>End the pose the nice way: play the outro if there is one, else stand at once.</summary>
        private void End(NPC npc, PoseState state, string reason)
        {
            if (state.Data?.Outro is { Count: > 0 })
            {
                state.OutroDone = false;
                this.PlaySequence(npc, state, state.Data.Outro, null, Phase.Outro);
                this.Log(npc, state, $"ending ({reason})");
            }
            else
                this.StopNow(npc, state, reason);
        }

        /// <summary>Stand at once (no outro).</summary>
        private void StopNow(NPC npc, PoseState state, string reason)
        {
            if (state.Phase == Phase.None)
                return;

            AnimatedSprite sprite = npc.Sprite;
            if (sprite != null && this.IsOurs(npc, state, sprite.CurrentAnimation))
            {
                sprite.ClearAnimation();
                StandNormally(npc, state.Facing);
            }
            this.Log(npc, state, $"stopped ({reason})");
            this.Clear(npc, state);
        }

        /// <summary>The outro has played: stand normally.</summary>
        private void Finish(NPC npc, PoseState state)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite != null && (sprite.CurrentAnimation == null || this.IsOurs(npc, state, sprite.CurrentAnimation)))
            {
                sprite.ClearAnimation();
                StandNormally(npc, state.Facing);
            }
            this.Log(npc, state, "ended");
            this.Clear(npc, state);
        }

        private void Clear(NPC npc, PoseState state)
        {
            state.Phase = Phase.None;
            state.PoseId = null;
            state.Data = null;
            state.Stage = null;
            state.Slot = -1;
            state.Marker = null;
            state.Sprite = null;
            state.SegmentCount = 0;
            state.OutroDone = false;
            state.StillWhenFirstSeen = false;
            state.StillSinceTick = Game1.ticks;
            state.NextEvalTick = 0;
            this.Posing.Remove(npc);
            this.Engine.QueueRefresh(npc, "Pose");
        }

        private static void StandNormally(NPC npc, int facing)
        {
            AnimatedSprite sprite = npc.Sprite;
            sprite.currentFrame = 0;
            npc.faceDirection(facing);
            sprite.UpdateSourceRect();
        }

        /// <summary>
        /// Play <paramref name="once"/> (if any), then loop <paramref name="loop"/> (if any; else the pose ends after
        /// <paramref name="once"/>: used for the outro). The switch happens inside the animation, without a gap.
        /// </summary>
        private void PlaySequence(NPC npc, PoseState state, List<IdlePoseFrame> once, List<IdlePoseFrame> loop, Phase phase)
        {
            AnimatedSprite sprite = npc.Sprite;
            state.Sprite = sprite;

            if (once == null || once.Count == 0)
            {
                this.SetSegment(npc, state, loop, Phase.Loop, restartIndex: false, onLastFrame: null);
                return;
            }

            AnimatedSprite.endOfAnimationBehavior next;
            if (loop != null && loop.Count > 0)
                next = _ => this.SetSegment(npc, state, loop, Phase.Loop, restartIndex: true, onLastFrame: null);
            else
                next = _ =>
                {
                    state.OutroDone = true;
                    sprite.ClearAnimation();
                };
            this.SetSegment(npc, state, once, phase, restartIndex: false, onLastFrame: next);
        }

        /// <summary>Put these frames on the sprite (looping).</summary>
        /// <param name="restartIndex">Called from inside <see cref="AnimatedSprite.animateOnce"/>, which advances the index right after: start at -1 so frame 0 shows.</param>
        /// <param name="onLastFrame">Called when the last frame ends; null for a loop (a no-op marker is used to recognize LAF's frames).</param>
        private void SetSegment(NPC npc, PoseState state, List<IdlePoseFrame> frames, Phase phase, bool restartIndex, AnimatedSprite.endOfAnimationBehavior onLastFrame)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (!ReferenceEquals(sprite, state.Sprite))
                return; // the NPC got a new sprite object meanwhile; UpdateRunning restarts the stage

            AnimatedSprite.endOfAnimationBehavior marker = onLastFrame ?? (_ => { });
            var list = new List<FarmerSprite.AnimationFrame>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                IdlePoseFrame frame = frames[i] ?? new IdlePoseFrame();
                bool last = i == frames.Count - 1;
                list.Add(new FarmerSprite.AnimationFrame(frame.Frame, Math.Max(1, frame.Duration), secondaryArm: false, flip: frame.Flip, frameBehavior: last ? marker : null, behaviorAtEndOfFrame: true));
            }

            sprite.setCurrentAnimation(list);
            sprite.loop = true;
            if (restartIndex)
                sprite.currentAnimationIndex = -1;
            else
                sprite.UpdateSourceRect();

            state.Phase = phase;
            state.Marker = marker;
            state.SegmentCount = list.Count;
        }

        /// <summary>Whether the sprite's current animation is the one LAF set for this pose.</summary>
        private bool IsOurs(NPC npc, PoseState state, List<FarmerSprite.AnimationFrame> animation)
        {
            return animation != null
                && state.Marker != null
                && animation.Count == state.SegmentCount
                && ReferenceEquals(animation[animation.Count - 1].frameEndBehavior, state.Marker);
        }

        /*********
        ** Console
        *********/
        public string Describe(NPC npc, bool detailed)
        {
            this.EnsureData();
            if (npc == null || !this.ByNpc.TryGetValue(npc.Name, out var entries))
                return detailed ? $"{npc?.Name}: no idle poses target her ({this.AssetName})." : null;

            PoseState state = this.GetState(npc);
            var text = new StringBuilder();
            text.Append(state.Phase != Phase.None
                ? $"Idle pose: '{state.PoseId}', stage '{state.Stage?.Id}', {state.Phase.ToString().ToLowerInvariant()}."
                : "Idle pose: none running.");
            if (!detailed)
                return text.ToString();

            int stillMs = state.Tracked ? (Game1.ticks - state.StillSinceTick) * 1000 / 60 : 0;
            text.Append($" Standing still for {stillMs} ms{(state.StillWhenFirstSeen ? " (already there when you arrived)" : "")}.");
            var reasons = new List<string>();
            this.FindMatch(npc, state, starting: state.Phase == Phase.None, sheetAsset: null, reasons: reasons);
            foreach (string line in reasons)
                text.Append($"\n  {line}");
            foreach ((string id, IdlePoseData data) in entries.Where(p => p.Data.ShuffleStages))
            {
                var slots = GetStages(data);
                var contents = this.Shuffle(npc, id, slots);
                text.Append($"\n  {id}: today's stage order: {string.Join(", ", slots.Select((s, i) => $"[{(string.IsNullOrWhiteSpace(s.Condition) ? "always" : s.Condition)}] → '{contents[i].Id}'"))}");
            }
            return text.ToString();
        }

        /*********
        ** Helpers
        *********/
        private PoseState GetState(NPC npc) => this.States.GetOrCreateValue(npc);

        private static bool TryParseTile(string raw, out Point tile)
        {
            tile = Point.Zero;
            string[] parts = raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
                return false;
            tile = new Point(x, y);
            return true;
        }

        private static Microsoft.Xna.Framework.Graphics.Texture2D TryLoadTexture(string asset)
        {
            try
            {
                return Game1.content.DoesAssetExist<Microsoft.Xna.Framework.Graphics.Texture2D>(asset)
                    ? Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(asset)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private void Log(NPC npc, PoseState state, string message)
        {
            this.Monitor.Log($"{npc.Name}: idle pose '{state.PoseId}' {message} ({Game1.timeOfDay}).", LogLevel.Trace);
        }

        private void LogOnce(string key, string message)
        {
            if (this.Logged.Add(key))
                this.Monitor.Log(message, LogLevel.Trace);
        }
    }
}
