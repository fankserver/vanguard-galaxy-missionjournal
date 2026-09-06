using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using VGModAPI;
using VGMissionJournal.Logging;

namespace VGMissionJournal.Persistence;

internal sealed class CoordinatedPersistence : IJournalPersistence
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly MissionStore _store;
    private readonly IPersistenceRegistration _registration;
    private readonly Action<string> _warn;
    private bool _disposed;

    internal CoordinatedPersistence(IPersistenceApi api, MissionStore store, bool importLegacy, Action<string> warn)
    {
        _store = store; _warn = warn;
        _store.LoadFrom(Array.Empty<MissionRecord>());
        _registration = api.Register(new PersistenceProvider("vgmissionjournal", JournalSchema.CurrentVersion,
            Capture, (session, payload) => Restore(session, payload, importLegacy), Validate));
    }

    public bool CanRecord => !_disposed && _registration.MutationAllowed;
    internal string Status => _registration.Status;

    private byte[] Capture() => Utf8.GetBytes(JsonConvert.SerializeObject(
        new JournalSchema(JournalSchema.CurrentVersion, _store.CaptureRecords()), JournalSchema.SerializerSettings));

    private static JournalSchema Decode(byte[] payload)
    {
        if (payload.Length > MaxBytes) throw new InvalidDataException("Journal payload exceeds coordinated limit.");
        var schema = JsonConvert.DeserializeObject<JournalSchema>(Utf8.GetString(payload), JournalSchema.SerializerSettings);
        if (schema == null || schema.Version != JournalSchema.CurrentVersion || schema.Missions == null)
            throw new InvalidDataException("Unsupported or invalid journal schema.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in schema.Missions)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.MissionInstanceId) || !ids.Add(record.MissionInstanceId) || record.Timeline == null)
                throw new InvalidDataException("Invalid journal record identity or timeline.");
            foreach (var entry in record.Timeline) if (entry == null) throw new InvalidDataException("Invalid journal timeline entry.");
        }
        return schema;
    }

    private static bool Validate(byte[] payload)
    {
        try { _ = Decode(payload); return true; }
        catch { return false; }
    }

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
                if (file.Length > MaxBytes) throw new InvalidDataException("Legacy journal exceeds coordinated limit.");
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Legacy journal grew beyond limit.");
                    buffer.Write(chunk, 0, read);
                }
                payload = buffer.ToArray();
                imported = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (payload != null) _store.LoadFrom(Decode(payload).Missions);
        if (imported) _warn("Explicit legacy journal import: source remains untouched; no historical snapshot matching is inferred.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose(); _disposed = true;
        _store.LoadFrom(Array.Empty<MissionRecord>());
    }
}
