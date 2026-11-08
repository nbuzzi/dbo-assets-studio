using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// TextAll is a native container of variable-width text tables, not a fixed-row
// RdfSchema. This additive operation copies existing MAP_NAME records exactly.
internal static class MapNameRdfService
{
    private const int MaxBytes = 512 * 1024 * 1024;
    internal sealed record Chunk(uint Kind, byte[] Raw, byte[] Payload);

    // Shared read-only TextAll lookup for content pickers. Categories are the
    // TABLETYPE values in TextAllTable.h (ITEM_DATA=3, MOB_DATA=5).
    public static IReadOnlyDictionary<uint, string> ReadTextCategory(string path, uint category)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("TextAll exceeds 512 MiB.");
        var chunk = ReadChunks(File.ReadAllBytes(path)).SingleOrDefault(c => c.Kind == category);
        if (chunk == null) return new Dictionary<uint, string>();
        var encoding = new System.Text.UnicodeEncoding(false, false, true);
        // TextTable's native reader accepts ID 0 (present in ITEM_DATA). Keep
        // Clone's stricter default while accepting that sentinel for lookup.
        return ReadRecords(chunk.Payload, allowZero: true).ToDictionary(p => p.Key, p => encoding.GetString(p.Value.AsSpan(6)).TrimEnd('\0'));
    }

    public static object Clone(string path, string patchPath, string destination)
    {
        if (!Path.GetFileName(path).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("map-clone requires table_text_all_data.rdf.");
        ContentSafety.NoLinks(path); ContentSafety.NoLinks(patchPath);
        if (new FileInfo(path).Length > MaxBytes || new FileInfo(patchPath).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("TextAll or patch exceeds the allowed size.");
        byte[] original = File.ReadAllBytes(path), patchBytes = File.ReadAllBytes(patchPath);
        string sourceHash = Convert.ToHexString(SHA256.HashData(original));
        using var patch = JsonDocument.Parse(patchBytes);
        if (!patch.RootElement.TryGetProperty("sourceSha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            !sourceHash.Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TextAll sourceSha256 is required and must match.");
        if (!patch.RootElement.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() == 0 || operations.GetArrayLength() > 100000)
            throw new InvalidDataException("map-clone requires 1..100000 clone operations.");
        var chunks = ReadChunks(original);
        var map = chunks.Single(c => c.Kind == 10);
        var records = ReadRecords(map.Payload);
        var occupied = records.Keys.ToHashSet();
        using var added = new MemoryStream();
        var changes = new List<object>();
        Span<byte> key = stackalloc byte[4];
        foreach (var operation in operations.EnumerateArray())
        {
            if (!operation.TryGetProperty("op", out var op) || op.GetString() != "clone" ||
                operation.EnumerateObject().Any(p => p.Name is not ("op" or "templateId" or "newId")))
                throw new InvalidDataException("MAP_NAME supports only exact record clones: op/templateId/newId.");
            uint template = Id(operation, "templateId"), target = Id(operation, "newId");
            if (!records.TryGetValue(template, out byte[]? record)) throw new InvalidDataException($"MAP_NAME template {template} absent.");
            if (!occupied.Add(target)) throw new InvalidDataException($"MAP_NAME duplicate ID {target}.");
            BinaryPrimitives.WriteUInt32LittleEndian(key, target);
            added.Write(key); added.Write(record.AsSpan(4));
            changes.Add(new { templateId = template.ToString(CultureInfo.InvariantCulture), newId = target.ToString(CultureInfo.InvariantCulture), textBytes = record.Length - 6 });
        }
        if ((long)original.Length + added.Length > MaxBytes) throw new InvalidDataException("Cloned TextAll exceeds 512 MiB.");
        using var result = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        foreach (var chunk in chunks)
        {
            if (chunk.Kind != 10) { result.Write(chunk.Raw); continue; }
            BinaryPrimitives.WriteUInt32LittleEndian(header, 10);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(chunk.Payload.Length + added.Length)));
            result.Write(header); result.Write(chunk.Payload); added.Position = 0; added.CopyTo(result);
        }
        byte[] bytes = result.ToArray();
        var final = ReadChunks(bytes); var finalRecords = ReadRecords(final.Single(c => c.Kind == 10).Payload);
        if (finalRecords.Count != occupied.Count || records.Any(p => !finalRecords[p.Key].AsSpan().SequenceEqual(p.Value)))
            throw new IOException("MAP_NAME baseline preservation failed.");
        for (int i = 0; i < chunks.Count; i++)
            if (chunks[i].Kind != 10 && !chunks[i].Raw.AsSpan().SequenceEqual(final[i].Raw))
                throw new IOException("Non-MAP_NAME TextAll chunk changed.");
        string fullDestination = ContentSafety.SafeOutput(destination), sourcePath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullDestination, sourcePath);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new IOException("Output must not contain or replace source TextAll.");
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(source)) != sourceHash) throw new IOException("TextAll source changed during preparation.");
        string pending = ContentSafety.FreshDirectory(fullDestination);
        try
        {
            string file = Path.GetFileName(path); Write(Path.Combine(pending, file), bytes);
            Write(Path.Combine(pending, file + ".original.bak"), original);
            if (!File.ReadAllBytes(Path.Combine(pending, file)).AsSpan().SequenceEqual(bytes)) throw new IOException("TextAll disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, schema = "Taiwan TextAll / MAP_NAME", utc = DateTime.UtcNow,
                source = sourcePath, sourceSha256 = sourceHash, patchSha256 = Convert.ToHexString(SHA256.HashData(patchBytes)),
                outputSha256 = Convert.ToHexString(SHA256.HashData(bytes)), sourceRecords = records.Count, records = finalRecords.Count,
                category = 10, operations = changes, diskReadback = "byte-exact", otherChunks = "preserved-byte-exact", runtimeTested = false });
            Directory.Move(pending, fullDestination);
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
        return new { ok = true, output = Path.Combine(fullDestination, Path.GetFileName(path)), addedRecords = changes.Count,
            audit = Path.Combine(fullDestination, "audit.json"), outputSha256 = Convert.ToHexString(SHA256.HashData(bytes)), runtimeTested = false };
    }

    private static uint Id(JsonElement operation, string name)
    {
        if (!operation.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            !uint.TryParse(field.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value is 0 or uint.MaxValue)
            throw new InvalidDataException($"{name} must be a nonzero uint32 ID string.");
        return value;
    }
    internal static List<Chunk> ReadChunks(byte[] source)
    {
        var chunks = new List<Chunk>(); var kinds = new HashSet<uint>(); int offset = 0;
        while (offset < source.Length)
        {
            if (source.Length - offset < 8) throw new InvalidDataException("Truncated TextAll header.");
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4)), size = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4, 4));
            if (kind >= 28 || !kinds.Add(kind) || size == 0 || size > source.Length - offset - 8) throw new InvalidDataException("Invalid Taiwan TextAll chunk bounds/type.");
            chunks.Add(new Chunk(kind, source.AsSpan(offset, checked(8 + (int)size)).ToArray(), source.AsSpan(offset + 8, (int)size).ToArray()));
            offset = checked(offset + 8 + (int)size);
        }
        if (!kinds.Contains(10)) throw new InvalidDataException("MAP_NAME chunk absent.");
        return chunks;
    }
    internal static Dictionary<uint, byte[]> ReadRecords(byte[] payload, bool allowZero = false)
    {
        if (payload.Length == 0 || payload[0] != 1) throw new InvalidDataException("MAP_NAME margin must be 1.");
        var records = new Dictionary<uint, byte[]>(); int offset = 1;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < 6) throw new InvalidDataException("Truncated MAP_NAME record.");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            int length = 6 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4, 2));
            if ((!allowZero && id == 0) || id == uint.MaxValue || length > payload.Length - offset || !records.TryAdd(id, payload.AsSpan(offset, length).ToArray()))
                throw new InvalidDataException("Invalid/duplicate MAP_NAME record.");
            offset += length;
        }
        return records;
    }
    private static void Write(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true);
    }
}
