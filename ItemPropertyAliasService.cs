using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Shared Studio/CLI service. Both inputs must pass the strict native version2
// parser before any selected row can be appended with explicit string aliases.
internal static class ItemPropertyAliasService
{
    internal sealed record Alias(string Source, string Name, string MeshPath, string Mesh,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnimationPath = null);
    private sealed record Plan(int Version, string Profile, string Baseline, string BaselineSha256,
        string Source, string SourceSha256, string AliasesPath, string AliasesSha256,
        string HelperSha256, string OutputSha256, long OutputBytes, int BaselineCount, int SourceCount,
        List<Alias> Aliases, List<ItemPropertyCatalog.Entry> Selected);
    private sealed record Prepared(Plan Plan, byte[] Bytes);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const string Profile = "native-item-property-v2-add-aliases-v1";
    private const string ResourceProfile = "native-item-property-v2-add-resource-aliases-v2";
    private static bool Name(string value) => value.Length is > 0 and <= 256 && value.All(c => c is >= (char)33 and <= (char)126 && c != '\t');
    private static bool PathPart(string value) => Name(value) && !value.Contains(':') && !value.StartsWith('/') && !value.StartsWith('\\') &&
        !value.Replace('\\', '/').Split('/').Contains("..");
    private static async Task<Prepared> PrepareAsync(string baseline, string source, string aliasesPath)
    {
        baseline = Path.GetFullPath(baseline); source = Path.GetFullPath(source); aliasesPath = Path.GetFullPath(aliasesPath);
        foreach (var file in new[] { baseline, source, aliasesPath }) ContentSafety.NoLinks(file);
        using var baselineLock = File.Open(baseline, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceLock = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var aliasesLock = File.Open(aliasesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (new FileInfo(aliasesPath).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Bounded alias JSON required.");
        string aliasHash = ContentSafety.Hash(aliasesPath);
        var aliases = JsonSerializer.Deserialize<List<Alias>>(File.ReadAllText(aliasesPath), Strict) ?? throw new InvalidDataException("Alias list required.");
        if (aliases.Count is < 1 or > 1000 || aliases.Any(a => a == null || !Name(a.Source) || !Name(a.Name) || a.Source == a.Name ||
            !PathPart(a.MeshPath) || !PathPart(a.Mesh) || a.Mesh.Contains('/') || a.Mesh.Contains('\\') || !a.Mesh.EndsWith(".dff", StringComparison.OrdinalIgnoreCase) ||
            (a.AnimationPath is not null && (!PathPart(a.AnimationPath) || !(a.AnimationPath.EndsWith('/') || a.AnimationPath.EndsWith('\\'))))) ||
            aliases.Select(a => a.Source).Distinct(StringComparer.Ordinal).Count() != aliases.Count ||
            aliases.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Count)
            throw new InvalidDataException("Unique native source/own keys and safe DFF aliases required.");
        aliases = aliases.OrderBy(a => a.Source, StringComparer.Ordinal).ToList();
        var before = await ItemPropertyCatalog.InspectAsync(baseline); var foreign = await ItemPropertyCatalog.InspectAsync(source);
        var wanted = aliases.Select(a => a.Source).ToHashSet(StringComparer.Ordinal);
        var own = aliases.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = foreign.Entries.Where(e => wanted.Contains(e.Name)).ToList();
        if (selected.Count != aliases.Count || before.Entries.Any(e => own.Contains(e.Name))) throw new InvalidDataException("Source key absent or own key occupied in baseline.");
        bool resources = aliases.Any(a => a.AnimationPath is not null);
        if (aliases.Any(a => a.AnimationPath is not null && selected.Single(e => e.Name == a.Source).AnimationCount == 0))
            throw new InvalidDataException("Animation folder alias requires native animations.");
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsItemAliases", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        try
        {
            string bp = Path.Combine(cache, "baseline.rdf"), sp = Path.Combine(cache, "source.rdf"), mp = Path.Combine(cache, "aliases.tsv"), result = Path.Combine(cache, "itemproperty.rdf");
            File.Copy(baseline, bp); File.Copy(source, sp);
            File.WriteAllLines(mp, aliases.Select(a => resources ? string.Join('\t', a.Source, a.Name, a.MeshPath, a.Mesh, a.AnimationPath ?? "-") :
                string.Join('\t', a.Source, a.Name, a.MeshPath, a.Mesh)), Encoding.ASCII);
            if (ContentSafety.Hash(bp) != before.SourceSha256 || ContentSafety.Hash(sp) != foreign.SourceSha256 || before.HelperSha256 != foreign.HelperSha256)
                throw new IOException("Source/helper changed during snapshots.");
            await ItemPropertyCatalog.RunAsync(cache, resources ? "--add-resource-aliases" : "--add-aliases", bp, sp, mp, result);
            var round = await ItemPropertyCatalog.InspectAsync(result);
            if (round.Count != before.Count + aliases.Count || ContentSafety.Hash(baseline) != before.SourceSha256 || ContentSafety.Hash(source) != foreign.SourceSha256 ||
                ContentSafety.Hash(aliasesPath) != aliasHash || ContentSafety.Hash(ItemPropertyCatalog.Helper) != before.HelperSha256)
                throw new IOException("Input/output/helper changed.");
            foreach (var a in aliases)
            {
                var row = round.Entries.Single(e => e.Name == a.Name); var old = selected.Single(e => e.Name == a.Source);
                if (row.MeshPath != a.MeshPath || row.Mesh != a.Mesh || row.ResourceType != old.ResourceType || row.EquipSlot != old.EquipSlot || row.AnimationCount != old.AnimationCount)
                    throw new IOException("Native alias consumer metadata mismatch.");
            }
            return new(new(resources ? 2 : 1, resources ? ResourceProfile : Profile, baseline, before.SourceSha256, source, foreign.SourceSha256, aliasesPath, aliasHash,
                before.HelperSha256, ContentSafety.Hash(result), new FileInfo(result).Length, before.Count, foreign.Count, aliases, selected), File.ReadAllBytes(result));
        }
        finally { Directory.Delete(cache, true); }
    }
    public static async Task<object> CreatePlanAsync(string baseline, string source, string aliases, string destination)
    {
        var p = await PrepareAsync(baseline, source, aliases); destination = ContentSafety.SafeOutput(destination);
        foreach (var input in new[] { baseline, source, aliases, ItemPropertyCatalog.Helper })
            if (Path.GetFullPath(input).Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Disjoint new plan required.");
        ContentSafety.WriteJson(destination, p.Plan, canonicalLf: true); return new { planFile = destination, p.Plan, nativeReaderPass = true, renderingTested = false };
    }
    private static async Task<Prepared> VerifyAsync(string path)
    {
        ContentSafety.NoLinks(path); if (new FileInfo(path).Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException("Bounded sealed plan required.");
        using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), Strict) ?? throw new InvalidDataException("Plan required.");
        if (!((plan.Version == 1 && plan.Profile == Profile) || (plan.Version == 2 && plan.Profile == ResourceProfile)))
            throw new InvalidDataException("Explicit native-v2 alias plan required.");
        var fresh = await PrepareAsync(plan.Baseline, plan.Source, plan.AliasesPath);
        if (JsonSerializer.Serialize(plan, ContentSafety.Json) != JsonSerializer.Serialize(fresh.Plan, ContentSafety.Json)) throw new IOException("Stale sealed plan.");
        return fresh;
    }
    public static async Task<object> PreviewAsync(string plan)
    {
        var p = await VerifyAsync(plan); return new { p.Plan, nativeReaderPass = true, baselineRowsPreserved = true, nonStringSourceBytesPreserved = true, renderingTested = false };
    }
    public static async Task<object> ApplyAsync(string plan, string destination)
    {
        destination = ContentSafety.SafeOutput(destination); string planHash = ContentSafety.Hash(plan); var p = await VerifyAsync(plan);
        if (ContentSafety.Hash(plan) != planHash) throw new IOException("Plan changed during verification.");
        foreach (var input in new[] { Path.GetFullPath(plan), p.Plan.Baseline, p.Plan.Source, p.Plan.AliasesPath, ItemPropertyCatalog.Helper })
            if (Path.GetFullPath(input).Equals(destination, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(input).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output must be disjoint from all inputs.");
        string pending = destination + ".pending-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(pending);
        try
        {
            File.WriteAllBytes(Path.Combine(pending, "itemproperty.rdf"), p.Bytes); File.Copy(p.Plan.Baseline, Path.Combine(pending, "baseline.rdf.bak"));
            File.Copy(p.Plan.Source, Path.Combine(pending, "source.rdf.bak")); File.Copy(p.Plan.AliasesPath, Path.Combine(pending, "aliases.json")); File.Copy(plan, Path.Combine(pending, "plan.json"));
            if (ContentSafety.Hash(Path.Combine(pending, "itemproperty.rdf")) != p.Plan.OutputSha256 || ContentSafety.Hash(Path.Combine(pending, "baseline.rdf.bak")) != p.Plan.BaselineSha256 ||
                ContentSafety.Hash(Path.Combine(pending, "source.rdf.bak")) != p.Plan.SourceSha256 || ContentSafety.Hash(Path.Combine(pending, "aliases.json")) != p.Plan.AliasesSha256 ||
                ContentSafety.Hash(Path.Combine(pending, "plan.json")) != planHash || ContentSafety.Hash(plan) != planHash)
                throw new IOException("Candidate/backups readback mismatch.");
            var audit = new { profile = p.Plan.Profile, at = DateTimeOffset.Now, plan = p.Plan, planSha256 = planHash, nativeReaderPass = true,
                baselineRowsPreserved = true, nonStringSourceBytesPreserved = true, eventsPreserved = true, eventDependenciesClosed = false, renderingTested = false, installed = false, published = false };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit, canonicalLf: true); Directory.Move(pending, destination); return new { destination, audit };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
