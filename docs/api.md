# Consumer API

Everything a modder needs to read mission history from VGMissionJournal. If you want to integrate, start here.

## Integration

Two paths, same behaviour. Pick one.

### Typed reference (recommended)

Drop `VGMissionJournal.dll` into your plugin's `libs/` folder and add a `<Reference>` in your csproj. Then soft-dep on the plugin GUID and put API usage behind a presence-check so your mod still loads when VGMissionJournal isn't installed.

Public API surface spans two namespaces:

- `VGMissionJournal.Api` — the facade (`MissionJournalApi`), the query interface (`IMissionJournalQuery`), and `SystemActivity`.
- `VGMissionJournal.Logging` — the record types returned by the query methods (`MissionRecord`, `MissionStepDefinition`, `MissionObjectiveDefinition`, `MissionRewardSnapshot`, `TimelineEntry`, `TimelineState`, `Outcome`).

Consumers typically `using` both.

```csharp
using BepInEx;
using BepInEx.Bootstrap;
using VGMissionJournal.Api;
using VGMissionJournal.Logging;

[BepInPlugin("my.consumer", "My Consumer", "1.0.0")]
[BepInDependency("vgmissionjournal", BepInDependency.DependencyFlags.SoftDependency)]
public class MyPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        if (Chainloader.PluginInfos.ContainsKey("vgmissionjournal"))
            UseMissionJournal();
    }

    // Separate method so the JIT doesn't resolve VGMissionJournal types
    // until this branch is actually taken (important when the plugin
    // isn't installed — referenced assembly would otherwise fail to load).
    private void UseMissionJournal()
    {
        if (MissionJournalApi.Current is not { } api) return;
        foreach (var m in api.GetRecentMissions(10))
            Logger.LogInfo($"{m.MissionSubclass} {m.Outcome?.ToString() ?? "active"}");
    }
}
```

Field renames on `MissionRecord` are compile-time errors — you learn about schema drift at build time, not at runtime.

### Reflection fallback

For scripting-style mods that can't add a compile-time reference. Query methods now return `MissionRecord` (and sub-record) instances instead of dictionaries — read fields off each record via `PropertyInfo`. The lookup cost is the same as dict indexing and the failure mode (missing property) is clearer.

```csharp
var facade = Type.GetType("VGMissionJournal.Api.MissionJournalApi, VGMissionJournal");
var api    = facade?.GetProperty("Current")?.GetValue(null);
if (api is null) return;  // plugin not installed

var method   = api.GetType().GetMethod("GetRecentMissions", new[] { typeof(int) });
var missions = (System.Collections.IEnumerable)method!.Invoke(api, new object[] { 10 })!;

foreach (var m in missions)
{
    var t        = m.GetType();
    var subclass = (string)t.GetProperty("MissionSubclass")!.GetValue(m)!;
    var outcome  = t.GetProperty("Outcome")!.GetValue(m);  // null when active
    // ...
}
```

Both paths hand back the same `MissionRecord` shape described under [MissionRecord](#missionrecord) below.

## `MissionJournalApi.Current`

Static property on `VGMissionJournal.Api.MissionJournalApi`. Returns an `IMissionJournalQuery` once the plugin has finished loading, or `null` when:

- The plugin isn't installed.
- BepInEx has loaded the plugin assembly but `Awake` hasn't run yet (rare — consumers that query from their own `Awake` should use the `Chainloader.PluginInfos` guard shown above).
- The plugin is being torn down (`OnDestroy` nulls the facade before Harmony unpatches).

Always null-check.

## `IMissionJournalQuery`

Call journal queries on the Unity main thread. Lifecycle-gated queries can throw off-thread; take the data needed by background work before scheduling it. During loading or invalidation, queries return empty rather than another session's history.

All query methods return typed `MissionRecord` aggregates (or small typed records like `SystemActivity`). Consumers get IntelliSense, nullable flow-analysis, and compile-time errors on field renames.

### Properties

| Property | Type | Meaning |
|---|---|---|
| `SchemaVersion` | `int` | Current sidecar wire-format version (3). Tracks on-disk JSON shape only — does **not** bump when API methods are added. For API-level feature gating, reflect on the interface for method presence. |
| `TotalMissionCount` | `int` | Number of mission records currently in the journal. |
| `OldestAcceptedGameSeconds` | `double?` | Accept-time (game seconds) of the oldest retained mission. `null` when the journal is empty. |
| `NewestAcceptedGameSeconds` | `double?` | Accept-time of the most-recently-accepted mission. `null` when the journal is empty. |

### Filter methods

All filters that accept a time window (`sinceGameSeconds`, `untilGameSeconds`) anchor on a mission's **accept time** (`Timeline[0].GameSeconds`). Default window is `0` to `double.MaxValue` (entire log). Returns are fresh lists — safe to iterate without defensive copies.

| Method | Return | Purpose |
|---|---|---|
| `GetMission(missionInstanceId)` | `MissionRecord?` | Single mission by its instance id, or `null` when not found. |
| `GetActiveMissions()` | `IReadOnlyList<MissionRecord>` | Missions that have not yet reached a terminal state (Completed / Failed / Abandoned). |
| `GetAllMissions()` | `IReadOnlyList<MissionRecord>` | All missions in the journal, no filter. |
| `GetMissionsInSystem(systemId, since?, until?)` | `IReadOnlyList<MissionRecord>` | Missions whose `SourceSystemId` matches. Missions without a source system (synthesized archive backstops) are excluded. |
| `GetMissionsByFaction(factionId, since?, until?)` | `IReadOnlyList<MissionRecord>` | Missions whose `SourceFaction` matches. |
| `GetMissionsByMissionSubclass(subclass, since?, until?)` | `IReadOnlyList<MissionRecord>` | Exact match on `MissionSubclass` (= `mission.GetType().Name`) — e.g. `"BountyMission"`, `"PatrolMission"`, `"IndustryMission"`, `"Mission"`. Case-sensitive. |
| `GetMissionsByOutcome(outcome, since?, until?)` | `IReadOnlyList<MissionRecord>` | `outcome` is an `Outcome` enum value (`Outcome.Completed` / `Outcome.Failed` / `Outcome.Abandoned`). Active missions never match. |
| `GetMissionsForStoryId(storyId)` | `IReadOnlyList<MissionRecord>` | All mission records sharing a `StoryId` (authored story arcs may span multiple mission instances). |
| `GetMissionsWithObjective(objectiveType, since?, until?)` | `IReadOnlyList<MissionRecord>` | Missions whose `Steps[].Objectives[].Type` contains the given objective type name — e.g. `"KillEnemies"`, `"TravelToPOI"`, `"CollectItemTypes"`, `"Mining"`. Case-sensitive. |
| `GetRecentMissions(count)` | `IReadOnlyList<MissionRecord>` | Up to `count` missions, most-recently-accepted first. |

### Proximity

VGMissionJournal doesn't walk the galaxy graph itself — you pass a delegate that reports jumps between two systems.

```csharp
int JumpDistance(string fromSystemId, string toSystemId) => /* -1 if unreachable */;
IReadOnlyList<MissionRecord> nearby =
    api.GetMissionsWithinJumps("sys-zoran", maxJumps: 3, JumpDistance);
```

`GetMissionsWithinJumps(pivotSystemId, maxJumps, jumpDistance, since?, until?)` returns missions whose source system is within `maxJumps` of the pivot. Sourceless and unreachable missions are excluded.

### Aggregates

| Method | Return | Notes |
|---|---|---|
| `CountByMissionSubclass(since?, until?)` | `IReadOnlyDictionary<string, int>` | Key: raw subclass name (`"BountyMission"` etc.). |
| `CountByOutcome(since?, until?)` | `IReadOnlyDictionary<Outcome, int>` | Key: `Outcome` enum. Active missions are not counted. |
| `CountBySystem(since?, until?)` | `IReadOnlyDictionary<string, int>` | Key: system id. Sourceless missions excluded. |
| `CountByFaction(since?, until?)` | `IReadOnlyDictionary<string, int>` | Key: faction id. Factionless missions excluded. |
| `MostActiveSystemsInRange(pivotSystemId, jumpDistance, maxJumps, topN, since?, until?)` | `IReadOnlyList<SystemActivity>` | Entries are `SystemActivity(SystemId, Count, Jumps)`, sorted by `Count` desc with `SystemId` ordinal tiebreaker. |

## `MissionRecord`

Returned by every non-aggregate query method. Sidecar JSON keys are the same names, camelCased — e.g. the `MissionSubclass` C# property serializes as `missionSubclass`. Nullable reference properties may hold `null`.

### Identity fields

Captured once on acceptance and never mutate — vanilla doesn't change a mission after generation.

| Property | Type | Notes |
|---|---|---|
| `StoryId` | `string` | Vanilla `Mission.storyId`. **Empty for most missions** — vanilla only populates it for authored story arcs (Tutorial, Puppeteers). Use `MissionInstanceId` for correlating across the accept→complete lifecycle when `StoryId` is empty. |
| `MissionInstanceId` | `string` | Session-local GUID synthesized per `Mission` instance. Stable within a session; does *not* survive save/load — vanilla rebuilds mission objects on load, so a mission accepted in one session and finished in another carries different ids. |
| `MissionName` | `string?` | Display name when the mission has one; `null` otherwise. |
| `MissionSubclass` | `string` | Raw `mission.GetType().Name`. One of `"Mission"` (parametric missions — salvage, courier, trade, etc.), `"BountyMission"`, `"IndustryMission"`, `"PatrolMission"`, `"StoryMission"`. |
| `MissionLevel` | `int` | Currently always `0` — see [Known gaps](#known-gaps). |
| `SourceStationId` | `string?` | Source POI GUID, when the mission has a source station. |
| `SourceStationName` | `string?` | Snapshot at accept time — won't change if vanilla later renames it. |
| `SourceSystemId` | `string?` | System containing the source station. |
| `SourceSystemName` | `string?` | Snapshot at accept time. |
| `SourceSectorId` | `string?` | *Not yet populated — see [Known gaps](#known-gaps).* |
| `SourceSectorName` | `string?` | *Not yet populated.* |
| `SourceFaction` | `string?` | Faction identifier (e.g. `"BountyGuild"`). |
| `TargetStationId` | `string?` | *Not yet populated — see [Known gaps](#known-gaps).* |
| `TargetStationName` | `string?` | *Not yet populated.* |
| `TargetSystemId` | `string?` | *Not yet populated.* |
| `PlayerLevel` | `int` | Commander level at accept time. `0` in tests / when `GamePlayer.current` is null. |
| `PlayerShipName` | `string?` | *Not yet populated.* |
| `PlayerShipLevel` | `int?` | *Not yet populated.* |
| `PlayerCurrentSystemId` | `string?` | Where the player was when the mission was accepted — may differ from `SourceSystemId`. |

### Steps and objectives

`Steps` (`IReadOnlyList<MissionStepDefinition>`) is the mission's step/objective tree captured once at acceptance. Steps are definition-only (no runtime progress counters).

`MissionStepDefinition`:

| Property | Type | Meaning |
|---|---|---|
| `Description` | `string?` | Vanilla's display description (null when the step has none). |
| `RequireAllObjectives` | `bool` | `true` → every objective must complete; `false` → any one does. |
| `Hidden` | `bool` | Vanilla flag for branch-stub / guard steps the UI hides. Consumers usually skip these. |
| `Objectives` | `IReadOnlyList<MissionObjectiveDefinition>` | See below. |

`MissionObjectiveDefinition`:

| Property | Type | Meaning |
|---|---|---|
| `Type` | `string` | Raw `objective.GetType().Name` — e.g. `"KillEnemies"`, `"TravelToPOI"`, `"Mining"`, `"TradeOffer"`, `"CollectCredits"`, `"CollectItemTypes"`, `"Crafting"`, `"Reputation"`, `"ProtectUnit"`, `"Salvage"`, `"TriggerObjective"`, `"StationsInfected"`, `"SystemsConquered"`, `"ConquestFactionEliminated"`, `"ConquestFleetStrength"`, `"CreditOffer"`, `"DroneTrigger"`, `"Item"`. |
| `Fields` | `IReadOnlyDictionary<string, object?>?` | Best-effort primitive field dump (camelCase keys). For `KillEnemies`: `requiredAmount`, `shipType`, `enemyFaction` (faction identifier). For `TravelToPOI`: `targetPOI`. For `Mining`: `requiredAmount`, `itemType` (identifier), `miningFaction`. What you get depends on what the concrete subclass exposes — anything non-primitive / non-Faction / non-InventoryItemType / non-MapElement is skipped. `null` when extraction yields nothing. |

### Rewards

`Rewards` (`IReadOnlyList<MissionRewardSnapshot>`) carries one entry per `MissionReward` on the mission. Rewards are extracted at accept time and re-extracted at completion (when vanilla populates the final reward set).

`MissionRewardSnapshot`:

| Property | Type | Meaning |
|---|---|---|
| `Type` | `string` | Raw `reward.GetType().Name` — one of `"Credits"`, `"Experience"`, `"Reputation"`, `"Item"`, `"Ship"`, `"Crew"`, `"Skilltree"`, `"Skillpoint"`, `"WorkshopCredit"`, `"StoryMission"`, `"MissionFollowUp"`, `"POICoordinates"`, `"UmbralControl"`, `"ConquestStrength"`. |
| `Fields` | `IReadOnlyDictionary<string, object?>?` | Best-effort primitive field dump. `Credits` / `Experience` / `Skillpoint` / `WorkshopCredit` / `UmbralControl` / `ConquestStrength` → `amount`. `Item` → `item` (identifier) + `amount`. `Ship` → `ship` (identifier). `Skilltree` → `treeName`. `StoryMission` → `missionId`. `Reputation` → `amount` + `faction` (identifier). `null` when extraction yields nothing. |

Read all rewards off `Rewards` by `Type`.

**Identifier rule.** Every resolved reference in `Fields` — `enemyFaction`, `miningFaction`, `faction`, `itemType`, `item`, `deliverTo`, `targetPOI`, etc. — is the stable **system identifier** (what vanilla's `Faction.Get(id)` / `InventoryItemType.Get(id)` accept), never the translated `displayName` / `name`. For `InventoryItemType` references on runtime-cloned reward instances (where vanilla's own `identifier` field isn't serialized) we fall back to the Unity object's `name` with the `(Clone)` suffix stripped — still a valid registry key. Consumers can round-trip every id back to the vanilla object.

### Timeline

`Timeline` (`IReadOnlyList<TimelineEntry>`) is the explicit state-transition log. It grows by one entry on each lifecycle transition.

`TimelineEntry`:

| Property | Type | Meaning |
|---|---|---|
| `State` | `TimelineState` enum | One of `Accepted`, `Completed`, `Failed`, `Abandoned`. |
| `GameSeconds` | `double` | In-game clock at the transition. |
| `RealUtc` | `string?` | ISO-8601 wall-clock. Stamped on `Accepted` and terminal entries; `null` on any interior entries that may be added in a future version. |

A mission's timeline always starts with exactly one `Accepted` entry and ends with at most one terminal entry (Completed / Failed / Abandoned). An active mission has no terminal entry.

Derived helpers on `MissionRecord`:

| Property | Type | Meaning |
|---|---|---|
| `AcceptedAtGameSeconds` | `double` | Shorthand for `Timeline[0].GameSeconds`. |
| `TerminalAtGameSeconds` | `double?` | Game-seconds of the terminal entry; `null` when the mission is still active. |
| `Outcome` | `Outcome?` | `Outcome.Completed` / `Outcome.Failed` / `Outcome.Abandoned`, or `null` when still active. |
| `IsActive` | `bool` | `true` when the mission has no terminal entry yet. |
| `AgeSeconds(nowGameSeconds)` | `double` | Duration accept→terminal if terminated; else `nowGameSeconds − AcceptedAtGameSeconds`. |

**Time-window anchoring.** All `since` / `until` filters on query methods use `AcceptedAtGameSeconds` as the anchor — not the terminal time. A mission accepted at t=100 that completes at t=200 appears in a `since=50, until=150` window even though it completed outside it.

### Sidecar JSON example

The sidecar mirrors `MissionRecord` with camelCase keys:

```json
{
  "storyId": "anon:a4f2b1c8-...",
  "missionInstanceId": "inst-0001",
  "missionName": "Hunt the Crimson Fang",
  "missionSubclass": "BountyMission",
  "missionLevel": 0,
  "sourceStationId": "poi-zoran-bounty",
  "sourceStationName": "Zoran Bounty Office",
  "sourceSystemId": "sys-zoran",
  "sourceSystemName": "Zoran",
  "sourceSectorId": null,
  "sourceSectorName": null,
  "sourceFaction": "BountyGuild",
  "targetStationId": null,
  "targetStationName": null,
  "targetSystemId": null,
  "playerLevel": 12,
  "playerShipName": null,
  "playerShipLevel": null,
  "playerCurrentSystemId": "sys-zoran",
  "steps": [
    {
      "description": "Eliminate the Crimson Fang raiders",
      "requireAllObjectives": true,
      "hidden": false,
      "objectives": [
        { "type": "KillEnemies", "fields": { "enemyFaction": "CrimsonFang", "requiredAmount": 3 } }
      ]
    }
  ],
  "rewards": [
    { "type": "Credits",    "fields": { "amount": 12000 } },
    { "type": "Experience", "fields": { "amount": 350 } }
  ],
  "timeline": [
    { "state": "Accepted",  "gameSeconds": 12450.0, "realUtc": "2026-04-24T14:12:30Z" },
    { "state": "Completed", "gameSeconds": 13420.0, "realUtc": "2026-04-24T14:25:40Z" }
  ],
  "acceptedAtGameSeconds": 12450.0,
  "terminalAtGameSeconds": 13420.0,
  "outcome": "Completed",
  "isActive": false
}
```

## Sidecar format

If you'd rather read the journal without loading VGMissionJournal (offline analysis, external tooling), the sidecar JSON uses the same shape. File lives at `<GameDir>/Saves/<saveName>.save.vgmissionjournal.json`:

```json
{
  "version": 3,
  "missions": [
    { "storyId": "...", "missionInstanceId": "...", ... },
    ...
  ]
}
```

- Written atomically via tmp+rename, so partial files are never observed.
- Corrupt / unsupported-version files are quarantined to `<saveName>.save.vgmissionjournal.corrupt.<UTC>.json` and replaced by an empty log — VGMissionJournal never blocks vanilla's load on a bad sidecar.
- `version` bumps on breaking schema changes. Additive changes (new optional fields) stay at the current version.

## Known gaps

- **Pre-readiness new-game grants are not recorded.** Recording begins at PlayerReady. The pilot verifies clearing prior history, not that every starter mission is granted after that boundary.

These are documented limitations of the current shipping version. Consumers should treat the affected keys as always-absent for now.

- **`MissionLevel` is always `0`.** Vanilla's `Mission.level` getter chains through `GamePlayer.current`, which makes it unsafe to read outside the main thread. A safer accessor is on the roadmap.
- **Sector and target-station/system fields are never populated.** Vanilla's accessor graph is deeper than what's currently scouted. Fields are reserved in the schema; additive future.
- **Player ship fields are never populated.** Same reason.
- **`MissionInstanceId` is session-local.** Resets across save/load because vanilla rebuilds mission instances on load. Accept→Complete in the same session works; across sessions you need `StoryId` (if set) or your own joining logic.
- **No per-step or per-objective transitions in the timeline.** Vanilla exposes no hook for this — `MissionStep.isComplete` is a computed getter over `objectives.All(IsComplete)`, and `Mission.currentStep` just scans for the first non-complete step. Nothing fires when a step or objective flips. The timeline captures mission-level transitions only (Accepted → Completed/Failed/Abandoned); `Steps` on the Accepted snapshot describes the mission's structure, not its progress.
- **No Offered tracking.** Not captured. Additive if consumers ever need it.

## Retention

The journal defaults to a soft cap of **2000 missions per save**, with FIFO eviction once exceeded — oldest-accepted missions drop off first. Consumers should not assume the full playtime is always available; use `OldestAcceptedGameSeconds` if you need to know how far back the journal reaches.

The cap is configurable via `Journal.MaxMissions` in `vgmissionjournal.cfg`. Setting it to **`0` disables the cap entirely** — the journal then retains every mission for the save's lifetime, at the cost of an unbounded sidecar size. A user with 50 000 captured missions would sit around 25 MB on disk (rough estimate at ~500 bytes per record).

## Stability

- **Method signatures on `IMissionJournalQuery`** are part of the public API contract. Breaking changes require a major-version bump and release notes.
- **New methods are additive** — adding a method doesn't bump major. To feature-gate a call that was added in a newer version, reflect on the interface for method presence (e.g. `typeof(IMissionJournalQuery).GetMethod("NewMethod")`). `SchemaVersion` tracks the sidecar wire format only, not the API surface.
- **`MissionRecord`'s property set** follows the same rules: additions are non-breaking; renames / removals are breaking.
- **The sidecar `version` field** tracks only on-disk format changes. An additive API change doesn't necessarily bump it.
