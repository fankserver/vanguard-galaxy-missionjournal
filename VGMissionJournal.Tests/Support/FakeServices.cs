using System;
using System.Collections.Generic;
using VGModAPI;

namespace VGMissionJournal.Tests.Support;

/// <summary>Scriptable IServiceStatus view (SessionTracking / SaveOutcomes /
/// IdentityContinuity report health independently of the service object).</summary>
internal sealed class FakeStatus : IServiceStatus
{
    public ServiceAvailability Availability { get; set; } = ServiceAvailability.Available;

    public event Action<ServiceAvailability>? AvailabilityChanged
    {
        add { }
        remove { }
    }
}

/// <summary>Scriptable ILifecycleService for lifecycle/persistence tests.</summary>
internal sealed class FakeLifecycleService : ILifecycleService
{
    private readonly List<Action<LifecycleEvent>> _handlers = new();

    public IServiceStatus SessionTracking { get; set; } = new FakeStatus();
    public IServiceStatus SaveOutcomes { get; set; } = new FakeStatus();
    public SessionSnapshot? CurrentSession { get; set; }

    public event Action<LifecycleEvent>? Changed
    {
        add { if (value != null) _handlers.Add(value); }
        remove { if (value != null) _handlers.Remove(value); }
    }

    internal int SubscriberCount => _handlers.Count;

    public void Emit(LifecycleEvent e, bool updateCurrent = true)
    {
        if (updateCurrent) CurrentSession = e.Session;
        foreach (var handler in _handlers.ToArray()) handler(e);
    }
}

/// <summary>Scriptable IMissionService: Transitioned dispatch, availability
/// toggles, and controlled TryGetNative resolution for the exact snapshot
/// currently being dispatched.</summary>
internal sealed class FakeMissionService : IMissionService
{
    private readonly List<Action<MissionTransition>> _handlers = new();
    private readonly Dictionary<Guid, object?> _natives = new();
    private readonly Guid _session = Guid.NewGuid();
    private long _sequence;
    private Guid? _dispatching;

    public ServiceAvailability Availability { get; set; } = ServiceAvailability.Available;
    public IServiceStatus IdentityContinuity { get; set; } = new FakeStatus();
    internal Guid Session => _session;

    public event Action<ServiceAvailability>? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public event Action<MissionTransition>? Transitioned
    {
        add { if (value != null) _handlers.Add(value); }
        remove { if (value != null) _handlers.Remove(value); }
    }

    internal int SubscriberCount => _handlers.Count;

    /// <summary>Bind the native object returned by TryGetNative for a given
    /// occurrence while it is being dispatched.</summary>
    internal void BindNative(Guid instanceId, object? native) => _natives[instanceId] = native;

    public bool TryGetNative(MissionSnapshot snapshot, out object? native)
    {
        // Mirrors the contract: only the exact snapshot currently being
        // dispatched resolves; copies and stale snapshots are refused.
        if (_dispatching != snapshot.InstanceId || !_natives.TryGetValue(snapshot.InstanceId, out native))
        {
            native = null;
            return false;
        }
        return true;
    }

    /// <summary>Dispatch a witnessed transition for an occurrence.</summary>
    internal void Send(Guid instanceId, MissionTransitionKind kind, string? definitionId = "repeated-definition",
        string name = "mission", MissionIdentityEvidence evidence = MissionIdentityEvidence.SessionOnly)
    {
        var snapshot = new MissionSnapshot(_session, instanceId, definitionId, name, Array.Empty<string>(),
            kind == MissionTransitionKind.Accepted, evidence);
        Dispatch(new MissionTransition(kind, snapshot, ++_sequence));
    }

    internal void Dispatch(MissionTransition transition)
    {
        var previous = _dispatching;
        _dispatching = transition.Mission.InstanceId;
        try
        {
            foreach (var handler in _handlers.ToArray()) handler(transition);
        }
        finally { _dispatching = previous; }
    }
}

/// <summary>Scriptable ISaveDataService: captures the registered provider
/// and returns scripted registration results / registration states.</summary>
internal sealed class FakeSaveDataService : ISaveDataService
{
    private readonly List<FakeSaveDataRegistration> _issued = new();

    public ServiceAvailability Availability { get; set; } = ServiceAvailability.Available;

    public event Action<ServiceAvailability>? AvailabilityChanged
    {
        add { }
        remove { }
    }

    /// <summary>When set, the next Register call returns this refusal
    /// instead of a successful registration.</summary>
    internal SaveDataRegistrationResult? ScriptedRefusal { get; set; }

    internal PersistenceProvider? Provider { get; private set; }
    internal int RegisterCallCount { get; private set; }
    internal FakeSaveDataRegistration? Last => _issued.Count == 0 ? null : _issued[_issued.Count - 1];

    public SaveDataRegistrationResult Register(PersistenceProvider provider)
    {
        RegisterCallCount++;
        Provider = provider;
        if (ScriptedRefusal is { } refusal)
        {
            if (refusal.Succeeded) throw new InvalidOperationException("Scripted success must not be a refusal.");
            return refusal;
        }
        var registration = new FakeSaveDataRegistration();
        _issued.Add(registration);
        return new SaveDataRegistrationResult(SaveDataRegistrationStatus.Registered, registration, "");
    }
}

internal sealed class FakeSaveDataRegistration : ISaveDataRegistration
{
    private readonly List<Action<SaveDataState>> _handlers = new();
    private SaveDataState _state = new(SaveDataStateKind.Inactive);

    public SaveDataState State
    {
        get => _state;
        set { _state = value; }
    }

    public bool CanRead { get; set; }
    public bool CanMutate { get; set; }
    internal bool Disposed { get; private set; }

    public event Action<SaveDataState>? StateChanged
    {
        add { if (value != null) _handlers.Add(value); }
        remove { if (value != null) _handlers.Remove(value); }
    }

    internal void PublishState(SaveDataState state)
    {
        State = state;
        foreach (var handler in _handlers.ToArray()) handler(state);
    }

    internal void NotifyReady(Guid session)
    {
        CanRead = CanMutate = true;
        PublishState(new SaveDataState(SaveDataStateKind.Ready, session));
    }

    internal void NotifyBlocked(Guid? session, SaveDataBlockReason reason)
    {
        CanMutate = false;
        PublishState(new SaveDataState(SaveDataStateKind.Blocked, session, reason));
    }

    public void Dispose()
    {
        Disposed = true;
        CanRead = CanMutate = false;
        PublishState(new SaveDataState(SaveDataStateKind.Disposed));
    }
}
