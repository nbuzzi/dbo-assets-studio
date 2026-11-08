using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Native variable records in all 28 TextAll chunks. Reuses the same codecs
// as literal imports, while requiring explicit decisions for value conflicts.
internal static class TextAllReconcileService
{
    private sealed record Input(string Path, string Sha256);
    private sealed record Choice(uint Category, string Id, string Take);
    private sealed record PlanData(string Format, int Version, Input Base, Input Ours,
        Input Theirs, string Ordering, Choice[] Resolutions);
    private sealed record Change(uint Category, string Id, string Status, string? Base,
        string? Ours, string? Theirs);
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    private static byte[] Read(string path)
    {
        ContentSafety.NoLinks(path);
        if (!Path.GetFileName(path).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(path).Length is < 1 or > 128 * 1024 * 1024)
            throw new InvalidDataException("Native TextAll basename and maximum128MiB required.");
        TextAllRdfService.ValidateFile(path);
        byte[] b = File.ReadAllBytes(path);
        var chunks = MapNameRdfService.ReadChunks(b);
        if (chunks.Count != 28 || chunks.Any(c => c.Kind >= 28))
            throw new InvalidDataException("All28 registered native TextAll chunks required.");
        return b;
    }
    private static Input Pin(string p) => new(Path.GetFullPath(p), Hash(Read(p)));
    private static byte[] Pinned(Input p)
    {
        byte[] b = Read(p.Path);
        if (Hash(b) != p.Sha256) throw new IOException("Pinned TextAll input changed.");
        return b;
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static (byte[] Bytes, List<Change> Changes, int Conflicts) Resolve(PlanData p)
    {
        if (p.Format != "taiwan-textall-three-way-v1" || p.Version != 1 ||
            p.Ordering is not ("ours-preserve-union" or "theirs-preserve-union"))
            throw new InvalidDataException("Registered TextAll plan/order required.");
        var b = MapNameRdfService.ReadChunks(Pinned(p.Base));
        var o = MapNameRdfService.ReadChunks(Pinned(p.Ours));
        var t = MapNameRdfService.ReadChunks(Pinned(p.Theirs));
        var decisions = new Dictionary<(uint Category, uint Id), string>();
        foreach (var c in p.Resolutions)
            if (c.Category >= 28 || !uint.TryParse(c.Id, out uint id) ||
                c.Take is not ("base" or "ours" or "theirs") || !decisions.TryAdd((c.Category, id), c.Take))
                throw new InvalidDataException("Unique category/ID and explicit base/ours/theirs required.");
        var used = new HashSet<(uint, uint)>();
        var changes = new List<Change>(); int conflicts = 0;
        using var output = new MemoryStream();
        foreach (uint category in (p.Ordering == "ours-preserve-union" ? o : t).Select(c => c.Kind))
        {
            var br = MapNameRdfService.ReadRecords(b.Single(c => c.Kind == category).Payload, allowZero: true);
            var or = MapNameRdfService.ReadRecords(o.Single(c => c.Kind == category).Payload, allowZero: true);
            var tr = MapNameRdfService.ReadRecords(t.Single(c => c.Kind == category).Payload, allowZero: true);
            var first = p.Ordering == "ours-preserve-union" ? or : tr;
            var second = p.Ordering == "ours-preserve-union" ? tr : or;
            using var payload = new MemoryStream(); payload.WriteByte(1);
            foreach (uint id in first.Keys.Concat(second.Keys).Concat(br.Keys).Distinct())
            {
                br.TryGetValue(id, out var bv); or.TryGetValue(id, out var ov); tr.TryGetValue(id, out var tv);
                string take, status;
                if (ov is null) { take = tv is null ? "base" : "theirs"; status = "restore-incoming-or-base"; }
                else if (tv is null) { take = "ours"; status = "preserve-ours"; }
                else if (Equal(ov, tv)) { take = "ours"; status = "same-change"; }
                else if (Equal(ov, bv)) { take = "theirs"; status = "theirs-only"; }
                else if (Equal(tv, bv)) { take = "ours"; status = "ours-only"; }
                else
                {
                    status = "conflict"; ++conflicts;
                    if (!decisions.TryGetValue((category, id), out take!))
                        throw new InvalidDataException($"TextAll conflict category{category}/ID{id}; explicit decision required.");
                    used.Add((category, id));
                }
                byte[]? selected = take == "base" ? bv : take == "ours" ? ov : tv;
                if (selected is null) throw new InvalidDataException("TextAll reconciliation cannot remove any input ID.");
                payload.Write(selected);
                if (!Equal(bv, ov) || !Equal(bv, tv))
                    changes.Add(new(category, id.ToString(), status, bv is null ? null : Hash(bv),
                        ov is null ? null : Hash(ov), tv is null ? null : Hash(tv)));
            }
            byte[] header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, category);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)payload.Length));
            output.Write(header); payload.Position = 0; payload.CopyTo(output);
            if (output.Length > 128 * 1024 * 1024) throw new IOException("TextAll candidate exceeds128MiB.");
        }
        if (decisions.Keys.Any(k => !used.Contains(k))) throw new InvalidDataException("Decision names an absent/non-conflicting record.");
        return (output.ToArray(), changes, conflicts);
    }
    public static object Plan(string baseline, string ours, string theirs, string decisions, string output)
    {
        ContentSafety.NoLinks(decisions);
        if (new FileInfo(decisions).Length > 1024 * 1024) throw new IOException("Decisions exceed1MiB.");
        using var j = JsonDocument.Parse(File.ReadAllBytes(decisions));
        if (j.RootElement.EnumerateObject().Any(x => x.Name is not ("ordering" or "resolutions"))) throw new InvalidDataException("Unknown decision field.");
        foreach (var r in j.RootElement.GetProperty("resolutions").EnumerateArray())
            if (r.EnumerateObject().Any(x => x.Name is not ("category" or "id" or "take"))) throw new InvalidDataException("Unknown resolution field.");
        string ordering = j.RootElement.TryGetProperty("ordering", out var v) ? v.GetString()! : "ours-preserve-union";
        var p = new PlanData("taiwan-textall-three-way-v1", 1, Pin(baseline), Pin(ours), Pin(theirs), ordering,
            j.RootElement.GetProperty("resolutions").Deserialize<Choice[]>(ContentSafety.Json) ?? []);
        var r0 = Resolve(p); string path = ContentSafety.SafeOutput(output);
        if (File.Exists(path)) throw new IOException("Fresh plan required.");
        ContentSafety.WriteJson(path, p);
        return new { plan = path, planSha256 = ContentSafety.Hash(path), outputSha256 = Hash(r0.Bytes),
            conflictsResolved = r0.Conflicts, allBaseCurrentIncomingIDsPreserved = true, runtimeWritten = false };
    }
    private static PlanData Open(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Plan exceeds4MiB.");
        using var j = JsonDocument.Parse(File.ReadAllBytes(path));
        if (j.RootElement.EnumerateObject().Any(x => x.Name is not ("format" or "version" or "base" or "ours" or "theirs" or "ordering" or "resolutions"))) throw new InvalidDataException("Unknown plan field.");
        return j.RootElement.Deserialize<PlanData>(ContentSafety.Json) ?? throw new InvalidDataException("Plan required.");
    }
    public static object Preview(string path)
    {
        var r = Resolve(Open(path));
        return new { outputSha256 = Hash(r.Bytes), bytes = r.Bytes.Length, changes = r.Changes,
            conflictsResolved = r.Conflicts, allBaseCurrentIncomingIDsPreserved = true, runtimeWritten = false };
    }
    public static object Apply(string path, string output)
    {
        var p = Open(path); string planHash = ContentSafety.Hash(path);
        var locks = new[] { path, p.Base.Path, p.Ours.Path, p.Theirs.Path }.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read)).ToArray();
        string? pending = null;
        try
        {
            if (ContentSafety.Hash(path) != planHash) throw new IOException("Plan changed.");
            var r = Resolve(p); string destination = ContentSafety.SafeOutput(output);
            foreach (string input in new[] { path, p.Base.Path, p.Ours.Path, p.Theirs.Path })
                if (Path.GetFullPath(input).StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Output cannot contain an input.");
            pending = ContentSafety.FreshDirectory(destination);
            string file = Path.Combine(pending, "table_text_all_data.rdf"); File.WriteAllBytes(file, r.Bytes);
            TextAllRdfService.ValidateFile(file);
            if (ContentSafety.Hash(file) != Hash(r.Bytes)) throw new IOException("Readback differs.");
            foreach (var (role, input) in new[] { ("base", p.Base), ("ours", p.Ours), ("theirs", p.Theirs) }) File.Copy(input.Path, Path.Combine(pending, role + ".rdf.bak"));
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { schema = "TaiwanTextAll", version = 1,
                planSha256 = planHash, sourceSha256 = p.Ours.Sha256, outputSha256 = Hash(r.Bytes),
                comparison = r.Changes, conflictsResolved = r.Conflicts, ordering = p.Ordering,
                allBaseCurrentIncomingIDsPreserved = true, runtimeWritten = false });
            Directory.Move(pending, destination); pending = null;
            return new { output = Path.Combine(destination, "table_text_all_data.rdf"), outputSha256 = Hash(r.Bytes),
                audit = Path.Combine(destination, "audit.json"), runtimeWritten = false };
        }
        finally { foreach (var f in locks) f.Dispose(); if (pending is not null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
