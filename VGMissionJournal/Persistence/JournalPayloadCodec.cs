using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace VGMissionJournal.Persistence;

// Owner format 1: VGJ1 + gzip of the unchanged logical JournalSchema v3.
internal static class JournalPayloadCodec
{
    internal const int MaxJsonBytes = 16 * 1024 * 1024;
    private const int MaxEnvelopePayload = 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(JournalSchema schema)
    {
        var json = Utf8.GetBytes(JsonConvert.SerializeObject(schema, JournalSchema.SerializerSettings));
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("Journal JSON exceeds coordinated limit.");
        using var output = new MemoryStream();
        output.Write(new byte[] { 86, 71, 74, 49 }, 0, 4);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(json, 0, json.Length);
        if (output.Length > MaxEnvelopePayload) throw new InvalidDataException("Compressed journal exceeds owner payload limit.");
        return output.ToArray();
    }

    internal static JournalSchema Decode(byte[] payload)
    {
        if (payload.Length > MaxEnvelopePayload || payload.Length < 22 || payload[0] != 86 || payload[1] != 71 || payload[2] != 74 || payload[3] != 49)
            throw new InvalidDataException("Invalid journal owner payload.");
        using var input = new MemoryStream(payload, 4, payload.Length - 4, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var json = ReadBounded(gzip);
        int n = payload.Length;
        uint size = (uint)(payload[n - 4] | payload[n - 3] << 8 | payload[n - 2] << 16 | payload[n - 1] << 24);
        if (size != json.Length) throw new InvalidDataException("Truncated or multi-member journal payload.");
        return DecodeJson(json);
    }

    internal static byte[] ReadBounded(Stream source)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes) throw new InvalidDataException("Journal JSON exceeds coordinated limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static JournalSchema DecodeJson(byte[] json)
    {
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("Journal JSON exceeds coordinated limit.");
        var schema = JsonConvert.DeserializeObject<JournalSchema>(Utf8.GetString(json), JournalSchema.SerializerSettings);
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

    internal static bool IsValid(byte[] payload)
    {
        try { _ = Decode(payload); return true; }
        catch { return false; }
    }
}
