# Storyteller Telemetry

Phase 1 of an AI-driven dynamic difficulty system for RimWorld 1.7. This mod only **records**; it changes nothing about gameplay and saves nothing into the savegame.

## The overall plan

1. **Telemetry mod (this mod).** Log what the storyteller does, the colony state it did it to, and what happened afterwards.
2. **Synthetic data mod.** Run colonies unattended (generate colony → build a base → fire incidents → let them play out) with this mod active, sweeping over wealth, colony size, defenses, incident type and points, so the dataset covers far more situations than hand-played games.
3. **Model.** Train on `(colony state, incident + parameters) → outcome` so the model can predict how hard a given event would be for a given colony.
4. **Storyteller mod.** A custom storyteller that, each time it may fire something, scores the candidate incidents with the model and picks the one closest to the intensity it is aiming for.

## Output

`<RimWorld save data folder>/StorytellerTelemetry/<run_id>/session_<utc time>.jsonl`
(normally `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\StorytellerTelemetry`)

- One folder per colony (`run_id` = world seed + the world's persistent random value).
- One file per time a game is started or loaded. One JSON object per line.
- Loading an older save starts a new file at an earlier tick. Events recorded after that point in the previous file are still valid examples; only drop them if you need one continuous timeline. A crisis with no `crisis_end` was cut off.

## Concepts

- **Incident**: something the storyteller (or a quest, or a mod) fired.
- **Engagement**: a stretch of time with an active hostile threat on a map. `internal` = the "enemy" is one of your own colonists (berserk).
- **Crisis**: a stretch of time in which something is seriously wrong on a map. Factors: `combat`, `internal_conflict`, `fire`, `disease`, `starvation`, `temperature`, `colonist_death`. Overlapping problems are **one** crisis, because what kills a colony is usually several of them at once. A crisis closes after an in-game hour with no factor active.
- **Aftermath** (`recovery`): from the end of a crisis until the colony has been back to its pre-crisis condition for an in-game hour.
- **Rating**: asked once per crisis, when its aftermath closes — or immediately if nobody is left standing.

## Record types

Every record has `type`, `tick` (game ticks; 60000 = 1 day) and `day` (days since settling). `session_start.schema` is 4.

| type | when | what |
|---|---|---|
| `session_start` | file opened | game version, mod list, scenario, storyteller and difficulty |
| `catalog` | file opened | every incident def with its static properties, raid strategies, arrival modes — the model's action space |
| `snapshot` | every 2500 ticks; every 250 ticks during a fight; every 1000 ticks during a crisis; right before every incident; at engagement and crisis start and end | full state of one map (see below). `reason` says why it was taken |
| `candidates` | a storyteller component is about to fire an incident | every incident that could fire on that target right now, with the storyteller's weight for each, and which one it chose. `decision_id` matches the following `incident` |
| `incident` | any incident executes, from any source | def, category, source, points before and after resolution, faction, strategy, arrival mode, raid composition. `seq` matches the `incident_seq` of the snapshot taken just before it. Relief the game sends (man in black, good events) shows up here like anything else |
| `incident_rejected` | the storyteller picked an incident that could not fire | def, source, points |
| `engagement_start` / `engagement_end` | an active hostile threat appears / is gone | linked `incident_seqs`, hostile composition, duration, peaks, `totals`, the `player` block, `crisis_id` |
| `crisis_start` | the first factor becomes active | factors, every incident fired on the map in the previous day (any category), a `fragility` block |
| `crisis_end` | no factor active for an in-game hour | all factors seen, `factor_timeline` (the order they arrived in and how many were open at once), `max_concurrent_factors`, linked engagements and incidents, peak fires (map and home area), `totals`, wealth / buildings / colonists before and after, `player` block |
| `recovery` | the aftermath closes: stable for an hour, a new crisis starts, or 5 days pass | ticks until stable and until each condition held for good (everyone tended, downed count / health / mood / sickness back to pre-crisis levels; -1 = not reached), lowest mood and health, peak sick colonists, and what happened meanwhile (`during`) |
| `rating` | you answer (or close) the prompt | `score` 1–5 (Trivial, Easy, Good, Hard, Too much) or `skipped`, `ended_run`, `crisis_id`, factors, linked engagements and incidents, why and how long after it was asked |
| `colony_incapacitated` | no colonist is left standing (checked the instant one goes down, before the man in black can arrive) | storyteller block. Also triggers the rating prompt at once, with "This ended my run" ticked |
| `pawn_event` | a pawn is downed, dies or is kidnapped | side (colonist / berserk / colony / prisoner / hostile / neutral), kind, faction, damage type, who did it and which side they were on, in home area, roofed, `crisis_id`. For colony pawns also: age, life stage, burning, in bed, `cause_hediff`, current bad health `conditions`, distance to the nearest able colonist |
| `mental_break` | a colonist enters a mental state | state, life stage, `aggressive`, whether mood caused it. Babies crying or giggling are logged but not counted as breaks |
| `storyteller_changed` | storyteller, difficulty or threat scale changes mid-game | new storyteller block |
| `game_over` | the game-over sequence starts | storyteller block |

### `totals` (in `engagement_end`, `crisis_end`, `recovery.during`, `snapshot.since_last`)

Damage taken by colonists, by other colony pawns, by berserk colonists and by hostiles (and how much of that the colony dealt); colonists downed, died, kidnapped; hostiles downed, died (and how many the colony killed), exited the map, taken prisoner; mental breaks; colony buildings destroyed and their value; value of items carried off the map; fires started by the colony's own people, by hostiles, and naturally (spreading fire inherits the origin of the fire it came from).

### Snapshot contents

- `storyteller`: def, difficulty, threat scale, adaptation days and factor, time factor, population intent, colonists off-map, faction relations.
- `wealth`, `threat` (the raid points the game would use right now), `colonists` (aggregates), `defense`, `hostiles`, `resources`, `env`.
- `pressure`: days of food left, sick colonists, net and stored power, fires.
- `fragility`: able adults, dependents (babies, children, colonists who can barely move), able doctors, how many colonists are below each mental-break threshold and the smallest mood margin, share of flammable buildings and walls, rain rate, fires on the map and in the home area, whether the game sees a large fire (which makes it favour rain), and for how long rain is blocked (a flashstorm blocks it).
- `positions`: camera position and view size, each colonist's cell and whether they are drafted and able, centre of the fires in the home area, centre of the hostiles.
- `base` (not in combat / crisis snapshots): home area size, roofed and mountain-roof share, share reachable from the map edge without passing a door, walls, doors, beds.
- `colonist_details` (not in combat / crisis snapshots): per colonist — age, life stage, dependent, xenotype, traits, all skills, health, mood, key capacities, bionics, permanent injuries, missing parts, psylink, weapon, armor, market value, and `diseases` (each potentially lethal condition with severity as a share of lethal, immunity, tended, chronic).
- `since_last`: the `totals` counters since the previous snapshot of that map.

## Notes

- Wealth figures come from the game's own wealth watcher, which only recounts every few thousand ticks, so they can lag slightly.
- `incident.hostiles_spawned` only lists hostiles physically on the map right after the incident; raids arriving by drop pod are fully described in `incident.raid` instead.
- `stolen_value` only counts what a raider is carrying in their hands when leaving the map.
- The `disease` factor needs a non-chronic, potentially lethal condition that is past half-way to lethal or losing the immunity race. The `fire` factor needs 5 fires in the home area or 50 on the map.
- The rating prompt does not pause the game and can be dragged, ignored or closed.
- Mod settings: on/off switch, rating prompt on/off, and the two snapshot intervals.

## Viewer

`Tools/TelemetryViewer.html` is an interactive viewer for these files. Open it in a browser (double-click), then choose the `StorytellerTelemetry` folder or drop session files on it. It reads everything locally; the chart library loads from the internet. Tabs: overview timeline, crises and fights, incidents, storyteller decisions, colony state at any snapshot, and a searchable event log. Crisis, fight and incident tables export to CSV.

## Building

```
cd Source
dotnet build -c Release
```

Needs the .NET 9 SDK (or newer). The project compiles against the assemblies in the RimWorld folder and the Harmony workshop mod, and writes `1.7/Assemblies/StorytellerTelemetry.dll`.
