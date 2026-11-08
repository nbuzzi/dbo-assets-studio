using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal static partial class ToonTopologyService
{
    private const string SelectedProfile = "selected-mismatched-toon-preserve-own-metadata-v1";
    private sealed record SelectedGeometry(int Index, bool RetainDegenerateFaces, bool SplitNonManifoldEdges);
    private sealed record SelectedRequest(int Version, string Profile, RdfRevision Source,
        SelectedGeometry[] Geometries, string Reason);
    private sealed record SelectedPlan(SelectedRequest Selection, string TargetSha256, int TargetBytes);
    private static readonly JsonSerializerOptions SelectedJson = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static T ReadSelected<T>(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1048576) throw new InvalidDataException("Bounded selected Toon plan required.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(doc, SelectedJson) ?? throw new InvalidDataException("Selected Toon plan required.");
    }
    private static (byte[] Original, byte[] Bytes, object Evidence) PrepareSelected(SelectedRequest selection)
    {
        if (selection.Version != 1 || selection.Profile != SelectedProfile || selection.Source == null ||
            selection.Geometries is null || selection.Geometries.Length is < 1 or > 64 ||
            selection.Geometries.Any(x => x == null) ||
            selection.Geometries.Select(x => x.Index).Distinct().Count() != selection.Geometries.Length ||
            string.IsNullOrWhiteSpace(selection.Reason) || selection.Reason.Length > 2048)
            throw new InvalidDataException("Explicit distinct geometries, input hash and correction reason required.");
        ContentSafety.NoLinks(selection.Source.Path);
        using var sourceLock = new FileStream(selection.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] raw = Read(selection.Source.Path);
        if (!Hash(raw).Equals(selection.Source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Selected Toon source changed.");
        var originalTree = Nodes(raw);
        var originalGeometries = All(originalTree).Where(n => n.Kind == 15 && n.Children != null).ToArray();
        if (selection.Geometries.Any(x => x.Index < 0 || x.Index >= originalGeometries.Length))
            throw new InvalidDataException("Selected geometry index out of range.");
        byte[] bytes = raw;
        var corrections = new List<object>();
        foreach (var geometry in selection.Geometries)
        {
            // Null compatibleToon deliberately uses each geometry's own thickness,
            // ink IDs/names and paint. It also rejects already matching topology.
            var result = Convert(bytes, geometry.Index, null, false,
                geometry.RetainDegenerateFaces, geometry.SplitNonManifoldEdges);
            bytes = result.bytes;
            corrections.Add(new { geometry.Index, result.evidence });
        }
        var baselineLeaves = All(originalTree).Where(n => n.Children == null && n.Kind != 0x12e).ToArray();
        var candidateTree = Nodes(bytes);
        var candidateLeaves = All(candidateTree).Where(n => n.Children == null && n.Kind != 0x12e).ToArray();
        if (baselineLeaves.Length != candidateLeaves.Length || baselineLeaves.Zip(candidateLeaves).Any(pair =>
            pair.First.Kind != pair.Second.Kind || pair.First.Stamp != pair.Second.Stamp || !pair.First.Body.SequenceEqual(pair.Second.Body)))
            throw new InvalidDataException("Payload changed outside Toon metadata.");
        var newGeometries = All(candidateTree).Where(n => n.Kind == 15 && n.Children != null).ToArray();
        if (newGeometries.Length != originalGeometries.Length) throw new InvalidDataException("Geometry count changed.");
        var selected = selection.Geometries.Select(x => x.Index).ToHashSet();
        for (int i = 0; i < originalGeometries.Length; i++)
            if (!selected.Contains(i) && !Encode([originalGeometries[i]]).SequenceEqual(Encode([newGeometries[i]])))
                throw new InvalidDataException("Unselected geometry changed.");
        if (ContentSafety.Hash(selection.Source.Path) != Hash(raw)) throw new IOException("Source changed during conversion.");
        return (raw, bytes, new { corrections, nonToonPayloadsVerified = baselineLeaves.Length,
            originalGeometryCount = originalGeometries.Length, unselectedGeometriesByteExact = true,
            ownInkNamesPaintAndThicknessPreserved = true, geometryMaterialsRigAndOtherPluginsPreserved = true,
            nativeReaderTested = false, renderingTested = false });
    }
    public static object CreateSelectedPlan(string selectionPath, string destination)
    {
        destination = ContentSafety.SafeOutput(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Fresh selected Toon plan required.");
        var selection = ReadSelected<SelectedRequest>(selectionPath);
        var result = PrepareSelected(selection);
        ContentSafety.WriteJson(destination, new SelectedPlan(selection, Hash(result.Bytes), result.Bytes.Length));
        return new { plan = Path.GetFullPath(destination), result.Evidence, installed = false };
    }
    private static (SelectedPlan Plan, byte[] Original, byte[] Bytes, object Evidence) ResolveSelected(string path)
    {
        var plan = ReadSelected<SelectedPlan>(path); var result = PrepareSelected(plan.Selection);
        if (Hash(result.Bytes) != plan.TargetSha256 || result.Bytes.Length != plan.TargetBytes)
            throw new IOException("Selected Toon result differs from sealed plan.");
        return (plan, result.Original, result.Bytes, result.Evidence);
    }
    public static object PreviewSelected(string path)
    {
        var result = ResolveSelected(path); return new { result.Plan, result.Evidence, installed = false };
    }
    public static object ApplySelected(string path, string destination)
    {
        string planHash = ContentSafety.Hash(path); var result = ResolveSelected(path);
        destination = ContentSafety.SafeOutput(destination);
        if (Path.GetFullPath(path).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(Path.GetDirectoryName(Path.GetFullPath(result.Plan.Selection.Source.Path))! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("Output outside inputs required.");
        string pending = ContentSafety.FreshDirectory(destination);
        try
        {
            File.WriteAllBytes(Path.Combine(pending, "original.dff.bak"), result.Original);
            File.WriteAllBytes(Path.Combine(pending, "model.dff"), result.Bytes);
            if (ContentSafety.Hash(Path.Combine(pending, "model.dff")) != result.Plan.TargetSha256 ||
                ContentSafety.Hash(path) != planHash || ContentSafety.Hash(result.Plan.Selection.Source.Path) != Hash(result.Original))
                throw new IOException("Selected Toon disk/input verification failed.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { result.Plan, planSha256 = planHash,
                result.Evidence, installed = false, nativeReaderTested = false, renderingTested = false });
            Directory.Move(pending, destination);
            return new { output = Path.Combine(destination, "model.dff"), sha256 = result.Plan.TargetSha256, result.Evidence, installed = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
