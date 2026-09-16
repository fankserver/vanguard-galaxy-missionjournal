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
    private static ApiMissionObserver Observer(FakeMissionService events, MissionStore store, Action<string>? log = null, Func<bool>? ready = null)
        => new(events, store, ready ?? (() => true), _ => TestRecords.Record(),
            (record, state, _) => record with { Timeline = record.Timeline.Concat(new[] { new TimelineEntry(state, 10, "2026-01-01T00:00:10Z") }).ToArray() }, log ?? (_ => { }));

    [Fact]
    public void SubscribesToTransitionedAndUnsubscribesOnDispose()
    {
        var events = new FakeMissionService(); var store = new MissionStore();
        using (var observer = Observer(events, store))
            Assert.Equal(1, events.SubscriberCount);
        Assert.Equal(0, events.SubscriberCount);
        var id = Guid.NewGuid();
        events.Send(id, MissionTransitionKind.Accepted);   // must be ignored after teardown
        Assert.Empty(store.AllMissions);
    }

    [Fact]
    public void RepeatedDefinitionsRemainDistinctAndArchiveDoesNotInventCompletion()
    {
        var events = new FakeMissionService(); var store = new MissionStore(); using var observer = Observer(events, store);
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
        var events = new FakeMissionService(); var store = new MissionStore(); var id = Guid.NewGuid(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add);
        events.Send(id, MissionTransitionKind.Restored); Assert.Empty(store.AllMissions);
        store.LoadFrom(new[] { TestRecords.Record(instanceId: id.ToString()) });
        events.Send(id, MissionTransitionKind.Completed);
        Assert.Equal(Outcome.Completed, store.GetByInstanceId(id.ToString())!.Outcome);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Completed);
        Assert.Single(store.AllMissions); Assert.Single(logs); Assert.Contains("no matching acceptance", logs[0]);
    }

    [Fact]
    public void AmbiguousSavedIdentityNeverMatchesByNameOrDefinition()
    {
        // MissingOrAmbiguous identity evidence yields session-local occurrence ids;
        // an outcome so marked can only update a record with the exact id — never
        // one merely sharing the definition or name.
        var events = new FakeMissionService(); var store = new MissionStore();
        var saved = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add);
        store.LoadFrom(new[] { TestRecords.Record(instanceId: saved.ToString()) });
        events.Send(unrelated, MissionTransitionKind.Completed, definitionId: "repeated-definition",
            evidence: MissionIdentityEvidence.MissingOrAmbiguous);
        Assert.Equal(TimelineState.Accepted, store.GetByInstanceId(saved.ToString())!.Timeline.Single().State);
        Assert.Null(store.GetByInstanceId(unrelated.ToString()));
        Assert.Contains("no matching acceptance", Assert.Single(logs));
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
        var events = new FakeMissionService(); var store = new MissionStore(); using var observer = Observer(events, store);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted); events.Send(id, MissionTransitionKind.Removed);
        var record = Assert.Single(store.AllMissions); Assert.False(record.IsActive); Assert.Null(record.Outcome);
        Assert.Equal(TimelineState.Removed, record.Timeline.Last().State);
    }

    [Fact]
    public void FailureThenRemovalKeepsFailureAndReplacementGetsFreshHistory()
    {
        var events = new FakeMissionService(); var store = new MissionStore(); using var observer = Observer(events, store);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted); events.Send(id, MissionTransitionKind.Failed); events.Send(id, MissionTransitionKind.Removed);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Equal(2, store.AllMissions.Count); Assert.Equal(2, store.GetByInstanceId(id.ToString())!.Timeline.Count);
        Assert.Single(store.GetActiveMissions());
    }

    [Fact]
    public void UnavailableSaveDataAndDisposalDoNotWrite()
    {
        var events = new FakeMissionService(); var store = new MissionStore(); var observer = Observer(events, store, ready: () => false);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted); observer.Dispose(); events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Empty(store.AllMissions);
    }

    [Fact]
    public void FutureEventDoesNotFaultOrChangeKnownHistory()
    {
        var events = new FakeMissionService(); var store = new MissionStore(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add);
        var id = Guid.NewGuid(); events.Send(id, MissionTransitionKind.Accepted);
        // A newer producer can define values rejected by this reference's constructor.
        var value = new MissionTransition(MissionTransitionKind.Accepted,
            new MissionSnapshot(events.Session, id, null, "mission", Array.Empty<string>(), false), 99);
        typeof(MissionTransition).GetField("<Kind>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(value, (MissionTransitionKind)999);
        events.Dispatch(value);
        Assert.False(observer.Faulted); Assert.Single(store.GetByInstanceId(id.ToString())!.Timeline);
        Assert.Contains("Unsupported", Assert.Single(logs));
        events.Send(id, MissionTransitionKind.Completed); Assert.Equal(Outcome.Completed, store.GetByInstanceId(id.ToString())!.Outcome);
    }

    [Fact]
    public void UnavailableWarningsAreBoundedUntilReadinessIsObservedAgain()
    {
        bool ready = false; var events = new FakeMissionService(); var store = new MissionStore(); var logs = new List<string>();
        using var observer = Observer(events, store, logs.Add, () => ready);
        for (int i = 0; i < 10; i++) events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Single(logs); ready = true; events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        ready = false; events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted); Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void InspectionFailureImmediatelyStopsSaveDataAndFutureObservations()
    {
        var events = new FakeMissionService(); var store = new MissionStore(); bool stopped = false;
        using var observer = new ApiMissionObserver(events, store, () => true, _ => throw new Exception("inspection failed"), (r, _, _) => r, _ => { }, () => stopped = true);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.True(stopped); Assert.True(observer.Faulted); Assert.Empty(store.AllMissions);
        events.Send(Guid.NewGuid(), MissionTransitionKind.Accepted);
        Assert.Empty(store.AllMissions);
    }

    [Fact]
    public void AcceptedRecordCarriesSnapshotNameAndDefinition()
    {
        var events = new FakeMissionService(); var store = new MissionStore();
        using var observer = new ApiMissionObserver(events, store, () => true,
            snapshot => TestRecords.Record() with { MissionName = snapshot.Name, StoryId = snapshot.DefinitionId ?? string.Empty },
            (record, state, _) => record with { Timeline = record.Timeline.Concat(new[] { new TimelineEntry(state, 10, "2026-01-01T00:00:10Z") }).ToArray() },
            _ => { });
        var id = Guid.NewGuid();
        events.Send(id, MissionTransitionKind.Accepted, definitionId: "story.op-3", name: "Recovered Cargo");
        var record = Assert.Single(store.AllMissions);
        Assert.Equal("Recovered Cargo", record.MissionName);
        Assert.Equal("story.op-3", record.StoryId);
        Assert.Equal(id.ToString(), record.MissionInstanceId);
    }
}
