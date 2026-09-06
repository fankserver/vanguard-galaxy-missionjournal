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
    private static byte[] Payload() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new JournalSchema(3, new[] { TestRecords.Record(instanceId: "kept") }), JournalSchema.SerializerSettings));
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
    public void LegacyImportIsExplicitReadOnlyAndNeverWritesOnCapture()
    {
        Directory.CreateDirectory(_root);
        var save = Path.Combine(_root, "fixture.save");
        var sidecar = JournalPathResolver.From(save); var original = Payload(); File.WriteAllBytes(sidecar, original);
        var store = new MissionStore(); var api = new FakeApi();
        using (var disabled = new CoordinatedPersistence(api, store, false, _ => { }))
        { api.Provider!.Restore(Session(save), null); Assert.Empty(store.AllMissions); }
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
