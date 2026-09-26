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

### LAF's game state query
`tyr4ntx.LiveAppearanceFramework_NPC_APPEARANCE <npc> <appearance id>+`

True if the Appearance entry the game would pick for that NPC right now is one of the IDs (case-insensitive). Use it
anywhere a game state query works: dialogue, CP `When` with `{{Query}}`, trigger actions, Positional Audio sounds.
Don't use it inside that same NPC's own Appearance conditions (circular; it returns false there and logs a warning).

### Schedule queries ("where is she going today?")
These read the schedule the game actually loaded for today, so they work for any schedule source (vanilla, Harem
Valley, Custom Schedule Keys, CP edits...), not just schedule key names. Prefix: `tyr4ntx.LiveAppearanceFramework_`.

| Query | True if |
|---|---|
| `SCHEDULE_VISITS <npc> <location>+` | a stop today has one of these destinations |
| `SCHEDULE_PASSES_THROUGH <npc> <location>+` | she walks through one of these maps on the way to a stop today (destinations don't count) |
| `SCHEDULE_BEFORE_LEAVING <npc> <location>` | she visits the location today, and it's before she sets off from her last stop there (false if she never goes there) |
| `SCHEDULE_CURRENT_TARGET <npc> <location>+` | the stop she's walking to or standing at right now has one of these destinations (false before her first stop) |
| `SCHEDULE_CURRENT_ROUTE <npc> <location>+` | her current leg passes through or ends at one of these maps |
| `SCHEDULE_LEAVES_FOR <npc> <location> <min time> [max time]` | she sets off towards the location between these times today |

Good to know:
- A stop's time is when she **sets off**, not when she arrives (walking time isn't known in advance). For the real
  arrival moment, use BETAS's `Spiderbuttons.BETAS_NpcArrived` trigger with LAF's `_Refresh` trigger action.
- Location names are the map names the game uses (`Beach`, `Town`, `HaleyHouse`...), case-insensitive.
- The maps she walks through are worked out the same way the game plans her route.
- LAF re-checks outfits on every clock change (stops start at clock times) and whenever her schedule is replaced
  (Ginger Island days, loading a save, schedule edits applied mid-day).
- `laf_schedule <npc>` lists today's stops, the maps in between, and which stop she's on.
- Multiplayer: currently the host knows schedules; on farmhands these queries are false (coming in a later version).

Example: Haley leaves home in her beach outfit on sunny days her schedule goes to the Beach, and switches back once
she has left the Beach map:
```json
"{{ModId}}_Haley_BeachDay": {
  "Id": "{{ModId}}_Haley_BeachDay",
  "Condition": "WEATHER Target Sun, ANY \"tyr4ntx.LiveAppearanceFramework_SCHEDULE_BEFORE_LEAVING Haley Beach\" \"LOCATION_NAME Target Beach\"",
  "Portrait": "Portraits/Haley_Beach",
  "Sprite": "Characters/Haley_Beach",
  "Precedence": -600
}
```
In an Appearance condition, `Target` is the NPC's own location. When her beach stop ends, `BEFORE_LEAVING` turns
false, but `LOCATION_NAME Target Beach` keeps the outfit until she walks off the map; the game then re-picks her
outfit on the map change. Add `SYNCED_RANDOM` for "only some beach days".

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
      "Condition": "tyr4ntx.LiveAppearanceFramework_NPC_APPEARANCE Haley {{ModId}}_Towel",
      "CueName": "{{ModId}}_Shower",
      "TilePosition": { "X": 5, "Y": 4 }
    }
  }
}
```
(The field is `Location`, not `LocationName`.)

### Trigger action
`tyr4ntx.LiveAppearanceFramework_Refresh [npc]+` re-checks those NPCs now. With no NPC, or `All`, it re-checks every
changeable NPC in the player's location. Useful after something your conditions depend on changed outside the clock,
e.g. a mail flag set by a dialogue `$action` (the change is applied once the dialogue box closes).

### Things to avoid in Appearance conditions
Conditions are now checked often. Queries with **side effects** (e.g. BETAS `CHECK_AND_SET_MAIL`,
`CHECK_AND_REMOVE_MAIL`) would run every time. Don't use them there.

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
- `laf_refresh [npc|all]`: force a re-check.

## Multiplayer
Every player dresses the NPCs in their own game (textures are local). The inputs (clock, date, NPC positions and
animations, conditions) are the same for everyone, so everyone sees the same outfit. No messages, no save data.
Works in split-screen.

## For C# mods
Copy `ILiveAppearanceApi.cs` into your mod and use
`Helper.ModRegistry.GetApi<ILiveAppearanceApi>("tyr4ntx.LiveAppearanceFramework")` (null without LAF):
- `AppearanceChanged` event (NPC, old ID, new ID, trigger),
- `Refresh(npc)`, `GetAppearanceId(npc)`, `GetExpectedAppearanceId(npc)`,
- `SafeChooseAppearance(npc)`: use it instead of `npc.ChooseAppearance()`. Vanilla's method resets the sprite size on
  every call, which breaks animations with wider frames (e.g. Clint hammering). This version keeps them.
