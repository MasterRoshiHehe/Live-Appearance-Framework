using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>One stop of an NPC's schedule for today.</summary>
    internal sealed class ScheduleStop
    {
        /// <summary>When she sets off towards this stop (not when she arrives: walking time isn't known in advance).</summary>
        public int Time { get; init; }

        /// <summary>The destination map.</summary>
        public string Location { get; init; }

        /// <summary>The destination tile.</summary>
        public Point Tile { get; init; }

        /// <summary>The end-of-route behavior (animation key or message), if any.</summary>
        public string Behavior { get; init; }

        /// <summary>The maps she walks through on the way, excluding where she comes from and the destination.</summary>
        public string[] Through { get; init; } = Array.Empty<string>();
    }

    /// <summary>An NPC's schedule for today, as facts content packs can query.</summary>
    internal sealed class ScheduleInfo
    {
        /// <summary>The schedule key the game chose (e.g. "summer_Mon", "rain2"). Informational.</summary>
        public string Key { get; init; }

        /// <summary>The day these facts belong to (<see cref="WorldDate.TotalDays"/>).</summary>
        public int Day { get; init; }

        /// <summary>Where she starts the day (her home map).</summary>
        public string StartLocation { get; init; }

        /// <summary>Today's stops, ordered by time.</summary>
        public IReadOnlyList<ScheduleStop> Stops { get; init; } = Array.Empty<ScheduleStop>();

        /// <summary>The live schedule object these facts were built from (host), to notice replacements.</summary>
        public object Source { get; init; }

        /// <summary>Index of the stop she's walking to or standing at right now, or -1 before her first stop.</summary>
        public int GetCurrentStopIndex(int timeOfDay)
        {
            int index = -1;
            for (int i = 0; i < this.Stops.Count; i++)
            {
                if (this.Stops[i].Time <= timeOfDay)
                    index = i;
                else
                    break;
            }
            return index;
        }

        /// <summary>The location she comes from when setting off towards stop <paramref name="index"/>.</summary>
        public string GetOrigin(int index)
        {
            return index <= 0 ? this.StartLocation : this.Stops[index - 1].Location;
        }
    }

    /// <summary>
    /// Works out NPCs' schedules for today from the route the game actually loaded (<see cref="NPC.Schedule"/>), so it
    /// works for any schedule source (vanilla, Harem Valley, Custom Schedule Keys...), not just key names.
    ///
    /// Host only for now: the game loads schedules on the host (<c>NPC.dayUpdate</c> → <c>resetForNewDay</c> →
    /// <c>TryLoadSchedule</c>), and <see cref="NPC.Schedule"/> isn't synced. Farmhand support (facts written into
    /// synced modData by the host) is the next step.
    /// </summary>
    internal sealed class ScheduleTracker
    {
        private readonly IReflectionHelper Reflection;
        private readonly IMonitor Monitor;
        private ConditionalWeakTable<NPC, ScheduleInfo> Cache = new();
        private readonly HashSet<string> Warned = new(StringComparer.Ordinal);

        /// <summary>Incremented whenever any NPC's schedule was (re)read, so the engine knows to re-check outfits.</summary>
        public int Version { get; private set; }

        public ScheduleTracker(IReflectionHelper reflection, IMonitor monitor)
        {
            this.Reflection = reflection;
            this.Monitor = monitor;
        }

        public void Reset()
        {
            this.Cache = new ConditionalWeakTable<NPC, ScheduleInfo>();
            this.Version++;
        }

        /// <summary>Whether this client can know schedules (the host; farmhands get them in a later step).</summary>
        public static bool CanRead => Context.IsWorldReady && Context.IsMainPlayer;

        /// <summary>Get today's schedule facts for the NPC, or null if she has no schedule today (or this client can't know it).</summary>
        public ScheduleInfo Get(NPC npc)
        {
            if (npc == null || !CanRead)
                return null;

            Dictionary<int, SchedulePathDescription> schedule = npc.Schedule;
            int day = Game1.Date.TotalDays;
            if (this.Cache.TryGetValue(npc, out ScheduleInfo cached) && ReferenceEquals(cached.Source, schedule) && cached.Day == day)
                return cached.Stops.Count > 0 || schedule != null ? cached : null;

            ScheduleInfo info = this.Build(npc, schedule, day);
            this.Cache.AddOrUpdate(npc, info);
            this.Version++;
            return schedule != null ? info : null;
        }

        /// <summary>
        /// Notice schedules that were replaced since they were last read (Ginger Island days, save load, schedule edits
        /// reloaded mid-day, other mods). Returns true if any changed. Cheap: one reference compare per NPC.
        /// </summary>
        public bool DetectChanges()
        {
            if (!CanRead)
                return false;

            bool changed = false;
            int day = Game1.Date.TotalDays;
            Utility.ForEachVillager(npc =>
            {
                if (this.Cache.TryGetValue(npc, out ScheduleInfo cached) && (!ReferenceEquals(cached.Source, npc.Schedule) || cached.Day != day))
                {
                    this.Cache.Remove(npc);
                    changed = true;
                }
                return true;
            });

            if (changed)
                this.Version++;
            return changed;
        }

        private ScheduleInfo Build(NPC npc, Dictionary<int, SchedulePathDescription> schedule, int day)
        {
            string start = npc.DefaultMap ?? npc.currentLocation?.NameOrUniqueName;
            var stops = new List<ScheduleStop>();

            if (schedule != null)
            {
                string origin = start;
                foreach (KeyValuePair<int, SchedulePathDescription> pair in schedule.OrderBy(p => p.Key))
                {
                    SchedulePathDescription path = pair.Value;
                    if (path == null)
                        continue;

                    string target = path.targetLocationName;
                    stops.Add(new ScheduleStop
                    {
                        Time = pair.Key,
                        Location = target,
                        Tile = path.targetTile,
                        Behavior = !string.IsNullOrEmpty(path.endOfRouteBehavior) ? path.endOfRouteBehavior : path.endOfRouteMessage,
                        Through = this.GetMapsBetween(npc, origin, target)
                    });

                    if (!string.IsNullOrEmpty(target))
                        origin = target;
                }
            }

            return new ScheduleInfo
            {
                Key = npc.ScheduleKey,
                Day = day,
                StartLocation = start,
                Stops = stops,
                Source = schedule
            };
        }

        /// <summary>
        /// The maps between two locations on the NPC's way, using the same routing the game's schedule parser uses
        /// (<c>NPC.getLocationRoute</c>: warp route cache for her gender).
        /// </summary>
        private string[] GetMapsBetween(NPC npc, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.Ordinal))
                return Array.Empty<string>();

            try
            {
                string[] route = this.Reflection.GetMethod(npc, "getLocationRoute").Invoke<string[]>(from, to);
                if (route == null || route.Length <= 2)
                    return Array.Empty<string>();
                return route.Skip(1).Take(route.Length - 2).ToArray();
            }
            catch (Exception ex)
            {
                if (this.Warned.Add($"route:{from}>{to}"))
                    this.Monitor.Log($"Couldn't work out {npc.Name}'s route from {from} to {to}: {ex.Message}", LogLevel.Trace);
                return Array.Empty<string>();
            }
        }
    }
}
