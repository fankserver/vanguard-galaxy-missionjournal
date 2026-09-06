using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Persistence;

internal sealed class LifecyclePersistence : IJournalPersistence
{
    private readonly ILifecycleApi _api;
    private readonly MissionStore _store;
    private readonly JournalIO _io;
    private readonly Action<string> _warn;
    private readonly IDisposable _subscription;
    private readonly HashSet<string> _swept = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _ready;
    private bool _disposed;

    internal LifecyclePersistence(ILifecycleApi api, MissionStore store, JournalIO io, Action<string> warn)
    {
        _api = api; _store = store; _io = io; _warn = warn;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        _subscription = api.Subscribe("vgmissionjournal.persistence", Observe);
        var current = api.CurrentSession;
        if (current != null && IsReady(current)) Restore(current);
    }

    internal static bool IsCompatible(Version version, ILifecycleApi? api) => version.Major == 0 && version.Minor == 1
        && version >= new Version(0, 1, 2) && api != null
        && api.Capabilities.Any(c => c.Available && c.Name == "session-lifecycle")
        && api.Capabilities.Any(c => c.Available && c.Name == "save-outcomes");

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
        _subscription.Dispose();
        _store.LoadFrom(Array.Empty<MissionRecord>());
    }
}
