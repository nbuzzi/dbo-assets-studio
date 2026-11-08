using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Three-way preservation for Taiwan's variable-width QuestText and TextAll RDFs.
// Records are selected as raw byte spans; the service never normalizes text.
internal static class RdfVariableTextReconcileService
{
    private const string Format = "dbo-taiwan-variable-text-reconcile-v1";
    private const int MaxBytes = 128 * 1024 * 1024;
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private sealed record Revision(string Path, string Sha256);
    private sealed record Resolution(string Id, string Field, string Choice);
    private sealed record TextPlan(string Format, int Version, string Mode, string SchemaSha256,
        Revision Base, Revision Ours, Revision Theirs, IReadOnlyList<Resolution> Resolutions);
    private sealed record Chunk(uint Kind, IReadOnlyList<Row> Rows);
    private sealed record Row(uint Id, byte[] Raw);
    private sealed record Snapshot(string Path, byte[] Bytes, string Hash, IReadOnlyList<Chunk> Chunks);

    internal static bool Handles(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("table_quest_text_data.rdf", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPlan(string path)
    {
        ContentSafety.NoLinks(path);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        return json.RootElement.ValueKind == JsonValueKind.Object &&
               json.RootElement.TryGetProperty("format", out var format) && format.GetString() == Format;
    }

    private static string Mode(string path) => Path.GetFileName(path).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase)
        ? "text-all" : "quest-text";

    private static string Fingerprint(string mode) => Hash(Encoding.UTF8.GetBytes("dbo-taiwan-variable-text-v1|" + mode + "|margin1|uint32-id|uint16-utf16le-length|preserve-raw-records"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static bool Equal(byte[]? left, byte[]? right) => left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    private static Snapshot Read(string path, string mode)
    {
        ContentSafety.NoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > MaxBytes) throw new InvalidDataException("Variable text RDF size or path invalid.");
        byte[] bytes = File.ReadAllBytes(path);
        if (mode == "quest-text")
        {
            if (!Path.GetFileName(path).Equals("table_quest_text_data.rdf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("QuestText reconciliation requires table_quest_text_data.rdf.");
            return new(Path.GetFullPath(path), bytes, Hash(bytes), [new Chunk(0, ParseRows(bytes, 0, bytes.Length))]);
        }
        if (!Path.GetFileName(path).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TextAll reconciliation requires table_text_all_data.rdf.");
        var chunks = new List<Chunk>(); var kinds = new HashSet<uint>(); int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated TextAll chunk header.");
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (kind >= 28 || !kinds.Add(kind) || size < 1 || size > bytes.Length - offset - 8)
                throw new InvalidDataException("Invalid TextAll chunk bounds/type.");
            int payload = offset + 8, end = checked(payload + (int)size);
            chunks.Add(new(kind, ParseRows(bytes, payload, end)));
            offset = end;
        }
        if (!kinds.Contains(10)) throw new InvalidDataException("TextAll MAP_NAME chunk is required.");
        return new(Path.GetFullPath(path), bytes, Hash(bytes), chunks);
    }

    private static IReadOnlyList<Row> ParseRows(byte[] bytes, int offset, int end)
    {
        if (offset >= end || bytes[offset++] != 1) throw new InvalidDataException("Variable text RDF margin must be 1.");
        var rows = new List<Row>(); var ids = new HashSet<uint>();
        while (offset < end)
        {
            if (end - offset < 6) throw new InvalidDataException("Truncated variable text record header.");
            int start = offset;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            ushort chars = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
            int length = checked(6 + chars * 2);
            if (id == uint.MaxValue || !ids.Add(id) || length > end - offset)
                throw new InvalidDataException("Invalid/duplicate variable text record.");
            _ = Utf16.GetString(bytes, offset + 6, length - 6);
            rows.Add(new(id, bytes.AsSpan(start, length).ToArray()));
            offset += length;
        }
        return rows;
    }

    private static Dictionary<uint, byte[]> Map(Chunk? chunk) => chunk?.Rows.ToDictionary(r => r.Id, r => r.Raw) ?? [];
    private static Dictionary<uint, Chunk> ChunkMap(Snapshot s) => s.Chunks.ToDictionary(c => c.Kind);
    private static string Key(string mode, uint kind, uint id) => mode == "text-all" ? kind.ToString() + ":" + id.ToString() : id.ToString();

    private static object Comparison(Snapshot b, Snapshot o, Snapshot t, string mode, out List<(uint Kind, uint Id, string Status)> changes,
        out List<Resolution> unresolved)
    {
        var bm = ChunkMap(b); var om = ChunkMap(o); var tm = ChunkMap(t);
        var kinds = b.Chunks.Select(c => c.Kind).Concat(o.Chunks.Select(c => c.Kind)).Concat(t.Chunks.Select(c => c.Kind)).Distinct().Order().ToArray();
        changes = []; unresolved = [];
        var baseIds = new HashSet<string>(); var oursIds = new HashSet<string>(); var theirsIds = new HashSet<string>();
        foreach (uint kind in kinds)
        {
            var br = Map(bm.GetValueOrDefault(kind)); var or = Map(om.GetValueOrDefault(kind)); var tr = Map(tm.GetValueOrDefault(kind));
            foreach (uint id in br.Keys.Concat(or.Keys).Concat(tr.Keys).Distinct().Order())
            {
                br.TryGetValue(id, out var bv); or.TryGetValue(id, out var ov); tr.TryGetValue(id, out var tv);
                string key = Key(mode, kind, id);
                if (bv != null) baseIds.Add(key); if (ov != null) oursIds.Add(key); if (tv != null) theirsIds.Add(key);
                string status = Equal(ov, tv) ? "same_change" : Equal(bv, ov) ? "theirs_only" : Equal(bv, tv) ? "ours_only" : "conflict";
                if (Equal(bv, ov) && Equal(bv, tv)) continue;
                changes.Add((kind, id, status));
                if (status == "conflict") unresolved.Add(new(key, "$record", ""));
            }
        }
        var deleted = baseIds.Where(id => !oursIds.Contains(id) || !theirsIds.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        var data = changes.Select(c => new { id = Key(mode, c.Kind, c.Id), category = c.Kind, status = c.Status });
        return new
        {
            mode, schemaSha256 = Fingerprint(mode), @base = new { path = b.Path, sha256 = b.Hash },
            ours = new { path = o.Path, sha256 = o.Hash }, theirs = new { path = t.Path, sha256 = t.Hash },
            changes = data.Select(c => new { c.id, field = "$record", c.category, c.status, reserved = false }),
            hasConflicts = unresolved.Count > 0, removedIds = Array.Empty<string>(),
            inputDeletionsPreserved = deleted, safeToReplaceOurs = unresolved.Count == 0
        };
    }

    private static IReadOnlyList<Resolution> ReadResolutions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name != "resolutions") ||
            !root.TryGetProperty("resolutions", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Decisions must contain only a resolutions array.");
        var result = new List<Resolution>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("id" or "field" or "choice")) ||
                !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("field", out var field) || field.GetString() != "$record" ||
                !item.TryGetProperty("choice", out var choice) || choice.GetString() is not ("ours" or "theirs"))
                throw new InvalidDataException("Variable text resolution requires id, field=$record and choice ours|theirs.");
            result.Add(new(id.GetString()!, "$record", choice.GetString()!));
        }
        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("Duplicate variable text resolution.");
        return result;
    }

    private static IReadOnlyList<Resolution> Decisions(string path)
    {
        ContentSafety.NoLinks(path);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        return ReadResolutions(json.RootElement);
    }

    public static object Plan(string bPath, string oPath, string tPath, string decisionsPath, string output)
    {
        string mode = Mode(bPath);
        if (!Handles(bPath) || !Handles(oPath) || !Handles(tPath) || Mode(oPath) != mode || Mode(tPath) != mode)
            throw new InvalidDataException("Variable text reconcile sources must be the same supported table.");
        Snapshot b = Read(bPath, mode), o = Read(oPath, mode), t = Read(tPath, mode);
        var resolutions = Decisions(decisionsPath);
        var comparison = Comparison(b, o, t, mode, out _, out var unresolved);
        var wanted = unresolved.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (resolutions.Any(r => !wanted.Contains(r.Id))) throw new InvalidDataException("Resolution does not identify an exact record conflict.");
        var plan = new TextPlan(Format, 1, mode, Fingerprint(mode), new(b.Path, b.Hash), new(o.Path, o.Hash), new(t.Path, t.Hash), resolutions);
        string full = Path.GetFullPath(output); ContentSafety.NoLinks(full); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Use the shared sealed-plan writer so Git's LF checkout preserves
        // the plan hash on Windows as well as other hosts.
        ContentSafety.WriteJson(full, plan);
        return new { plan = full, planSha256 = Hash(File.ReadAllBytes(full)), comparison };
    }

    private static (TextPlan Plan, byte[] PlanBytes, Snapshot Base, Snapshot Ours, Snapshot Theirs, object Comparison, List<(uint Kind, uint Id, string Status)> Changes, List<Resolution> Unresolved) Load(string path)
    {
        ContentSafety.NoLinks(path); byte[] bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        if (root.GetProperty("format").GetString() != Format || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported variable text reconciliation plan.");
        var plan = JsonSerializer.Deserialize<TextPlan>(bytes, ContentSafety.Json) ?? throw new InvalidDataException("Invalid variable text plan.");
        if (plan.SchemaSha256 != Fingerprint(plan.Mode) || !new[] { "quest-text", "text-all" }.Contains(plan.Mode))
            throw new InvalidDataException("Variable text schema fingerprint mismatch.");
        Snapshot b = Read(plan.Base.Path, plan.Mode), o = Read(plan.Ours.Path, plan.Mode), t = Read(plan.Theirs.Path, plan.Mode);
        if (b.Hash != plan.Base.Sha256 || o.Hash != plan.Ours.Sha256 || t.Hash != plan.Theirs.Sha256)
            throw new IOException("A pinned variable text source changed.");
        var comparison = Comparison(b, o, t, plan.Mode, out var changes, out var unresolved);
        if (plan.Resolutions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != plan.Resolutions.Count ||
            plan.Resolutions.Any(x => !unresolved.Any(c => c.Id == x.Id)))
            throw new InvalidDataException("Variable text conflict decisions are stale.");
        unresolved = unresolved.Where(c => !plan.Resolutions.Any(x => x.Id == c.Id)).ToList();
        return (plan, bytes, b, o, t, comparison, changes, unresolved);
    }

    public static object Preview(string path)
    {
        var x = Load(path);
        return new { plan = Path.GetFullPath(path), planSha256 = Hash(x.PlanBytes), comparison = x.Comparison, applicable = x.Unresolved.Count == 0 };
    }

    private static byte[] Select(byte[]? b, byte[]? o, byte[]? t, string id, IReadOnlyList<Resolution> resolutions)
    {
        // IDs are a set union. A deletion can never remove an existing record.
        if (o == null && t == null) return b ?? throw new InvalidDataException("Missing union record.");
        if (o == null) return t!;
        if (t == null) return o;
        if (Equal(o, t)) return o;
        if (Equal(b, o)) return t;
        if (Equal(b, t)) return o;
        string choice = resolutions.Single(r => r.Id == id).Choice;
        return choice == "ours" ? o : t;
    }

    private static byte[] Candidate(Snapshot b, Snapshot o, Snapshot t, string mode, IReadOnlyList<Resolution> resolutions)
    {
        var bm = ChunkMap(b); var om = ChunkMap(o); var tm = ChunkMap(t);
        var kinds = o.Chunks.Select(c => c.Kind).Concat(b.Chunks.Select(c => c.Kind)).Concat(t.Chunks.Select(c => c.Kind)).Distinct().ToArray();
        using var file = new MemoryStream();
        foreach (uint kind in kinds)
        {
            var bc = bm.GetValueOrDefault(kind); var oc = om.GetValueOrDefault(kind); var tc = tm.GetValueOrDefault(kind);
            var br = Map(bc); var or = Map(oc); var tr = Map(tc);
            var unionOrder = (oc?.Rows.Select(r => r.Id) ?? []).Concat(bc?.Rows.Select(r => r.Id) ?? []).Concat(tc?.Rows.Select(r => r.Id) ?? []).Distinct().ToArray();
            bool incomingAlreadyContainsSelectedUnion = tc != null && unionOrder.All(tr.ContainsKey) && unionOrder.All(id =>
            {
                br.TryGetValue(id, out var bv); or.TryGetValue(id, out var ov); tr.TryGetValue(id, out var tv);
                return Equal(Select(bv, ov, tv, Key(mode, kind, id), resolutions), tv);
            });
            var order = incomingAlreadyContainsSelectedUnion ? tc!.Rows.Select(r => r.Id) : unionOrder;
            using var payload = new MemoryStream(); payload.WriteByte(1);
            foreach (uint id in order)
            {
                br.TryGetValue(id, out var bv); or.TryGetValue(id, out var ov); tr.TryGetValue(id, out var tv);
                string key = Key(mode, kind, id); byte[] raw = Select(bv, ov, tv, key, resolutions); payload.Write(raw);
            }
            if (mode == "quest-text") { file.Write(payload.ToArray()); continue; }
            byte[] header = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(header, kind);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)payload.Length)); file.Write(header); payload.Position = 0; payload.CopyTo(file);
        }
        return file.ToArray();
    }

    public static object Apply(string path, string destination)
    {
        var x = Load(path);
        if (x.Unresolved.Count > 0) throw new InvalidDataException("Every variable text conflict requires an explicit decision before apply.");
        using var baseLock = new FileStream(x.Plan.Base.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var oursLock = new FileStream(x.Plan.Ours.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var theirsLock = new FileStream(x.Plan.Theirs.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Hash(baseLock) != x.Plan.Base.Sha256 || Hash(oursLock) != x.Plan.Ours.Sha256 || Hash(theirsLock) != x.Plan.Theirs.Sha256)
            throw new IOException("A variable text source changed before apply.");
        byte[] candidate = Candidate(x.Base, x.Ours, x.Theirs, x.Plan.Mode, x.Plan.Resolutions);
        string full = ContentSafety.SafeOutput(destination);
        foreach (var revision in new[] { x.Plan.Base, x.Plan.Ours, x.Plan.Theirs })
            if (revision.Path.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Candidate folder cannot contain an input.");
        string pending = ContentSafety.FreshDirectory(full);
        try
        {
            string name = x.Plan.Mode == "text-all" ? "table_text_all_data.rdf" : "table_quest_text_data.rdf";
            File.WriteAllBytes(Path.Combine(pending, name), candidate);
            foreach (var pair in new[] { ("base", x.Base), ("ours", x.Ours), ("theirs", x.Theirs) })
            {
                string folder = Path.Combine(pending, "inputs", pair.Item1); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name), pair.Item2.Bytes);
            }
            var readback = Read(Path.Combine(pending, name), x.Plan.Mode);
            if (readback.Hash != Hash(candidate)) throw new IOException("Variable text candidate readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new
            {
                version = 1, kind = "rdf-reconcile", mode = x.Plan.Mode, utc = DateTime.UtcNow,
                planSha256 = Hash(x.PlanBytes), sourceSha256 = x.Ours.Hash, outputSha256 = Hash(candidate),
                comparison = x.Comparison, resolutions = x.Plan.Resolutions,
                dependencies = new[] { x.Base.Hash, x.Ours.Hash, x.Theirs.Hash }, runtimeTested = false
            });
            Directory.Move(pending, full);
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
        string candidatePath = Path.Combine(full, x.Plan.Mode == "text-all" ? "table_text_all_data.rdf" : "table_quest_text_data.rdf");
        return new { output = candidatePath, outputSha256 = Hash(candidate), audit = Path.Combine(full, "audit.json"), runtimeTested = false };
    }
}
