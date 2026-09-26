using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Characters;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>What LAF knows about one NPC whose appearance can change during the day.</summary>
    internal sealed class DynamicNpcInfo
    {
        public string Name { get; init; }
        public int EntryCount { get; init; }

        /// <summary>Some condition uses a query that depends on the NPC's tile.</summary>
        public bool PositionSensitive => this.PositionQueries.Count > 0;

        /// <summary>Some condition uses a query that depends on the NPC's schedule animation.</summary>
        public bool AnimationSensitive => this.AnimationQueries.Count > 0;

        /// <summary>Some condition uses one of LAF's schedule queries.</summary>
        public bool ScheduleSensitive { get; set; }

        public List<string> PositionQueries { get; } = new();
        public List<string> AnimationQueries { get; } = new();
    }

    /// <summary>
    /// Scans <c>Data/Characters</c> for NPCs that can change appearance during the day, and what each one depends on.
    /// Rebuilt lazily whenever the data changes. NPCs that aren't in here are never touched by the automatic triggers.
    /// </summary>
    internal sealed class DynamicNpcRegistry
    {
        /// <summary>Known queries that depend on an NPC's tile.</summary>
        private static readonly string[] KnownPositionQueries =
        {
            "Spiderbuttons.BETAS_NPC_NEAR_AREA",
            "Spiderbuttons.BETAS_NPC_NEAR_NPC",
            "ichortower.PositionalAudio_NPC_POSITION" // also matches _NPC_POSITION_RECT
        };

        /// <summary>Known queries that depend on an NPC's schedule animation.</summary>
        private static readonly string[] KnownAnimationQueries =
        {
            "ichortower.PositionalAudio_NPC_ANIMATING"
        };

        private readonly Dictionary<string, DynamicNpcInfo> Npcs = new(StringComparer.Ordinal);
        private readonly IMonitor Monitor;
        private readonly string OwnQueryName;
        private readonly string ScheduleMarker;
        private readonly HashSet<string> WarnedCircular = new(StringComparer.Ordinal);

        /// <summary>Whether the data must be rescanned before the next use.</summary>
        public bool IsDirty { get; private set; } = true;

        public IReadOnlyCollection<DynamicNpcInfo> All => this.Npcs.Values;

        public DynamicNpcRegistry(IMonitor monitor, string ownQueryName, string scheduleMarker)
        {
            this.Monitor = monitor;
            this.OwnQueryName = ownQueryName;
            this.ScheduleMarker = scheduleMarker;
        }

        /// <summary>Whether any dynamic NPC's conditions use a schedule query.</summary>
        public bool AnyScheduleSensitive => this.Npcs.Values.Any(p => p.ScheduleSensitive);

        public void MarkDirty() => this.IsDirty = true;

        public bool TryGet(string name, out DynamicNpcInfo info)
        {
            if (name == null)
            {
                info = null;
                return false;
            }
            return this.Npcs.TryGetValue(name, out info);
        }

        public bool IsDynamic(string name) => name != null && this.Npcs.ContainsKey(name);

        /// <summary>Rescan <c>Data/Characters</c> if it changed since the last scan.</summary>
        public void EnsureFresh(ModConfig config)
        {
            if (this.IsDirty)
                this.Rebuild(config);
        }

        public void Rebuild(ModConfig config)
        {
            this.IsDirty = false;
            this.Npcs.Clear();

            IDictionary<string, CharacterData> allData = Game1.characterData;
            if (allData == null)
                return;

            var positionQueries = KnownPositionQueries.Concat(config.ExtraPositionQueries ?? new List<string>()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToArray();
            var animationQueries = KnownAnimationQueries.Concat(config.ExtraAnimationQueries ?? new List<string>()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToArray();

            foreach ((string name, CharacterData data) in allData)
            {
                List<CharacterAppearanceData> entries = data?.Appearance;
                if (entries == null || entries.Count == 0)
                    continue;

                // One conditional entry is already a switch (between it and the default textures).
                bool dynamic = entries.Count >= 2 || entries.Any(p => !string.IsNullOrWhiteSpace(p?.Condition));
                if (!dynamic)
                    continue;

                var info = new DynamicNpcInfo { Name = name, EntryCount = entries.Count };
                foreach (CharacterAppearanceData entry in entries)
                {
                    string condition = entry?.Condition;
                    if (string.IsNullOrWhiteSpace(condition))
                        continue;

                    foreach (string query in positionQueries)
                    {
                        if (condition.Contains(query, StringComparison.OrdinalIgnoreCase) && !info.PositionQueries.Contains(query))
                            info.PositionQueries.Add(query);
                    }
                    foreach (string query in animationQueries)
                    {
                        if (condition.Contains(query, StringComparison.OrdinalIgnoreCase) && !info.AnimationQueries.Contains(query))
                            info.AnimationQueries.Add(query);
                    }

                    if (condition.Contains(this.ScheduleMarker, StringComparison.OrdinalIgnoreCase))
                        info.ScheduleSensitive = true;

                    if (condition.Contains(this.OwnQueryName, StringComparison.OrdinalIgnoreCase) && this.WarnedCircular.Add($"{name}/{entry.Id}"))
                    {
                        this.Monitor.Log(
                            $"{name}'s Appearance entry '{entry.Id}' uses {this.OwnQueryName} in its own condition. That's circular: the query always returns false there.",
                            LogLevel.Warn);
                    }
                }

                this.Npcs[name] = info;
            }
        }
    }
}
