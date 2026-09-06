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
    private static SessionSnapshot Session(string? path = null) => new(Guid.NewGuid(), SessionPhase.PlayerReady, path == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, path);
    private static JournalSchema Schema() => new(3, new[] { TestRecords.Record(instanceId: "kept") });
    private static byte[] JsonPayload() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Schema(), JournalSchema.SerializerSettings));
    private static byte[] Payload() => JournalPayloadCodec.Encode(Schema());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void CaptureBypassesClosedPublicGateWithoutLosingRecords()
    {
        var api = new FakeApi(); var store = new MissionStore();
        using var controller = new CoordinatedPersistence(api, store, false, _ => { });
        store.RecordingAllowed = () => controller.CanRecord;
        api.Provider!.Restore(Session(), Payload()); api.Handle.MutationAllowed = true;
        Assert.Single(store.AllMissions);
        api.Handle.MutationAllowed = false;
        Assert.Empty(store.AllMissions);
        var captured = api.Provider.Capture();
        Assert.True(api.Provider.Validate(captured));
        api.Provider.Restore(Session(), captured); api.Handle.MutationAllowed = true;
        Assert.Equal("kept", Assert.Single(store.AllMissions).MissionInstanceId);
    }

    [Fact]
    public void SaveWithoutLegacyHistoryStartsEmptyWithoutImport()
    {
        var api = new FakeApi(); var store = new MissionStore();
        using var controller = new CoordinatedPersistence(api, store, false, _ => { });
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
        var store = new MissionStore(); var api = new FakeApi();
        string? warning = null;
        using (var disabled = new CoordinatedPersistence(api, store, false, message => warning = message))
        {
            Assert.Throws<InvalidDataException>(() => api.Provider!.Restore(Session(save), null));
            Assert.Contains("ImportLegacySidecars", warning!);
            Assert.Contains("UseApiSaveData", warning!);
            Assert.Empty(store.AllMissions); Assert.Equal(original, File.ReadAllBytes(sidecar));
        }
        using var enabled = new CoordinatedPersistence(api, store, true, _ => { });
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
        var api = new FakeApi();
        using var controller = new CoordinatedPersistence(api, new MissionStore(), true, _ => { });
        Assert.ThrowsAny<Exception>(() => api.Provider!.Restore(Session(save), null));
        Assert.False(api.Provider!.Validate(Encoding.UTF8.GetBytes(raw)));
        Assert.Equal(raw, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void DisposalDisablesBeforeClearingState()
    {
        var api = new FakeApi(); var store = new MissionStore();
        var controller = new CoordinatedPersistence(api, store, false, _ => { });
        api.Provider!.Restore(Session(), Payload()); api.Handle.MutationAllowed = true;
        Assert.True(controller.CanRecord);
        controller.Dispose(); controller.Dispose();
        Assert.True(api.Handle.Disposed); Assert.False(controller.CanRecord); Assert.Empty(store.AllMissions);
    }

    [Fact]
    public void KnownPayloadWinsOverLegacyAndMissingDirectoryMeansNoImport()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save"); var path = JournalPathResolver.From(save);
        File.WriteAllText(path, "broken");
        var api = new FakeApi(); var store = new MissionStore(); int warnings = 0;
        using var controller = new CoordinatedPersistence(api, store, true, _ => warnings++);
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
        var api = new FakeApi(); var store = new MissionStore();
        using var controller = new CoordinatedPersistence(api, store, true, _ => { });
        Assert.Throws<InvalidDataException>(() => api.Provider!.Restore(Session(save), null));
        Assert.Equal(oversized, File.ReadAllText(path));
        store.LoadFrom(new[] { TestRecords.Record(instanceId: oversized) });
        Assert.Throws<InvalidDataException>(() => api.Provider!.Capture());
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

    private sealed class FakeApi : IPersistenceApi
    {
        internal PersistenceProvider? Provider;
        internal FakeHandle Handle = new();
        public IPersistenceRegistration Register(PersistenceProvider provider)
        { Provider = provider; Handle = new FakeHandle(); return Handle; }
    }
    private sealed class FakeHandle : IPersistenceRegistration
    {
        public bool MutationAllowed { get; set; }
        public string Status => Disposed ? "inactive" : "ready";
        internal bool Disposed;
        public void Dispose() { Disposed = true; MutationAllowed = false; }
    }
}
