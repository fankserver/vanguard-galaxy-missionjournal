using System;
using System.IO;
using VGModAPI;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Persistence;

internal sealed class CoordinatedPersistence : IJournalPersistence
{
    private readonly MissionStore _store;
    private readonly IPersistenceRegistration _registration;
    private readonly Action<string> _warn;
    private bool _disposed;

    internal CoordinatedPersistence(IPersistenceApi api, MissionStore store, bool importLegacy, Action<string> warn)
    {
        _store = store; _warn = warn;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        _registration = api.Register(new PersistenceProvider("vgmissionjournal", 1,
            Capture, (session, payload) => Restore(session, payload, importLegacy), JournalPayloadCodec.IsValid));
    }

    public bool CanRecord => !_disposed && _registration.MutationAllowed;
    internal string Status => _registration.Status;

    private byte[] Capture() => JournalPayloadCodec.Encode(new JournalSchema(JournalSchema.CurrentVersion, _store.CaptureRecords()));

    private void Restore(SessionSnapshot session, byte[]? payload, bool importLegacy)
    {
        _store.LoadFrom(Array.Empty<MissionRecord>());
        bool imported = false;
        if (payload == null && importLegacy && session.SavePath != null)
        {
            var path = JournalPathResolver.From(session.SavePath);
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > JournalPayloadCodec.MaxJsonBytes) throw new InvalidDataException("Legacy journal exceeds coordinated limit.");
                payload = JournalPayloadCodec.ReadBounded(file);
                imported = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (payload != null) _store.LoadFrom((imported ? JournalPayloadCodec.DecodeJson(payload) : JournalPayloadCodec.Decode(payload)).Missions);
        if (imported) _warn("Explicit legacy journal import: source remains untouched; no historical snapshot matching is inferred.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose(); _disposed = true;
        _store.LoadFrom(Array.Empty<MissionRecord>());
    }
}
