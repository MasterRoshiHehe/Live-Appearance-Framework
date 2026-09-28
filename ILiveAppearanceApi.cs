using System;
using StardewValley;

namespace LiveAppearanceFramework
{
    /// <summary>
    /// Public API of Live Appearance Framework. Other mods copy this file (and <see cref="IAppearanceChangedEventArgs"/>)
    /// into their own code and get it with
    /// <c>Helper.ModRegistry.GetApi&lt;ILiveAppearanceApi&gt;("MasterRoshiHehe.LiveAppearanceFramework")</c>.
    /// Returns null when LAF isn't installed, so use it as a soft dependency.
    /// </summary>
    public interface ILiveAppearanceApi
    {
        /// <summary>
        /// Raised after LAF changed an NPC's chosen Appearance entry (never during events or while the player
        /// talks to that NPC). Raised on the client that made the change; every client makes its own.
        /// </summary>
        event EventHandler<IAppearanceChangedEventArgs> AppearanceChanged;

        /// <summary>
        /// Re-check this NPC's Appearance entries now and apply the result if it changed.
        /// Respects LAF's guards: during an event or while the player talks to her, the refresh is queued instead.
        /// </summary>
        void Refresh(NPC npc);

        /// <summary>
        /// The Appearance entry ID the NPC is wearing according to LAF (or the game's <c>LastAppearanceId</c> if LAF
        /// hasn't handled her yet). Null if no entry applies (default textures).
        /// </summary>
        string GetAppearanceId(NPC npc);

        /// <summary>
        /// The Appearance entry ID the game would pick for this NPC right now (a dry run: nothing is loaded or changed).
        /// Null if no entry applies.
        /// </summary>
        string GetExpectedAppearanceId(NPC npc);

        /// <summary>
        /// Drop-in replacement for <c>npc.ChooseAppearance()</c>. Calls it, but keeps the sprite size / source
        /// rectangle when the NPC is in a special animation (vanilla resets them on every call), and keeps LAF's
        /// records in sync. Ignores LAF's guards: the caller decides when it's safe.
        /// </summary>
        void SafeChooseAppearance(NPC npc);

        /// <summary>Whether the NPC plays an idle pose right now (on this client).</summary>
        bool IsInPose(NPC npc);

        /// <summary>The ID of the idle pose the NPC plays right now (the key in <c>MasterRoshiHehe.LiveAppearanceFramework/IdlePoses</c>), or null.</summary>
        string GetPoseId(NPC npc);

        /// <summary>The ID of the stage of that pose playing right now, or null.</summary>
        string GetPoseStageId(NPC npc);
    }

    /// <summary>Details of an appearance change.</summary>
    public interface IAppearanceChangedEventArgs
    {
        /// <summary>The NPC whose appearance changed.</summary>
        NPC Npc { get; }

        /// <summary>The previous Appearance entry ID, or null (default textures / not known).</summary>
        string OldAppearanceId { get; }

        /// <summary>The new Appearance entry ID, or null (default textures).</summary>
        string NewAppearanceId { get; }

        /// <summary>What caused the check: Clock, Tile, Animation, Warp, DayStarted, DataChanged, EventEnded, DialogueClosed, AnimationEnded, PoseEnded, TriggerAction, Api, Console.</summary>
        string Trigger { get; }
    }
}
