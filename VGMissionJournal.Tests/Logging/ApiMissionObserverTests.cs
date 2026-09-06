using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
using VGMissionJournal.Logging;
using VGMissionJournal.Tests.Support;
using Xunit;

namespace VGMissionJournal.Tests.Logging;
public sealed class ApiMissionObserverTests
{
    private sealed class Events : IMissionEvents, IDisposable
    {
        private Action<MissionTransition>? _callback;
        private long _sequence;
        public IDisposable Subscribe(string owner, Action<MissionTransition> callback) { _callback = callback; return this; }
        internal void Send(Guid id, MissionTransitionKind kind) => _callback?.Invoke(new MissionTransition(kind,
            new MissionSnapshot(Guid.NewGuid(), id, "repeated-definition", "mission", Array.Empty<string>(), kind == MissionTransitionKind.Accepted), ++_sequence));
        internal void SendFuture(Guid id)
        {
            var value = new MissionTransition(MissionTransitionKind.Accepted,
                new MissionSnapshot(Guid.NewGuid(), id, null, "mission", Array.Empty<string>(), false), ++_sequence);
            // A newer producer can define values rejected by this test reference's constructor.
            typeof(MissionTransition).GetField("<Kind>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(value, (MissionTransitionKind)999);
            _callback?.Invoke(value);
        }
        public void Dispose() => _callback = null;
    }
    private static ApiMissionObserver Observer(Events events, MissionStore store, Action<string>? log = null, Func<bool>? ready = null)
        => new(events, store, ready ?? (() => true), _ => TestRecords.Record(),
            (record, state, _) => record with { Timeline = record.Timeline.Concat(new[] { new TimelineEntry(state, 10, "2026-01-01T00:00:10Z") }).ToArray() }, log ?? (_ => { }));
    [Fact]
    public void RepeatedDefinitionsRemainDistinctAndArchiveDoesNotInventCompletion()
    {
        var events = new Events(); var store = new MissionStore(); using var observer = Observer(events, store);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        events.Send(a, MissionTransitionKind.Accepted); events.Send(a, MissionTransitionKind.Accepted);
        events.Send(a, MissionTransitionKind.Completed); events.Send(a, MissionTransitionKind.Archived);
        events.Send(a, MissionTransitionKind.Removed); events.Send(b, MissionTransitionKind.Accepted);
        Assert.Equal(2, store.AllMissions.Count);
        Assert.Equal(new[] { TimelineState.Accepted, TimelineState.Completed }, store.GetByInstanceId(a.ToString())!.Timeline.Select(e => e.State));
        Assert.True(store.GetByInstanceId(b.ToString())!.IsActive);
    }
    [Fact]
    public void RestoredHistoryMatchesOnlyExactInstanceAndNeverFabricatesAcceptance()
    {
        var events = new Events(); var store = new MissionStore(); var id = Guid.NewGuid(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add);
        events.Send(id, MissionTransitionKind.Restored); Assert.Empty(store.AllMissions);
        store.LoadFrom(new[] { TestRecords.Record(instanceId: id.ToString()) });
        events.Send(id, MissionTransitionKind.Completed);
        Assert.Equal(Outcome.Completed, store.GetByInstanceId(id.ToString())!.Outcome);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Completed);
        Assert.Single(store.AllMissions); Assert.Single(logs); Assert.Contains("no matching acceptance", logs[0]);
    }
    [Fact]
    public void NeutralRemovalSurvivesExistingSaveDataFormat()
    {
        var schema = new VGMissionJournal.Persistence.JournalSchema(3, new[] { TestRecords.Record(terminal: TimelineState.Removed) });
        var bytes = VGMissionJournal.Persistence.JournalPayloadCodec.Encode(schema);
        var loaded = VGMissionJournal.Persistence.JournalPayloadCodec.Decode(bytes);
        var record = Assert.Single(loaded.Missions); Assert.False(record.IsActive); Assert.Null(record.Outcome);
        Assert.Equal(TimelineState.Removed, record.Timeline.Last().State);
    }
    [Fact]
    public void NeutralRemovalIsNotAbandonmentOrFailure()
    {
        var events = new Events(); var store = new MissionStore(); using var observer = Observer(events, store);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted); events.Send(id, MissionTransitionKind.Removed);
        var record = Assert.Single(store.AllMissions); Assert.False(record.IsActive); Assert.Null(record.Outcome);
        Assert.Equal(TimelineState.Removed, record.Timeline.Last().State);
    }
    [Fact]
    public void FailureThenRemovalKeepsFailureAndReplacementGetsFreshHistory()
    {
        var events = new Events(); var store = new MissionStore(); using var observer = Observer(events, store);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted); events.Send(id, MissionTransitionKind.Failed); events.Send(id, MissionTransitionKind.Removed);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Equal(2, store.AllMissions.Count); Assert.Equal(2, store.GetByInstanceId(id.ToString())!.Timeline.Count);
        Assert.Single(store.GetActiveMissions());
    }
    [Fact]
    public void UnavailableSaveDataAndDisposalDoNotWrite()
    {
        var events = new Events(); var store = new MissionStore(); var observer = Observer(events, store, ready: () => false);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted); observer.Dispose(); events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Empty(store.AllMissions);
    }
    [Fact]
    public void FutureEventDoesNotFaultOrChangeKnownHistory()
    {
        var events = new Events(); var store = new MissionStore(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted); events.SendFuture(id);
        Assert.False(observer.Faulted); Assert.Single(store.GetByInstanceId(id.ToString())!.Timeline);
        Assert.Contains("Unsupported", Assert.Single(logs));
        events.Send(id, MissionTransitionKind.Completed); Assert.Equal(Outcome.Completed, store.GetByInstanceId(id.ToString())!.Outcome);
    }
    [Fact]
    public void UnavailableWarningsAreBoundedUntilReadinessIsObservedAgain()
    {
        bool ready = false; var events = new Events(); var store = new MissionStore(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add, () => ready);
        for (int i = 0; i < 10; i++) events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Single(logs); ready = true; events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        ready = false; events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted); Assert.Equal(2, logs.Count);
    }
    [Fact]
    public void InspectionFailureImmediatelyStopsSaveDataAndFutureObservations()
    {
        var events = new Events(); var store = new MissionStore(); bool stopped = false;
        using var observer = new ApiMissionObserver(events, store, () => true, _ => throw new Exception("inspection failed"), (r, _, _) => r, _ => { }, () => stopped = true);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.True(stopped); Assert.True(observer.Faulted); Assert.Empty(store.AllMissions);
    }
}
