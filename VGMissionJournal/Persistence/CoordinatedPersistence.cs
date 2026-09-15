using System;
using System.IO;
using VGModAPI;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Persistence;

/// <summary>API-managed save-data persistence: the journal registers a
/// <see cref="PersistenceProvider"/> with the API save-data coordinator and
/// never writes save files itself. Recording is gated by the live
/// registration state; session invalidation clears witnessed history.</summary>
internal sealed class CoordinatedPersistence : IJournalPersistence
{
    private readonly MissionStore _store;
    private readonly ILifecycleService _lifecycle;
    private readonly ISaveDataRegistration _registration;
    private readonly Action<string> _warn;
    private Guid? _session;
    private bool _disposed;

    internal CoordinatedPersistence(ILifecycleService lifecycle, ISaveDataService api, MissionStore store, bool importLegacy, Action<string> warn)
    {
        _store = store; _warn = warn; _lifecycle = lifecycle;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        var result = api.Register(new PersistenceProvider("vgmissionjournal", 1,
            Capture, (session, payload) => Restore(session, payload, importLegacy), JournalPayloadCodec.IsValid));
        if (!result.Succeeded)
            throw new InvalidOperationException("Journal save-data registration refused: " + result.Status + ": " + result.Detail);
        _registration = result.Registration!;
        try { _lifecycle.Changed += Observe; }
        catch { _registration.Dispose(); throw; }
    }

    public bool CanRecord => !_disposed && _registration.CanMutate;

    /// <summary>Result/status diagnostics for log surfacing; branch on
    /// <see cref="ISaveDataRegistration.State"/>, never this text.</summary>
    internal string Status => _disposed
        ? "disposed"
        : _registration.State.Kind.ToString().ToLowerInvariant()
          + (_registration.State.Reason == SaveDataBlockReason.None ? "" : ":" + _registration.State.Reason);

    private byte[] Capture() => JournalPayloadCodec.Encode(new JournalSchema(JournalSchema.CurrentVersion, _store.CaptureRecords()));

    private void Restore(SessionSnapshot session, byte[]? payload, bool importLegacy)
    {
        try { RestoreCore(session, payload, importLegacy); }
        catch (Exception error)
        {
            _warn("Journal save-data restore failed: " + error.GetType().Name + ": " + error.Message);
            throw;
        }
    }

    private void RestoreCore(SessionSnapshot session, byte[]? payload, bool importLegacy)
    {
        _session = session.Id;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        bool imported = false;
        if (payload == null && session.SavePath != null)
        {
            var path = JournalPathResolver.From(session.SavePath);
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!importLegacy) throw new InvalidDataException("Existing journal save data found. Enable ImportLegacySidecars to read it, or disable UseApiSaveData to keep legacy saves.");
                if (file.Length > JournalPayloadCodec.MaxJsonBytes) throw new InvalidDataException("Legacy journal exceeds the API save-data size limit.");
                payload = JournalPayloadCodec.ReadBounded(file);
                imported = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (payload != null)
        {
            var schema = imported ? JournalPayloadCodec.DecodeJson(payload) : JournalPayloadCodec.Decode(payload);
            if (imported)
            {
                // Fail at import time rather than wedging the coordinator at the
                // first save: a legacy file that decodes but cannot re-encode
                // inside the API envelope would abort capture for every
                // registered owner. The untouched source stays legacy-only.
                try { JournalPayloadCodec.Encode(new JournalSchema(JournalSchema.CurrentVersion, schema.Missions)); }
                catch (Exception error)
                {
                    throw new InvalidDataException("Legacy journal cannot fit the API-managed save-data limits; keep it in legacy mode ([Persistence] UseApiSaveData = false).", error);
                }
            }
            _store.LoadFrom(schema.Missions);
        }
        if (imported) _warn("Explicit legacy journal import: source remains untouched; no historical snapshot matching is inferred.");
    }

    private void Observe(LifecycleEvent e)
    {
        if (_disposed || e.Session == null) return;
        if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
        {
            if (_session == e.Session.Id || _lifecycle.CurrentSession?.Id == e.Session.Id) Clear();
        }
    }

    private void Clear()
    {
        _session = null;
        _store.LoadFrom(Array.Empty<MissionRecord>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose();
        _lifecycle.Changed -= Observe;
        Clear();
        _disposed = true;
    }
}
