using System;
using VGModAPI;

namespace VGMissionJournal.Logging;

/// <summary>Projects witnessed outcomes into the journal's acceptance-first history. Never synthesizes acceptance on load.</summary>
internal sealed class ApiMissionObserver : IDisposable
{
    private readonly MissionStore _store;
    private readonly Func<bool> _ready;
    private readonly Func<MissionSnapshot, MissionRecord> _accept;
    private readonly Func<MissionRecord, TimelineState, MissionSnapshot, MissionRecord> _append;
    private readonly Action<string> _log;
    private readonly Action? _stop;
    private readonly IDisposable _subscription;
    private bool _disposed;
    private bool _unavailableLogged;
    internal bool Faulted { get; private set; }

    internal ApiMissionObserver(IMissionEvents events, MissionStore store, Func<bool> ready,
        Func<MissionSnapshot, MissionRecord> accept,
        Func<MissionRecord, TimelineState, MissionSnapshot, MissionRecord> append, Action<string> log, Action? stop = null)
    {
        _store = store; _ready = ready; _accept = accept; _append = append; _log = log; _stop = stop;
        _subscription = events.Subscribe("vgmissionjournal", Receive);
    }
    private void Receive(MissionTransition transition)
    {
        if (_disposed || Faulted) return;
        // Restored is correspondence, not acceptance; persistent records are looked up only on a later witnessed outcome.
        if (transition.Kind == MissionTransitionKind.Restored || transition.Kind == MissionTransitionKind.Archived) return;
        TimelineState? state = transition.Kind switch
        {
            MissionTransitionKind.Completed => TimelineState.Completed,
            MissionTransitionKind.Failed => TimelineState.Failed,
            MissionTransitionKind.Abandoned => TimelineState.Abandoned,
            MissionTransitionKind.Removed => TimelineState.Removed,
            _ => null
        };
        if (transition.Kind != MissionTransitionKind.Accepted && state == null)
        { _log("Unsupported mission event ignored."); return; }
        if (!_ready())
        {
            if (!_unavailableLogged) _log("Mission transition not recorded while save data is unavailable.");
            _unavailableLogged = true; return;
        }
        _unavailableLogged = false;
        try
        {
            var snapshot = transition.Mission;
            string id = snapshot.InstanceId.ToString();
            var existing = _store.GetByInstanceId(id);
            if (transition.Kind == MissionTransitionKind.Accepted)
            {
                if (existing == null) _store.Upsert(_accept(snapshot) with { MissionInstanceId = id });
                return;
            }
            if (existing == null)
            {
                _log("Mission outcome has no matching acceptance history; no acceptance or historical correspondence was inferred.");
                return;
            }
            if (!existing.IsActive) return;
            _store.Upsert(_append(existing, state!.Value, snapshot));
        }
        catch (Exception error) { Faulted = true; _stop?.Invoke(); _log("Mission history observer stopped: " + error.Message); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _subscription.Dispose(); }
}
