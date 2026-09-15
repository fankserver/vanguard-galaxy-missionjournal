using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using VGModAPI;
using VGMissionJournal.Logging;
using VGMissionJournal.Persistence;
using VGMissionJournal.Tests.Support;
using Xunit;

namespace VGMissionJournal.Tests.Persistence;

public sealed class CoordinatedPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-coord-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLifecycleService _lifecycle = new();
    private static SessionSnapshot Session(string? path = null) => new(Guid.NewGuid(), SessionPhase.PlayerReady, path == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, path);
    private static JournalSchema Schema() => new(3, new[] { TestRecords.Record(instanceId: "kept") });
    private static byte[] JsonPayload() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Schema(), JournalSchema.SerializerSettings));
    private static byte[] Payload() => JournalPayloadCodec.Encode(Schema());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static CoordinatedPersistence Create(FakeSaveDataService api, MissionStore store, bool importLegacy, Action<string> warn, FakeLifecycleService? lifecycle = null)
        => new(lifecycle ?? new FakeLifecycleService(), api, store, importLegacy, warn);

    [Fact]
    public void RegistersProviderWithOwnerAndSchemaShape()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { }, _lifecycle);
        Assert.Equal(1, api.RegisterCallCount);
        Assert.Equal("vgmissionjournal", api.Provider!.Owner);
        Assert.Equal(1, api.Provider.SchemaVersion);
        Assert.NotNull(api.Provider.Capture);
        Assert.NotNull(api.Provider.Restore);
        Assert.NotNull(api.Provider.Validate);
        Assert.Empty(api.Provider.Migrations);
        Assert.Equal(1, _lifecycle.SubscriberCount); // lifecycle.Changed subscribed
    }

    [Theory]
    [InlineData(SaveDataRegistrationStatus.Unavailable)]
    [InlineData(SaveDataRegistrationStatus.SessionAlreadyStarted)]
    [InlineData(SaveDataRegistrationStatus.DuplicateProvider)]
    [InlineData(SaveDataRegistrationStatus.LimitExceeded)]
    [InlineData(SaveDataRegistrationStatus.InvalidProvider)]
    public void RegistrationRefusalIsReportedWithStatusAndDetail(SaveDataRegistrationStatus status)
    {
        var api = new FakeSaveDataService { ScriptedRefusal = new SaveDataRegistrationResult(status, null, "operator-readable detail") };
        var store = new MissionStore();
        var error = Assert.Throws<InvalidOperationException>(() => Create(api, store, false, _ => { }, _lifecycle));
        Assert.Contains("refused", error.Message);
        Assert.Contains(status.ToString(), error.Message);
        Assert.Contains("operator-readable detail", error.Message);
        Assert.Empty(store.AllMissions);
        Assert.Equal(0, _lifecycle.SubscriberCount); // never subscribed on refusal
    }

    [Fact]
    public void CaptureBypassesClosedPublicGateWithoutLosingRecords()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { });
        store.RecordingAllowed = () => controller.CanRecord;
        api.Provider!.Restore(Session(), Payload()); api.Last!.CanMutate = true;
        Assert.Single(store.AllMissions);
        api.Last!.CanMutate = false;
        Assert.Empty(store.AllMissions);
        var captured = api.Provider.Capture();
        Assert.True(api.Provider.Validate(captured));
        api.Provider.Restore(Session(), captured); api.Last!.CanMutate = true;
        Assert.Equal("kept", Assert.Single(store.AllMissions).MissionInstanceId);
    }

    [Fact]
    public void SaveWithoutLegacyHistoryStartsEmptyWithoutImport()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { });
        api.Provider!.Restore(Session(Path.Combine(_root, "new.save")), null);
        Assert.Empty(JournalPayloadCodec.Decode(api.Provider.Capture()).Missions);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void LegacyImportIsExplicitReadOnlyAndNeverWritesOnCapture()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save");
        var sidecar = JournalPathResolver.From(save); var original = JsonPayload(); File.WriteAllBytes(sidecar, original);
        var store = new MissionStore(); var api = new FakeSaveDataService();
        string? warning = null;
        using (var disabled = Create(api, store, false, message => warning = message))
        {
            Assert.Throws<InvalidDataException>(() => api.Provider!.Restore(Session(save), null));
            Assert.Contains("ImportLegacySidecars", warning!);
            Assert.Contains("UseApiSaveData", warning!);
            Assert.Empty(store.AllMissions); Assert.Equal(original, File.ReadAllBytes(sidecar));
        }
        using var enabled = Create(api, store, true, _ => { });
        api.Provider!.Restore(Session(save), null);
        Assert.Equal("kept", Assert.Single(store.AllMissions).MissionInstanceId);
        _ = api.Provider.Capture();
        Assert.Equal(original, File.ReadAllBytes(sidecar));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":999,\"missions\":[]}")]
    [InlineData("{\"version\":3,\"missions\":null}")]
    public void BadLegacySourceIsPreservedAndRestoreThrows(string raw)
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        File.WriteAllText(path, raw);
        var api = new FakeSaveDataService();
        using var controller = Create(api, new MissionStore(), true, _ => { });
        Assert.ThrowsAny<Exception>(() => api.Provider!.Restore(Session(save), null));
        Assert.False(api.Provider!.Validate(Encoding.UTF8.GetBytes(raw)));
        Assert.Equal(raw, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void RestoreFailureIsSurfacedAndRethrownToTheCoordinator()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        File.WriteAllText(path, "{broken");
        var api = new FakeSaveDataService(); string? warning = null;
        using var controller = Create(api, new MissionStore(), true, message => warning = message);
        Assert.ThrowsAny<Exception>(() => api.Provider!.Restore(Session(save), null));
        Assert.Contains("restore failed", warning);
        Assert.Matches("restore failed: (JsonReader|InvalidData)Exception", warning);
    }

    [Fact]
    public void DisposalDisablesBeforeClearingState()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        var controller = Create(api, store, false, _ => { }, _lifecycle);
        api.Provider!.Restore(Session(), Payload()); api.Last!.NotifyReady(Guid.NewGuid());
        Assert.True(controller.CanRecord);
        controller.Dispose(); controller.Dispose();
        Assert.True(api.Last!.Disposed); Assert.False(controller.CanRecord); Assert.Empty(store.AllMissions);
        Assert.Equal(0, _lifecycle.SubscriberCount); // unsubscribed
    }

    [Fact]
    public void StatusReportsRegistrationStateForDiagnostics()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { });
        var session = Guid.NewGuid();
        api.Provider!.Restore(Session(), Payload()); api.Last!.NotifyReady(session);
        Assert.StartsWith("ready", controller.Status);
        api.Last!.NotifyBlocked(session, SaveDataBlockReason.RestoreFailed);
        Assert.Equal("blocked:RestoreFailed", controller.Status);
        controller.Dispose();
        Assert.Equal("disposed", controller.Status);
    }

    [Theory]
    [InlineData(LifecycleEventKind.SessionStarting)]
    [InlineData(LifecycleEventKind.SessionInvalidated)]
    [InlineData(LifecycleEventKind.SessionStartFailed)]
    public void SessionLossClearsWitnessedHistory(LifecycleEventKind kind)
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { }, _lifecycle);
        var session = Session();
        api.Provider!.Restore(session, Payload()); api.Last!.NotifyReady(session.Id);
        store.RecordingAllowed = () => controller.CanRecord;
        Assert.Single(store.AllMissions);
        _lifecycle.Emit(new LifecycleEvent(kind, new SessionSnapshot(session.Id, SessionPhase.Invalidated, session.Origin, session.SavePath)));
        Assert.Empty(store.AllMissions);
    }

    [Fact]
    public void UnrelatedSessionEventsDoNotClearCurrentHistory()
    {
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, false, _ => { }, _lifecycle);
        var session = Session();
        api.Provider!.Restore(session, Payload()); api.Last!.NotifyReady(session.Id);
        store.RecordingAllowed = () => controller.CanRecord;
        // updateCurrent=false: a stray event for another, non-current session.
        var unrelated = Session();
        _lifecycle.Emit(new LifecycleEvent(LifecycleEventKind.SessionInvalidated, unrelated), updateCurrent: false);
        Assert.Single(store.AllMissions);
    }

    [Fact]
    public void KnownPayloadWinsOverLegacyAndMissingDirectoryMeansNoImport()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        File.WriteAllText(path, "broken");
        var api = new FakeSaveDataService(); var store = new MissionStore(); int warnings = 0;
        using var controller = Create(api, store, true, _ => warnings++);
        api.Provider!.Restore(Session(save), Payload());
        Assert.Single(store.AllMissions); Assert.Equal(0, warnings); Assert.Equal("broken", File.ReadAllText(path));
        api.Provider.Restore(Session(Path.Combine(_root, "absent", "fixture.save")), null);
        Assert.Empty(store.AllMissions); Assert.Equal(0, warnings);
        Assert.ThrowsAny<Exception>(() => api.Provider.Restore(Session(save), null));
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void OversizedImportIsPreservedAndOversizedCaptureIsRejected()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        var oversized = new string('x', JournalPayloadCodec.MaxJsonBytes + 1); File.WriteAllText(path, oversized);
        var api = new FakeSaveDataService(); var store = new MissionStore();
        using var controller = Create(api, store, true, _ => { });
        Assert.Throws<InvalidDataException>(() => api.Provider!.Restore(Session(save), null));
        Assert.Equal(oversized, File.ReadAllText(path));
        store.LoadFrom(new[] { TestRecords.Record(instanceId: oversized) });
        Assert.Throws<InvalidDataException>(() => api.Provider!.Capture());
    }

    [Fact]
    public void ImportOverCapTrialsThePostEvictionSetNotTheRawFile()
    {
        // A legacy journal whose FULL set exceeds the compressed envelope but
        // whose post-Eviction set (store cap) fits must import: capture would
        // only ever publish the capped set.
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        var rng = new Random(11);
        string Big() { var b = new byte[600_000]; rng.NextBytes(b); return Convert.ToBase64String(b); }
        var schema = new JournalSchema(3, new[]
        {
            TestRecords.Record(instanceId: "old-" + Big(), acceptedAt: 1),
            TestRecords.Record(instanceId: "new-" + Big(), acceptedAt: 2),
        });
        var raw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(schema, JournalSchema.SerializerSettings));
        Assert.True(raw.Length <= JournalPayloadCodec.MaxJsonBytes);
        // Sanity: the two-record set exceeds the envelope, the surviving one does not.
        Assert.Throws<InvalidDataException>(() => JournalPayloadCodec.Encode(schema));
        File.WriteAllBytes(path, raw);
        var api = new FakeSaveDataService();
        var store = new MissionStore(maxMissions: 1);
        using var controller = Create(api, store, true, _ => { });
        api.Provider!.Restore(Session(save), null);   // must not throw
        Assert.Single(store.AllMissions);
        var captured = JournalPayloadCodec.Decode(api.Provider.Capture());
        var kept = Assert.Single(captured.Missions);
        Assert.StartsWith("new-", kept.MissionInstanceId);
        Assert.Equal(raw, File.ReadAllBytes(path));
    }

    [Fact]
    public void ImportThatCannotReEncodeIsRefusedAtImportTimeAndSourcePreserved()
    {
        // A legacy file inside the 16 MiB JSON limit but over the 1 MiB
        // compressed envelope must be refused at import, not admitted and
        // then wedge coordinated capture for every owner at the first save.
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        var rng = new byte[1_500_000]; new Random(7).NextBytes(rng);
        var raw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
            new JournalSchema(3, new[] { TestRecords.Record(instanceId: Convert.ToBase64String(rng)) }), JournalSchema.SerializerSettings));
        Assert.True(raw.Length <= JournalPayloadCodec.MaxJsonBytes);
        File.WriteAllBytes(path, raw);
        var api = new FakeSaveDataService(); var store = new MissionStore(); string? warning = null;
        using var controller = Create(api, store, true, message => warning = message);
        Assert.Throws<InvalidDataException>(() => api.Provider!.Restore(Session(save), null));
        Assert.Contains("API-managed save-data limits", warning);
        Assert.Contains("UseApiSaveData", warning);
        Assert.Empty(store.AllMissions);
        Assert.Equal(raw, File.ReadAllBytes(path));
    }

    [Fact]
    public void LargeLogicalHistoryRoundtripsBoundedlyWithoutTruncation()
    {
        var records = new VGMissionJournal.Logging.MissionRecord[2000];
        for (int i = 0; i < records.Length; i++) records[i] = TestRecords.Record(instanceId: i + new string('x', 2500));
        var schema = new JournalSchema(3, records);
        var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(schema, JournalSchema.SerializerSettings));
        Assert.True(json.Length > 1024 * 1024);
        var encoded = JournalPayloadCodec.Encode(schema);
        Assert.True(encoded.Length <= 1024 * 1024);
        var restored = JournalPayloadCodec.Decode(encoded);
        Assert.Equal(json, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(restored, JournalSchema.SerializerSettings)));
        Assert.False(JournalPayloadCodec.IsValid(encoded[..^1]));
        Assert.False(JournalPayloadCodec.IsValid(JsonPayload()));
    }

    [Fact]
    public void CompressionDoesNotBypassEitherSizeLimit()
    {
        var random = new byte[2 * 1024 * 1024]; new Random(42).NextBytes(random);
        Assert.Throws<InvalidDataException>(() => JournalPayloadCodec.Encode(new JournalSchema(3, new[] { TestRecords.Record(instanceId: Convert.ToBase64String(random)) })));
        using var output = new MemoryStream(); output.Write(new byte[] { 86, 71, 74, 49 });
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
            gzip.Write(new byte[JournalPayloadCodec.MaxJsonBytes + 1]);
        Assert.False(JournalPayloadCodec.IsValid(output.ToArray()));
    }
}
