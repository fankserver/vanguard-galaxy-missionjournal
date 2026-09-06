using System;
using System.Collections.Generic;

namespace VGMissionJournal.Logging;

/// <summary>
/// In-memory store of <see cref="MissionRecord"/> aggregates, keyed by
/// <see cref="MissionRecord.MissionInstanceId"/>. Replaces the v1 event-oriented
/// <c>ActivityLog</c>.
///
/// <para>Ordering is first-insert (stable). Eviction at the soft cap drops the
/// oldest-accepted mission, preserving a FIFO invariant on accept time.</para>
///
/// <para><b>Thread-safety:</b> single-threaded (Unity main). No locking.</para>
/// </summary>
internal sealed class MissionStore
{
    public const int DefaultMaxMissions = 2000;
    public const int Unbounded          = 0;

    private readonly int _maxMissions;
    private readonly Action<int>? _onFirstEviction;
    private bool _evictionNotified;

    private readonly List<MissionRecord> _records = new();
    private readonly Dictionary<string, MissionRecord> _byInstanceId =
        new(StringComparer.Ordinal);

    public MissionStore(int maxMissions = DefaultMaxMissions, Action<int>? onFirstEviction = null)
    {
        _maxMissions    = maxMissions > 0 ? maxMissions : Unbounded;
        _onFirstEviction = onFirstEviction;
    }

    internal Func<bool>? RecordingAllowed { get; set; }

    // Persistence capture runs during dispatch, when public recording/query gates are closed.
    internal MissionRecord[] CaptureRecords() => _records.ToArray();

    public bool IsUnbounded => _maxMissions == Unbounded;
    public int  MaxMissions => _maxMissions;
    public int  TotalMissionCount => AllMissions.Count;

    public IReadOnlyList<MissionRecord> AllMissions => RecordingAllowed?.Invoke() == false ? Array.Empty<MissionRecord>() : _records;

    /// <summary>Fires after every <see cref="Upsert"/>. Swallowed on throw.</summary>
    public event Action<MissionRecord>? OnMissionChanged;

    public MissionRecord? GetByInstanceId(string instanceId) =>
        RecordingAllowed?.Invoke() != false && _byInstanceId.TryGetValue(instanceId, out var r) ? r : null;

    /// <summary>
    /// Insert or replace a record. Replacement is by
    /// <see cref="MissionRecord.MissionInstanceId"/>; the insertion position
    /// is preserved so iteration order stays stable.
    /// </summary>
    public void Upsert(MissionRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (RecordingAllowed?.Invoke() == false) return;

        if (_byInstanceId.TryGetValue(record.MissionInstanceId, out var existing))
        {
            // Replace in-place without moving.
            var idx = _records.IndexOf(existing);
            _records[idx] = record;
            _byInstanceId[record.MissionInstanceId] = record;
        }
        else
        {
            _records.Add(record);
            _byInstanceId[record.MissionInstanceId] = record;
            if (!IsUnbounded && _records.Count > _maxMissions)
            {
                EvictOldest();
                NotifyFirstEvictionOnce();
            }
        }

        try { OnMissionChanged?.Invoke(record); } catch { /* subscriber failures must not interrupt */ }
    }

    public IReadOnlyList<MissionRecord> GetActiveMissions()
    {
        var result = new List<MissionRecord>();
        foreach (var r in AllMissions) if (r.IsActive) result.Add(r);
        return result;
    }

    public IReadOnlyList<MissionRecord> GetMissionsInSystem(string systemId)
    {
        var result = new List<MissionRecord>();
        foreach (var r in AllMissions)
            if (string.Equals(r.SourceSystemId, systemId, StringComparison.Ordinal))
                result.Add(r);
        return result;
    }

    public IReadOnlyList<MissionRecord> GetMissionsByFaction(string factionId)
    {
        var result = new List<MissionRecord>();
        foreach (var r in AllMissions)
            if (string.Equals(r.SourceFaction, factionId, StringComparison.Ordinal))
                result.Add(r);
        return result;
    }

    public void LoadFrom(IEnumerable<MissionRecord> records)
    {
        if (records is null) throw new ArgumentNullException(nameof(records));
        _records.Clear();
        _byInstanceId.Clear();
        _evictionNotified = false;
        foreach (var r in records)
        {
            if (r is null) continue;
            _records.Add(r);
            _byInstanceId[r.MissionInstanceId] = r;
        }
        while (!IsUnbounded && _records.Count > _maxMissions) EvictOldest();
    }

    private void EvictOldest()
    {
        var evicted = _records[0];
        _records.RemoveAt(0);
        _byInstanceId.Remove(evicted.MissionInstanceId);
    }

    private void NotifyFirstEvictionOnce()
    {
        if (_evictionNotified) return;
        _evictionNotified = true;
        _onFirstEviction?.Invoke(_maxMissions);
    }
}
