using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Separate profile for an observed NTL material plugin writer size defect.
// uiVersion is a WORD, then an embedded Texture header/body. The audited files
// declared WORD + Texture body, omitting the 12-byte embedded header in every
// ancestor size. Original DFF omitted-kind repair remains unchanged.
internal static class NtlMaterialTextureSizeRepairService
{
    private const string Profile = "dff-ntl-material-v0-embedded-texture-header-size-v1";
    private const int Limit = 128 * 1024 * 1024;
    internal sealed record SizeChange(int Header, uint Kind, int SizeOffset, uint Before, uint After);
    internal sealed record EmbeddedTexture(int PluginHeader, int TextureHeader, int End, string NameHex, string NameChunkHex, string MaskChunkHex, int[] Ancestors);
    internal sealed record Plan(int Version, string Profile, string Source, string SourceSha256, string TargetSha256,
        int TargetBytes, string Reason, List<DffRepairService.Insertion> Insertions, List<SizeChange> Sizes, List<EmbeddedTexture> Textures);
    private sealed record Prepared(byte[] Original, byte[] Candidate, Plan Plan);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    private static byte[] Read(string source)
    {
        ContentSafety.NoLinks(source);
        if (!Path.GetExtension(source).Equals(".dff", StringComparison.OrdinalIgnoreCase) || new FileInfo(source).Length is < 24 or > Limit) throw new InvalidDataException("Bounded DFF required.");
        return File.ReadAllBytes(source);
    }
    private static Prepared Prepare(string source)
    {
        source = Path.GetFullPath(source); byte[] raw = Read(source);
        var issues = new List<DffRepairService.BoundaryIssue>(); var chunks = new List<DffRepairService.ChunkObservation>();
        try { DffRepairService.Inspect(raw, true, boundaryIssues: issues, chunkObservations: chunks); }
        catch (InvalidDataException error) when (error.Message == "EOF físico/lógico DFF inválido.") { }
        var roots = chunks.Where(c => c.Header == 0 && c.Parent == null).ToArray();
        if (roots.Length != 1 || roots[0].PhysicalEnd != raw.Length - 12 || !raw.AsSpan(raw.Length - 12).SequenceEqual(Convert.FromHexString("03000000000000006500021C"))) throw new InvalidDataException("Observed final empty Clump Extension tail required.");
        var textures = new List<EmbeddedTexture>(); var deltas = new Dictionary<int, int>();
        uint Word(int at)
        {
            if (at < 0 || at > raw.Length - 4) throw new InvalidDataException("Truncated embedded texture.");
            return BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at, 4));
        }
        foreach (var plugin in chunks.Where(c => c.Kind == 0x1fc && c.Parent == 3))
        {
            int p = plugin.PayloadStart;
            if (plugin.MissingKind || plugin.Stamp != 0x1c020065 || p > raw.Length - 14 || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(p, 2)) != 0 || Word(p + 2) != 6 || Word(p + 10) != 0x1c020065) continue;
            uint size = Word(p + 6);
            if (size is < 48 or > 4096 || plugin.DeclaredBytes != size + 2) continue;
            int textureHeader = p + 2, end = checked(textureHeader + 12 + (int)size); int cursor = textureHeader + 12;
            void Header(uint kind, uint length)
            {
                if (cursor > end - 12 || Word(cursor) != kind || Word(cursor + 4) != length || Word(cursor + 8) != 0x1c020065) throw new InvalidDataException("Embedded texture chunk contract differs.");
                cursor += 12;
            }
            Header(1, 4); cursor += 4;
            if (cursor > end - 12) throw new InvalidDataException("Embedded texture name missing.");
            uint nameSize = Word(cursor + 4);
            if (nameSize is < 4 or > 32 || nameSize % 4 != 0) throw new InvalidDataException("Bounded embedded texture name required.");
            Header(2, nameSize);
            if (cursor > raw.Length - (int)nameSize) throw new InvalidDataException("Embedded texture name truncated.");
            var nameBytes = raw.AsSpan(cursor, (int)nameSize); int nul = nameBytes.IndexOf((byte)0);
            if (nul is < 1 or > 31) throw new InvalidDataException("Embedded texture name terminator differs.");
            string nameHex = Convert.ToHexString(nameBytes[..nul]), nameChunkHex = Convert.ToHexString(nameBytes); cursor += (int)nameSize;
            Header(2, 4); if (cursor > raw.Length - 4 || raw[cursor] != 0) throw new InvalidDataException("Separate embedded texture mask unsupported.");
            // RenderWare reads terminated names. Preserve and audit every byte
            // after NUL as well; the size repair never normalizes string tails.
            string maskChunkHex = Convert.ToHexString(raw.AsSpan(cursor, 4)); cursor += 4;
            Header(3, 0);
            if (cursor != end || end > raw.Length || plugin.ParentHeaders.Length != 6 || plugin.ParentHeaders[0] != 0) throw new InvalidDataException("Embedded texture/ancestor boundaries differ.");
            uint[] expectedKinds = [16, 26, 15, 8, 7, 3];
            for (int i = 0; i < expectedKinds.Length; i++)
            {
                var ancestor = chunks.SingleOrDefault(c => c.Header == plugin.ParentHeaders[i]);
                if (ancestor is null || ancestor.Kind != expectedKinds[i] || (ancestor.MissingKind && i != 4) || ancestor.Stamp != 0x1c020065) throw new InvalidDataException("Exact existing or audited omitted-Material NTL plugin ancestor chain required.");
            }
            textures.Add(new(plugin.Header, textureHeader, end, nameHex, nameChunkHex, maskChunkHex, plugin.ParentHeaders));
            foreach (int header in plugin.ParentHeaders.Append(plugin.Header)) deltas[header] = deltas.GetValueOrDefault(header) + 12;
        }
        if (textures.Count is < 1 or > 32 || issues.Count == 0 || issues.Any(i => !deltas.ContainsKey(i.Header) || i.ConsumedEnd <= i.DeclaredEnd || i.ConsumedEnd - i.DeclaredEnd > deltas[i.Header])) throw new InvalidDataException("No exact observed NTL embedded-header size defect; unrelated boundaries blocked.");
        var insertions = chunks.Where(c => c.MissingKind).OrderBy(c => c.Header).Select(c => new DffRepairService.Insertion(c.Header, c.Kind, Convert.ToHexString(raw.AsSpan(c.Header, 12)))).ToList();
        byte[] patched = (byte[])raw.Clone(); var sizes = new List<SizeChange>();
        foreach (var pair in deltas.OrderBy(x => x.Key))
        {
            var chunk = chunks.Single(c => c.Header == pair.Key); int sizeOffset = pair.Key + (chunk.MissingKind ? 0 : 4);
            uint before = Word(sizeOffset), after = checked(before + (uint)pair.Value);
            if (after > Limit) throw new InvalidDataException("NTL parent size overflow.");
            BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(sizeOffset, 4), after); sizes.Add(new(pair.Key, chunk.Kind, sizeOffset, before, after));
        }
        byte[] candidate = DffRepairService.Restore(patched, insertions);
        DffRepairService.Inspect(candidate, false);
        if (ContentSafety.Hash(source) != Hash(raw)) throw new IOException("NTL source changed.");
        var plan = new Plan(1, Profile, source, Hash(raw), Hash(candidate), candidate.Length,
            "Account for the embedded Texture 12-byte header in NTL v0 material plugin and its exact ancestors; preserve all payloads, restore only previously audited omitted kinds.", insertions, sizes, textures);
        return new(raw, candidate, plan);
    }
    internal static object CreatePlan(string source, string output)
    {
        output = ContentSafety.SafeOutput(output); var prepared = Prepare(source);
        if (Path.GetFullPath(source).Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Plan overlaps source.");
        ContentSafety.WriteJson(output, prepared.Plan);
        return new { plan = Path.GetFullPath(output), profile = Profile, textureHeaders = prepared.Plan.Textures.Count, sourcePreserved = true, payloadsPreserved = true, nativeReaderPass = false, installed = false };
    }
    private static Prepared Resolve(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1048576) throw new InvalidDataException("Bounded NTL repair plan required.");
        byte[] bytes = File.ReadAllBytes(path); using var doc = JsonDocument.Parse(bytes); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        var plan = JsonSerializer.Deserialize<Plan>(bytes, Strict) ?? throw new InvalidDataException("NTL repair plan required.");
        if (plan.Version != 1 || plan.Profile != Profile) throw new InvalidDataException("NTL repair profile differs.");
        var prepared = Prepare(plan.Source);
        if (JsonSerializer.Serialize(plan, ContentSafety.Json) != JsonSerializer.Serialize(prepared.Plan, ContentSafety.Json)) throw new InvalidDataException("Sealed NTL repair differs from canonical boundaries.");
        return prepared;
    }
    internal static object Preview(string path)
    {
        var prepared = Resolve(path); return new { plan = prepared.Plan, payloadsPreserved = true, structure = DffRepairService.Inspect(prepared.Candidate, false), nativeReaderPass = false, renderingTested = false, installed = false };
    }
    internal static object Apply(string path, string output)
    {
        string planHash = ContentSafety.Hash(path); var prepared = Resolve(path); output = ContentSafety.SafeOutput(output);
        if (output.StartsWith(Path.GetDirectoryName(prepared.Plan.Source)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || prepared.Plan.Source.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Output must be disjoint from sources/plan.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
            File.WriteAllBytes(Path.Combine(pending, "original.dff.bak"), prepared.Original); File.WriteAllBytes(Path.Combine(pending, "model.dff"), prepared.Candidate);
            var audit = new { profile = Profile, plan = prepared.Plan, planSha256 = planHash, payloadsPreserved = true, sourcePreserved = true, nativeReaderPass = false, renderingTested = false, worldTested = false, installed = false };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            if (ContentSafety.Hash(prepared.Plan.Source) != prepared.Plan.SourceSha256 || ContentSafety.Hash(path) != planHash || ContentSafety.Hash(Path.Combine(pending, "model.dff")) != prepared.Plan.TargetSha256 || ContentSafety.Hash(Path.Combine(pending, "original.dff.bak")) != prepared.Plan.SourceSha256) throw new IOException("NTL repair source/plan/output changed.");
            Directory.Move(pending, output); return new { output, sha256 = prepared.Plan.TargetSha256, bytes = prepared.Plan.TargetBytes, payloadsPreserved = true, nativeReaderPass = false, installed = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
