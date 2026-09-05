using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using VGModAPI;
using HarmonyLib;
using Source.Galaxy;
using Source.Player;
using Source.Util;
using UnityEngine;
using VGMissionJournal.Api;
using VGMissionJournal.Config;
using VGMissionJournal.Logging;
using VGMissionJournal.Patches;
using VGMissionJournal.Persistence;

namespace VGMissionJournal;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency(ModApi.PluginId, "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid    = "vgmissionjournal";
    public const string PluginName    = "Vanguard Galaxy Mission Journal";
    public const string PluginVersion = "0.2.0";

    internal static Plugin          Instance { get; private set; } = null!;
    internal static ManualLogSource Log      { get; private set; } = null!;

    internal MissionStore         Store   { get; private set; } = null!;
    internal IClock               Clock   { get; private set; } = null!;
    internal JournalIO                Io      { get; private set; } = null!;
    internal MissionRecordBuilder Builder { get; private set; } = null!;
    internal MissionJournalConfig     Cfg     { get; private set; } = null!;

    private Harmony _harmony = null!;
    private LifecyclePersistence? _lifecycle;

    // Reflection-resolved once — MapElement.<guid>k__BackingField on the
    // player's current POI -> system.
    private static readonly FieldInfo? _mapElementGuidField =
        typeof(Source.Galaxy.MapElement).GetField(
            "<guid>k__BackingField",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static string? ResolvePlayerCurrentSystemId()
    {
        try
        {
            var player = GamePlayer.current;
            var system = player?.currentPointOfInterest?.system;
            if (system is null || _mapElementGuidField is null) return null;
            return _mapElementGuidField.GetValue(system) as string;
        }
        catch
        {
            return null;
        }
    }

    private void Awake()
    {
        Instance = this;
        Log      = Logger;

        // --- config (spec R4.5) -----------------------------------------
        Cfg = new MissionJournalConfig(Config);

        // --- singletons -------------------------------------------------
        Clock   = new GameClock();
        Io      = new JournalIO(() => DateTime.UtcNow);
        Store   = new MissionStore(
            maxMissions:     Cfg.MaxMissions.Value,
            onFirstEviction: cap => Log.LogWarning(
                $"Mission store hit cap of {cap} missions — oldest entries are now being evicted FIFO"));
        Builder = new MissionRecordBuilder(Clock, ResolvePlayerCurrentSystemId);

        if (Cfg.Verbose.Value)
        {
            Store.OnMissionChanged += r =>
            {
                var last = r.Timeline[r.Timeline.Count - 1];
                Log.LogDebug($"{last.State} {r.MissionSubclass} instanceId={r.MissionInstanceId} @ {last.GameSeconds:F1}s");
            };
        }

        var api = ModApi.Current;
        if (!Chainloader.PluginInfos.TryGetValue(ModApi.PluginId, out var apiPlugin)
            || !LifecyclePersistence.IsCompatible(apiPlugin.Metadata.Version, api))
        {
            enabled = false;
            Log.LogError("Requires VGModAPI 0.1.x with available session-lifecycle and save-outcomes; journal disabled without touching sidecars.");
            return;
        }
        Store.RecordingAllowed = () => _lifecycle?.CanRecord == true;
        try
        {
            Log.LogWarning("Using experimental VGModAPI lifecycle; full runtime qualification remains pending.");
            PatchWiring.WireAll(Builder, Store, Log);

            // Finish domain binding before subscribing or restoring any sidecar.
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(MissionAcceptPatch));
            _harmony.PatchAll(typeof(MissionCompletePatch));
            _harmony.PatchAll(typeof(MissionFailPatch));
            _harmony.PatchAll(typeof(MissionAbandonPatch));
            _harmony.PatchAll(typeof(MissionArchivePatch));

            _lifecycle = new LifecyclePersistence(api!, Store, Io, message => Log.LogWarning(message));
            MissionJournalApi.Current = new MissionJournalQueryAdapter(Store);
            var patchCount = _harmony.GetPatchedMethods().Count();
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({patchCount} patched method(s))");
        }
        catch (Exception error)
        {
            enabled = false;
            MissionJournalApi.Current = null;
            _lifecycle?.Dispose();
            _lifecycle = null;
            _harmony?.UnpatchSelf();
            Log.LogError($"Journal initialization failed; persistence disabled: {error}");
        }
    }

    private void OnDestroy()
    {
        MissionJournalApi.Current = null;
        _lifecycle?.Dispose();
        _harmony?.UnpatchSelf();
    }
}
