using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Shared UI/CLI motor. Native vertex extraction is deliberately distinct from Navi.
internal static class TerrainGeometryService
{
    private const string Profile = "taiwan14-terrain-vertices-v1";
    internal sealed record Input(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, Input Gwp, Input[] Fields, string HelperSha256);
    internal sealed record Geometry(int FieldCross, int SectorCross, int VertexCross, int TileSize,
        int FieldSize, int WorldHalf, int Fields, int Sectors, long Vertices, float MinY, float MaxY);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "terrain-geometry-reader.exe");

    private static Input Pin(string path)
    {
        path = System.IO.Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c > 127 || c < 32 || c == '|')) throw new InvalidDataException("Native paths must be ASCII without controls.");
        long bytes = new FileInfo(path).Length;
        if (bytes < 1 || bytes > 128L * 1024 * 1024) throw new InvalidDataException("Terrain input size.");
        return new(path, ContentSafety.Hash(path), bytes);
    }
    internal static object CreatePlan(string gwp, string fieldsRoot, string output)
    {
        fieldsRoot = Path.GetFullPath(fieldsRoot); ContentSafety.NoLinks(fieldsRoot);
        if (Path.GetFileName(gwp) != "#######.gwp") throw new InvalidDataException("Literal project GWP required.");
        var files = Directory.GetDirectories(fieldsRoot).Select(dir =>
        {
            ContentSafety.NoLinks(dir); string name = Path.GetFileName(dir);
            if (!name.StartsWith("wfif", StringComparison.Ordinal) || !uint.TryParse(name[4..], out uint id) || name != "wfif" + id)
                throw new InvalidDataException("Only literal numbered field folders accepted.");
            return (id, input: Pin(Path.Combine(dir, name)));
        }).OrderBy(x => x.id).ToArray();
        if (files.Length is < 1 or > 4096 || files.Where((x, i) => x.id != i).Any() || Math.Sqrt(files.Length) % 1 != 0)
            throw new InvalidDataException("Complete contiguous square grid required; no missing-field fill.");
        var plan = new Plan(Profile, Pin(gwp), files.Select(x => x.input).ToArray(), Pin(Helper).Sha256);
        if (plan.Fields.Sum(x => x.Bytes) > 256L * 1024 * 1024) throw new InvalidDataException("Terrain bundle exceeds 256MiB.");
        output = ContentSafety.SafeOutput(output);
        if (plan.Fields.Append(plan.Gwp).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Output overlaps source.");
        ContentSafety.WriteJson(output, plan); return new { plan = output, fields = files.Length, profile = plan.Profile, navigationGenerated = false };
    }
    internal static async Task<object> PreviewAsync(string path) => await ExecuteAsync(path, null);
    internal static async Task<object> ApplyAsync(string path, string output) => await ExecuteAsync(path, output);

    private static async Task<object> ExecuteAsync(string planPath, string? destination)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Plan exceeds 2MiB.");
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(planPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            byte[] planBytes = File.ReadAllBytes(planPath);
            using (var document = JsonDocument.Parse(planBytes)) NoDuplicateKeys(document.RootElement);
            var plan = JsonSerializer.Deserialize<Plan>(planBytes, Strict) ?? throw new InvalidDataException("Missing plan.");
            if (plan.Profile != Profile || plan.Gwp == null || plan.Fields == null || plan.Fields.Length is < 1 or > 4096 ||
                Math.Sqrt(plan.Fields.Length) % 1 != 0 || Path.GetFileName(plan.Gwp.Path) != "#######.gwp") throw new InvalidDataException("Unsupported terrain plan.");
                var inputs = plan.Fields.Prepend(plan.Gwp).ToArray();
            if (inputs.Any(x => x == null) || inputs.Select(x => Path.GetFullPath(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length ||
                inputs.Sum(x => x.Bytes) > 256L * 1024 * 1024) throw new InvalidDataException("Duplicate or oversized inputs.");
            for (int i = 0; i < plan.Fields.Length; ++i)
                if (Path.GetFileName(plan.Fields[i].Path) != "wfif" + i || Path.GetFileName(Path.GetDirectoryName(plan.Fields[i].Path)) != "wfif" + i)
                    throw new InvalidDataException("Literal field identity/order required.");
            foreach (var input in inputs)
            {
                ContentSafety.NoLinks(input.Path); locks.Add(new(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Pin(input.Path) != input) throw new InvalidDataException("Stale terrain source pin.");
            }
            ContentSafety.NoLinks(Helper); locks.Add(new(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Pin(Helper).Sha256 != plan.HelperSha256) throw new InvalidDataException("Rebuild requires a new helper-pinned plan.");
            if (destination != null)
            {
                destination = ContentSafety.SafeOutput(destination);
                string prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (inputs.Select(x => x.Path).Append(planPath).Append(Helper).Any(x => x.Equals(destination, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Candidate would contain its inputs.");
                pending = ContentSafety.FreshDirectory(destination);
            }
            else
            {
                pending = Path.Combine(Path.GetTempPath(), "DboAssetsTerrain", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending);
            }
            string snapshots = Path.Combine(pending, "sources"); Directory.CreateDirectory(snapshots);
            string gwpCopy = Path.Combine(snapshots, "project.gwp"); File.Copy(plan.Gwp.Path, gwpCopy);
            var paths = new List<string>();
            for (int i = 0; i < plan.Fields.Length; ++i)
            {
                string copy = Path.Combine(snapshots, "wfif" + i); File.Copy(plan.Fields[i].Path, copy);
                if (ContentSafety.Hash(copy) != plan.Fields[i].Sha256) throw new IOException("Snapshot mismatch."); paths.Add(copy);
            }
            if (ContentSafety.Hash(gwpCopy) != plan.Gwp.Sha256) throw new IOException("GWP snapshot mismatch.");
            string nativeGwp = gwpCopy;
            string list = Path.Combine(pending, "native-input.txt"); File.WriteAllLines(list, paths, Encoding.ASCII);
            string binary = Path.Combine(pending, "terrain.tgv");
            var start = new ProcessStartInfo(Helper) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = pending, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(nativeGwp); start.ArgumentList.Add(list); start.ArgumentList.Add(binary);
            using var process = Process.Start(start) ?? throw new IOException("Native terrain helper missing.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Native terrain timeout60s."); }
            string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, "native.log"), log);
            if (process.ExitCode != 0) throw new InvalidDataException("Native terrain reader failed: " + log);
            var geometry = Validate(binary, plan.Fields.Length);
            string materialFile = binary + ".materials.tsv";
            var materials = ValidateMaterials(materialFile, plan.Fields.Length);
            foreach (var input in inputs) if (Pin(input.Path) != input) throw new IOException("Input changed during extraction.");
            if (ContentSafety.Hash(Helper) != plan.HelperSha256) throw new IOException("Helper changed.");
            var audit = new { profile = plan.Profile, at = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(planPath), sourcePins = inputs,
                nativeInspectionGwpSha256 = ContentSafety.Hash(nativeGwp),
                helperSha256 = plan.HelperSha256, geometry, output = "terrain.tgv", outputSha256 = ContentSafety.Hash(binary), outputBytes = new FileInfo(binary).Length,
                materials, materialOutput = "terrain.tgv.materials.tsv", materialSha256 = ContentSafety.Hash(materialFile),
                nativeCompleteEof = true, coordinates = "world XYZ; native X decreases across each row", terrainOnly = true, navigationGenerated = false,
                objectsIncluded = false, readyToEnable = false, runtimeModified = false, installed = false, published = false, gameplayTested = false };
            if (destination != null)
            {
                File.Copy(planPath, Path.Combine(pending, "plan.json")); ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
                Directory.Move(pending, destination); pending = null;
            }
            return audit;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null) Directory.Delete(pending, true); }
    }

    internal static object ValidateMaterials(string path, int fields, bool allowParentheses = false)
    {
        if (new FileInfo(path).Length is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("Material catalog size.");
        var identities = new HashSet<(int, int, int)>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 4 || !int.TryParse(parts[0], out int field) || field < 0 || field >= fields ||
                !int.TryParse(parts[1], out int sector) || sector < 0 || sector >= 4 ||
                !int.TryParse(parts[2], out int layer) || layer < 0 || layer > 5 || !identities.Add((field, sector, layer)) ||
                parts[3].Length is < 1 or > 31 || parts[3].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' || allowParentheses && c is '(' or ')')))
                throw new InvalidDataException("Native material identity/basename.");
            names.Add(parts[3]);
        }
        for (int field = 0; field < fields; ++field)
        for (int sector = 0; sector < 4; ++sector)
            if (!identities.Contains((field, sector, 0))) throw new InvalidDataException("Missing base material sector.");
        return new { references = identities.Count, baseSectors = fields * 4, names = names.ToArray(), nativeBoundaryChecked = true,
            texturesDecoded = false, packedLookupTested = false };
    }

    internal static Geometry Validate(string path, int expectedFields)
    {
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("Terrain output exceeds bound.");
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "TGV1") throw new InvalidDataException("Native snapshot header.");
        int fieldsCross = reader.ReadInt32(), sectorsCross = reader.ReadInt32(), cross = reader.ReadInt32(), tile = reader.ReadInt32(), fieldSize = reader.ReadInt32(), half = reader.ReadInt32(), fields = reader.ReadInt32();
        if (fields != expectedFields || fieldsCross is < 1 or > 64 || fieldsCross * fieldsCross != fields || sectorsCross != 2 || cross is < 2 or > 129 ||
            tile is < 1 or > 1024 || fieldSize != 2 * (cross - 1) * tile || half != fieldsCross * fieldSize / 2) throw new InvalidDataException("Native geometry dimensions/coverage.");
        int count = cross * cross; long vertices = (long)fields * 4 * count;
        if (reader.BaseStream.Length != 32 + (long)fields * 4 * (12 + count * 12)) throw new InvalidDataException("Native output size/EOF.");
        float min = float.MaxValue, max = -float.MaxValue;
        for (int field = 0; field < fields; ++field)
        for (int sector = 0; sector < 4; ++sector)
        {
            if (reader.ReadInt32() != field || reader.ReadInt32() != sector || reader.ReadInt32() != count) throw new InvalidDataException("Native sector coverage.");
            float baseX = 0, baseZ = 0;
            for (int i = 0; i < count; ++i)
            {
                float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                if (i == 0)
                {
                    baseX = x; baseZ = z;
                    int sectorSize = (cross - 1) * tile;
                    if (baseX != -half + field % fieldsCross * fieldSize + (sector % 2 + 1) * sectorSize ||
                        baseZ != -half + field / fieldsCross * fieldSize + sector / 2 * sectorSize)
                        throw new InvalidDataException("Native sector belongs to wrong field.");
                }
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || Math.Abs(x) > half || Math.Abs(z) > half ||
                    x != baseX - i % cross * tile || z != baseZ + i / cross * tile) throw new InvalidDataException("Native finite lattice.");
                min = Math.Min(min, y); max = Math.Max(max, y);
            }
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Native geometry trailing data.");
        return new(fieldsCross, sectorsCross, cross, tile, fieldSize, half, fields, fields * 4, vertices, min, max);
    }

    internal static object CompareGeometry(string sourcePath, string currentPath)
    {
        sourcePath = Path.GetFullPath(sourcePath); currentPath = Path.GetFullPath(currentPath);
        ContentSafety.NoLinks(sourcePath); ContentSafety.NoLinks(currentPath);
        using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var currentLock = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var source = Pin(sourcePath); var current = Pin(currentPath);
        static int Fields(FileStream stream)
        {
            if (stream.Length < 32) throw new InvalidDataException("Terrain comparison header size.");
            stream.Position = 28;
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            return reader.ReadInt32();
        }
        Geometry a = Validate(sourcePath, Fields(sourceLock));
        Geometry b = Validate(currentPath, Fields(currentLock));
        bool layoutEqual = (a.FieldCross, a.SectorCross, a.VertexCross, a.TileSize, a.FieldSize, a.WorldHalf, a.Fields) ==
                           (b.FieldCross, b.SectorCross, b.VertexCross, b.TileSize, b.FieldSize, b.WorldHalf, b.Fields);
        long numericDifferences = 0, bitDifferences = 0, signedZeroOnlyVertices = 0, verticesCompared = 0;
        double maxDistance = 0, maxAbsX = 0, maxAbsY = 0, maxAbsZ = 0;
        object? firstDifference = null;
        if (layoutEqual)
        {
            sourceLock.Position = currentLock.Position = 32;
            using var ar = new BinaryReader(sourceLock, Encoding.ASCII, leaveOpen: true);
            using var br = new BinaryReader(currentLock, Encoding.ASCII, leaveOpen: true);
            for (int field = 0; field < a.Fields; ++field)
            for (int sector = 0; sector < 4; ++sector)
            {
                int count = a.VertexCross * a.VertexCross;
                if (ar.ReadInt32() != field || br.ReadInt32() != field || ar.ReadInt32() != sector || br.ReadInt32() != sector ||
                    ar.ReadInt32() != count || br.ReadInt32() != count) throw new InvalidDataException("Terrain comparison coverage changed.");
                for (int vertex = 0; vertex < count; ++vertex)
                {
                    float ax = ar.ReadSingle(), ay = ar.ReadSingle(), az = ar.ReadSingle();
                    float bx = br.ReadSingle(), by = br.ReadSingle(), bz = br.ReadSingle();
                    bool bitsEqual = BitConverter.SingleToInt32Bits(ax) == BitConverter.SingleToInt32Bits(bx) &&
                                     BitConverter.SingleToInt32Bits(ay) == BitConverter.SingleToInt32Bits(by) &&
                                     BitConverter.SingleToInt32Bits(az) == BitConverter.SingleToInt32Bits(bz);
                    bool valuesEqual = ax == bx && ay == by && az == bz;
                    if (!bitsEqual) ++bitDifferences;
                    if (!valuesEqual) ++numericDifferences;
                    else if (!bitsEqual) ++signedZeroOnlyVertices;
                    double dx = (double)ax - bx, dy = (double)ay - by, dz = (double)az - bz;
                    maxAbsX = Math.Max(maxAbsX, Math.Abs(dx)); maxAbsY = Math.Max(maxAbsY, Math.Abs(dy)); maxAbsZ = Math.Max(maxAbsZ, Math.Abs(dz));
                    maxDistance = Math.Max(maxDistance, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    if (!valuesEqual && firstDifference == null)
                        firstDifference = new { field, sector, vertex, source = new[] { ax, ay, az }, current = new[] { bx, by, bz } };
                    ++verticesCompared;
                }
            }
            if (sourceLock.Position != sourceLock.Length || currentLock.Position != currentLock.Length || verticesCompared != a.Vertices)
                throw new InvalidDataException("Terrain comparison EOF/coverage.");
        }
        return new { profile = "taiwan-tgv1-ordered-geometry-comparison-v1", source, current, sourceGeometry = a, currentGeometry = b,
            validatedBothSnapshots = true, layoutEqual, verticesCompared, numericDifferentVertices = numericDifferences,
            bitDifferentVertices = bitDifferences, signedZeroOnlyVertices, maxDistance, maxAbsX, maxAbsY, maxAbsZ, firstDifference,
            sameNumericGeometry = layoutEqual && numericDifferences == 0, sameVertexBits = layoutEqual && bitDifferences == 0,
            snapshotByteExact = source.Sha256 == current.Sha256, materialsCompared = false, objectsCompared = false,
            completeMapEquivalent = false, runtimeModified = false, worldTested = false };
    }

    internal static void NoDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate plan property."); NoDuplicateKeys(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) NoDuplicateKeys(item);
    }
}
