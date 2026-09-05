using BepInEx.Logging;
using VGMissionJournal.Logging;
using VGMissionJournal.Persistence;

namespace VGMissionJournal.Patches;

/// <summary>
/// Centralises the static-slot wiring for every patch class. Keeping this in
/// one place makes it impossible to forget a slot when adding a new patch.
/// </summary>
internal static class PatchWiring
{
    public static void WireAll(
        MissionRecordBuilder builder,
        MissionStore         store,
        ManualLogSource      bepLog)
    {
        MissionAcceptPatch.Builder   = builder;
        MissionAcceptPatch.Store     = store;
        MissionAcceptPatch.BepLog    = bepLog;

        MissionCompletePatch.Builder = builder;
        MissionCompletePatch.Store   = store;
        MissionCompletePatch.BepLog  = bepLog;

        MissionFailPatch.Builder     = builder;
        MissionFailPatch.Store       = store;
        MissionFailPatch.BepLog      = bepLog;

        MissionAbandonPatch.Builder  = builder;
        MissionAbandonPatch.Store    = store;
        MissionAbandonPatch.BepLog   = bepLog;

        MissionArchivePatch.Builder  = builder;
        MissionArchivePatch.Store    = store;
        MissionArchivePatch.BepLog   = bepLog;

    }
}
