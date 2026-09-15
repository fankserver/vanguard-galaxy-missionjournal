using System;
using System.Collections.Generic;
using System.IO;

namespace VGMissionJournal.Persistence;

/// <summary>
/// Cleanup of sidecars (for an observed save directory) whose vanilla save file has been
/// removed outside the game (manual deletion, save-manager tooling).
/// Prevents unbounded accumulation across a player's Steam cloud dir.
///
/// <para>Skips <c>.vgmissionjournal.corrupt.*.json</c> quarantine files —
/// they carry forensic value and the player may want to inspect them
/// before deciding to delete. Skips anything that doesn't end with the
/// live-sidecar suffix so peer mods' sidecars are left alone.</para>
///
/// <para>Runs from <c>LifecyclePersistence.Restore</c> for an observed save
/// directory. Any IO
/// failure is swallowed by the caller and warn-logged per R5.2; the
/// sweep is purely janitorial and must never affect vanilla load.</para>
/// </summary>
internal static class DeadSidecarSweeper
{
    public static IReadOnlyList<string> Sweep(string saveDirectory)
    {
        if (string.IsNullOrEmpty(saveDirectory) || !Directory.Exists(saveDirectory))
            return Array.Empty<string>();

        var deleted = new List<string>();
        foreach (var sidecar in Directory.EnumerateFiles(saveDirectory, "*" + JournalPathResolver.Suffix))
        {
            if (!JournalPathResolver.IsSidecar(sidecar)) continue;

            var baseSave = JournalPathResolver.BaseSavePathFrom(sidecar);
            if (File.Exists(baseSave)) continue;

            try
            {
                File.Delete(sidecar);
                deleted.Add(sidecar);
            }
            catch (IOException)
            {
                // Sidecar is locked / permission denied — skip, try again next launch.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: don't let a stubborn file abort the sweep.
            }
        }
        return deleted;
    }
}
