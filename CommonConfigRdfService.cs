using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// CCommonConfigTable::LoadFromBinary/SaveToBinary: margin1, DWORD ID,
// eleven WORD-length UTF16 strings. Never serialize std::wstring's ABI.
internal static class CommonConfigRdfService
{
    private sealed record Row(uint Id, string Name, string[] Values, byte[] Raw);
    private sealed record Snapshot(byte[] Bytes, string Hash, Dictionary<uint, Row> Rows);
    internal sealed record Entry(uint SourceId, uint TargetId);
    private sealed record Plan(int Version, string Profile, string Target, string TargetSha256,
        string Source, string SourceSha256, Entry[] Entries);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Snapshot Read(string path)
    {
        ContentSafety.NoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("Bounded CommonConfig snapshot required.");
        byte[] bytes = new byte[input.Length]; input.ReadExactly(bytes);
        if (bytes[0] != 1) throw new InvalidDataException("CommonConfig margin1 required.");
        int at = 1; var rows = new Dictionary<uint, Row>();
        while (at < bytes.Length)
        {
            int start = at;
            if (bytes.Length - at < 4) throw new InvalidDataException("Truncated CommonConfig ID.");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)); at += 4;
            var strings = new string[11];
            for (int i = 0; i < strings.Length; i++)
            {
                if (bytes.Length - at < 2) throw new InvalidDataException("Truncated CommonConfig string length.");
                int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)) * 2; at += 2;
                if (length > bytes.Length - at) throw new InvalidDataException("Truncated CommonConfig string.");
                strings[i] = Utf16.GetString(bytes, at, length); at += length;
                if (strings[i].Contains('\0')) throw new InvalidDataException("Embedded CommonConfig NUL.");
            }
            if (id is 0 or uint.MaxValue || rows.Count >= 10000 ||
                !rows.TryAdd(id, new(id, strings[0], strings[1..], bytes.AsSpan(start, at - start).ToArray())))
                throw new InvalidDataException("Invalid/duplicate CommonConfig ID or count.");
        }
        return new(bytes, Hash(bytes), rows);
    }
    internal static object Inspect(string path)
    {
        var s = Read(path);
        return new { schema = "taiwan-common-config-variable-v1", source = Path.GetFullPath(path), sourceSha256 = s.Hash,
            count = s.Rows.Count, records = s.Rows.Values.Select(r => new { id = r.Id, name = r.Name, values = r.Values, rowSha256 = Hash(r.Raw) }),
            nativeConsumerTested = false, runtimeModified = false };
    }
    internal static object CreatePlan(string target, string source, string selection, string output)
    {
        ContentSafety.NoLinks(selection);
        using var file = new FileStream(selection, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Bounded explicit CommonConfig entries required.");
        using var json = JsonDocument.Parse(file); TerrainGeometryService.NoDuplicateKeys(json.RootElement);
        Entry[] entries = json.RootElement.Deserialize<Entry[]>(Strict) ?? throw new InvalidDataException("Entries missing.");
        var p = new Plan(1, "taiwan-common-config-add-v1", Path.GetFullPath(target), Read(target).Hash,
            Path.GetFullPath(source), Read(source).Hash, entries);
        _ = Candidate(p); ContentSafety.WriteJson(ContentSafety.SafeOutput(output), p, canonicalLf: true); return p;
    }
    private static (Snapshot Target, Snapshot Source, byte[] Bytes) Candidate(Plan p)
    {
        if (p.Version != 1 || p.Profile != "taiwan-common-config-add-v1" || p.Entries is not { Length: > 0 and <= 4096 } ||
            !Path.IsPathFullyQualified(p.Target) || !Path.IsPathFullyQualified(p.Source)) throw new InvalidDataException("Explicit CommonConfig additive plan required.");
        var target = Read(p.Target); var source = Read(p.Source);
        if (p.TargetSha256 != target.Hash || p.SourceSha256 != source.Hash) throw new IOException("Stale CommonConfig inputs.");
        var ids = target.Rows.Keys.ToHashSet(); var selected = new HashSet<uint>();
        using var output = new MemoryStream(); output.Write(target.Bytes);
        foreach (var e in p.Entries)
        {
            if (e.TargetId is 0 or uint.MaxValue || !selected.Add(e.SourceId) || !ids.Add(e.TargetId) || !source.Rows.TryGetValue(e.SourceId, out var row))
                throw new InvalidDataException("New unique target and unique existing source CommonConfig IDs required.");
            byte[] bytes = row.Raw.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, e.TargetId); output.Write(bytes);
        }
        if (output.Length > 16 * 1024 * 1024) throw new InvalidDataException("CommonConfig candidate exceeds limit.");
        return (target, source, output.ToArray());
    }
    internal static object Run(string plan, string? output)
    {
        ContentSafety.NoLinks(plan);
        using var planLock = new FileStream(plan, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (planLock.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Bounded CommonConfig plan required.");
        using var json = JsonDocument.Parse(planLock); TerrainGeometryService.NoDuplicateKeys(json.RootElement);
        var p = json.RootElement.Deserialize<Plan>(Strict) ?? throw new InvalidDataException("Plan missing.");
        using var targetLock = new FileStream(p.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceLock = new FileStream(p.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ready = Candidate(p);
        if (output == null) return new { outputSha256 = Hash(ready.Bytes), preservedRows = ready.Target.Rows.Count, addedRows = p.Entries.Length, runtimeModified = false };
        output = ContentSafety.SafeOutput(output);
        foreach (string input in new[] { p.Target, p.Source, Path.GetFullPath(plan) })
            if (input.Equals(output, StringComparison.OrdinalIgnoreCase) || input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("CommonConfig output contains an input.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
            string file = Path.Combine(pending, "table_common_config_data.rdf"); File.WriteAllBytes(file, ready.Bytes);
            var readback = Read(file);
            if (readback.Hash != Hash(ready.Bytes) || readback.Rows.Count != ready.Target.Rows.Count + p.Entries.Length ||
                !readback.Bytes.AsSpan(0, ready.Target.Bytes.Length).SequenceEqual(ready.Target.Bytes)) throw new IOException("CommonConfig readback/preservation failed.");
            File.Copy(plan, Path.Combine(pending, "plan.json")); File.WriteAllBytes(Path.Combine(pending, "table_common_config_data.rdf.original.bak"), ready.Target.Bytes);
            File.WriteAllBytes(Path.Combine(pending, "source-snapshot.bin"), ready.Source.Bytes);
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { profile = p.Profile, planSha256 = ContentSafety.Hash(plan), targetSha256 = p.TargetSha256,
                sourceSha256 = p.SourceSha256, outputSha256 = readback.Hash, added = p.Entries, preservedRows = ready.Target.Rows.Count,
                existingRowsByteExact = true, selectedFieldsByteExactExceptOwnId = true, runtimeModified = false, nativeConsumerTested = false });
            Directory.Move(pending, output); return new { output, outputSha256 = readback.Hash, preservedRows = ready.Target.Rows.Count, addedRows = p.Entries.Length, runtimeModified = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
