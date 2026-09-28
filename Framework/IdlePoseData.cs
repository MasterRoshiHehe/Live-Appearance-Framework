using System.Collections.Generic;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// One entry of the <c>MasterRoshiHehe.LiveAppearanceFramework/IdlePoses</c> asset: frames of the NPC's current sheet that
    /// play while she stands still somewhere, without editing any schedule.
    /// </summary>
    public sealed class IdlePoseData
    {
        /// <summary>The NPC's internal name.</summary>
        public string Npc { get; set; }

        /// <summary>Optional: the location she must be in (NameOrUniqueName, case-insensitive).</summary>
        public string Location { get; set; }

        /// <summary>Optional: she must stand on the destination tile of the schedule stop she's on right now (any schedule source). Host only for now.</summary>
        public bool AtScheduleStop { get; set; }

        /// <summary>Optional: she must stand on this tile, as "x y".</summary>
        public string Tile { get; set; }

        /// <summary>Optional: she must face this way when the pose starts (0 up, 1 right, 2 down, 3 left).</summary>
        public int? Facing { get; set; }

        /// <summary>Optional game state query for the whole pose, checked with her location as <c>Target</c>.</summary>
        public string Condition { get; set; }

        /// <summary>How long (ms) she must stand still before the pose starts.</summary>
        public int StillFor { get; set; } = 1000;

        /// <summary>Played once when she goes from standing into the pose (e.g. lying down).</summary>
        public List<IdlePoseFrame> Intro { get; set; }

        /// <summary>Played once when the pose ends while she's still standing there (e.g. getting up).</summary>
        public List<IdlePoseFrame> Outro { get; set; }

        /// <summary>Shortcut for a pose with a single stage: the looping frames.</summary>
        public List<IdlePoseFrame> Frames { get; set; }

        /// <summary>
        /// The pose's stages. The first stage whose condition is true plays (a stage without a condition is always
        /// true, so put it last as the fallback). When the playing stage changes, the new stage's Transition plays,
        /// then its Frames loop.
        /// </summary>
        public List<IdlePoseStage> Stages { get; set; }

        /// <summary>
        /// Shuffle the stages' animations (Id, Transition, Frames) between the stage conditions once per day, the same
        /// for every player. The conditions stay in place: e.g. two time windows get their animations in a random order.
        /// </summary>
        public bool ShuffleStages { get; set; }

        /// <summary>Whether she keeps the pose while a player talks to her. If false, she stands, faces the player, and poses again after the dialogue.</summary>
        public bool KeepWhenTalkedTo { get; set; } = true;

        /// <summary>If several entries match the same NPC, the highest priority wins.</summary>
        public int Priority { get; set; }
    }

    /// <summary>A stage of an idle pose.</summary>
    public sealed class IdlePoseStage
    {
        /// <summary>A name for the stage (for NPC_IN_POSE, logs and laf_pose).</summary>
        public string Id { get; set; }

        /// <summary>Optional game state query: this stage plays while it's true (the first true stage wins).</summary>
        public string Condition { get; set; }

        /// <summary>Played once when she switches into this stage from another stage (not when the pose starts: the Intro plays then).</summary>
        public List<IdlePoseFrame> Transition { get; set; }

        /// <summary>The looping frames of this stage.</summary>
        public List<IdlePoseFrame> Frames { get; set; }
    }

    /// <summary>One frame of an idle pose.</summary>
    public sealed class IdlePoseFrame
    {
        /// <summary>The frame index on the NPC's sheet (left to right, top to bottom, starting at 0).</summary>
        public int Frame { get; set; }

        /// <summary>How long it shows, in milliseconds.</summary>
        public int Duration { get; set; } = 100;

        /// <summary>Whether to mirror it horizontally.</summary>
        public bool Flip { get; set; }
    }
}
