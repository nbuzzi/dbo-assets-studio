using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Shared content engine: native FILE placement reader, native RenderWare SRT,
// and bounded OSM1 world-space triangles. No guessed obstacle classification.
internal static class SceneGeometryService
{
    private const string Profile = "taiwan14-placed-object-geometry-v1";
    private const string ObservationProfile = "taiwan14-placed-object-observation-v1";
    internal sealed record Input(string Path, string Sha256, long Bytes);
    internal sealed record Model(string Key, Input Mesh);
    internal sealed record Plan(string Profile, Input Gwp, Input[] Fields, Model[] Models, string HelperSha256);
    internal sealed record Placement(uint Field, uint Sector, uint ObjectIndex, string Model, uint Type, uint Flags,
        uint TriggerId, uint ObjectId, uint NameIndex, float[] Scale, float[] Rotation, float[] Position, float[] Matrix);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "scene-object-reader.exe");
    private static bool Key(string key) => key.Length is > 0 and < 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static Input Pin(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c > 127 || c < 32 || c == '|')) throw new InvalidDataException("Native paths must be ASCII without controls.");
        long bytes = new FileInfo(path).Length;
        if (bytes is < 1 or > 134217728) throw new InvalidDataException("Bounded scene input required.");
        return new(path, ContentSafety.Hash(path), bytes);
    }
    internal static object CreatePlan(string gwp, string fieldsRoot, string meshRoot, string output)
    {
        return CreatePlanCore(gwp, fieldsRoot, meshRoot, output);
    }
    internal static object CreateObservationPlan(string gwp, string fieldsRoot, string output) =>
        CreatePlanCore(gwp, fieldsRoot, null, output);
    private static object CreatePlanCore(string gwp, string fieldsRoot, string? meshRoot, string output)
    {
        fieldsRoot = Path.GetFullPath(fieldsRoot);
        ContentSafety.NoLinks(fieldsRoot);
        if (meshRoot != null) { meshRoot = Path.GetFullPath(meshRoot); ContentSafety.NoLinks(meshRoot); }
        var fields = Directory.GetDirectories(fieldsRoot).Select(dir =>
        {
            ContentSafety.NoLinks(dir); string name = Path.GetFileName(dir);
            if (!name.StartsWith("wfif", StringComparison.Ordinal) || !uint.TryParse(name[4..], out uint id) || name != "wfif" + id)
                throw new InvalidDataException("Literal numbered field folders required.");
            return (id, input: Pin(Path.Combine(dir, name)));
        }).OrderBy(x => x.id).ToArray();
        if (fields.Where((x, i) => x.id != i).Any()) throw new InvalidDataException("Contiguous ordered fields required.");
        var models = (meshRoot == null ? Array.Empty<string>() : Directory.GetFiles(meshRoot, "*.mesh")).Select(x => new Model(Path.GetFileNameWithoutExtension(x).ToUpperInvariant(), Pin(x)))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        var plan = new Plan(meshRoot == null ? ObservationProfile : Profile, Pin(gwp), fields.Select(x => x.input).ToArray(), models, Pin(Helper).Sha256);
        ValidatePlan(plan);
        output = ContentSafety.SafeOutput(output);
        if (Inputs(plan).Append(Pin(Helper)).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Plan overlaps input.");
        ContentSafety.WriteJson(output, plan);
        return new { plan = output, profile = plan.Profile, fields = fields.Length, models = models.Length, collisionGenerated = false };
    }
    private static IEnumerable<Input> Inputs(Plan plan) => plan.Fields.Prepend(plan.Gwp).Concat(plan.Models.Select(x => x.Mesh));
    private static void ValidatePlan(Plan plan)
    {
        if (plan.Profile is not (Profile or ObservationProfile) || plan.Gwp == null || plan.Fields == null || plan.Models == null ||
            plan.Fields.Length is < 1 or > 4096 || Math.Sqrt(plan.Fields.Length) % 1 != 0 || plan.Models.Length > 4096 ||
            Path.GetFileName(plan.Gwp.Path) != "#######.gwp" || plan.Models.Any(x => x == null || x.Mesh == null || !Key(x.Key) || x.Key != x.Key.ToUpperInvariant()))
            throw new InvalidDataException("Unsupported complete scene plan.");
        if (plan.Profile == ObservationProfile ? plan.Models.Length != 0 : plan.Models.Length == 0)
            throw new InvalidDataException("Complete Taiwan meshes required.");
        var inputs = Inputs(plan).ToArray();
        // Main World 03's complete 64x64 grid is 320 MiB. Observation has no
        // geometry baking; retain the tighter limit for geometry candidates.
        long aggregateLimit = (plan.Profile == ObservationProfile ? 512L : 256L) * 1024 * 1024;
        if (inputs.Any(x => x == null || x.Bytes is < 1 or > 134217728) || inputs.Sum(x => x.Bytes) > aggregateLimit ||
            inputs.Select(x => Path.GetFullPath(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length ||
            plan.Models.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != plan.Models.Length)
            throw new InvalidDataException("Duplicate, null or oversized scene input.");
        for (int i = 0; i < plan.Fields.Length; ++i)
            if (Path.GetFileName(plan.Fields[i].Path) != "wfif" + i || Path.GetFileName(Path.GetDirectoryName(plan.Fields[i].Path)) != "wfif" + i)
                throw new InvalidDataException("Literal field identity/order required.");
        foreach (var model in plan.Models)
            if (!Path.GetExtension(model.Mesh.Path).Equals(".mesh", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(model.Mesh.Path).ToUpperInvariant() != model.Key)
                throw new InvalidDataException("Mesh/model identity mismatch.");
    }
    internal static async Task<object> PreviewAsync(string plan) => await ExecuteAsync(plan, null);
    internal static async Task<object> ApplyAsync(string plan, string output) => await ExecuteAsync(plan, output);
    private static async Task<object> ExecuteAsync(string planPath, string? destination)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Bounded plan required.");
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(planPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            byte[] bytes = File.ReadAllBytes(planPath);
            using (var document = JsonDocument.Parse(bytes)) TerrainGeometryService.NoDuplicateKeys(document.RootElement);
            var plan = JsonSerializer.Deserialize<Plan>(bytes, Strict) ?? throw new InvalidDataException("Missing plan."); ValidatePlan(plan);
            var inputs = Inputs(plan).ToArray();
            foreach (var input in inputs)
            {
                ContentSafety.NoLinks(input.Path); locks.Add(new(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Pin(input.Path) != input) throw new InvalidDataException("Stale scene pin.");
            }
            ContentSafety.NoLinks(Helper); locks.Add(new(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Pin(Helper).Sha256 != plan.HelperSha256) throw new InvalidDataException("Stale native matrix helper.");
            if (destination != null)
            {
                destination = ContentSafety.SafeOutput(destination); string prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (inputs.Select(x => x.Path).Append(planPath).Append(Helper).Any(x => x.Equals(destination, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Candidate would contain source.");
                pending = ContentSafety.FreshDirectory(destination);
            }
            else { pending = Path.Combine(Path.GetTempPath(), "DboAssetsScene", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending); }
            string snapshots = Path.Combine(pending, "sources"); Directory.CreateDirectory(snapshots);
            string Copy(Input input, string name)
            {
                string target = Path.Combine(snapshots, name); File.Copy(input.Path, target);
                if (ContentSafety.Hash(target) != input.Sha256) throw new IOException("Snapshot mismatch."); return target;
            }
            string gwp = Copy(plan.Gwp, "project.gwp");
            string[] fields = plan.Fields.Select((x, i) => Copy(x, "wfif" + i)).ToArray();
            var models = plan.Models.ToDictionary(x => x.Key, x => Copy(x.Mesh, x.Key + ".mesh"), StringComparer.Ordinal);
            string list = Path.Combine(pending, "native-input.txt"); File.WriteAllLines(list, fields, Encoding.ASCII);
            string binary = Path.Combine(pending, "objects.tos");
            var start = new ProcessStartInfo(Helper) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = pending, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { gwp, list, binary }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Native scene helper missing.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Scene reader timeout60s."); }
            string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, "native.log"), log);
            if (process.ExitCode != 0) throw new InvalidDataException("Native scene failed exit=" + process.ExitCode + ": " + log);
            var placements = ReadPlacements(binary, fields.Length);
            if (plan.Profile == ObservationProfile)
            {
                foreach (var input in inputs) if (Pin(input.Path) != input) throw new IOException("Scene input changed.");
                if (ContentSafety.Hash(Helper) != plan.HelperSha256) throw new IOException("Matrix helper changed.");
                string observation = Path.Combine(pending, "placements.json");
                ContentSafety.WriteJson(observation, new { profile = plan.Profile, objects = placements });
                var observationAudit = new { profile = plan.Profile, at = DateTimeOffset.UtcNow,
                    planSha256 = ContentSafety.Hash(planPath), sourcePins = inputs, helperSha256 = plan.HelperSha256,
                    fields = fields.Length, placedObjects = placements.Length,
                    modelKeys = placements.Select(x => x.Model).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    placementSha256 = ContentSafety.Hash(binary), observationSha256 = ContentSafety.Hash(observation),
                    nativeCompleteEof = true, nativeRenderWareMatrices = true,
                    meshesIncluded = false, collisionClassified = false, navigationGenerated = false,
                    renderingTested = false, readyToEnable = false, runtimeModified = false, installed = false, published = false };
                if (destination != null)
                {
                    File.Copy(planPath, Path.Combine(pending, "plan.json"));
                    ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), observationAudit);
                    Directory.Move(pending, destination); pending = null;
                }
                return observationAudit;
            }
            var used = placements.Select(x => x.Model).ToHashSet(StringComparer.Ordinal);
            if (!used.SetEquals(models.Keys)) throw new InvalidDataException("Every placed model required; unused models rejected.");
            // Validate all local meshes before baking; bound aggregate work/output.
            var localStats = models.Select(x => BakeMesh(x.Value, null, null)).ToArray();
            long expectedBytes = placements.Sum(x => new FileInfo(models[x.Model]).Length);
            if (expectedBytes > 128L * 1024 * 1024) throw new InvalidDataException("Placed geometry exceeds 128MiB.");
            string meshDir = Path.Combine(pending, "meshes"); Directory.CreateDirectory(meshDir);
            long vertices = 0, triangles = 0; var baked = new List<object>();
            foreach (var placement in placements)
            {
                string name = $"f{placement.Field}-s{placement.Sector}-o{placement.ObjectIndex}-{placement.Model}.mesh";
                string target = Path.Combine(meshDir, name); var geometry = BakeMesh(models[placement.Model], target, placement.Matrix);
                vertices += geometry.Vertices; triangles += geometry.Triangles;
                baked.Add(new { placement, path = "meshes/" + name, sha256 = ContentSafety.Hash(target), bytes = new FileInfo(target).Length, geometry });
            }
            foreach (var input in inputs) if (Pin(input.Path) != input) throw new IOException("Scene input changed.");
            if (ContentSafety.Hash(Helper) != plan.HelperSha256) throw new IOException("Matrix helper changed.");
            ContentSafety.WriteJson(Path.Combine(pending, "scene.json"), new { profile = plan.Profile, coordinates = "world XYZ; native RenderWare basis", objects = baked });
            var audit = new { profile = plan.Profile, at = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(planPath), sourcePins = inputs, helperSha256 = plan.HelperSha256,
                fields = fields.Length, models = models.Count, placedObjects = placements.Length, vertices, triangles, geometryBytes = expectedBytes,
                placementSha256 = ContentSafety.Hash(binary), sceneSha256 = ContentSafety.Hash(Path.Combine(pending, "scene.json")),
                nativeCompleteEof = true, nativeRenderWareMatrices = true, placedTransformsApplied = true, collisionClassified = false,
                navigationGenerated = false, renderingTested = false, readyToEnable = false, runtimeModified = false, installed = false, published = false };
            if (destination != null)
            {
                File.Copy(planPath, Path.Combine(pending, "plan.json")); ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
                Directory.Move(pending, destination); pending = null;
            }
            return audit;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null) Directory.Delete(pending, true); }
    }
    private static float[] Floats(BinaryReader reader, int count)
    {
        var result = Enumerable.Range(0, count).Select(_ => reader.ReadSingle()).ToArray();
        if (result.Any(x => !float.IsFinite(x) || Math.Abs(x) > 1000000)) throw new InvalidDataException("Finite bounded native values required."); return result;
    }
    internal static Placement[] ReadPlacements(string path, int fields)
    {
        if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("Oversized placement output.");
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "TOS1" || r.ReadUInt32() != fields) throw new InvalidDataException("Native TOS1 header/grid.");
        uint count = r.ReadUInt32(); if (count > 100000) throw new InvalidDataException("Bounded scene objects required.");
        var result = new List<Placement>(); var identities = new HashSet<(uint, uint, uint)>();
        for (int i = 0; i < count; ++i)
        {
            uint field = r.ReadUInt32(), sector = r.ReadUInt32(), index = r.ReadUInt32(), length = r.ReadUInt32();
            if (field >= fields || sector >= 4 || index > 100000 || length is < 1 or >= 128 || !identities.Add((field, sector, index))) throw new InvalidDataException("Native object identity.");
            var nameBytes = r.ReadBytes((int)length);
            if (nameBytes.Length != length || nameBytes.Any(x => x > 127)) throw new InvalidDataException("Native object key.");
            string name = Encoding.ASCII.GetString(nameBytes).ToUpperInvariant(); if (!Key(name)) throw new InvalidDataException("Literal model key.");
            uint type = r.ReadUInt32(), flags = r.ReadUInt32(), trigger = r.ReadUInt32(), objectId = r.ReadUInt32(), nameIndex = r.ReadUInt32();
            var scale = Floats(r, 3); var rotation = Floats(r, 3); var position = Floats(r, 3); var matrix = Floats(r, 12);
            if (scale.Any(x => x <= 0) || !position.SequenceEqual(matrix[9..])) throw new InvalidDataException("Scale/translation mismatch.");
            result.Add(new(field, sector, index, name, type, flags, trigger, objectId, nameIndex, scale, rotation, position, matrix));
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Exact native placement EOF required."); return result.ToArray();
    }
    internal sealed record Geometry(uint Meshes, long Vertices, long Triangles, float[] Minimum, float[] Maximum);
    internal static object CompareModelGeometry(string source, string current)
    {
        source = Path.GetFullPath(source); current = Path.GetFullPath(current);
        ContentSafety.NoLinks(source); ContentSafety.NoLinks(current);
        using var sourceLock = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var currentLock = File.Open(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceGeometry = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var currentGeometry = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var a = BakeMesh(source, null, null, sourceGeometry);
        var b = BakeMesh(current, null, null, currentGeometry);
        string sourceOrderedGeometrySha256 = Convert.ToHexString(sourceGeometry.GetHashAndReset());
        string currentOrderedGeometrySha256 = Convert.ToHexString(currentGeometry.GetHashAndReset());
        return new { source, current, sourceSha256 = ContentSafety.Hash(source), currentSha256 = ContentSafety.Hash(current),
            sourceGeometry = a, currentGeometry = b, sourceOrderedGeometrySha256, currentOrderedGeometrySha256,
            orderedGeometryByteExact = sourceOrderedGeometrySha256 == currentOrderedGeometrySha256,
            exactEof = true, scope = "OSM1 ordered mesh counts, XYZ float bits and triangle indices; UVs/materials excluded",
            physicalEquivalenceUnderReorderingProven = false, normalsCompared = false, pluginsCompared = false,
            realRasterBindingsVerified = false, renderingTested = false, worldTested = false, installed = false };
    }
    internal static Geometry BakeMesh(string source, string? output, float[]? matrix, IncrementalHash? orderedGeometry = null)
    {
        void HashWord(uint word)
        {
            if (orderedGeometry == null) return;
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
            orderedGeometry.AppendData(bytes);
        }
        using var r = new BinaryReader(File.OpenRead(source));
        using var w = output == null ? null : new BinaryWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        if (r.BaseStream.Length > 128L * 1024 * 1024 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "OSM1") throw new InvalidDataException("Bounded OSM1 required.");
        uint meshes = r.ReadUInt32(); if (meshes is < 1 or > 4096) throw new InvalidDataException("Mesh count.");
        HashWord(meshes);
        w?.Write(Encoding.ASCII.GetBytes("OSM1")); w?.Write(meshes);
        long vertices = 0, triangles = 0; var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; var max = new[] { -float.MaxValue, -float.MaxValue, -float.MaxValue };
        for (int mesh = 0; mesh < meshes; ++mesh)
        {
            uint nv = r.ReadUInt32(), nt = r.ReadUInt32(), nm = r.ReadUInt32();
            if (nv is < 1 or > 2000000 || nt is < 1 or > 2000000 || nm != 1 || (vertices += nv) > 2000000 || (triangles += nt) > 2000000) throw new InvalidDataException("Bounded mesh counts.");
            HashWord(nv); HashWord(nt);
            w?.Write(nv); w?.Write(nt); w?.Write(nm);
            for (int i = 0; i < nv; ++i)
            {
                float[] v = Floats(r, 5); float[] xyz = v[..3];
                if (matrix != null) for (int k = 0; k < 3; ++k) xyz[k] = v[0] * matrix[k] + v[1] * matrix[3 + k] + v[2] * matrix[6 + k] + matrix[9 + k];
                for (int k = 0; k < 3; ++k)
                {
                    if (!float.IsFinite(xyz[k]) || Math.Abs(xyz[k]) > 1000000) throw new InvalidDataException("Bounded placed vertex.");
                    HashWord(unchecked((uint)BitConverter.SingleToInt32Bits(xyz[k])));
                    min[k] = Math.Min(min[k], xyz[k]); max[k] = Math.Max(max[k], xyz[k]); w?.Write(xyz[k]);
                }
                w?.Write(v[3]); w?.Write(v[4]);
            }
            for (int i = 0; i < nt; ++i)
            {
                uint a = r.ReadUInt32(), b = r.ReadUInt32(), c = r.ReadUInt32(), material = r.ReadUInt32();
                if (a >= nv || b >= nv || c >= nv || material != 0) throw new InvalidDataException("Triangle indices/material.");
                HashWord(a); HashWord(b); HashWord(c);
                w?.Write(a); w?.Write(b); w?.Write(c); w?.Write(material);
            }
            byte[] mat = r.ReadBytes(132); if (mat.Length != 132 || !mat.Skip(4).Contains((byte)0)) throw new InvalidDataException("Bounded material."); w?.Write(mat);
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Exact OSM1 EOF required.");
        return new(meshes, vertices, triangles, min, max);
    }
}
