# Live Appearance Framework (LAF)

In Stardew Valley 1.6, an NPC's outfit comes from the `Appearance` entries in `Data/Characters`. But the game only
picks an entry at certain moments: the start of the day, when the NPC changes location, and a few others. If her
pyjamas are valid until 9:30 and you stand next to her at 9:30, she keeps them on until she walks into another map.

LAF makes the game check again **while you watch**, and applies the result right away:

- when the clock changes (every 10 in-game minutes),
- when you enter a location (the game doesn't re-check NPCs who were already there),
- when an NPC steps onto another tile, if her conditions depend on her position,
- when her schedule animation starts or stops, if her conditions depend on it,
- after events, after you finish talking to her, and whenever a mod or trigger action asks.

**LAF has no outfit system of its own.** You write normal Content Patcher `Appearance` entries with normal game state
queries. With LAF installed they update live. Without LAF they still work, just at the game's usual moments.

## For pack authors

### Just write Appearance entries
```json
{
  "Action": "EditData",
  "Target": "Data/Characters",
  "TargetField": [ "Haley", "Appearance" ],
  "Entries": {
    "{{ModId}}_Pyjamas": {
      "Id": "{{ModId}}_Pyjamas",
      "Condition": "TIME 600 930",
      "Portrait": "Portraits/Haley_Pyjamas",
      "Sprite": "Characters/Haley_Pyjamas",
      "Precedence": -1000
    }
  }
}
```
`TIME 600 930` includes 9:30, so at 9:40 she changes, even if you're standing in her room. Nothing else to do.

Reminders about vanilla's rules (LAF follows them exactly):
- The **lowest `Precedence`** that matches wins. Entries with a higher Precedence aren't even checked once a lower one
  matched.
- Ties at the same Precedence are a weighted random pick. It's the same all day and for every player, so checking
  more often never re-rolls.
- `TIME <min> <max>` includes both ends and takes **one** range. For two windows use `ANY "TIME 600 930" "TIME 2000 2600"`.
- The sheets an NPC switches between must share the same frame layout. LAF swaps the texture in place and keeps the
  current frame.

### Schedule animations and outfit sheets
Some mods add animation frames only to an NPC's **default** sheet. Harem Valley, for example, paints Haley's scene
frames (196+) into `Characters/Haley` and `Characters/Haley_Winter`. An outfit sheet from another mod is often smaller
(a 64×416 sheet only holds frames 0–51). When she plays such an animation on that sheet, the game can't find the frame
and silently shows frame 0: she just stands there, while the animation's dialogue and sounds still run.

LAF handles this for you (config `AnimationFrameFallback`, on by default):
- **Frame check before every outfit switch.** LAF only switches an NPC to a sheet that has every frame she's using
  right now: her current frame, the frames queued in her sprite's animation, and her schedule animation. If the new
  sheet lacks any of them, the switch waits and is retried until she's back on frames it has.
- **Fallback sheet.** If she plays a schedule animation her current sheet can't show (for example because the game
  gave her that outfit when she entered the room), LAF shows the first sheet that has the frames (the winter sheet in
  winter, then the default sheet) until the animation, including its outro, has ended. Then her Appearance entry
  comes back.
- **Interrupted animations** (config `RepairInterruptedAnimations`, on by default). Other mods sometimes break into
  a schedule animation without ending it properly:
  - *Replaced and not restarted* (e.g. a kiss animation from another mod): she stands on her spot, the game still
    thinks she's animating, but nothing plays. After a short moment, LAF restarts the loop the way the game builds it.
  - *Led away mid-loop* (e.g. a companion recruiter): the loop stays on her sprite and overrides her walking frames,
    so she only turns and doesn't walk. LAF ends the animation the way the game does at the end of its outro, and her
    walking and outfit come back.
  - LAF remembers the tile where the animation started. A companion who stops somewhere else isn't treated as
    "in the scene", even if the game's flag is still on. Vanilla animations with special setup (Clint's hammer,
    Abigail's video games...) are never restarted by LAF.
- **Events.** Event actors start with the outfit sheet the NPC wears. When an event's `animate` or `showFrame`
  command (or another mod's command that animates an actor) uses frames that sheet doesn't have, LAF switches that
  actor to a sheet that has them (winter sheet in winter, then the default sheet) for the rest of the event. The
  overworld NPC isn't affected.
- **Event actors and position conditions** (config `EventActorsUseOwnPosition`, on by default). An event actor is a
  copy of the NPC. Queries that find an NPC by name (BETAS `NPC_NEAR_AREA`, Positional Audio `NPC_POSITION`...)
  would find the real NPC, who is still wherever her schedule put her. For example, "pyjamas while Haley is in her
  room" would stay on in an outdoor event started while she's in her room. While an actor picks its outfit, LAF
  makes those queries see the actor's tile instead (or "not here" if the actor is in another map). Limit: a query
  for the event's own map can't find the actor (actors aren't part of the map's NPC list), so it's false.
- **Outfits for event actors.** Events don't choose a sheet: every actor starts on the default sheet, and the game
  then gives it the Appearance entry that matches, like the NPC herself. So a beach outfit that's valid at 10:00 also
  shows in an event at 10:00, and the event's poses are drawn from the beach sheet. To keep an outfit off event actors,
  add `!MasterRoshiHehe.LiveAppearanceFramework_EVENT_ACTOR` to its condition (see "LAF's game state queries"). The
  actor then gets the next matching entry (or the default sheet), and the NPC herself keeps the outfit. Events that
  want a specific sheet use `changeSprite`, which Appearance entries never override.
- **Other mods' appearance changes.** Any mod (or the game) can re-pick an outfit at any time, e.g. when a
  conversation starts. LAF hooks the game's `NPC.ChooseAppearance` so that, while an NPC animates, the new outfit is
  still applied but her current frame, frame size and a sheet that has her animation frames are kept. Without this
  she'd snap to frame 0 (standing, facing down) until the animation moved on.

All of this costs a few number comparisons per NPC in your location per tick.

If you'd rather have your outfit visible during those animations, add the missing frames to your outfit sheets (same
rows as the other mod uses). LAF then has nothing to do.

### Position and animation
LAF re-checks an NPC when she moves or animates **only if** her conditions use one of these queries (it scans your
conditions for them):

| Query | Mod | Re-checked when |
|---|---|---|
| `Spiderbuttons.BETAS_NPC_NEAR_AREA` | BETAS | she steps onto another tile |
| `Spiderbuttons.BETAS_NPC_NEAR_NPC` | BETAS | she steps onto another tile |
| `ichortower.PositionalAudio_NPC_POSITION` / `_NPC_POSITION_RECT` | Positional Audio | she steps onto another tile |
| `ichortower.PositionalAudio_NPC_ANIMATING` | Positional Audio | her schedule animation starts/stops |

Other queries can be added in `config.json` (`ExtraPositionQueries`, `ExtraAnimationQueries`).

**For outfits, prefer `Spiderbuttons.BETAS_NPC_NEAR_AREA`.** Positional Audio's position queries return false while
the NPC walks, so an outfit using them flips every time she starts walking. In multiplayer, only the host can tell
that she's walking, so host and farmhands would see different outfits. `NPC_ANIMATING` has neither problem.

### LAF's game state queries
`MasterRoshiHehe.LiveAppearanceFramework_EVENT_ACTOR [event id]+` is true while the game chooses the outfit of an event
actor (of one of these events, if IDs are given). Use it in Appearance conditions: `!..._EVENT_ACTOR` keeps an outfit
off event actors, `!..._EVENT_ACTOR 14` only off event 14's actors. Unlike vanilla's `IS_EVENT`, it's false for the
NPC herself, so her own outfit isn't touched while an event runs.

`MasterRoshiHehe.LiveAppearanceFramework_NPC_APPEARANCE <npc> <appearance id>+`

True if the Appearance entry the game would pick for that NPC right now is one of the IDs (case-insensitive). Use it
anywhere a game state query works: dialogue (`$query`), event preconditions (`G`), trigger action conditions, other
Appearance entries, Positional Audio sounds. (Content Patcher's `When` can't check game state queries.)
Don't use it inside that same NPC's own Appearance conditions (circular; it returns false there and logs a warning).

### Schedule queries ("where is she going today?")
These read the schedule the game actually loaded for today, so they work for any schedule source (vanilla, Harem
Valley, Custom Schedule Keys, CP edits...), not just schedule key names. Prefix: `MasterRoshiHehe.LiveAppearanceFramework_`.

| Query | True if |
|---|---|
| `SCHEDULE_VISITS <npc> <location>+` | a stop today has one of these destinations |
| `SCHEDULE_PASSES_THROUGH <npc> <location>+` | she walks through one of these maps on the way to a stop today (destinations don't count) |
| `SCHEDULE_BEFORE_LEAVING <npc> <location>` | she visits the location today, and it's before she sets off from her last stop there (false if she never goes there) |
| `SCHEDULE_CURRENT_TARGET <npc> <location>+` | the stop she's walking to or standing at right now has one of these destinations (false before her first stop) |
| `SCHEDULE_CURRENT_ROUTE <npc> <location>+` | her current leg passes through or ends at one of these maps |
| `SCHEDULE_LEAVES_FOR <npc> <location> <min time> [max time]` | she sets off towards the location between these times today |
| `SCHEDULE_MINUTES_BEFORE_LEAVING <npc> <location> <min> [max]` | the in-game minutes until she sets off from her current (or next) visit to the location are between min and max |

Good to know:
- `SCHEDULE_MINUTES_BEFORE_LEAVING` counts real clock minutes (13:30 − 12:40 = 50), so "the last 30 minutes before she
  leaves the Beach" is `... Beach 0 30` whatever time her schedule leaves. Several stops in a row at the same location
  are one visit. False once she has left, or if she doesn't leave again today.
- A stop's time is when she **sets off**, not when she arrives (walking time isn't known in advance). For the real
  arrival moment, use BETAS's `Spiderbuttons.BETAS_NpcArrived` trigger with LAF's `_Refresh` trigger action.
- Location names are the map names the game uses (`Beach`, `Town`, `HaleyHouse`...), case-insensitive.
- The maps she walks through are worked out the same way the game plans her route.
- LAF re-checks outfits on every clock change (stops start at clock times) and whenever her schedule is replaced
  (Ginger Island days, loading a save, schedule edits applied mid-day).
- `laf_schedule <npc>` lists today's stops, the maps in between, and which stop she's on.
- Multiplayer: currently the host knows schedules; on farmhands these queries are false (coming in a later version).

Example: Haley leaves home in her beach outfit on sunny days her schedule goes to the Beach, and switches back once
she has left the Beach map (for "10 minutes before she leaves" instead, add
`MasterRoshiHehe.LiveAppearanceFramework_SCHEDULE_MINUTES_BEFORE_LEAVING Haley Beach 20`):
```json
"{{ModId}}_Haley_BeachDay": {
  "Id": "{{ModId}}_Haley_BeachDay",
  "Condition": "WEATHER Target Sun, ANY \"MasterRoshiHehe.LiveAppearanceFramework_SCHEDULE_BEFORE_LEAVING Haley Beach\" \"LOCATION_NAME Target Beach\"",
  "Portrait": "Portraits/Haley_Beach",
  "Sprite": "Characters/Haley_Beach",
  "Precedence": -600
}
```
In an Appearance condition, `Target` is the NPC's own location. When her beach stop ends, `BEFORE_LEAVING` turns
false, but `LOCATION_NAME Target Beach` keeps the outfit until she walks off the map; the game then re-picks her
outfit on the map change. Add `SYNCED_RANDOM` for "only some beach days".

### Idle poses ("while she stands there, lie on the towel")
Play frames of the NPC's **current sheet** while she stands still somewhere, without editing any schedule. So it works
with vanilla, Harem Valley, Custom Schedule Keys or any other schedule source. Add entries to the asset
`MasterRoshiHehe.LiveAppearanceFramework/IdlePoses`:

```json
{
  "Action": "EditData",
  "Target": "MasterRoshiHehe.LiveAppearanceFramework/IdlePoses",
  "Entries": {
    "{{ModId}}_Haley_BeachTowel": {
      "Npc": "Haley",
      "Location": "Beach",
      "AtScheduleStop": true,
      "Condition": "MasterRoshiHehe.LiveAppearanceFramework_NPC_APPEARANCE Haley {{ModId}}_BeachDay, MasterRoshiHehe.LiveAppearanceFramework_SCHEDULE_MINUTES_BEFORE_LEAVING Haley Beach 30",
      "StillFor": 1000,
      "Intro": [ { "Frame": 20, "Duration": 300 }, { "Frame": 21, "Duration": 300 } ],
      "Outro": [ { "Frame": 21, "Duration": 300 }, { "Frame": 20, "Duration": 300 } ],
      "ShuffleStages": true,
      "Stages": [
        {
          "Id": "Stomach",
          "Condition": "MasterRoshiHehe.LiveAppearanceFramework_SCHEDULE_MINUTES_BEFORE_LEAVING Haley Beach 30 50",
          "Transition": [ { "Frame": 21, "Duration": 300 } ],
          "Frames": [ { "Frame": 18, "Duration": 4000 }, { "Frame": 19, "Duration": 250 }, { "Frame": 18, "Duration": 250 }, { "Frame": 19, "Duration": 250 } ]
        },
        {
          "Id": "Back",
          "Transition": [ { "Frame": 21, "Duration": 300 } ],
          "Frames": [ { "Frame": 22, "Duration": 1000 } ]
        }
      ]
    }
  }
}
```

| Field | Meaning |
|---|---|
| `Npc` | The NPC (internal name). Required. |
| `Location` | Optional. The map she must be in. |
| `AtScheduleStop` | Optional. She must stand on the destination tile of the schedule stop she's on right now, whatever tile the loaded schedule uses. Host only for now (see Multiplayer). |
| `Tile` | Optional. An exact tile, `"x y"`. |
| `Facing` | Optional. She must face this way when the pose starts (0 up, 1 right, 2 down, 3 left). |
| `Condition` | Optional. A game state query for the whole pose (`Target` = her location). When it turns false, the pose ends. |
| `StillFor` | Milliseconds she must stand still before the pose starts (default 1000). |
| `Intro` | Played once when she goes from standing into the pose. |
| `Outro` | Played once when the pose ends while she still stands there. Then she stands normally. |
| `Frames` | The looping frames, for a pose with a single stage. |
| `Stages` | Several looping stages, each with an optional `Condition`, a `Transition` (played when she switches into it from another stage) and `Frames`. The **first** stage whose condition is true plays, so put a stage without a condition last as the fallback. |
| `ShuffleStages` | Shuffle the stages' animations (`Id`, `Transition`, `Frames`) between the stage conditions once per day, the same for every player. The conditions stay in place. In the example, some days she lies on her back first and on her stomach for the last half hour, other days the other way round. |
| `KeepWhenTalkedTo` | Default true: she stays in the pose while a player talks to her. False: she stands, faces the player, and poses again after the dialogue. |
| `Priority` | If several entries apply to the same NPC, the highest wins. |

Frames are numbered left to right, top to bottom, starting at 0. On a 64 px wide sheet with 16×32 frames, the frame at
pixel (x, y) is `y / 32 * 4 + x / 16`: (32, 160) is frame 22. `Duration` is in milliseconds, `Flip` mirrors a frame.

How it behaves:
- A pose starts when she stands still (position unchanged for `StillFor`), no schedule animation or other animation is
  running, every filter and the condition match, and **every frame of the pose exists on her current sheet**
  (otherwise it doesn't start, and the log says why once).
- If you arrive (warp, save load) while she already stands there, she's already in the pose: no intro.
- It ends at once, without the outro, when she moves (her next schedule leg, a companion mod), a schedule animation or
  another mod's animation starts, or her sheet changes to one without the frames.
- If an outfit change would break the pose (its condition turns false with the new outfit, or the new sheet lacks the
  frames), the outro plays first and the outfit changes once she's up.
- She doesn't turn towards the player while posing (the game doesn't turn NPCs that play an animation).
- Stage conditions and the pose condition are checked every scan interval, so clock-based stages switch within a
  fraction of a second of the clock changing.

`MasterRoshiHehe.LiveAppearanceFramework_NPC_IN_POSE <npc> [pose id | pose id/stage id]+` is true while she plays that pose
(any pose if no ID), e.g. for dialogue or an Appearance entry. Appearance entries using it are re-checked when a pose
starts, switches stage or ends.

### Positional Audio sounds that follow an outfit
Positional Audio re-checks its sounds on its own schedule (clock, movement, animation), not when an outfit changes.
LAF handles that from its side: after an outfit changes, if a Positional Audio sound in the player's location uses
LAF's query, LAF runs Positional Audio's own refresh action. Nothing to set up; Positional Audio isn't required.

```json
{
  "Action": "EditData",
  "Target": "Mods/ichortower.PositionalAudio/Data",
  "Entries": {
    "{{ModId}}_HaleyShower": {
      "Location": "HaleyHouse",
      "Condition": "MasterRoshiHehe.LiveAppearanceFramework_NPC_APPEARANCE Haley {{ModId}}_Towel",
      "CueName": "{{ModId}}_Shower",
      "TilePosition": { "X": 5, "Y": 4 }
    }
  }
}
```
(The field is `Location`, not `LocationName`.)

### Trigger action
`MasterRoshiHehe.LiveAppearanceFramework_Refresh [npc]+` re-checks those NPCs now. With no NPC, or `All`, it re-checks every
changeable NPC in the player's location. Useful after something your conditions depend on changed outside the clock,
e.g. a mail flag set by a dialogue `$action` (the change is applied once the dialogue box closes).

### Things to avoid in Appearance conditions
Conditions are now checked often. Queries with **side effects** (some mods offer queries that set or remove a mail
flag while they're checked) would run every time. Don't use them there. Also avoid `RANDOM` (it re-rolls on every check,
so the outfit would flicker); use `SYNCED_RANDOM day <key> <chance>` instead, which is the same all day for everyone.

## Recipes: LAF with BETAS, vanilla and other mods
LAF doesn't replace any of these mods, it makes their facts count *live*. The split of work:

| Who | Does what |
|---|---|
| **Content Patcher** | Writes the data: Appearance entries, `IdlePoses`, `Data/TriggerActions`, dialogue. |
| **Game state queries** (vanilla, BETAS, LAF, Positional Audio) | Answer "is this true right now?" in conditions. |
| **Trigger actions** (vanilla triggers, BETAS triggers) | React to things that happen: a kiss, a gift, an arrival, a dialogue line. |
| **LAF** | Re-checks outfits and poses when the answer may have changed, applies them safely, and adds facts nobody else has (today's schedule, her outfit, her pose). |

### Which query for which idea
| You want her outfit/pose to depend on... | Use | LAF re-checks it |
|---|---|---|
| the time of day | vanilla `TIME <min> <max>` | every clock change |
| the map she's in | vanilla `LOCATION_NAME Target <map>+` (`Target` = her own location) | when she changes map (vanilla) and when you arrive |
| an area of a map (her room, the dresser, a towel) | BETAS `Spiderbuttons.BETAS_NPC_NEAR_AREA <map> <x> <y> <radius> <npc>` | every time she steps onto another tile |
| another NPC nearby | BETAS `Spiderbuttons.BETAS_NPC_NEAR_NPC <npc> <radius> <other npc>` | when **she** moves (not when the other one does), plus clock changes |
| the player nearby | BETAS `Spiderbuttons.BETAS_NPC_NEAR_PLAYER Current <radius> <npc>` | clock changes only. Each player checks it for themselves, so in multiplayer players can see different outfits. Better for dialogue than outfits. |
| her schedule animation | Positional Audio `ichortower.PositionalAudio_NPC_ANIMATING` | when the animation starts/stops |
| where her schedule takes her today | LAF `SCHEDULE_VISITS`, `SCHEDULE_CURRENT_TARGET`, `SCHEDULE_MINUTES_BEFORE_LEAVING`... | clock changes and schedule changes; works for every schedule source |
| a specific schedule key | BETAS `Spiderbuttons.BETAS_NPC_FOLLOWING_SCHEDULE <npc> <key>+` | clock changes. Only matches key names: LAF's schedule queries also work when another mod (Harem Valley, Custom Schedule Keys) picks the route. |
| being kissed today | BETAS `Spiderbuttons.BETAS_NPC_KISSED_TODAY <npc>` | clock changes, or right away with the trigger below |
| a story flag | vanilla `PLAYER_HAS_MAIL Host <id>` | clock changes, or right away with LAF's `_Refresh` action |
| weather, season, day, hearts | vanilla `WEATHER Target Sun`, `SEASON`, `DAY_OF_WEEK`, `PLAYER_HEARTS Host <npc> <min>` | day start and clock changes |
| a daily coin flip | vanilla `SYNCED_RANDOM day <key> <chance>` | same all day, same for every player |
| her current outfit (for dialogue, sounds, poses) | LAF `NPC_APPEARANCE <npc> <id>+` | - |
| her idle pose (for outfits, dialogue, sounds) | LAF `NPC_IN_POSE <npc> [pose[/stage]]+` | when a pose starts, switches stage or ends |
| not being an event actor (keep an outfit out of events) | LAF `!EVENT_ACTOR [event id]+` | each time the game dresses an actor |

Use `Host` rather than `Current` for player-based conditions in outfits (mail, hearts...), so every player sees the
same outfit. The whole list of vanilla queries is on the wiki (Modding:Game state queries); BETAS's list is in its
documentation.

### Recipe: change right after something happens
The clock re-check can be up to 10 in-game minutes late. For instant changes, run LAF's refresh from a trigger:

```json
{
  "Action": "EditData",
  "Target": "Data/TriggerActions",
  "Entries": {
    "{{ModId}}_HaleyKissed": {
      "Id": "{{ModId}}_HaleyKissed",
      "Trigger": "Spiderbuttons.BETAS_NpcKissed",
      "Condition": "ITEM_ID Target Haley",
      "Action": "MasterRoshiHehe.LiveAppearanceFramework_Refresh Haley",
      "MarkActionApplied": false
    }
  }
}
```
with an Appearance entry whose condition is `Spiderbuttons.BETAS_NPC_KISSED_TODAY Haley`. `MarkActionApplied: false`
is needed, or the trigger only ever runs once per save. The same pattern works with BETAS's `NpcArrived` (she reached
a schedule stop), `GiftGiven` and `DialogueOpened`. For these BETAS triggers, the `Target` item is a placeholder
whose ID is the NPC's name, hence `ITEM_ID Target Haley`.

### Recipe: a story flag from dialogue or an event
In dialogue, two `$action` commands, each in its own `#` segment:
`$action AddMail Host {{ModId}}_HaleyTan received#$action MasterRoshiHehe.LiveAppearanceFramework_Refresh Haley#Do you like my tan?`. The Appearance entry checks
`PLAYER_HAS_MAIL Host {{ModId}}_HaleyTan`. LAF applies the change as soon as the dialogue box closes (never
mid-conversation). In an event script, use the `action` command the same way; LAF re-checks everyone after the event
anyway.

### Recipe: a temporary outfit ("wet for a while after swimming")
Set a flag, then remove it later with BETAS's delay, and let LAF refresh both times:
```json
"Actions": [
  "AddMail Host {{ModId}}_HaleyWet received",
  "MasterRoshiHehe.LiveAppearanceFramework_Refresh Haley",
  "Spiderbuttons.BETAS_DelayedAction \"RemoveMail Host {{ModId}}_HaleyWet\" 60000",
  "Spiderbuttons.BETAS_DelayedAction \"MasterRoshiHehe.LiveAppearanceFramework_Refresh Haley\" 60100"
]
```
(`DelayedAction` counts real milliseconds.) For in-game time windows, use `TIME` or LAF's schedule queries instead.

### Recipe: outfits and poses that follow the schedule
This is how PWR's Haley beach day works; no schedule is edited, so it works with vanilla, Harem Valley and other
schedule mods:
- `SCHEDULE_VISITS Haley Beach` + `SCHEDULE_LEAVES_FOR Haley Town 1100 1600`: "today is a beach day".
- `SCHEDULE_MINUTES_BEFORE_LEAVING Haley Beach 20`: beach outfit until 20 minutes before she leaves, outfit of the day
  after (10 minutes before).
- BETAS `NPC_NEAR_AREA HaleyHouse 5 6 4 Haley` + `TIME 910 920`: she changes at her dresser.
- An idle pose with `AtScheduleStop` + `NPC_APPEARANCE` + `SCHEDULE_MINUTES_BEFORE_LEAVING`: she lies on the towel
  while she wears the beach outfit, and gets up in time.

### Recipe: poses and outfits working together
- **Pose follows the outfit**: put `NPC_APPEARANCE <npc> <id>` in the pose's `Condition`. When the outfit changes,
  the pose ends first (outro), then the outfit changes.
- **Outfit follows the pose**: `NPC_IN_POSE <npc> <pose>/<stage>` in an Appearance entry (e.g. sunglasses while she
  lies on her back). The new sheet must have the pose's frames, or the pose stops.
- Don't do both for the same pair (the pose needs the outfit, the outfit needs the pose): neither would ever start.
- A daily random choice between poses or outfits: `SYNCED_RANDOM day {{ModId}}_towel 0.5` in a condition, or
  `ShuffleStages` inside one pose.

### Recipe: sounds and dialogue
- Positional Audio sound `Condition`: `MasterRoshiHehe.LiveAppearanceFramework_NPC_APPEARANCE Haley {{ModId}}_Towel` (LAF tells
  Positional Audio to re-check when her outfit changes) or `MasterRoshiHehe.LiveAppearanceFramework_NPC_IN_POSE Haley
  {{ModId}}_BeachTowel` (Positional Audio picks up pose changes at its next own check, at the latest on the next
  clock change).
- Dialogue: `$query MasterRoshiHehe.LiveAppearanceFramework_NPC_IN_POSE Haley#Mmh, the sun feels so good...|Hey, @!`

### Other mods
- **Animated Portrait Framework**: works with Appearance roots. LAF picks the root (e.g. `Characters/Haley_Beach`),
  APF rolls its variant. LAF's frame checks and poses look at whatever sheet APF loaded.
- **Harem Valley and other animation mods**: frames they add only to the default sheet stay visible
  (see "Schedule animations and outfit sheets" and "Events").
- **Companion mods**: LAF notices she moved (position-based), ends poses and interrupted schedule loops, and
  `NPC_NEAR_AREA` outfits follow her.
- **BETAS `UpdateAppearance`**: without an ID it's the same as a refresh. With an ID it forces that outfit, but LAF
  puts the entry her conditions choose back at the next re-check. For a lasting change, make the condition true
  (a mail flag) and refresh instead.

## When LAF won't change an NPC
- During events and festivals (event actors are separate NPCs). She's re-checked when the event ends.
- While any local player is talking to her. She's re-checked right after the dialogue box closes.
- If an event or mod overrode her textures (`portraitOverridden` / `spriteOverridden`), or `AllowDynamicAppearance`
  is off.
- NPCs whose appearance can't change during the day (no conditional entry) are never touched.

## Console commands
- `laf_watch`: which NPCs LAF watches, and for what.
- `laf_why <npc>`: every Appearance entry, whether it matched and why, the winner, and what's loaded now.
- `laf_schedule <npc>`: her schedule today as the schedule queries see it.
- `laf_pose <npc>`: her idle poses: which one plays (and its stage), why each entry applies or not, and today's order
  of shuffled stages.
- `laf_refresh [npc|all]`: force a re-check.

## Multiplayer
Every player dresses the NPCs in their own game (textures are local). The inputs (clock, date, NPC positions and
animations, conditions) are the same for everyone, so everyone sees the same outfit. No messages, no save data.
Works in split-screen.

Idle poses are local too, and the game advances their frames on every client. For now, only the host knows schedules,
so poses using `AtScheduleStop` or schedule queries only play on the host (farmhands see her standing). Syncing that is
planned.

## For C# mods
Copy `ILiveAppearanceApi.cs` into your mod and use
`Helper.ModRegistry.GetApi<ILiveAppearanceApi>("MasterRoshiHehe.LiveAppearanceFramework")` (null without LAF):
- `AppearanceChanged` event (NPC, old ID, new ID, trigger),
- `Refresh(npc)`, `GetAppearanceId(npc)`, `GetExpectedAppearanceId(npc)`,
- `IsInPose(npc)`, `GetPoseId(npc)`, `GetPoseStageId(npc)`: her idle pose on this client,
- `SafeChooseAppearance(npc)`: use it instead of `npc.ChooseAppearance()`. Vanilla's method resets the sprite size on
  every call, which breaks animations with wider frames (e.g. Clint hammering). This version keeps them.

