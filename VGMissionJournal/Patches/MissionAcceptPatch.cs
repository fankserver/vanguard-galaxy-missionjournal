using System;
using BepInEx.Logging;
using HarmonyLib;
using Source.MissionSystem;
using Source.Player;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Patches;

/// <summary>
/// Observer on <see cref="GamePlayer.AddMissionWithLog(Mission, bool)"/>
/// (decomp line 34880). Creates a <see cref="MissionRecord"/> with a
/// single <see cref="TimelineState.Accepted"/> timeline entry whenever
/// vanilla registers a mission as "in progress" for the player.
///
/// <para>Vanilla also has a string-overload of
/// <c>AddMissionWithLog(string)</c> at line 34911 that resolves a
/// <c>StoryMission</c> template and dispatches through the
/// <c>Mission</c>-overload we patch here — so this single hook covers
/// both acceptance paths.</para>
///
/// <para>Exception safety per spec R5.2: the postfix body is wrapped in
/// try/catch; any failure warn-logs and is swallowed.</para>
/// </summary>
[HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.AddMissionWithLog), new[] { typeof(Mission), typeof(bool) })]
internal static class MissionAcceptPatch
{
    internal static MissionRecordBuilder Builder = null!;
    internal static MissionStore         Store   = null!;
    internal static ManualLogSource      BepLog  = null!;

    [HarmonyPrefix]
    private static void Prefix(GamePlayer __instance, Mission mission, out int __state)
    {
        __state = -1;
        try { __state = Count(__instance.missions, mission); }
        catch (Exception e) { BepLog.LogWarning($"MissionAcceptPatch snapshot failed: {e}"); }
    }

    internal static bool WasInserted(System.Collections.Generic.IEnumerable<Mission> missions, Mission mission, int before)
        => before >= 0 && Count(missions, mission) > before;

    internal static int Count(System.Collections.Generic.IEnumerable<Mission> missions, Mission mission)
    {
        var count = 0;
        foreach (var item in missions)
            if (ReferenceEquals(item, mission)) count++;
        return count;
    }

    [HarmonyPostfix]
    private static void Postfix(GamePlayer __instance, Mission mission, int __state)
    {
        if (mission is null || __state < 0) return;
        try
        {
            // 0.8.2.3 can return normally after rejecting a duplicate story mission.
            if (!WasInserted(__instance.missions, mission, __state)) return;
            var record = Builder.CreateFromAccept(mission);
            Store.Upsert(record);
        }
        catch (Exception e)
        {
            BepLog.LogWarning($"MissionAcceptPatch swallowed: {e}");
        }
    }
}
