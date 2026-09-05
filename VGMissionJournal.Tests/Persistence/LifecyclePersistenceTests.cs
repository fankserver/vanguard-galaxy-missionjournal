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
    private readonly FakeApi _api = new();
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
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.9", true)]
    [InlineData("0.0.9", false)]
    [InlineData("0.2.0", false)]
    [InlineData("1.0.0", false)]
    public void CompatibilityIsExplicit(string version, bool expected)
        => Assert.Equal(expected, LifecyclePersistence.IsCompatible(new Version(version), _api));

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

    [Fact]
    public void MissingOrUnavailableApiIsRejected()
    {
        Assert.False(LifecyclePersistence.IsCompatible(new Version(0, 1, 0), null));
        _api.Capabilities = Array.Empty<CapabilityStatus>();
        Assert.False(LifecyclePersistence.IsCompatible(new Version(0, 1, 0), _api));
    }

    public void Dispose() { _controller.Dispose(); Directory.Delete(_root, true); }
    private sealed class FakeApi : ILifecycleApi
    {
        private readonly List<Action<LifecycleEvent>> _callbacks = new();
        public SessionSnapshot? CurrentSession { get; set; }
        public IReadOnlyList<CapabilityStatus> Capabilities { get; set; } = new[] {
            new CapabilityStatus("session-lifecycle", true, false, "test"), new CapabilityStatus("save-outcomes", true, false, "test") };
        public IDisposable Subscribe(string owner, Action<LifecycleEvent> callback)
        { _callbacks.Add(callback); return new Subscription(() => _callbacks.Remove(callback)); }
        public void Emit(LifecycleEvent e, bool updateCurrent = true)
        { if (updateCurrent) CurrentSession = e.Session; foreach (var c in _callbacks.ToArray()) c(e); }
    }
    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        internal Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() { var d = _dispose; _dispose = null; d?.Invoke(); }
    }
}
