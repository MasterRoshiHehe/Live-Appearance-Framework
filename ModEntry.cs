using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiveAppearanceFramework.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Delegates;
using StardewValley.GameData.Characters;
using StardewValley.Triggers;

namespace LiveAppearanceFramework
{
    /// <summary>
    /// Live Appearance Framework: makes the game re-check NPC Appearance entries (Data/Characters) during the day and
    /// applies the result right away. Content Patcher decides which outfit applies; LAF only decides when to ask again.
    /// </summary>
    internal sealed class ModEntry : Mod
    {
        private ModConfig Config;
        private DynamicNpcRegistry Registry;
        private PositionalAudioBridge PositionalAudio;
        private RefreshEngine Engine;
        private LiveAppearanceApi Api;
        private ScheduleTracker Schedules;

        private string QueryName;
        private string RefreshActionName;

        private readonly HashSet<string> WarnedQueryErrors = new(StringComparer.Ordinal);

        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();

            string id = this.ModManifest.UniqueID;
            this.QueryName = $"{id}_NPC_APPEARANCE";
            this.RefreshActionName = $"{id}_Refresh";

            AppearanceResolver.Init(helper.Reflection);
            this.Schedules = new ScheduleTracker(helper.Reflection, this.Monitor);
            var scheduleQueries = new ScheduleQueries(this.Schedules, this.Monitor, id);
            this.Registry = new DynamicNpcRegistry(this.Monitor, this.QueryName, scheduleQueries.Marker);
            this.PositionalAudio = new PositionalAudioBridge(this.Monitor, helper.Reflection, this.QueryName);
            this.Engine = new RefreshEngine(this.Monitor, () => this.Config, this.Registry, this.PositionalAudio, new AnimationFrameGuard(helper.Reflection, this.Monitor), this.Schedules);
            this.Api = new LiveAppearanceApi(this.Engine);

            try
            {
                AppearancePatches.Apply(new HarmonyLib.Harmony(id), this.Monitor, () => this.Config, this.Engine);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't patch NPC.ChooseAppearance; other mods' appearance changes may interrupt animations: {ex.Message}", LogLevel.Warn);
            }

            GameStateQuery.Register(this.QueryName, this.QueryNpcAppearance);
            scheduleQueries.Register();
            TriggerActionManager.RegisterAction(this.RefreshActionName, this.ActionRefresh);

            helper.ConsoleCommands.Add("laf_watch", "Lists the NPCs whose appearance can change during the day, and what each one is watched for.", this.CommandWatch);
            helper.ConsoleCommands.Add("laf_why", "Explains an NPC's appearance choice.\n\nUsage: laf_why <npc>", this.CommandWhy);
            helper.ConsoleCommands.Add("laf_schedule", "Shows an NPC's schedule for today as LAF's schedule queries see it.\n\nUsage: laf_schedule <npc>", this.CommandSchedule);
            helper.ConsoleCommands.Add("laf_refresh", "Forces an appearance re-check.\n\nUsage: laf_refresh [npc|all]\n- npc: re-apply that NPC's appearance.\n- all (default): every dynamic NPC in your location.", this.CommandRefresh);

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += (_, _) => this.Engine.Reset();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.Engine.Reset();
            helper.Events.GameLoop.DayStarted += (_, _) => this.Engine.OnDayStarted();
            helper.Events.GameLoop.UpdateTicked += (_, _) => this.Engine.OnUpdateTicked();
            helper.Events.Player.Warped += this.OnWarped;
            helper.Events.Content.AssetsInvalidated += this.OnAssetsInvalidated;
        }

        public override object GetApi()
        {
            return this.Api;
        }

        /*********
        ** Events
        *********/
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.PositionalAudio.Init(this.Helper.ModRegistry);
            this.RegisterGmcm();
        }

        private void OnWarped(object sender, WarpedEventArgs e)
        {
            if (e.IsLocalPlayer)
                this.Engine.OnWarped();
        }

        private void OnAssetsInvalidated(object sender, AssetsInvalidatedEventArgs e)
        {
            if (e.NamesWithoutLocale.Any(name => name.IsEquivalentTo("Data/Characters")))
                this.Engine.OnCharacterDataChanged();

            this.PositionalAudio.OnAssetsInvalidated(e.NamesWithoutLocale);
        }

        /*********
        ** Game state query
        *********/
        /// <summary>
        /// <c>tyr4ntx.LiveAppearanceFramework_NPC_APPEARANCE &lt;npc&gt; &lt;appearance id&gt;+</c>: true if the Appearance entry the
        /// game would pick for the NPC right now is one of the given IDs. Uses a dry run, so it's correct even for NPCs in
        /// locations nobody is watching, and gives the same answer on every client.
        /// </summary>
        private bool QueryNpcAppearance(string[] query, GameStateQueryContext context)
        {
            if (!ArgUtility.TryGet(query, 1, out string npcName, out string error, allowBlank: false))
                return GameStateQuery.Helpers.ErrorResult(query, error);
            if (query.Length < 3)
                return GameStateQuery.Helpers.ErrorResult(query, "at least one appearance ID is required");

            if (AppearanceResolver.IsResolving(npcName))
            {
                if (this.WarnedQueryErrors.Add("circular:" + npcName))
                    this.Monitor.Log($"{this.QueryName} was used inside {npcName}'s own Appearance conditions. That's circular, so it returns false there.", LogLevel.Warn);
                return false;
            }

            NPC npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
                return false;

            string current = this.Engine.GetExpectedId(npc);
            for (int i = 2; i < query.Length; i++)
            {
                if (string.Equals(query[i], current, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /*********
        ** Trigger action
        *********/
        /// <summary><c>tyr4ntx.LiveAppearanceFramework_Refresh [npc|All]+</c>: re-check the given NPCs (or every dynamic NPC in the player's location).</summary>
        private bool ActionRefresh(string[] args, TriggerActionContext context, out string error)
        {
            error = null;
            if (!Context.IsWorldReady)
                return true;

            if (args.Length < 2 || args.Skip(1).Any(arg => arg.Equals("All", StringComparison.OrdinalIgnoreCase)))
            {
                this.Engine.RequestLocationRefresh("TriggerAction");
                return true;
            }

            List<string> missing = new();
            for (int i = 1; i < args.Length; i++)
            {
                NPC npc = Game1.getCharacterFromName(args[i]);
                if (npc == null)
                    missing.Add(args[i]);
                else
                    this.Engine.Refresh(npc, "TriggerAction");
            }

            if (missing.Count > 0)
            {
                error = $"no NPC found with name {string.Join(", ", missing.Select(p => $"'{p}'"))}";
                return false;
            }
            return true;
        }

        /*********
        ** Console commands
        *********/
        private void CommandWatch(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }

            this.Registry.EnsureFresh(this.Config);
            var all = this.Registry.All.OrderBy(p => p.Name).ToList();
            if (all.Count == 0)
            {
                this.Monitor.Log("No NPC has conditional Appearance entries, so LAF has nothing to do.", LogLevel.Info);
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"{all.Count} NPC(s) can change appearance during the day (clock changes are checked for all of them):");
            foreach (DynamicNpcInfo info in all)
            {
                NPC npc = Game1.getCharacterFromName(info.Name);
                string where = npc?.currentLocation?.NameOrUniqueName ?? "not loaded";
                text.Append($"  {info.Name,-16} {info.EntryCount} entries, in {where}");
                if (info.PositionSensitive)
                    text.Append($" | tile: {string.Join(", ", info.PositionQueries)}");
                if (info.AnimationSensitive)
                    text.Append($" | animation: {string.Join(", ", info.AnimationQueries)}");
                if (info.ScheduleSensitive)
                    text.Append(" | schedule");
                if (npc != null)
                    text.Append($" | wears '{this.Engine.GetAppliedId(npc) ?? "(default)"}'");
                text.AppendLine();
            }
            text.Append(this.PositionalAudio.IsLoaded ? "Positional Audio is installed." : "Positional Audio isn't installed.");
            this.Monitor.Log(text.ToString(), LogLevel.Info);
        }

        private void CommandWhy(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            if (args.Length < 1)
            {
                this.Monitor.Log("Usage: laf_why <npc>", LogLevel.Info);
                return;
            }

            NPC npc = Game1.getCharacterFromName(args[0]);
            if (npc == null)
            {
                this.Monitor.Log($"No NPC found with name '{args[0]}'.", LogLevel.Info);
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"{npc.Name} at {Game1.timeOfDay}, in {npc.currentLocation?.NameOrUniqueName ?? "(no location)"} (tile {npc.TilePoint.X}, {npc.TilePoint.Y}){(npc.doingEndOfRouteAnimation.Value ? $", animating '{npc.endOfRouteBehaviorName.Value}'" : "")}:");

            if (!RefreshEngine.CanHandle(npc, out string reason))
                text.AppendLine($"  LAF won't touch her right now: {reason}.");
            if (this.Engine.IsTalkingTo(npc))
                text.AppendLine("  Someone is talking to her: changes wait until the dialogue closes.");
            if (this.Engine.IsFallbackActive(npc))
                text.AppendLine($"  Her outfit sheet lacks frames of animation '{npc.endOfRouteBehaviorName.Value}', so LAF shows '{AppearanceResolver.GetSpriteAsset(npc)}' until it ends. Outfit changes wait until then.");
            string scheduleInfo = this.DescribeSchedule(npc);
            if (scheduleInfo != null)
                text.AppendLine("  " + scheduleInfo);
            string animationInfo = this.Engine.DescribeAnimation(npc);
            if (animationInfo != null)
                text.AppendLine("  " + animationInfo);
            if (this.Engine.HasPending(npc, out string pendingTrigger))
                text.AppendLine($"  A refresh is queued ({pendingTrigger}).");

            var trace = new List<EntryTrace>();
            ResolveResult result = npc.currentLocation != null ? AppearanceResolver.Resolve(npc, trace) : null;
            if (result == null)
            {
                text.Append("  The game wouldn't pick an appearance for her here.");
                this.Monitor.Log(text.ToString(), LogLevel.Info);
                return;
            }

            if (result.IslandAttire)
                text.AppendLine("  She's in island attire: only IsIslandAttire entries count.");
            if (result.MapPortrait != null)
                text.AppendLine($"  The location's UniquePortrait map property forces {result.MapPortrait}.");
            if (result.MapSprite != null)
                text.AppendLine($"  The location's UniqueSprite map property forces {result.MapSprite}.");

            CharacterData data = npc.GetData();
            if (data?.Appearance == null || data.Appearance.Count == 0)
                text.AppendLine("  She has no Appearance entries.");
            else if (trace.Count == 0)
                text.AppendLine("  Appearance entries weren't checked (both textures come from map properties).");

            foreach (EntryTrace line in trace)
            {
                CharacterAppearanceData entry = line.Entry;
                bool isWinner = ReferenceEquals(entry, result.Winner);
                text.AppendLine($"  {(isWinner ? "=>" : "  ")} {entry.Id}  (Precedence {entry.Precedence}, Weight {entry.Weight}{(entry.Season.HasValue ? $", {entry.Season}" : "")}{(!entry.Indoors ? ", not indoors" : "")}{(!entry.Outdoors ? ", not outdoors" : "")}{(entry.IsIslandAttire ? ", island" : "")})");
                text.AppendLine($"       {(isWinner ? "WINNER" : line.Status)}");
                if (!string.IsNullOrWhiteSpace(entry.Condition))
                    text.AppendLine($"       Condition: {entry.Condition}");
            }
            if (result.Candidates > 1)
                text.AppendLine($"  {result.Candidates} entries tied at the best Precedence: the winner is today's weighted random pick (same all day, same for every player).");

            (string portrait, string sprite) = AppearanceResolver.GetExpectedAssets(result);
            text.AppendLine($"  Result: '{result.WinnerId ?? "(no entry: default textures)"}' → sprite {sprite}, portrait {portrait}");
            text.AppendLine($"  Loaded now: sprite {AppearanceResolver.GetSpriteAsset(npc) ?? "(none)"}, portrait {AppearanceResolver.GetLoadedPortrait(npc)?.Name ?? "(none)"}{(AppearanceResolver.TexturesMatch(npc, result) ? " (matches)" : " (DIFFERENT)")}");
            text.Append($"  Game's LastAppearanceId: '{npc.LastAppearanceId ?? "(null)"}'. {this.Engine.DescribeState(npc)}.");

            this.Monitor.Log(text.ToString(), LogLevel.Info);
        }

        private void CommandRefresh(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }

            if (args.Length == 0 || args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                this.Registry.EnsureFresh(this.Config);
                this.Engine.RefreshLocation(Game1.currentLocation, "Console", force: true);
                this.Monitor.Log($"Re-checked every dynamic NPC in {Game1.currentLocation?.NameOrUniqueName}.", LogLevel.Info);
                return;
            }

            NPC npc = Game1.getCharacterFromName(args[0]);
            if (npc == null)
            {
                this.Monitor.Log($"No NPC found with name '{args[0]}'.", LogLevel.Info);
                return;
            }

            RefreshOutcome outcome = this.Engine.Refresh(npc, "Console", force: true);
            this.Monitor.Log($"{npc.Name}: {outcome}. Now wearing '{this.Engine.GetAppliedId(npc) ?? "(default)"}'.", LogLevel.Info);
        }

        private void CommandSchedule(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Info);
                return;
            }
            if (args.Length < 1)
            {
                this.Monitor.Log("Usage: laf_schedule <npc>", LogLevel.Info);
                return;
            }

            NPC npc = Game1.getCharacterFromName(args[0]);
            if (npc == null)
            {
                this.Monitor.Log($"No NPC found with name '{args[0]}'.", LogLevel.Info);
                return;
            }
            if (!ScheduleTracker.CanRead)
            {
                this.Monitor.Log("Schedules are only known to the host (farmhand support comes in a later version).", LogLevel.Info);
                return;
            }

            ScheduleInfo info = this.Schedules.Get(npc);
            if (info == null)
            {
                this.Monitor.Log($"{npc.Name} has no schedule today (key: '{npc.ScheduleKey ?? "(none)"}'). Schedule queries are false for her.", LogLevel.Info);
                return;
            }

            int current = info.GetCurrentStopIndex(Game1.timeOfDay);
            var text = new StringBuilder();
            text.AppendLine($"{npc.Name}'s schedule today (key '{info.Key ?? "(none)"}'), starting in {info.StartLocation}. Now {Game1.timeOfDay}, in {npc.currentLocation?.NameOrUniqueName ?? "(no location)"}:");
            for (int i = 0; i < info.Stops.Count; i++)
            {
                ScheduleStop stop = info.Stops[i];
                string marker = i == current ? "=>" : "  ";
                string through = stop.Through.Length > 0 ? $" via {string.Join(" > ", stop.Through)}" : "";
                string behavior = string.IsNullOrEmpty(stop.Behavior) ? "" : $", then '{stop.Behavior}'";
                text.AppendLine($"  {marker} {stop.Time,4}: from {info.GetOrigin(i)}{through} to {stop.Location} ({stop.Tile.X}, {stop.Tile.Y}){behavior}");
            }
            text.Append(current < 0 ? "  (Before her first stop: SCHEDULE_CURRENT_TARGET/ROUTE are false.)" : "  (=> marks the stop she's walking to or standing at.)");
            this.Monitor.Log(text.ToString(), LogLevel.Info);
        }

        /// <summary>One line for laf_why.</summary>
        private string DescribeSchedule(NPC npc)
        {
            if (!ScheduleTracker.CanRead)
                return null;
            ScheduleInfo info = this.Schedules.Get(npc);
            if (info == null)
                return "Schedule: none today.";
            int current = info.GetCurrentStopIndex(Game1.timeOfDay);
            string now = current >= 0 ? $"current stop {info.Stops[current].Time} → {info.Stops[current].Location}" : "before her first stop";
            return $"Schedule: key '{info.Key ?? "(none)"}', {info.Stops.Count} stop(s) ({string.Join(", ", info.Stops.Select(p => $"{p.Time} {p.Location}"))}), {now}. Details: laf_schedule {npc.Name}";
        }

        /*********
        ** GMCM
        *********/
        private void RegisterGmcm()
        {
            var gmcm = this.Helper.ModRegistry.GetApi<IGmcmApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null)
                return;

            gmcm.Register(
                mod: this.ModManifest,
                reset: () => this.Config = new ModConfig(),
                save: () =>
                {
                    this.Helper.WriteConfig(this.Config);
                    this.Registry.MarkDirty();
                }
            );

            gmcm.AddBoolOption(this.ModManifest, () => this.Config.Enabled, v => this.Config.Enabled = v,
                () => "Enabled", () => "Re-check NPC outfits during the day. Off: outfits only change at the game's usual moments (day start, changing location).");

            gmcm.AddSectionTitle(this.ModManifest, () => "When to re-check");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.RefreshOnClock, v => this.Config.RefreshOnClock = v,
                () => "Clock changes", () => "Every 10 in-game minutes, for NPCs in your location.");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.RefreshOnTile, v => this.Config.RefreshOnTile = v,
                () => "NPC moves", () => "When an NPC steps onto another tile, if her outfit conditions depend on her position.");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.RefreshOnAnimation, v => this.Config.RefreshOnAnimation = v,
                () => "NPC animates", () => "When an NPC's schedule animation starts or stops, if her outfit conditions depend on it.");
            gmcm.AddNumberOption(this.ModManifest, () => this.Config.ScanInterval, v => this.Config.ScanInterval = v,
                () => "Scan interval (ticks)", () => "How often LAF looks for NPC movement/animation changes. 60 ticks = 1 second. Lower reacts faster.", min: 1, max: 60);

            gmcm.AddSectionTitle(this.ModManifest, () => "Compatibility");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.AnimationFrameFallback, v => this.Config.AnimationFrameFallback = v,
                () => "Keep animations visible", () => "Only change an NPC's outfit sheet when the new sheet has the frames she's using, and if her outfit sheet lacks her schedule animation's frames (e.g. Harem Valley scenes with custom outfits), show a sheet that has them until it ends.");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.RepairInterruptedAnimations, v => this.Config.RepairInterruptedAnimations = v,
                () => "Repair interrupted animations", () => "Restart an NPC's schedule animation when another mod interrupted it and she's still on her spot (e.g. after a kiss), and end it properly when a mod leads her away mid-animation (e.g. a companion recruiter).");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.DeferDuringAnimations, v => this.Config.DeferDuringAnimations = v,
                () => "Wait for animations", () => "Don't change an NPC's sprite in the middle of a schedule animation. Only needed if an outfit sheet lacks the animation frames.");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.RefreshPositionalAudio, v => this.Config.RefreshPositionalAudio = v,
                () => "Update Positional Audio", () => "After an outfit change, let Positional Audio re-check sounds that depend on outfits (only if it's installed).");

            gmcm.AddSectionTitle(this.ModManifest, () => "Debug");
            gmcm.AddBoolOption(this.ModManifest, () => this.Config.DebugLogging, v => this.Config.DebugLogging = v,
                () => "Debug logging", () => "Log every outfit change in the SMAPI console. Console commands: laf_watch, laf_why <npc>, laf_refresh [npc].");
        }
    }
}
