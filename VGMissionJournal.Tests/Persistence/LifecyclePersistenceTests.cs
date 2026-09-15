using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
using VGMissionJournal.Logging;
using VGMissionJournal.Persistence;
using VGMissionJournal.Tests.Support;
using Xunit;

namespace VGMissionJournal.Tests.Persistence;

public sealed class LifecyclePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLifecycleService _api = new();
    private readonly MissionStore _store = new();
    private readonly JournalIO _io = new(() => DateTime.UtcNow);
    private readonly LifecyclePersistence _controller;
    public LifecyclePersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _controller = new LifecyclePersistence(_api, _store, _io, _ => { });
        _store.RecordingAllowed = () => _controller.CanRecord;
    }
    private string Save(string name) { var p = Path.Combine(_root, name + ".save"); File.WriteAllText(p, "synthetic"); return p; }
    private SessionSnapshot Ready(string? path)
    {
        var id = Guid.NewGuid();
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SessionStarting, new SessionSnapshot(id, SessionPhase.Starting, path == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, path)));
        var s = new SessionSnapshot(id, SessionPhase.PlayerReady, path == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, path);
        _api.Emit(new LifecycleEvent(LifecycleEventKind.PlayerReady, s)); return s;
    }
    private void Outcome(SessionSnapshot? session, string destination, LifecycleEventKind kind = LifecycleEventKind.SaveSucceeded)
        => _api.Emit(new LifecycleEvent(kind, session, Guid.NewGuid(), destination), updateCurrent: false);

    [Fact]
    public void SubscribesBeforeReadingCurrentState()
    {
        Assert.Equal(1, _api.SubscriberCount);

        // Contract: subscribe, THEN read CurrentSession. Pre-set a ready
        // session and attach a fresh controller: its first CurrentSession
        // read must observe the already-registered subscription.
        var path = Save("preread");
        _io.Write(JournalPathResolver.From(path), new JournalSchema(JournalSchema.CurrentVersion, new[] { TestRecords.Record(instanceId: "preread") }));
        var lateApi = new FakeLifecycleService();
        var session = new SessionSnapshot(Guid.NewGuid(), SessionPhase.PlayerReady, SessionOrigin.SaveLoad, path);
        lateApi.CurrentSession = session;
        Assert.Equal(-1, lateApi.FirstReadSubscriberCount);
        using var late = new LifecyclePersistence(lateApi, new MissionStore(), _io, _ => { });
        Assert.Equal(1, lateApi.FirstReadSubscriberCount);
    }

    [Fact]
    public void RestoresOnlyAfterReadiness()
    {
        var path = Save("source");
        _io.Write(JournalPathResolver.From(path), new JournalSchema(JournalSchema.CurrentVersion, new[] { TestRecords.Record(instanceId: "restored") }));
        var id = Guid.NewGuid();
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SessionStarting, new SessionSnapshot(id, SessionPhase.Starting, SessionOrigin.SaveLoad, path)));
        _store.Upsert(TestRecords.Record(instanceId: "during-load"));
        Assert.Empty(_store.AllMissions);
        _api.Emit(new LifecycleEvent(LifecycleEventKind.PlayerReady, new SessionSnapshot(id, SessionPhase.PlayerReady, SessionOrigin.SaveLoad, path)));
        Assert.Equal("restored", Assert.Single(_store.AllMissions).MissionInstanceId);
    }

    [Fact]
    public void SaveAsWritesOnlySuccessfulDestination()
    {
        var source = Save("source"); var destination = Save("destination"); var s = Ready(source);
        _store.Upsert(TestRecords.Record(instanceId: "saved"));
        Outcome(s, destination);
        Assert.False(File.Exists(JournalPathResolver.From(source)));
        Assert.Equal("saved", Assert.Single(_io.Read(JournalPathResolver.From(destination)).Schema!.Missions).MissionInstanceId);
    }

    [Theory]
    [InlineData(LifecycleEventKind.SaveStarted)]
    [InlineData(LifecycleEventKind.SaveFailed)]
    [InlineData(LifecycleEventKind.SaveSkipped)]
    public void NonSuccessNeverWrites(LifecycleEventKind kind)
    {
        var s = Ready(Save("source")); var destination = Save("target");
        _store.Upsert(TestRecords.Record()); Outcome(s, destination, kind);
        Assert.False(File.Exists(JournalPathResolver.From(destination)));
    }

    [Fact]
    public void ClosedPluginGateDoesNotEmptyLegacySaveWrites()
    {
        // Mission-service availability is a plugin-level gate; a save
        // observed between the availability flip and the observer teardown
        // must persist the full history, not the gate-emptied view.
        var destination = Save("gated"); var s = Ready(Save("source"));
        _store.Upsert(TestRecords.Record(instanceId: "kept"));
        _store.RecordingAllowed = () => false;
        Outcome(s, destination);
        Assert.Equal("kept", Assert.Single(_io.Read(JournalPathResolver.From(destination)).Schema!.Missions).MissionInstanceId);
    }

    [Fact]
    public void InvalidationClearsAndDisablesRecording()
    {
        var s = Ready(Save("source")); _store.Upsert(TestRecords.Record());
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SessionInvalidated, new SessionSnapshot(s.Id, SessionPhase.Invalidated, s.Origin, s.SavePath)));
        _store.Upsert(TestRecords.Record());
        Assert.Empty(_store.AllMissions); Assert.False(_controller.CanRecord);
    }

    [Fact]
    public void ReentrantCurrentReplacementRejectsOldSaveBeforeQueuedEvents()
    {
        var s = Ready(Save("source")); _store.Upsert(TestRecords.Record());
        _api.CurrentSession = new SessionSnapshot(Guid.NewGuid(), SessionPhase.Starting, SessionOrigin.NewGame, null);
        Assert.Empty(_store.AllMissions);
        Assert.Equal(0, _store.TotalMissionCount);
        Assert.Empty(_store.GetActiveMissions());
        var target = Save("stale"); Outcome(s, target);
        Assert.False(File.Exists(JournalPathResolver.From(target)));
    }

    [Fact]
    public void NullSessionSaveAfterFailureDoesNotPersistEmptyState()
    {
        var s = Ready(Save("source"));
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SessionStartFailed, new SessionSnapshot(s.Id, SessionPhase.Failed, s.Origin, s.SavePath)));
        var target = Save("failed"); Outcome(null, target);
        Assert.False(File.Exists(JournalPathResolver.From(target)));
    }

    [Fact]
    public void NewGameDoesNotInheritOldRecords()
    {
        Ready(Save("source")); _store.Upsert(TestRecords.Record()); Ready(null);
        Assert.Empty(_store.AllMissions); Assert.True(_controller.CanRecord);
    }

    [Fact]
    public void DisposalStopsWritesAndRecording()
    {
        var s = Ready(Save("source")); _controller.Dispose(); _controller.Dispose();
        var target = Save("disposed"); Outcome(s, target); _store.Upsert(TestRecords.Record());
        Assert.False(File.Exists(JournalPathResolver.From(target))); Assert.Empty(_store.AllMissions);
        Assert.Equal(0, _api.SubscriberCount); // unsubscribed from Changed
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void CompatibilityRequiresBothTypedServiceViews(bool sessionTracking, bool saveOutcomes, bool expected)
    {
        _api.SessionTracking = Available(sessionTracking, "session-tracking");
        _api.SaveOutcomes = Available(saveOutcomes, "save-outcomes");
        Assert.Equal(expected, LifecyclePersistence.IsCompatible(_api));
    }

    private static IServiceStatus Available(bool ok, string feature) =>
        new FakeStatus { Availability = ok ? ServiceAvailability.Available : new ServiceAvailability(ServiceUnavailableReason.BindingFailed, feature + " binding failed") };

    [Fact]
    public void MissingOrUnavailableApiIsRejected()
    {
        Assert.False(LifecyclePersistence.IsCompatible(null));
        _api.SessionTracking = new FakeStatus { Availability = new ServiceAvailability(ServiceUnavailableReason.UnsupportedGame, "hash gate") };
        Assert.False(LifecyclePersistence.IsCompatible(_api));
    }

    [Fact]
    public void LateAttachmentAndQueuedReadinessRestoreOnlyOnce()
    {
        var path = Save("late");
        _io.Write(JournalPathResolver.From(path), new JournalSchema(JournalSchema.CurrentVersion, new[] { TestRecords.Record(instanceId: "restored") }));
        var s = Ready(path);
        var store = new MissionStore();
        using var late = new LifecyclePersistence(_api, store, _io, _ => { });
        Assert.Equal("restored", Assert.Single(store.AllMissions).MissionInstanceId);
        store.Upsert(TestRecords.Record(instanceId: "new"));
        _api.Emit(new LifecycleEvent(LifecycleEventKind.PlayerReady, s));
        Assert.Equal(2, store.TotalMissionCount);
    }

    public void Dispose() { _controller.Dispose(); Directory.Delete(_root, true); }
}
