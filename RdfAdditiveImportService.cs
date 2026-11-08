using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed record RdfAdditiveImportPlan(int Version, string Format, string SchemaSha256,
    RdfRevision Target, RdfRevision Source, string[] Ids, string[] ExcludedSourceDuplicateIds);

// Import exact, explicitly selected registered rows into a valid target. A reviewed
// duplicate in an external source is quarantined, never repaired or selected.
// The existing reconciliation validator remains strict and unchanged.
internal static class RdfAdditiveImportService
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string SchemaHash(RdfSchema s) => Hash(Encoding.UTF8.GetBytes(
        s.Name + ":" + s.RowSize + ":" + string.Join(';', s.Fields.Select(f => $"{f.Name}:{f.Type}:{f.Count}:{f.Offset}"))));
    private sealed class Inputs : IDisposable
    {
        private readonly List<FileStream> locks = [];
        public RdfDocument Target { get; }
        public RdfDocument Source { get; }
        public RdfField Key { get; }
        public Dictionary<string, byte[]> Existing { get; }
        public Dictionary<string, RdfRow[]> Available { get; }
        public string[] Duplicates { get; }
        public Inputs(string target, string source)
        {
            try
            {
                foreach (string path in new[] { target, source }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ContentSafety.NoLinks(path);
                    var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(handle);
                    if (handle.Length > 128L * 1024 * 1024) throw new IOException("Item RDF exceeds128MiB.");
                }
                (Target, Key) = RdfCli.Open(target);
                (Source, _) = RdfCli.Open(source);
                if (Target.Schema.Name is not ("ItemData" or "UseItem" or "ItemOption" or "Charm" or "SetItemTable" or "Skill" or "MobData" or "ObjectSpawn" or "SpawnData") || Target.Margin != 1 || Source.Margin != 1
                    || SchemaHash(Target.Schema) != SchemaHash(Source.Schema))
                    throw new InvalidDataException("Requires matching registered Taiwan Item/UseItem/ItemOption/Charm/SetItem/Skill/Mob/ObjectSpawn schemas/margin1.");
                if(Target.Schema.Name=="MobData"&&Target.Schema.RowSize!=584)
                    throw new InvalidDataException("Registered native Taiwan Mob584 ABI required.");
                if(Target.Schema.Name=="ObjectSpawn"&&Target.Schema.RowSize!=868)
                    throw new InvalidDataException("Registered native Taiwan ObjectSpawn868 ABI required.");
                if(Target.Schema.Name=="SpawnData"&&Target.Schema.RowSize!=84)
                    throw new InvalidDataException("Registered native Taiwan Mob/NPC Spawn84 ABI required.");
                RdfCli.ValidateFinalRows(Target, Key);
                Existing = Target.Rows.ToDictionary(r => Target.Schema.Decode(Key, r.Current), r => r.Current);
                Available = Source.Rows.GroupBy(r => Source.Schema.Decode(Key, r.Current)).ToDictionary(g => g.Key, g => g.ToArray());
                if (Available.Keys.Any(id => id is "0" or "4294967295")) throw new InvalidDataException("Invalid source Item ID.");
                Duplicates = Available.Where(p => p.Value.Length != 1).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
                if(Target.Schema.Name is "MobData" or "ObjectSpawn" or "SpawnData" && Duplicates.Length!=0)
                    throw new InvalidDataException("Native Mob/ObjectSpawn source requires unique IDs; no duplicate quarantine is allowed.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in locks) handle.Dispose(); }
    }
    private static byte[] Candidate(Inputs input, RdfAdditiveImportPlan plan)
    {
        if (plan.Version != 1 || plan.Format is not ("taiwan-item-additive-import-v1" or "taiwan-fixed-additive-import-v1")
            || (plan.Format == "taiwan-item-additive-import-v1" && input.Target.Schema.Name != "ItemData")
            || plan.Ids == null || plan.ExcludedSourceDuplicateIds == null || plan.SchemaSha256 != SchemaHash(input.Target.Schema)
            || !plan.Target.Sha256.Equals(input.Target.OriginalHash, StringComparison.OrdinalIgnoreCase)
            || !plan.Source.Sha256.Equals(input.Source.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plan/source/target/schema changed.");
        if (plan.ExcludedSourceDuplicateIds.Length > 64 || plan.ExcludedSourceDuplicateIds.Distinct().Count() != plan.ExcludedSourceDuplicateIds.Length
            || !plan.ExcludedSourceDuplicateIds.Order(StringComparer.Ordinal).SequenceEqual(input.Duplicates))
            throw new InvalidDataException("All external duplicate IDs require an exact explicit quarantine.");
        if (plan.Ids.Length is < 1 or > 4096 || plan.Ids.Distinct().Count() != plan.Ids.Length)
            throw new InvalidDataException("Select1..4096 distinct new Item IDs.");
        using var output = new MemoryStream(); output.Write(input.Target.OriginalBytes);
        foreach (string id in plan.Ids)
        {
            if (!uint.TryParse(id, out uint value) || value is 0 or uint.MaxValue || value.ToString() != id
                || input.Existing.ContainsKey(id) || !input.Available.TryGetValue(id, out var rows) || rows.Length != 1)
                throw new InvalidDataException("Selected Item must be new, present, unique and outside quarantine: " + id);
            output.Write(rows[0].Current);
        }
        if (output.Length > 128L * 1024 * 1024) throw new IOException("Candidate exceeds128MiB.");
        return output.ToArray();
    }
    public static object Plan(string target, string source, string selection, string output)
    {
        ContentSafety.NoLinks(selection);
        if (new FileInfo(selection).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Selection exceeds1MiB.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(selection)); var p = document.RootElement;
        if (!p.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "excludedSourceDuplicateIds", "ids" }))
            throw new InvalidDataException("Selection only accepts ids/excludedSourceDuplicateIds.");
        using var input = new Inputs(target, source);
        var plan = new RdfAdditiveImportPlan(1, input.Target.Schema.Name == "ItemData" ? "taiwan-item-additive-import-v1" : "taiwan-fixed-additive-import-v1", SchemaHash(input.Target.Schema),
            new(Path.GetFullPath(target), input.Target.OriginalHash), new(Path.GetFullPath(source), input.Source.OriginalHash),
            p.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            p.GetProperty("excludedSourceDuplicateIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        byte[] bytes = Candidate(input, plan);
        ContentSafety.WriteJson(ContentSafety.SafeOutput(output), plan);
        return new { plan = Path.GetFullPath(output), outputSha256 = Hash(bytes), addedRows = plan.Ids.Length, quarantinedDuplicateIds = input.Duplicates, worldTested = false };
    }
    private static (RdfAdditiveImportPlan Plan, byte[] Bytes) ReadPlan(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Plan exceeds1MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        return (JsonSerializer.Deserialize<RdfAdditiveImportPlan>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing Item import plan."), bytes);
    }
    public static object Preview(string path)
    {
        var (plan, _) = ReadPlan(path); using var input = new Inputs(plan.Target.Path, plan.Source.Path);
        byte[] bytes = Candidate(input, plan);
        return new { outputSha256 = Hash(bytes), bytes = bytes.Length, preservedRows = input.Existing.Count,
            addedIds = plan.Ids, quarantinedSourceRows = input.Duplicates.Select(id => new { id, rows = input.Available[id].Select(r => Hash(r.Current)).ToArray() }), worldTested = false };
    }
    public static object Apply(string path, string output)
    {
        var (plan, planBytes) = ReadPlan(path); using var input = new Inputs(plan.Target.Path, plan.Source.Path);
        byte[] bytes = Candidate(input, plan); string full = ContentSafety.SafeOutput(output);
        foreach (string source in new[] { path, plan.Target.Path, plan.Source.Path })
            if (Path.GetFullPath(source).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output cannot contain inputs.");
        string pending = ContentSafety.FreshDirectory(full);
        try
        {
            string name = Path.GetFileName(plan.Target.Path), file = Path.Combine(pending, name);
            File.WriteAllBytes(file, bytes);
            var readback = RdfDocument.Open(file, input.Target.Schema); RdfCli.ValidateFinalRows(readback, input.Key);
            if (readback.OriginalHash != Hash(bytes) || !bytes.AsSpan(0, input.Target.OriginalBytes.Length).SequenceEqual(input.Target.OriginalBytes))
                throw new IOException("Native schema readback/preservation failed.");
            File.WriteAllBytes(Path.Combine(pending, name + ".original.bak"), input.Target.OriginalBytes);
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, kind = plan.Format, utc = DateTime.UtcNow,
                schema = input.Target.Schema.Name, planSha256 = Hash(planBytes), targetSha256 = input.Target.OriginalHash,
                sourceSha256 = input.Source.OriginalHash, outputSha256 = Hash(bytes), preservedRows = input.Existing.Count,
                addedIds = plan.Ids, excludedSourceDuplicateIds = input.Duplicates, existingRows = "byte-exact", selectedRows = "byte-exact", worldTested = false });
            Directory.Move(pending, full);
            return new { output = Path.Combine(full, name), outputSha256 = Hash(bytes), preservedRows = input.Existing.Count, addedRows = plan.Ids.Length, worldTested = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
