using System.Collections.Generic;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>The mod settings (config.json, and GMCM if installed).</summary>
    public sealed class ModConfig
    {
        /// <summary>Master switch.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Re-check NPCs in the player's location when the clock changes (every 10 in-game minutes).</summary>
        public bool RefreshOnClock { get; set; } = true;

        /// <summary>Re-check an NPC when she moves to another tile, if her conditions use a position query.</summary>
        public bool RefreshOnTile { get; set; } = true;

        /// <summary>Re-check an NPC when her schedule animation starts/stops, if her conditions use an animation query.</summary>
        public bool RefreshOnAnimation { get; set; } = true;

        /// <summary>How often (in ticks, 60 = 1 second) LAF looks for tile/animation changes and retries deferred NPCs.</summary>
        public int ScanInterval { get; set; } = 10;

        /// <summary>
        /// Wait until an NPC's schedule animation ends before changing her sprite (unless her conditions depend on
        /// animations). Only needed if some outfit sheets lack the animation frames.
        /// </summary>
        public bool DeferDuringAnimations { get; set; } = false;

        /// <summary>
        /// Keep animations and outfit sheets compatible: LAF only switches an NPC to a sheet that has every frame she's
        /// using right now (otherwise the change waits); if her sheet lacks her schedule animation's frames (e.g. Harem
        /// Valley frames that only exist on the default sheet), a sheet that has them is shown until the animation
        /// ends; and other mods' appearance changes keep her running animation intact.
        /// </summary>
        public bool AnimationFrameFallback { get; set; } = true;

        /// <summary>
        /// Repair schedule animations other mods interrupt: restart the loop when an NPC stands on her animation spot with
        /// nothing playing (e.g. after another mod's kiss animation), and end the animation the vanilla way when she's led
        /// away while it still loops (e.g. a companion recruiter).
        /// </summary>
        public bool RepairInterruptedAnimations { get; set; } = true;

        /// <summary>
        /// While an event actor picks its outfit, position queries that look the NPC up by name (BETAS NPC_NEAR_AREA,
        /// Positional Audio NPC_POSITION...) see the actor's position instead of the overworld NPC's.
        /// </summary>
        public bool EventActorsUseOwnPosition { get; set; } = true;

        /// <summary>Play idle poses from content packs (<c>MasterRoshiHehe.LiveAppearanceFramework/IdlePoses</c>).</summary>
        public bool IdlePoses { get; set; } = true;

        /// <summary>After an appearance change, tell Positional Audio to re-check its sounds (only if it's installed and a sound uses LAF's query).</summary>
        public bool RefreshPositionalAudio { get; set; } = true;

        /// <summary>Extra game state queries that depend on an NPC's tile (config.json only).</summary>
        public List<string> ExtraPositionQueries { get; set; } = new();

        /// <summary>Extra game state queries that depend on an NPC's animation (config.json only).</summary>
        public List<string> ExtraAnimationQueries { get; set; } = new();

        /// <summary>Log every check and change at Debug level instead of Trace.</summary>
        public bool DebugLogging { get; set; } = false;
    }
}
