# VGModAPI lifecycle pilot (0.2.0)

Owner-authorized delivery for fankserver/vanguard-galaxy-api#5; owning issue #14.

## Boundaries and retained behavior

- Removed two save/load Harmony classes and their static wiring/path caches, plus the separate quit callback. A Store return no longer implies a successful sidecar commit.
- Restore at PlayerReady, after deserialization and before GameplayManager initialization. Sidecars no longer load speculatively in a file-load prefix. Loading/failed/invalidated sessions cannot record or persist journal state.
- SaveSucceeded uses its own destination and captured session, checked against both local readiness and the API's current snapshot. This prevents queued stale save events from writing replacement-session history.
- Instance-ID behavior, schema, FIFO cap and public query signatures are unchanged. The acceptance hook was repaired for inspected 0.8.2.3 `(Mission, bool)` and verifies insertion rather than logging a rejected duplicate. Other domain hooks remain. This pilot does not claim broader mission-path qualification or persistent mission identity improvements.
- Sweeping moves from startup to the first observed directory restoration. There is no attempt to invent a save path for a new game or unavailable API.
- API 0.1.x and both bound capabilities are required. Full RuntimeQualified remains false; this is an explicitly experimental consumer. Unsupported API versions/capabilities leave the public facade absent and install no domain patches.

## Evidence

Untouched baseline against historical reference: clean build and 164 tests. Current local Release suite: 179 tests (six obsolete patch-shape tests replaced by two dependency/no-save-hook checks, 17 lifecycle cases, and two insertion/rejection cases). Tests exercise delayed restoration, save-as destination, failed/skipped/null-session refusal, reentrant current replacement, clearing, teardown, and version/capability checks.

qa-16 failed because the historical reference concealed the changed acceptance signature. The private reference was regenerated from the installed binary, without changing the sibling TTS reference. Initialization now finishes binding before subscribing/restoring; failure removes partial patches and disables persistence.

qa-17 passed 24 checks, including copied nonempty histories, switching and reload, successful/failed/skipped saves, autosave rotation, new-game clearing and component teardown. All 37 original file hashes and exported preferences were independently checked unchanged. Subsequent host-tested read-scope and late-attachment hardening still needs a latest-build native rerun. Missing/unavailable API consumer startup and final Fable review remain pending. No normal installation or real sidecar is a test target.

## Distribution

Abstractions is compile-only for the plugin and runtime-only in tests. Install one canonical API copy. Owner-local packaging contains the journal, Newtonsoft.Json and documentation/licenses only. Game reference metadata is no longer tracked or used by public release CI; historical Git objects are not purged.
