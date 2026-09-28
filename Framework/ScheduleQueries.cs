using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Delegates;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Game state queries about an NPC's route for today. They read the schedule the game actually loaded, so they
    /// work for any schedule source. All of them only need the stop list and the current time.
    /// <list type="bullet">
    ///   <item><c>SCHEDULE_VISITS &lt;npc&gt; &lt;location&gt;+</c>: a stop of today has one of these destinations.</item>
    ///   <item><c>SCHEDULE_PASSES_THROUGH &lt;npc&gt; &lt;location&gt;+</c>: she walks through one of these maps on the way to a stop today (not counting destinations).</item>
    ///   <item><c>SCHEDULE_BEFORE_LEAVING &lt;npc&gt; &lt;location&gt;</c>: today she visits the location, and it's before she sets off from her last stop there.</item>
    ///   <item><c>SCHEDULE_CURRENT_TARGET &lt;npc&gt; &lt;location&gt;+</c>: the stop she's walking to or standing at has one of these destinations (false before her first stop).</item>
    ///   <item><c>SCHEDULE_CURRENT_ROUTE &lt;npc&gt; &lt;location&gt;+</c>: her current leg passes through or ends at one of these maps.</item>
    ///   <item><c>SCHEDULE_LEAVES_FOR &lt;npc&gt; &lt;location&gt; &lt;min time&gt; [max time]</c>: she sets off towards the location between these times today.</item>
    ///   <item><c>SCHEDULE_MINUTES_BEFORE_LEAVING &lt;npc&gt; &lt;location&gt; &lt;min&gt; [max]</c>: the in-game minutes until she sets off from her (current or next) visit to the location are between min and max.</item>
    /// </list>
    /// </summary>
    internal sealed class ScheduleQueries
    {
        private readonly ScheduleTracker Tracker;
        private readonly IMonitor Monitor;
        private readonly string Prefix;
        private readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

        public ScheduleQueries(ScheduleTracker tracker, IMonitor monitor, string prefix)
        {
            this.Tracker = tracker;
            this.Monitor = monitor;
            this.Prefix = prefix;
        }

        /// <summary>The query name marker used to detect schedule-dependent Appearance conditions.</summary>
        public string Marker => $"{this.Prefix}_SCHEDULE_";

        public void Register()
        {
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_VISITS", this.Visits);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_PASSES_THROUGH", this.PassesThrough);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_BEFORE_LEAVING", this.BeforeLeaving);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_CURRENT_TARGET", this.CurrentTarget);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_CURRENT_ROUTE", this.CurrentRoute);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_LEAVES_FOR", this.LeavesFor);
            GameStateQuery.Register($"{this.Prefix}_SCHEDULE_MINUTES_BEFORE_LEAVING", this.MinutesBeforeLeaving);
        }

        /*********
        ** Queries
        *********/
        private bool Visits(string[] query, GameStateQueryContext context)
        {
            if (!this.TryParse(query, out ScheduleInfo info, out HashSet<string> locations, out bool error))
                return error ? GameStateQuery.Helpers.ErrorResult(query, "usage: <npc> <location>+") : false;

            return info.Stops.Any(stop => locations.Contains(stop.Location ?? ""));
        }

        private bool PassesThrough(string[] query, GameStateQueryContext context)
        {
            if (!this.TryParse(query, out ScheduleInfo info, out HashSet<string> locations, out bool error))
                return error ? GameStateQuery.Helpers.ErrorResult(query, "usage: <npc> <location>+") : false;

            return info.Stops.Any(stop => stop.Through.Any(locations.Contains));
        }

        private bool BeforeLeaving(string[] query, GameStateQueryContext context)
        {
            if (!this.TryParse(query, out ScheduleInfo info, out HashSet<string> locations, out bool error))
                return error ? GameStateQuery.Helpers.ErrorResult(query, "usage: <npc> <location>") : false;

            int last = -1;
            for (int i = 0; i < info.Stops.Count; i++)
            {
                if (locations.Contains(info.Stops[i].Location ?? ""))
                    last = i;
            }
            if (last < 0)
                return false;

            int departure = last + 1 < info.Stops.Count ? info.Stops[last + 1].Time : int.MaxValue;
            return Game1.timeOfDay < departure;
        }

        private bool CurrentTarget(string[] query, GameStateQueryContext context)
        {
            if (!this.TryParse(query, out ScheduleInfo info, out HashSet<string> locations, out bool error))
                return error ? GameStateQuery.Helpers.ErrorResult(query, "usage: <npc> <location>+") : false;

            int index = info.GetCurrentStopIndex(Game1.timeOfDay);
            return index >= 0 && locations.Contains(info.Stops[index].Location ?? "");
        }

        private bool CurrentRoute(string[] query, GameStateQueryContext context)
        {
            if (!this.TryParse(query, out ScheduleInfo info, out HashSet<string> locations, out bool error))
                return error ? GameStateQuery.Helpers.ErrorResult(query, "usage: <npc> <location>+") : false;

            int index = info.GetCurrentStopIndex(Game1.timeOfDay);
            if (index < 0)
                return false;

            ScheduleStop stop = info.Stops[index];
            return locations.Contains(stop.Location ?? "") || stop.Through.Any(locations.Contains);
        }

        private bool LeavesFor(string[] query, GameStateQueryContext context)
        {
            if (!ArgUtility.TryGet(query, 1, out string npcName, out string error, allowBlank: false)
                || !ArgUtility.TryGet(query, 2, out string location, out error, allowBlank: false)
                || !ArgUtility.TryGetInt(query, 3, out int min, out error)
                || !ArgUtility.TryGetOptionalInt(query, 4, out int max, out error, int.MaxValue))
                return GameStateQuery.Helpers.ErrorResult(query, error);

            ScheduleInfo info = this.GetInfo(npcName);
            if (info == null)
                return false;

            return info.Stops.Any(stop => string.Equals(stop.Location, location, StringComparison.OrdinalIgnoreCase) && stop.Time >= min && stop.Time <= max);
        }

        private bool MinutesBeforeLeaving(string[] query, GameStateQueryContext context)
        {
            if (!ArgUtility.TryGet(query, 1, out string npcName, out string error, allowBlank: false)
                || !ArgUtility.TryGet(query, 2, out string location, out error, allowBlank: false)
                || !ArgUtility.TryGetInt(query, 3, out int min, out error)
                || !ArgUtility.TryGetOptionalInt(query, 4, out int max, out error, int.MaxValue))
                return GameStateQuery.Helpers.ErrorResult(query, error);

            ScheduleInfo info = this.GetInfo(npcName);
            if (info == null)
                return false;

            int minutes = GetMinutesBeforeLeaving(info, location, Game1.timeOfDay);
            return minutes >= 0 && minutes >= min && minutes <= max;
        }

        /// <summary>
        /// In-game minutes from <paramref name="timeOfDay"/> until the NPC sets off from her current or next visit to
        /// the location (the time of the first stop elsewhere after it). -1 if she doesn't visit it again today, or
        /// doesn't leave it again today.
        /// </summary>
        public static int GetMinutesBeforeLeaving(ScheduleInfo info, string location, int timeOfDay)
        {
            int current = info.GetCurrentStopIndex(timeOfDay);

            // The visit she's on (current stop is there) or the next one.
            int visit = -1;
            for (int i = Math.Max(current, 0); i < info.Stops.Count; i++)
            {
                if (string.Equals(info.Stops[i].Location, location, StringComparison.OrdinalIgnoreCase))
                {
                    visit = i;
                    break;
                }
            }
            if (visit < 0)
                return -1;

            // Several stops in a row at the same location are one visit: she leaves at the first stop elsewhere.
            for (int i = visit + 1; i < info.Stops.Count; i++)
            {
                if (!string.Equals(info.Stops[i].Location, location, StringComparison.OrdinalIgnoreCase))
                {
                    int departure = info.Stops[i].Time;
                    return departure > timeOfDay ? Utility.CalculateMinutesBetweenTimes(timeOfDay, departure) : -1;
                }
            }
            return -1;
        }

        /*********
        ** Helpers
        *********/
        /// <summary>Parse "&lt;npc&gt; &lt;location&gt;+". Returns false with error=false when the NPC is unknown or has no schedule today (query is simply false).</summary>
        private bool TryParse(string[] query, out ScheduleInfo info, out HashSet<string> locations, out bool error)
        {
            info = null;
            locations = null;
            error = query.Length < 3 || string.IsNullOrWhiteSpace(query[1]);
            if (error)
                return false;

            info = this.GetInfo(query[1]);
            if (info == null)
                return false;

            locations = new HashSet<string>(query.Skip(2).Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);
            return true;
        }

        private ScheduleInfo GetInfo(string npcName)
        {
            NPC npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
            {
                if (this.Warned.Add(npcName))
                    this.Monitor.Log($"Schedule query: no NPC named '{npcName}'. The query is false.", LogLevel.Warn);
                return null;
            }

            return this.Tracker.Get(npc);
        }
    }
}
