using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal static partial class ToonTopologyService
{
    private const string RebuildProfile = "explicit-compatible-toon-metadata-rebuild-v1";
    private sealed record RebuildSelection(int Version, string Profile, string Source, string SourceSha256,
        string Reference, string ReferenceSha256, int ReferenceGeometry, int[] Geometries, string Topology, bool RetainDegenerateFaces, bool SplitNonManifoldEdges, string Reason);
    private sealed record RebuildPlan(RebuildSelection Selection, string TargetSha256, int TargetBytes);
    private static readonly JsonSerializerOptions RebuildJson = new(ContentSafety.Json)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static T ReadRebuild<T>(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1048576) throw new InvalidDataException("Bounded rebuild plan required.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(doc, RebuildJson) ?? throw new InvalidDataException("Rebuild plan required.");
    }
    private static (byte[] original, byte[] bytes, object evidence) Rebuild(RebuildSelection selection)
    {
        if (selection.Version != 1 || selection.Profile != RebuildProfile || selection.Geometries is null ||
            selection.Geometries.Length is < 1 or > 64 || selection.Geometries.Distinct().Count() != selection.Geometries.Length ||
            (selection.Topology != "exact-position-weld" && selection.Topology != "indexed") || string.IsNullOrWhiteSpace(selection.Reason) || selection.Reason.Length > 2048)
            throw new InvalidDataException("Explicit geometries and correction reason required.");
        ContentSafety.NoLinks(selection.Source); ContentSafety.NoLinks(selection.Reference);
        using var sourceLock = new FileStream(selection.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var referenceLock = new FileStream(selection.Reference, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] raw = Read(selection.Source, true), reference = Read(selection.Reference, true);
        if (Hash(raw) != selection.SourceSha256 || Hash(reference) != selection.ReferenceSha256)
            throw new IOException("Rebuild source/reference changed.");
        var geometries = All(Nodes(reference)).Where(n => n.Kind == 15 && n.Children != null).ToArray();
        if (selection.ReferenceGeometry < 0 || selection.ReferenceGeometry >= geometries.Length)
            throw new InvalidDataException("Explicit compatible reference geometry required.");
        var extension = geometries[selection.ReferenceGeometry].Children!.Single(n => n.Kind == 3);
        byte[] metadata = extension.Children!.Single(n => n.Kind == 0x12e).Body;
        var evidence = new List<object>(); byte[] bytes = raw;
        foreach (int index in selection.Geometries)
        {
            var result = Convert(bytes, index, metadata, selection.Topology == "indexed", selection.RetainDegenerateFaces, selection.SplitNonManifoldEdges); bytes = result.bytes;
            evidence.Add(new { geometry = index, referenceGeometry = selection.ReferenceGeometry, result.evidence });
        }
        if (Hash(Read(selection.Source, true)) != selection.SourceSha256 || Hash(Read(selection.Reference, true)) != selection.ReferenceSha256)
            throw new IOException("Rebuild inputs changed during conversion.");
        var baselineLeaves = All(Nodes(raw)).Where(n => n.Children == null && n.Kind != 0x12e).ToArray();
        var candidateLeaves = All(Nodes(bytes)).Where(n => n.Children == null && n.Kind != 0x12e).ToArray();
        if (baselineLeaves.Length != candidateLeaves.Length || baselineLeaves.Zip(candidateLeaves).Any(pair =>
            pair.First.Kind != pair.Second.Kind || pair.First.Stamp != pair.Second.Stamp || !pair.First.Body.SequenceEqual(pair.Second.Body)))
            throw new InvalidDataException("Rebuild changed payloads outside Toon metadata.");
        return (raw, bytes, new { corrections = evidence, nonToonPayloadsVerified = baselineLeaves.Length, geometryMaterialsRigAndOtherPluginsPreserved = true,
            referenceMetadataExplicitlySelected = true, nativeReaderTested = false, renderingTested = false });
    }
    public static object CreateRebuildPlan(string selectionPath, string destination)
    {
        destination = ContentSafety.SafeOutput(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Fresh plan destination required.");
        var selection = ReadRebuild<RebuildSelection>(selectionPath);
        var result = Rebuild(selection);
        ContentSafety.WriteJson(destination, new RebuildPlan(selection, Hash(result.bytes), result.bytes.Length));
        return new { plan = Path.GetFullPath(destination), result.evidence, installed = false };
    }
    private static (RebuildPlan plan, byte[] original, byte[] bytes, object evidence) ResolveRebuild(string path)
    {
        var plan = ReadRebuild<RebuildPlan>(path); var result = Rebuild(plan.Selection);
        if (Hash(result.bytes) != plan.TargetSha256 || result.bytes.Length != plan.TargetBytes)
            throw new IOException("Rebuild candidate differs from sealed plan.");
        return (plan, result.original, result.bytes, result.evidence);
    }
    public static object PreviewRebuild(string path)
    {
        var result = ResolveRebuild(path); return new { result.plan, result.evidence, installed = false };
    }
    public static object ApplyRebuild(string path, string destination)
    {
        string planHash = ContentSafety.Hash(path); var result = ResolveRebuild(path);
        destination = ContentSafety.SafeOutput(destination);
        foreach (string source in new[] { result.plan.Selection.Source, result.plan.Selection.Reference })
            if (destination.StartsWith(Path.GetDirectoryName(Path.GetFullPath(source))! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("Output must be outside both inputs.");
        string pending = ContentSafety.FreshDirectory(destination);
        try
        {
            File.WriteAllBytes(Path.Combine(pending, "original.dff.bak"), result.original);
            File.WriteAllBytes(Path.Combine(pending, "model.dff"), result.bytes);
            if (ContentSafety.Hash(Path.Combine(pending, "model.dff")) != result.plan.TargetSha256 ||
                ContentSafety.Hash(path) != planHash || Hash(Read(result.plan.Selection.Source, true)) != result.plan.Selection.SourceSha256 ||
                Hash(Read(result.plan.Selection.Reference, true)) != result.plan.Selection.ReferenceSha256)
                throw new IOException("Rebuild disk/input/plan verification failed.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { result.plan, planSha256 = planHash,
                result.evidence, installed = false, nativeReaderTested = false, renderingTested = false });
            Directory.Move(pending, destination);
            return new { output = Path.Combine(destination, "model.dff"), sha256 = result.plan.TargetSha256,
                result.evidence, installed = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
