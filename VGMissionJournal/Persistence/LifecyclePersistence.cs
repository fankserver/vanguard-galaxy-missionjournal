using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Persistence;

/// <summary>Legacy file-based persistence: writes journal sidecars beside
/// vanilla saves on witnessed save successes and restores them on session
/// readiness. Used only when [Persistence] UseApiSaveData is disabled; the
/// API-managed path lives in <see cref="CoordinatedPersistence"/>.</summary>
internal sealed class LifecyclePersistence : IJournalPersistence
{
    private readonly ILifecycleService _api;
    private readonly MissionStore _store;
    private readonly JournalIO _io;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _swept = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _ready;
    private bool _disposed;

    internal LifecyclePersistence(ILifecycleService api, MissionStore store, JournalIO io, Action<string> warn)
    {
        _api = api; _store = store; _io = io; _warn = warn;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        _api.Changed += Observe; // subscribe before reading current state
        var current = api.CurrentSession;
        if (current != null && IsReady(current)) Restore(current);
    }

    /// <summary>Floor is the earliest API exposing the typed service root
    /// (ModApi.Services) with lifecycle session tracking and save outcomes
    /// (0.2.8, enforced by the hard BepInEx dependency). No exact-minor
    /// upper gate — typed availability reports health for newer versions.</summary>
    internal static bool IsCompatible(ILifecycleService? api) =>
        api != null
        && api.SessionTracking.Availability.IsAvailable
        && api.SaveOutcomes.Availability.IsAvailable;

    public bool CanRecord => !_disposed && _ready.HasValue && IsCurrent(_ready.Value);
    private static bool IsReady(SessionSnapshot s) => s.Phase == SessionPhase.PlayerReady || s.Phase == SessionPhase.GameplayInitialized;
    private bool IsCurrent(Guid id) => _api.CurrentSession is { } current && current.Id == id && IsReady(current);

    private void Observe(LifecycleEvent e)
    {
        if (_disposed) return;
        if (e.Kind == LifecycleEventKind.SessionStarting || e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed)
        {
            if (_ready == e.Session?.Id || _api.CurrentSession?.Id == e.Session?.Id)
            {
                _ready = null;
                _store.LoadFrom(Array.Empty<MissionRecord>());
            }
            return;
        }
        if (e.Kind == LifecycleEventKind.PlayerReady && e.Session != null && _ready != e.Session.Id && IsCurrent(e.Session.Id))
        {
            Restore(e.Session);
            return;
        }
        if (e.Kind != LifecycleEventKind.SaveSucceeded || e.Session == null || e.Destination == null
            || _ready != e.Session.Id || !CanRecord) return;
        try
        {
            _io.Write(JournalPathResolver.From(e.Destination), new JournalSchema(JournalSchema.CurrentVersion, _store.AllMissions.ToArray()));
        }
        catch (Exception ex) { _warn("Journal write failed after vanilla save success: " + ex); }
    }

    private void Restore(SessionSnapshot session)
    {
        _ready = null;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        try
        {
            if (session.SavePath != null)
            {
                var directory = Path.GetDirectoryName(session.SavePath)!;
                // Unlike the former startup sweep, this uses an observed save directory.
                if (_swept.Add(directory)) DeadSidecarSweeper.Sweep(directory);
                var result = _io.Read(JournalPathResolver.From(session.SavePath));
                if (result.Status == JournalReadStatus.Loaded) _store.LoadFrom(result.Schema!.Missions);
                else if (result.Status != JournalReadStatus.MissingFile)
                    _warn("Journal sidecar unavailable (" + result.Status + "); quarantine: " + result.QuarantinedTo);
            }
            if (IsCurrent(session.Id)) _ready = session.Id;
        }
        catch (Exception ex) { _warn("Journal restore failed; persistence disabled for this attempt: " + ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = null;
        _api.Changed -= Observe;
        _store.LoadFrom(Array.Empty<MissionRecord>());
    }
}
