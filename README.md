# VGMissionJournal — Observational mission-activity logger for Vanguard Galaxy

A BepInEx 5 plugin that observes instrumented mission transitions — acceptance, completion, failure and abandonment — for vanilla and mod-authored missions. The journal is persisted per save, exposes a reflection-friendly public query API for other mods to consume, and **never mutates vanilla state**.

## Design principle: pure observer

VGMissionJournal does not intentionally mutate vanilla state. API-managed journal saves are enabled by default; legacy saves use a separate file. Neither mode is an atomic transaction with the game save. Compatibility with arbitrary mods is not guaranteed.

Consumers (future stats dashboards, LLM-driven NPCs, progression mods, etc.) soft-dep via reflection on a stable API surface. Consumers bucket and interpret; VGMissionJournal records what the game hands it and nothing more.

## Install

1. Install BepInEx 5 for Vanguard Galaxy.
2. Install the experimental [VGModAPI 0.2.8+](https://github.com/fankserver/vanguard-galaxy-api) package once. Keep its DLLs together; do not duplicate Abstractions in consumer folders.
3. Extract the journal archive into `BepInEx/plugins/`, including its bundled Newtonsoft.Json and notices. Version 0.5.0 declares a hard BepInEx dependency on API 0.2.8 or newer — the loader gate is authoritative, and the plugin carries no compile-time reference to the game or Harmony assemblies. Unavailable typed services (mission transitions, identity continuity, session tracking, save outcomes) disable the journal with diagnostic log messages, without touching sidecars.
4. Launch the game. On a successful observed save, a paired `<saveName>.save.vgmissionjournal.json` appears next to the vanilla `.save` file.

Config lives at `<GameDir>/BepInEx/config/vgmissionjournal.cfg` after first run:

```ini
[Journal]
## When true, emit a Debug-level log line for every captured mission lifecycle transition.
# Default: false
Verbose = false

## Soft cap on retained missions per save (FIFO eviction, oldest-accepted first). 0 = unbounded.
# Default: 2000
MaxMissions = 2000
```

## What's captured

| Timeline state | Recorded when | Confidence |
|---|---|---|
| **Accepted**  | The API witnesses mission acceptance (`MissionTransitionKind.Accepted`)          | load-bearing |
| **Completed** | The API witnesses reward-claim completion (`Completed`); rewards re-extracted from the dispatched view | load-bearing |
| **Failed**    | The API witnesses a failure (`Failed`)                                           | load-bearing |
| **Abandoned** | The API witnesses abandonment (`Abandoned`)                                      | load-bearing |
| **Removed**   | Neutral membership removal (`Removed`) — neither failure nor abandonment; no claimed outcome | load-bearing |

`Restored` and `Archived` transitions are deliberately not recorded: restoration is identity correspondence, not acceptance, and archive notifications never invent completion or duplicate its timeline entry.

Every captured mission carries: in-game accept timestamp + wall-clock, storyId (from the event definition id), the API occurrence id (correlated across save/load by the API's mission identity continuity), mission name + raw subclass name (`mission.GetType().Name` via the version-sensitive dispatch view), source station / system / faction, a full snapshot of the step/objective tree (type per objective), a unified rewards list covering all 14 vanilla reward subtypes, a timeline of state transitions (Accepted → Completed/Failed/Abandoned/Removed), and a player-state snapshot. Consumers bucket by subclass / objective type if they want categories; VGMissionJournal does not classify.

See [`docs/api.md`](docs/api.md) for the full mission schema and method reference.

## For modders

Querying mission history from your own plugin takes about ten lines. Quick sketch:

```csharp
using VGMissionJournal.Api;
using VGMissionJournal.Logging;  // MissionRecord + Outcome live here

if (MissionJournalApi.Current is { } api)
    foreach (var m in api.GetRecentMissions(10))
        Logger.LogInfo($"{m.MissionSubclass} {m.Outcome?.ToString() ?? "active"}");
```

Full integration guide (typed reference, reflection fallback, soft-dep guard), method reference, mission field schema, and sidecar format: **[`docs/api.md`](docs/api.md)**.

## Known gaps

- **No per-step or per-objective transition events.** Vanilla has no hook: `MissionStep.isComplete` is a computed getter over `objectives.All(IsComplete)`, and `Mission.currentStep` just scans for the first non-complete step — nothing fires when a step or objective flips. The timeline captures mission-level transitions only (Accepted → Completed/Failed/Abandoned); the step/objective tree on the Accepted snapshot is structural, not progress-tracked.
- **No Offered tracking.** Not captured; if consumers ever need it, it's additive.
- **A few snapshot fields are never populated** — `missionLevel`, sector IDs, target station/system, player ship. Vanilla's accessor graph is deeper than what's currently scouted; fields are reserved in the schema.
- **One sidecar per save.** No cross-save aggregation.

Full list: [`docs/api.md#known-gaps`](docs/api.md#known-gaps).

## Safety invariants

The journal installs **no** Harmony patches and holds no compile-time reference to `Assembly-CSharp` or HarmonyX: mission transitions come exclusively from `ModApi.Services.Missions.Transitioned`, and native mission details are inspected only during the exact API dispatch callback through the version-sensitive read-only escape hatch (reflection-only member reads, copied out immediately). The API's lifecycle observations drive state: SessionStarting/invalidation clears state, and legacy mode restores on PlayerReady and writes only on matching-session SaveSucceeded. Failed/skipped/unknown-session saves do not write sidecars. Quit autosave uses the same success path. Recording is gated while loading and after teardown. The former startup sweeper now runs only for an observed save directory.

Schema and public query signatures are unchanged. In legacy mode, missing/corrupt sidecars retain the existing empty-store/quarantine policy. A sidecar error cannot turn vanilla success into a rollback. These are narrow lifecycle guarantees, not universal UI readiness or complete runtime qualification.

### API-managed journal saves (default, experimental)

API-managed saves are enabled by default in VGModAPI and this mod. Set `[Persistence] UseApiSaveData = false` in `vgmissionjournal.cfg` to use legacy save files. The draft setting `UseCoordinatedPersistence` has been replaced; it is no longer read. There is no silent fallback if the service is unavailable. In this mode the logical JournalSchema v3 is preserved in a bounded gzip payload (`VGJ1`, owner format 1) inside the API envelope; no legacy sidecars are written or swept. Capture reads an internal snapshot even while public recording/query gates are closed during callbacks. Recording resumes only when the registration grants mutation; faults and removal remain fail-closed and status changes are logged.

`ImportLegacySidecars = true` separately opts into read-only adoption when the API has no journal data for this game save. If a legacy file exists and import is disabled, restoration stops with instructions rather than starting an empty journal. Leave it false unless you explicitly accept the old file as the intended history: legacy filenames do not prove snapshot consistency. Corrupt, future-version, unreadable or over-16-MiB JSON sources block restoration without quarantine or overwrite. Existing files remain untouched; no other mod's or account-wide history is imported. The API save-data limit is 1 MiB compressed and 16 MiB decompressed JSON; neither limit permits truncation. Histories exceeding either bound require retaining legacy mode. Full owner acceptance and native API-managed save qualification remain separate gates.

## Build

```bash
make build
make test
make package CONFIG=Release   # builds dist/VGMissionJournal.zip
make deploy      # copies the DLL into <GameDir>/BepInEx/plugins/VGMissionJournal/
```

The plugin needs only the sibling API Release Abstractions DLL (linked by `make link-api`, or pass `VGAPI_DLL=/path/to/VGModAPI.Abstractions.dll`). Game references are gone entirely: the pure-observer build carries no `Assembly-CSharp` or Harmony reference, so no publicized stub is required. Public release CI only validates an already uploaded owner-built `VGMissionJournal.zip`; it does not build with game assets. Build/test/package locally, inspect `dist/VGMissionJournal.zip`, and attach it when publishing a release. The zip excludes API, game, loader and Unity DLLs; its checker verifies layout, not binary provenance.

## License

MIT — see [`LICENSE`](LICENSE).
