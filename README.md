# VGMissionJournal — Observational mission-activity logger for Vanguard Galaxy

A BepInEx 5 plugin that observes instrumented mission transitions — acceptance, completion, failure and abandonment — for vanilla and mod-authored missions. The journal is persisted per save, exposes a reflection-friendly public query API for other mods to consume, and **never mutates vanilla state**.

## Design principle: pure observer

VGMissionJournal does not intentionally mutate vanilla state. Journal persistence is a separate sidecar write, not an atomic transaction with the game save. Compatibility with arbitrary mods is not guaranteed.

Consumers (future stats dashboards, LLM-driven NPCs, progression mods, etc.) soft-dep via reflection on a stable API surface. Consumers bucket and interpret; VGMissionJournal records what the game hands it and nothing more.

## Install

1. Install BepInEx 5 for Vanguard Galaxy.
2. Install the experimental [VGModAPI 0.1.x](https://github.com/fankserver/vanguard-galaxy-api) package once. Keep its three DLLs together; do not duplicate Abstractions in consumer folders.
3. Extract the journal archive into `BepInEx/plugins/`, including its bundled Newtonsoft.Json and notices. Version 0.2.0 requires the API; BepInEx refuses a missing/too-old dependency. Unsupported API series or unavailable lifecycle/save capabilities disable the journal with a log message, without touching sidecars.
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
| **Accepted**  | Player accepts a mission (vanilla's `GamePlayer.AddMissionWithLog`)              | load-bearing |
| **Completed** | Player turns in a mission (vanilla's `GamePlayer.CompleteMission`) + rewards    | load-bearing |
| **Failed**    | Mission fail condition triggers (vanilla's `Mission.MissionFailed`)             | load-bearing |
| **Abandoned** | Player drops a mission (vanilla's `RemoveMission(_, completed:false)`)          | load-bearing |
| *(Archived backstop)* | Synthesized Completed for unusual paths (dev cheats, swallow errors) | backstop |

Every captured mission carries: in-game accept timestamp + wall-clock, storyId, a session-local mission instance id (for correlating across the accept→complete lifecycle when `storyId` is empty), mission name + raw subclass name (`mission.GetType().Name`), source station / system / faction, a full snapshot of the step/objective tree (type per objective), a unified rewards list covering all 14 vanilla reward subtypes, a timeline of state transitions (Accepted → Completed/Failed/Abandoned), and a player-state snapshot. Consumers bucket by subclass / objective type if they want categories; VGMissionJournal does not classify.

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

Domain mission Harmony hooks remain, including completion/archive coordination. Acceptance targets the inspected `(Mission, bool)` overload and checks actual insertion, not a normally returning duplicate rejection. Save/load hooks and the independent quit flush are removed: API Starting/invalidation clears state, PlayerReady restores it, and only matching-session SaveSucceeded writes the event's destination. Failed/skipped/unknown-session saves do not write sidecars. Quit autosave uses the same success path. Recording is gated while loading and after teardown. The former startup sweeper now runs only for an observed save directory.

Schema and public query signatures are unchanged. Missing/corrupt sidecars retain the existing empty-store/quarantine policy. A sidecar error cannot turn vanilla success into a rollback. These are narrow lifecycle guarantees, not universal UI readiness or complete runtime qualification. See [migration evidence and limits](docs/lifecycle-migration.md).

## Build

```bash
# First build the sibling API Release package, or pass VGAPI_DLL=/path/to/VGModAPI.Abstractions.dll.
make build
make test
make package CONFIG=Release
```

Or via the Makefile:

```bash
make refresh-asm # owner-local current game metadata; requires assembly-publicizer
make build
make test
make deploy      # copies the DLL into <GameDir>/BepInEx/plugins/VGMissionJournal/
```

Game references (including stripped/publicized stubs) remain owner-local and are no longer tracked. The historical stub is not removed from Git history by this change. Public release CI only validates an already uploaded owner-built `VGMissionJournal.zip`; it does not build with game assets. Build/test/package locally, inspect `dist/VGMissionJournal.zip`, and attach it when publishing a release. The zip excludes API, game, loader and Unity DLLs; its checker verifies layout, not binary provenance.

## License

MIT — see [`LICENSE`](LICENSE).
