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
[BepInDependency(ModApi.PluginId, "0.1.2")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid    = "vgmissionjournal";
    public const string PluginName    = "Vanguard Galaxy Mission Journal";
    public const string PluginVersion = "0.4.0";

    internal static Plugin          Instance { get; private set; } = null!;
    internal static ManualLogSource Log      { get; private set; } = null!;

    internal MissionStore         Store   { get; private set; } = null!;
    internal IClock               Clock   { get; private set; } = null!;
    internal JournalIO                Io      { get; private set; } = null!;
    internal MissionRecordBuilder Builder { get; private set; } = null!;
    internal MissionJournalConfig     Cfg     { get; private set; } = null!;

    private Harmony _harmony = null!;
    private IJournalPersistence? _lifecycle;
    private ApiMissionObserver? _missionObserver;
    private string? _lastPersistenceStatus;

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
            Log.LogError("Requires VGModAPI 0.1.2+ within 0.1.x with available session-lifecycle and save-outcomes; journal disabled without touching sidecars.");
            return;
        }
        Store.RecordingAllowed = () => _lifecycle?.CanRecord == true && (_missionObserver == null ||
            (!_missionObserver.Faulted && api!.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available)));
        try
        {
            Log.LogWarning("Using experimental VGModAPI lifecycle; full runtime qualification remains pending.");
            PatchWiring.WireAll(Builder, Store, Log);

            // Finish domain binding before subscribing or restoring any sidecar.
            _harmony = new Harmony(PluginGuid);
            bool apiMissions = Config.Bind("Missions", "UseApiMissionEvents", false, "Use verified VGModAPI mission events and saved identities; requires API-managed save data and enabled mission identity continuity.").Value;
            if (!apiMissions)
            {
                _harmony.PatchAll(typeof(MissionAcceptPatch));
                _harmony.PatchAll(typeof(MissionCompletePatch));
                _harmony.PatchAll(typeof(MissionFailPatch));
                _harmony.PatchAll(typeof(MissionAbandonPatch));
                _harmony.PatchAll(typeof(MissionArchivePatch));
            }

            bool coordinated = Config.Bind("Persistence", "UseCoordinatedPersistence", false, "Experimental; requires explicitly enabled VGModAPI persistence.").Value;
            bool importLegacy = Config.Bind("Persistence", "ImportLegacySidecars", false, "Explicit read-only adoption when no coordinated data exists for this owner; historical snapshot consistency is not inferred.").Value;
            if (apiMissions && (!coordinated || apiPlugin.Metadata.Version < new Version(0, 1, 7)
                || !api!.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available)
                || !api.Capabilities.Any(c => c.Name == "mission-continuity" && c.Available)))
                throw new InvalidOperationException("API mission events require VGModAPI 0.1.7+, enabled mission events/identity continuity and API-managed save data; no direct-hook fallback.");
            _lifecycle = coordinated
                ? new CoordinatedPersistence(ModApi.Persistence ?? throw new InvalidOperationException("Coordinated persistence unavailable; no legacy fallback."), Store, importLegacy, message => Log.LogWarning(message))
                : new LifecyclePersistence(api!, Store, Io, message => Log.LogWarning(message));
            if (apiMissions)
            {
                var events = ModApi.Missions ?? throw new InvalidOperationException("Mission events unavailable.");
                var native = events as IVersionSensitiveMissionAccess ?? throw new InvalidOperationException("Read-only mission inspection unavailable.");
                _missionObserver = new ApiMissionObserver(events, Store, () => Store.RecordingAllowed?.Invoke() == true,
                    snapshot => Builder.CreateFromAccept(InspectMission(native, snapshot)) with { MissionName = snapshot.Name, StoryId = snapshot.DefinitionId ?? string.Empty },
                    (record, state, snapshot) => Builder.AppendTransition(record, state, InspectMission(native, snapshot)),
                    message => Log.LogWarning(message), () => _lifecycle?.Dispose());
            }
            MissionJournalApi.Current = new MissionJournalQueryAdapter(Store);
            var patchCount = _harmony.GetPatchedMethods().Count();
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({patchCount} patched method(s))");
        }
        catch (Exception error)
        {
            enabled = false;
            MissionJournalApi.Current = null;
            _missionObserver?.Dispose();
            _lifecycle?.Dispose();
            _lifecycle = null;
            _harmony?.UnpatchSelf();
            Log.LogError($"Journal initialization failed; persistence disabled: {error}");
        }
    }

    private static Source.MissionSystem.Mission InspectMission(IVersionSensitiveMissionAccess access, MissionSnapshot snapshot)
    {
        if (access.TryGetNative(snapshot, out var value) && value is Source.MissionSystem.Mission mission) return mission;
        throw new InvalidOperationException("Exact dispatched mission inspection unavailable.");
    }

    private void Update()
    {
        if (_missionObserver != null && (_missionObserver.Faulted || ModApi.Current?.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available) != true))
        {
            _missionObserver.Dispose(); _missionObserver = null;
            _lifecycle?.Dispose(); _lifecycle = null;
            MissionJournalApi.Current = null;
            Log.LogError("Mission history stopped; save data writes disabled until restart.");
            enabled = false; return;
        }
        if (_lifecycle is not CoordinatedPersistence coordinated) return;
        var status = coordinated.Status;
        if (status == _lastPersistenceStatus) return;
        _lastPersistenceStatus = status;
        Log.LogInfo("Coordinated journal persistence status: " + status);
    }

    private void OnDestroy()
    {
        MissionJournalApi.Current = null;
        _missionObserver?.Dispose();
        _lifecycle?.Dispose();
        _harmony?.UnpatchSelf();
    }
}
