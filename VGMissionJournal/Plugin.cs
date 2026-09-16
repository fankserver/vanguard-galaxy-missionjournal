using System;
using BepInEx;
using BepInEx.Logging;
using VGModAPI;
using VGMissionJournal.Api;
using VGMissionJournal.Config;
using VGMissionJournal.Logging;
using VGMissionJournal.Persistence;

namespace VGMissionJournal;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency(ModApi.PluginId, MinimumApiVersionText)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid    = "vgmissionjournal";
    public const string PluginName    = "Mission Journal";
    public const string PluginVersion = "0.5.0";

    /// <summary>Earliest API with the typed service root (ModApi.Services),
    /// mission transitions + identity continuity, lifecycle session/save
    /// outcomes and save-data provider registration. The hard BepInEx
    /// dependency is the authoritative load gate; runtime health comes from
    /// typed availability checks, with no exact-minor upper gate.</summary>
    internal const string MinimumApiVersionText = "0.2.8";

    internal static Plugin          Instance { get; private set; } = null!;
    internal static ManualLogSource Log      { get; private set; } = null!;

    internal MissionStore         Store   { get; private set; } = null!;
    internal IClock               Clock   { get; private set; } = null!;
    internal JournalIO                Io      { get; private set; } = null!;
    internal MissionRecordBuilder Builder { get; private set; } = null!;
    internal MissionJournalConfig     Cfg     { get; private set; } = null!;

    private IJournalPersistence? _lifecycle;
    private ApiMissionObserver? _missionObserver;
    private IMissionService? _missions;
    private string? _lastPersistenceStatus;

    // --- reflective player placement (no compile-time game reference) ---
    private static readonly Type? _gamePlayerType = VanillaReflection.GameType("Source.Player.GamePlayer");

    private static string? ResolvePlayerCurrentSystemId()
    {
        try
        {
            if (_gamePlayerType is null) return null;
            if (!VanillaReflection.TryGetStatic(_gamePlayerType, "current", out var player) || player is null) return null;
            if (!VanillaReflection.TryGet(player, "currentPointOfInterest", out var poi) || poi is null) return null;
            if (!VanillaReflection.TryGet(poi, "system", out var system) || system is null) return null;
            return VanillaReflection.GetString(system, "guid");
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

        ModServices services;
        try
        {
            services = ModApi.Services;
        }
        catch (InvalidOperationException error)
        {
            enabled = false;
            Log.LogError($"VGModAPI services unavailable: {error.Message}; journal disabled without touching sidecars.");
            return;
        }
        var lifecycle = services.Lifecycle;
        _missions = services.Missions;
        if (!LifecyclePersistence.IsCompatible(lifecycle))
        {
            enabled = false;
            Log.LogError($"Requires VGModAPI {MinimumApiVersionText}+ with available session tracking and save outcomes "
                + $"(session-tracking: {Describe(lifecycle.SessionTracking.Availability)}, save-outcomes: {Describe(lifecycle.SaveOutcomes.Availability)}); "
                + "journal disabled without touching sidecars.");
            return;
        }
        Store.RecordingAllowed = () => _lifecycle?.CanRecord == true
            && _missionObserver is { Faulted: false }
            && _missions?.Availability.IsAvailable == true;
        bool coordinated = false;
        try
        {
            // Pure observer: transitions come exclusively from the API. No
            // direct Harmony mission hooks exist; unavailable mission services
            // stop initialization instead of falling back.
            RequireAvailable(_missions!.Availability, "mission transitions");
            RequireAvailable(_missions.IdentityContinuity.Availability, "mission identity continuity");

            bool apiManagedSaves = Config.Bind("Persistence", "UseApiSaveData", true, "Use API-managed journal saves. Experimental; disable to use legacy save files.").Value;
            coordinated = apiManagedSaves;
            bool importLegacy = Config.Bind("Persistence", "ImportLegacySidecars", false, "Read existing journal files when no API-managed journal data exists. Sources remain untouched; matching the old history to this game save is your choice.").Value;
            _lifecycle = apiManagedSaves
                ? new CoordinatedPersistence(lifecycle, services.SaveData, Store, importLegacy, message => Log.LogWarning(message))
                : new LifecyclePersistence(lifecycle, Store, Io, message => Log.LogWarning(message));
            _missionObserver = new ApiMissionObserver(_missions, Store, () => Store.RecordingAllowed?.Invoke() == true,
                snapshot => Builder.CreateFromAccept(InspectMission(_missions, snapshot), snapshot.InstanceId.ToString())
                    with { MissionName = snapshot.Name, StoryId = snapshot.DefinitionId ?? string.Empty },
                (record, state, snapshot) => Builder.AppendTransition(record, state,
                    state == TimelineState.Completed ? InspectMission(_missions, snapshot) : null),
                message => Log.LogWarning(message), () => _lifecycle?.Dispose());
            MissionJournalApi.Current = new MissionJournalQueryAdapter(Store);
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded (pure observer on VGModAPI; {(coordinated ? "API-managed" : "legacy file")} saves)");
        }
        catch (Exception error)
        {
            enabled = false;
            MissionJournalApi.Current = null;
            _missionObserver?.Dispose();
            _lifecycle?.Dispose();
            _lifecycle = null;
            Log.LogError($"Journal initialization failed; persistence disabled: {error}");
        }
    }

    private void RequireAvailable(ServiceAvailability availability, string feature)
    {
        if (!availability.IsAvailable)
            throw new InvalidOperationException($"{feature} unavailable: {availability.Reason} {availability.Detail}".Trim());
    }

    private static string Describe(ServiceAvailability availability) =>
        availability.IsAvailable ? "available" : $"{availability.Reason} ({availability.Detail})";

    /// <summary>Read-only native inspection via the API's explicitly
    /// version-sensitive escape hatch, only during the exact dispatch
    /// callback. The object is copied out immediately by the builder and
    /// never retained.</summary>
    private static object InspectMission(IMissionService access, MissionSnapshot snapshot)
    {
        if (access.TryGetNative(snapshot, out var value) && value is not null) return value;
        throw new InvalidOperationException("Exact dispatched mission inspection unavailable.");
    }

    private void Update()
    {
        if (_missionObserver != null && (_missionObserver.Faulted || _missions?.Availability.IsAvailable != true))
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
        Log.LogInfo("Journal save-data status: " + status);
    }

    private void OnDestroy()
    {
        MissionJournalApi.Current = null;
        _missionObserver?.Dispose();
        _lifecycle?.Dispose();
    }
}
