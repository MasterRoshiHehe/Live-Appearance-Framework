using System;
using System.Collections;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Triggers;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Everything LAF does for Positional Audio (ichortower.PositionalAudio), handled entirely from LAF's side:
    /// PA's code is never changed and PA isn't a dependency.
    ///
    /// PA only re-checks its sound conditions when the clock changes, an NPC starts/stops moving or animating, on warp,
    /// on day start, or when its <c>ichortower.PositionalAudio_Refresh</c> trigger action runs. An appearance change on
    /// its own doesn't make PA look again, so sounds conditioned on LAF's NPC_APPEARANCE query would lag. After LAF
    /// changes an appearance, this runs PA's refresh action, but only if a PA sound in the player's location uses
    /// LAF's query (read from PA's data with reflection; if that fails, it refreshes anyway, which is harmless).
    /// </summary>
    internal sealed class PositionalAudioBridge
    {
        public const string PaModId = "ichortower.PositionalAudio";
        private const string RefreshAction = PaModId + "_Refresh";
        private const string DataAsset = "Mods/" + PaModId + "/Data";
        private const string AudioPlayerType = "ichortower.PositionalAudio.AudioPlayer";

        private readonly IMonitor Monitor;
        private readonly IReflectionHelper Reflection;
        private readonly string OwnQueryName;

        /// <summary>Location names that have at least one PA sound using LAF's query. Null = not scanned yet.</summary>
        private HashSet<string> LocationsUsingLaf;

        /// <summary>Reading PA's data failed: fall back to refreshing after every change.</summary>
        private bool ScanFailed;

        private bool RefreshQueued;

        public bool IsLoaded { get; private set; }

        public PositionalAudioBridge(IMonitor monitor, IReflectionHelper reflection, string ownQueryName)
        {
            this.Monitor = monitor;
            this.Reflection = reflection;
            this.OwnQueryName = ownQueryName;
        }

        public void Init(IModRegistry registry)
        {
            this.IsLoaded = registry.IsLoaded(PaModId);
            if (this.IsLoaded)
                this.Monitor.Log("Positional Audio found: LAF will refresh its sounds after appearance changes when needed.", LogLevel.Trace);
        }

        /// <summary>PA's data asset changed: rescan it when next needed.</summary>
        public void OnAssetsInvalidated(IEnumerable<IAssetName> names)
        {
            if (!this.IsLoaded)
                return;

            foreach (IAssetName name in names)
            {
                if (name.IsEquivalentTo(DataAsset))
                {
                    this.LocationsUsingLaf = null;
                    this.ScanFailed = false;
                    return;
                }
            }
        }

        public void Reset()
        {
            this.LocationsUsingLaf = null;
            this.ScanFailed = false;
            this.RefreshQueued = false;
        }

        /// <summary>An NPC's appearance changed on this screen: queue a PA refresh if the player's location has sounds that depend on it.</summary>
        public void NotifyAppearanceChanged()
        {
            if (!this.IsLoaded || Game1.currentLocation == null)
                return;

            if (this.UsesLafQuery(Game1.currentLocation.Name) || this.UsesLafQuery(Game1.currentLocation.NameOrUniqueName))
                this.RefreshQueued = true;
        }

        /// <summary>Run the queued refresh (once per tick at most). Call at the end of the tick, after all changes.</summary>
        public void Flush()
        {
            if (!this.RefreshQueued)
                return;

            this.RefreshQueued = false;
            try
            {
                if (!TriggerActionManager.TryRunAction(RefreshAction, out string error, out Exception ex))
                    this.Monitor.Log($"Couldn't refresh Positional Audio: {error}{(ex != null ? $"\n{ex}" : "")}", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't refresh Positional Audio: {ex}", LogLevel.Trace);
            }
        }

        private bool UsesLafQuery(string locationName)
        {
            if (this.ScanFailed)
                return true;

            if (this.LocationsUsingLaf == null && !this.TryScan())
                return true;

            return locationName != null && this.LocationsUsingLaf.Contains(locationName);
        }

        /// <summary>Read PA's loaded sound data (AudioPlayer.Data: Dictionary&lt;string, AudioItem&gt;) and note which locations use LAF's query.</summary>
        private bool TryScan()
        {
            try
            {
                Type type = FindType(AudioPlayerType);
                if (type == null)
                    throw new InvalidOperationException($"type {AudioPlayerType} not found");

                if (this.Reflection.GetProperty<object>(type, "Data").GetValue() is not IDictionary data)
                    throw new InvalidOperationException("AudioPlayer.Data isn't a dictionary");

                var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object item in data.Values)
                {
                    if (item == null)
                        continue;

                    string condition = this.Reflection.GetField<string>(item, "Condition").GetValue();
                    if (condition == null || !condition.Contains(this.OwnQueryName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string location = this.Reflection.GetField<string>(item, "Location").GetValue();
                    if (!string.IsNullOrWhiteSpace(location))
                        locations.Add(location.Trim());
                }

                this.LocationsUsingLaf = locations;
                this.Monitor.Log($"Positional Audio: {locations.Count} location(s) have sounds that use {this.OwnQueryName}.", LogLevel.Trace);
                return true;
            }
            catch (Exception ex)
            {
                this.ScanFailed = true;
                this.Monitor.Log($"Couldn't read Positional Audio's data, so LAF will refresh it after every appearance change in the player's location: {ex.Message}", LogLevel.Trace);
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }
            return null;
        }
    }
}
