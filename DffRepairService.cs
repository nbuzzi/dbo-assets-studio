using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// Narrow audited repair: omitted geometry-extension/material chunk kinds.
// Walk declared parent boundaries; never scan opaque plugin/geometry payloads.
internal static class DffRepairService
{
    private const int Limit = 128 * 1024 * 1024;
    // RWLIBRARYIDUNPACKVERSION from the shipped rwversion.h gives 0x37002
    // for every one of these observed build stamps. Keep the original stamp;
    // the same bounded chunk/layout validation applies to all five builds.
    private static bool NativeStamp(uint stamp)=>stamp is 0x1c02000a or 0x1c02002d or 0x1c020037 or 0x1c020065 or 0x1c02ffff;
    internal sealed record Insertion(int Offset, uint Kind, string BeforeHex);
    internal sealed record BoundaryIssue(int Header, uint Kind, uint? Parent, int Depth, uint DeclaredBytes, long DeclaredEnd, long ConsumedEnd, int PhysicalEnd);
    internal sealed record ChunkObservation(int Header, uint Kind, uint? Parent, int Depth, uint DeclaredBytes, uint Stamp,
        bool MissingKind, int PayloadStart, long LogicalPayloadStart, long DeclaredEnd, long ConsumedEnd, int PhysicalEnd, string PayloadPrefixHex, int[] ParentHeaders);
    internal sealed record Structure(int Geometries, int Materials, int Chunks, int Bytes, List<Insertion> Insertions);
    internal sealed record TextureName(int Offset, int Bytes, string NameHex, int[] ParentHeaders);
    internal sealed record Plan(int Version, string Source, string SourceSha256, string TargetSha256,
        int TargetBytes, string Reason, List<string> Dependencies, List<Insertion> Insertions);
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    private static byte[] Read(string path)
    {
        ContentSafety.NoLinks(path);
        if (!Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(path).Length is < 24 or > Limit) throw new InvalidDataException("DFF requerido (24 bytes..128 MiB).");
        return File.ReadAllBytes(path);
    }
    internal static Structure Inspect(byte[] raw, bool allowOmissions, bool allowStamp350 = false, List<TextureName>? textureNames = null, List<BoundaryIssue>? boundaryIssues = null, List<ChunkObservation>? chunkObservations = null, bool allowLegacyToon = false)
    {
        if (allowLegacyToon && (allowOmissions || textureNames == null || boundaryIssues != null || chunkObservations != null)) throw new InvalidDataException("Legacy Toon stamps require complete texture-aware structure validation.");
        if (textureNames != null && allowOmissions) throw new InvalidDataException("Texture names require complete DFF headers.");
        if (allowStamp350 && allowOmissions) throw new InvalidDataException("Stamp350 is strict read-only structure validation, not omitted-header repair.");
        if (boundaryIssues != null && (!allowOmissions || textureNames != null || allowStamp350)) throw new InvalidDataException("Boundary observation is limited to omitted-header source diagnosis.");
        if (chunkObservations != null && boundaryIssues == null) throw new InvalidDataException("Chunk observations require the diagnostic-only boundary profile.");
        int physical = 0, chunks = 0, geometries = 0, materials = 0;
        long logical = 0;
        var insertions = new List<Insertion>();
        var parents = new List<int>(); bool nextTextureName = false, nextTextureMask = false;
        uint Word(int offset = 0)
        {
            if ((long)physical + offset + 4 > raw.Length) throw new InvalidDataException("Header DFF truncado.");
            return BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(physical + offset, 4));
        }
        void Chunk(uint? expected, uint? parent, int depth)
        {
            int header = physical;
            if (depth > 32 || ++chunks > 200000) throw new InvalidDataException("Límite del árbol DFF.");
            uint kind, size, stamp;
            bool missing = allowOmissions && Word() != expected &&
                ((expected == 3 && parent == 15 && NativeStamp(Word(4)) && Word(8) == 0x50e) ||
                 (expected == 7 && parent == 8 && NativeStamp(Word(4)) && Word(8) == 1));
            if (missing)
            {
                if (insertions.Count >= 4096 || physical + 12 > raw.Length) throw new InvalidDataException("Límite de reparaciones DFF.");
                kind = expected!.Value; size = Word(); stamp = Word(4);
                insertions.Add(new(physical, kind, Convert.ToHexString(raw.AsSpan(physical, 12))));
                physical += 8; logical += 12;
            }
            else { kind = Word(); size = Word(4); stamp = Word(8); physical += 12; logical += 12; }
            if ((expected != null && kind != expected) || size > Limit ||
                (!NativeStamp(stamp) && !(allowStamp350 && stamp == 0x1400ffff) && !(allowLegacyToon && stamp == 0x1803ffff)))
                throw new InvalidDataException($"Tipo, tamaño o versión de chunk DFF no auditado: header={header}, kind=0x{kind:X}, expected=0x{expected:X}, parent=0x{parent:X}, depth={depth}, bytes={size}, stamp=0x{stamp:X8}.");
            long end = logical + size;
            int payloadStart = physical; long logicalPayloadStart = logical;
            if (end > Limit + 4096 * 4L) throw new InvalidDataException("Tamaño lógico DFF excesivo.");
            if (nextTextureName || nextTextureMask)
            {
                if (kind != 2 || parent != 6 || size is < 4 or > 128 || size % 4 != 0 || (long)physical + size > raw.Length)
                    throw new InvalidDataException("Bounded texture string chunk required.");
                int nul = raw.AsSpan(physical, (int)size).IndexOf((byte)0);
                if (nul < 0 || nul > 31 || (nextTextureName && nul == 0) || (nextTextureMask && nul != 0))
                    throw new InvalidDataException("Texture name maximum31bytes and empty mask required.");
                if (nextTextureName) textureNames!.Add(new(physical, (int)size, Convert.ToHexString(raw.AsSpan(physical, nul)), parents.ToArray()));
                nextTextureName = nextTextureMask = false;
            }
            parents.Add(header);
            if (kind == 15 && parent == 26)
            {
                geometries++; Chunk(1, kind, depth + 1); Chunk(8, kind, depth + 1); Chunk(3, kind, depth + 1);
            }
            else if (kind == 26 && parent == 16)
            {
                if (Word() != 1 || Word(4) != 4) throw new InvalidDataException("Struct GeometryList no auditado.");
                uint count = Word(12); if (count > 512) throw new InvalidDataException("Límite GeometryList.");
                Chunk(1, kind, depth + 1); for (uint i = 0; i < count; i++) Chunk(15, kind, depth + 1);
            }
            else if (kind == 8 && parent == 15)
            {
                if (Word() != 1) throw new InvalidDataException("MaterialList requiere struct.");
                uint count = Word(12);
                if (count > 4096 || Word(4) != 4 + count * 4) throw new InvalidDataException("Struct MaterialList no auditado.");
                var indices = new int[count];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = unchecked((int)Word(16 + i * 4));
                    if (indices[i] < -1 || indices[i] >= i) throw new InvalidDataException("Referencia MaterialList inválida.");
                }
                Chunk(1, kind, depth + 1);
                foreach (int index in indices) if (index == -1) { materials++; Chunk(7, kind, depth + 1); }
            }
            else if (textureNames != null && kind == 6 && parent == 7)
            {
                if (Word() != 1 || Word(4) != 4) throw new InvalidDataException("Texture Struct of4bytes required.");
                Chunk(1, kind, depth + 1);
                nextTextureName = true; Chunk(2, kind, depth + 1);
                nextTextureMask = true; Chunk(2, kind, depth + 1);
                Chunk(3, kind, depth + 1);
            }
            else if (parent != 3 && kind is 16 or 14 or 20 or 7 or 6 or 3)
                while (logical < end) Chunk(null, kind, depth + 1);
            else
            {
                if ((long)physical + size > raw.Length) throw new InvalidDataException("Payload DFF truncado.");
                physical += checked((int)size); logical += size;
            }
            if (chunkObservations != null) chunkObservations.Add(new(header, kind, parent, depth, size, stamp, missing, payloadStart,
                logicalPayloadStart, end, logical, physical, Convert.ToHexString(raw.AsSpan(payloadStart, Math.Min(24, Math.Min((int)size, raw.Length - payloadStart)))), parents.Take(parents.Count - 1).ToArray()));
            if (logical != end)
            {
                if (boundaryIssues != null) boundaryIssues.Add(new(header, kind, parent, depth, size, end, logical, physical));
                else throw new InvalidDataException($"Límite padre/hijo DFF inválido: header={header}, kind=0x{kind:X}, depth={depth}, declaredEnd={end}, consumedEnd={logical}, physicalEnd={physical}, bytes={raw.Length}.");
            }
            parents.RemoveAt(parents.Count - 1);
        }
        Chunk(16, null, 0);
        if (physical != raw.Length || logical != raw.Length + insertions.Count * 4L)
            throw new InvalidDataException("EOF físico/lógico DFF inválido.");
        return new(geometries, materials, chunks, raw.Length, insertions);
    }
    internal static byte[] Restore(byte[] raw, List<Insertion> insertions)
    {
        using var output = new MemoryStream(); int cursor = 0; Span<byte> word = stackalloc byte[4];
        foreach (var insertion in insertions)
        {
            if (insertion.Offset < cursor || insertion.Kind is not (3 or 7)) throw new InvalidDataException("Inserción DFF inválida.");
            output.Write(raw.AsSpan(cursor, insertion.Offset - cursor));
            BinaryPrimitives.WriteUInt32LittleEndian(word, insertion.Kind); output.Write(word);
            cursor = insertion.Offset;
        }
        output.Write(raw.AsSpan(cursor)); return output.ToArray();
    }
    public static object CreatePlan(string source, string output)
    {
        source = Path.GetFullPath(source); byte[] raw = Read(source); var structure = Inspect(raw, true);
        if (structure.Insertions.Count == 0) throw new InvalidDataException("DFF completo; no hay una reparación de este tipo.");
        byte[] restored = Restore(raw, structure.Insertions); var after = Inspect(restored, false);
        var plan = new Plan(1, source, Hash(raw), Hash(restored), restored.Length,
            "Restore omitted chunk kinds at declared geometry/material boundaries; preserve all original bytes.",
            ["Taiwan RenderWare reader; models/animations/textures/ObjectProperty and real client QA required"], structure.Insertions);
        ContentSafety.WriteJson(output, plan); return new { plan = Path.GetFullPath(output), structure, after, sourcePreserved = true, gameCompatibility = "not_tested" };
    }
    public static object InspectSource(string source, bool allowOmissions)
    {
        source = Path.GetFullPath(source);
        byte[] raw = Read(source);
        var structure = Inspect(raw, allowOmissions);
        if (ContentSafety.Hash(source) != Hash(raw)) throw new IOException("DFF source changed during inspection.");
        return new { source, sourceSha256 = Hash(raw), structure, allowOmissions,
            nativeReaderTested = false, renderingTested = false, installed = false };
    }
    internal static object ObserveBoundaries(string source)
    {
        source = Path.GetFullPath(source); byte[] raw = Read(source);
        var issues = new List<BoundaryIssue>(); var chunks = new List<ChunkObservation>(); Structure? structure = null; string? failure = null;
        try { structure = Inspect(raw, true, boundaryIssues: issues, chunkObservations: chunks); }
        catch (InvalidDataException error) { failure = error.Message; }
        if (ContentSafety.Hash(source) != Hash(raw)) throw new IOException("DFF source changed during diagnosis.");
        return new { profile = "dff-omitted-header-boundary-observation-v1", source, sourceSha256 = Hash(raw),
            structure, issues, chunks, sourceBytes = raw.Length,
            rootPhysicalEnd = chunks.LastOrDefault(c => c.Parent == null)?.PhysicalEnd,
            unconsumedRootTailHex = chunks.LastOrDefault(c => c.Parent == null) is { } root && root.PhysicalEnd <= raw.Length
                ? Convert.ToHexString(raw.AsSpan(root.PhysicalEnd, Math.Min(64, raw.Length - root.PhysicalEnd))) : null,
            failure, structurallyValid = structure != null && issues.Count == 0 && structure.Insertions.Count == 0,
            repairCreated = false, nativeReaderTested = false, installed = false };
    }
    private static (Plan plan, byte[] raw, byte[] restored, Structure structure) Resolve(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Plan DFF mayor a1MiB.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), ContentSafety.Json) ?? throw new InvalidDataException("Plan DFF vacío.");
        if (plan.Version != 1 || plan.Insertions is not { Count: > 0 and <= 4096 } || string.IsNullOrWhiteSpace(plan.Reason) || plan.Dependencies is not { Count: > 0 })
            throw new InvalidDataException("Contrato del plan DFF inválido.");
        byte[] raw = Read(plan.Source);
        if (Hash(raw) != plan.SourceSha256) throw new IOException("Fuente DFF cambió desde el plan.");
        var before = Inspect(raw, true);
        if (!before.Insertions.SequenceEqual(plan.Insertions)) throw new IOException("Operaciones no coinciden con límites DFF auditados.");
        byte[] restored = Restore(raw, plan.Insertions); var structure = Inspect(restored, false);
        if (restored.Length != plan.TargetBytes || Hash(restored) != plan.TargetSha256) throw new IOException("Destino DFF difiere del plan sellado.");
        return (plan, raw, restored, structure);
    }
    public static object Preview(string path)
    {
        var (plan, _, _, structure) = Resolve(path);
        return new { plan, structure, payloadsPreserved = true, nativeReaderTested = false, gameCompatibility = "not_tested" };
    }
    public static object Apply(string path, string output)
    {
        output = ContentSafety.SafeOutput(output); var (plan, raw, restored, structure) = Resolve(path);
        if (output.StartsWith(Path.GetDirectoryName(Path.GetFullPath(plan.Source))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Destino fuera de la carpeta fuente requerido.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
            File.WriteAllBytes(Path.Combine(pending, "original.dff.bak"), raw);
            File.WriteAllBytes(Path.Combine(pending, "model.dff"), restored);
            if (Hash(Read(plan.Source)) != plan.SourceSha256) throw new IOException("Fuente DFF cambió durante apply.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, plan, structure,
                payloadsPreserved = true, nativeReaderTested = false, renderingTested = false, installed = false });
            Directory.Move(pending, output); return new { output, sha256 = plan.TargetSha256, bytes = restored.Length, structure, payloadsPreserved = true, gameCompatibility = "not_tested" };
        }
        catch { Directory.Delete(pending, true); throw; }
    }
}
