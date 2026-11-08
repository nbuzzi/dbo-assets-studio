using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Explicit selected ground only. No automatic roof/floor classification,
// convex hull of whole caves, group attachments, deployment or readiness.
internal static class SceneNaviPieceService
{
    private const string Profile = "devildom-explicit-placed-ground-content-chunks-v1";
    private static string SceneProfile(string scene) => Profile;
    private static int PlacementCount(string profile) => 95;
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Selection(string Name, string SceneMesh, uint MeshIndex, uint[] Triangles, string Reason);
    internal sealed record Requests(Selection[] Pieces);
    internal sealed record Piece(Selection Selection, Pin Mesh);
    internal sealed record Plan(string Profile, Pin Scene, Pin Requests, Pin PathEngine, Piece[] Pieces, string HelperSha256);
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "scene-navi-piece-builder.exe");
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c < 32 || c > 127)) throw new InvalidDataException("Native ASCII path required.");
        long size = new FileInfo(path).Length;
        if (size is < 1 or > 128L * 1024 * 1024) throw new InvalidDataException("Bounded piece source required.");
        return new(path, ContentSafety.Hash(path), size);
    }
    private static T Read<T>(string file)
    {
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("Bounded piece metadata required.");
        byte[] bytes = File.ReadAllBytes(file); using var doc = JsonDocument.Parse(bytes); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new InvalidDataException("Null piece metadata.");
    }
    private static Dictionary<string, Pin> SceneMeshes(string scene)
    {
        if (new FileInfo(scene).Length > 2 * 1024 * 1024) throw new InvalidDataException("Bounded scene required.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(scene)); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        var root = doc.RootElement;
        if (root.GetProperty("profile").GetString() != "taiwan14-placed-object-geometry-v1" ||
            root.GetProperty("coordinates").GetString() != "world XYZ; native RenderWare basis")
            throw new InvalidDataException("Canonical native placed-scene snapshot required.");
        var result = new Dictionary<string, Pin>(StringComparer.Ordinal);
        string directory = Path.GetDirectoryName(scene)!;
        foreach (var row in root.GetProperty("objects").EnumerateArray())
        {
            string path = row.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing scene path.");
            string canonical = ContentSafety.ResourcePath(path).Replace('\\', '/');
            if (canonical != path || !path.StartsWith("meshes/") || !path.EndsWith(".mesh")) throw new InvalidDataException("Literal scene mesh path required.");
            string file = Path.GetFullPath(Path.Combine(directory, path));
            if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Scene traversal.");
            var pin = new Pin(file, row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64());
            if (!result.TryAdd(path, pin)) throw new InvalidDataException("Duplicate scene mesh identity.");
        }
        if (result.Count != PlacementCount(SceneProfile(scene))) throw new InvalidDataException("Reviewed complete scene required (Devildom95).");
        return result;
    }
    private static void Validate(Plan plan)
    {
        if (plan.Profile != Profile || plan.Scene == null || plan.Requests == null || plan.PathEngine == null ||
            plan.Pieces == null || plan.Pieces.Length is < 1 or > 256 || plan.HelperSha256 == null || plan.HelperSha256.Length != 64)
            throw new InvalidDataException("Sealed bounded explicit-ground plan required.");
        var names = new HashSet<string>(); var claimed = new HashSet<(string, uint, uint)>(); long total = 0;
        foreach (var piece in plan.Pieces)
        {
            var s = piece?.Selection;
            if (s == null || piece!.Mesh == null || s.Name == null || s.Name.Length is < 1 or > 64 ||
                !s.Name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') || !names.Add(s.Name) ||
                s.SceneMesh == null || s.Triangles == null || s.Triangles.Length is < 1 or > 100000 ||
                s.Reason == null || string.IsNullOrWhiteSpace(s.Reason) || s.Reason.Length > 500 ||
                !s.Triangles.SequenceEqual(s.Triangles.Distinct().Order())) throw new InvalidDataException("Unique named piece/sorted triangles/reason required.");
            foreach (uint triangle in s.Triangles) if (!claimed.Add((s.SceneMesh, s.MeshIndex, triangle))) throw new InvalidDataException("Triangle claimed by two ground pieces.");
            if ((total += s.Triangles.Length) > 1000000) throw new InvalidDataException("Selected ground triangle limit.");
        }
        var pins = plan.Pieces.Select(x => x.Mesh).Append(plan.Scene).Append(plan.Requests).Append(plan.PathEngine).Distinct().ToArray();
        if (pins.Any(x => x.Path == null || !Path.IsPathFullyQualified(x.Path) || x.Sha256 == null || x.Sha256.Length != 64 ||
                !x.Sha256.All(Uri.IsHexDigit) || x.Bytes is < 1 or > 128L * 1024 * 1024) ||
            pins.Sum(x => x.Bytes) > 256L * 1024 * 1024 || plan.Pieces.Sum(x => x.Mesh.Bytes) > 256L * 1024 * 1024)
            throw new InvalidDataException("Bounded absolute source pins and aggregate snapshots required.");
    }
    internal static object CreatePlan(string scene, string requests, string dll, string output)
    {
        scene = Path.GetFullPath(scene); requests = Path.GetFullPath(requests);
        ContentSafety.NoLinks(scene); ContentSafety.NoLinks(requests);
        var selection = Read<Requests>(requests);
        if (selection.Pieces == null || selection.Pieces.Length is < 1 or > 256 || selection.Pieces.Any(s => s == null || s.SceneMesh == null))
            throw new InvalidDataException("Bounded non-null explicit ground selections required.");
        var meshes = SceneMeshes(scene);
        var pieces = selection.Pieces.Select(s => new Piece(s, meshes.TryGetValue(s.SceneMesh, out var m) && Seal(m.Path) == m ? m : throw new InvalidDataException("Unselected/stale scene mesh."))).ToArray();
        var plan = new Plan(SceneProfile(scene), Seal(scene), Seal(requests), Seal(dll), pieces, Seal(Helper).Sha256); Validate(plan);
        foreach (var p in pieces) SceneGeometryService.BakeMesh(p.Mesh.Path, null, null);
        ContentSafety.WriteJson(output, plan); return new { plan = Path.GetFullPath(output), readyToEnable = false };
    }
    internal static Task<object> PreviewAsync(string path) => RunAsync(path, null);
    internal static Task<object> ApplyAsync(string path, string output) => RunAsync(path, output);
    private static async Task<object> RunAsync(string path, string? output)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path); var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read)); var p = Read<Plan>(path); Validate(p);
            var inputs = p.Pieces.Select(x => x.Mesh).Append(p.Scene).Append(p.Requests).Append(p.PathEngine).Distinct().ToArray();
            foreach (var input in inputs)
            {
                ContentSafety.NoLinks(input.Path); locks.Add(new(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Seal(input.Path) != input) throw new InvalidDataException("Stale scene/piece/request/DLL seal.");
            }
            ContentSafety.NoLinks(Helper); locks.Add(new(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Seal(Helper).Sha256 != p.HelperSha256) throw new InvalidDataException("Stale native chunk helper.");
            var requests = Read<Requests>(p.Requests.Path); var meshes = SceneMeshes(p.Scene.Path);
            if(p.Profile != SceneProfile(p.Scene.Path)) throw new InvalidDataException("Scene/profile identity mismatch.");
            if (JsonSerializer.Serialize(requests.Pieces, ContentSafety.Json) != JsonSerializer.Serialize(p.Pieces.Select(x => x.Selection), ContentSafety.Json) ||
                p.Pieces.Any(x => !meshes.TryGetValue(x.Selection.SceneMesh, out var m) || m != x.Mesh)) throw new InvalidDataException("Plan differs from explicit scene/request sources.");
            if (output != null)
            {
                output = ContentSafety.SafeOutput(output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (inputs.Select(x => x.Path).Append(path).Append(Helper).Any(x => x.Equals(output, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) throw new IOException("Piece candidate overlaps sources.");
                pending = ContentSafety.FreshDirectory(output);
            }
            else { pending = Path.Combine(Path.GetTempPath(), "DboAssetsSceneNavi", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending); }
            string sources = Path.Combine(pending, "sources"), tokens = Path.Combine(pending, "NaviModel"); Directory.CreateDirectory(sources); Directory.CreateDirectory(tokens);
            string dll = Path.Combine(sources, "PathEngine_x64.dll"); File.Copy(p.PathEngine.Path, dll);
            if (ContentSafety.Hash(dll) != p.PathEngine.Sha256) throw new IOException("DLL snapshot mismatch.");
            File.Copy(p.Scene.Path, Path.Combine(sources, "scene.json")); File.Copy(p.Requests.Path, Path.Combine(sources, "requests.json"));
            if (ContentSafety.Hash(Path.Combine(sources, "scene.json")) != p.Scene.Sha256 ||
                ContentSafety.Hash(Path.Combine(sources, "requests.json")) != p.Requests.Sha256) throw new IOException("Metadata snapshot mismatch.");
            var rows = new List<object>();
            foreach (var piece in p.Pieces)
            {
                string mesh = Path.Combine(sources, piece.Selection.Name + ".mesh"); File.Copy(piece.Mesh.Path, mesh);
                if (ContentSafety.Hash(mesh) != piece.Mesh.Sha256) throw new IOException("Ground mesh snapshot mismatch.");
                string snapshot = Path.Combine(sources, piece.Selection.Name + ".ncp"); WriteSnapshot(mesh, piece.Selection, snapshot);
                string name = piece.Selection.Name + "_PE_Nav_000.tok", token = Path.Combine(tokens, name);
                var start = new ProcessStartInfo(Helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = pending, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { dll, snapshot, token }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start) ?? throw new IOException("Native chunk helper absent.");
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); throw new IOException("Native ground chunk timeout."); }
                string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, piece.Selection.Name + ".log"), log);
                if (child.ExitCode != 0 || !log.Contains("PASS native selected-ground TOK") || !log.Contains("production-importer=true"))
                    throw new InvalidDataException("Native ground chunk/importer failed: " + log);
                rows.Add(new { name, sha256 = ContentSafety.Hash(token), bytes = new FileInfo(token).Length, selection = piece.Selection,
                    sourceSha256 = piece.Mesh.Sha256, snapshotSha256 = ContentSafety.Hash(snapshot), nativeRoundtrip = true });
            }
            foreach (var input in inputs) if (Seal(input.Path) != input) throw new IOException("Piece source changed.");
            if (Seal(Helper).Sha256 != p.HelperSha256) throw new IOException("Helper changed.");
            var audit = new { profile = p.Profile, at = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(path), sourcePins = inputs, pieces = rows,
                sourceSceneObjects = PlacementCount(p.Profile), selectedSceneObjects = p.Pieces.Select(x => x.Selection.SceneMesh).Distinct().Count(),
                coordinates = "world XYZ snapshot; TOK X/-Z/Y*100, native importer inverseZ/corner-swap", nativeContentChunkRoundtrip = true,
                productionImporterVerified = true, importerToleranceWorldXZ = 0.01, importerToleranceWorldY = 0.00001,
                allFloorsClassified = false, obstaclesClassified = false, groupAttachmentsGenerated = false, navigationGenerated = false,
                readyToEnable = false, installed = false, runtimeModified = false, published = false };
            if (output != null) { File.Copy(path, Path.Combine(pending, "plan.json")); ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit); Directory.Move(pending, output); pending = null; }
            return audit;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null) Directory.Delete(pending, true); }
    }
    internal static void WriteSnapshot(string path, Selection selection, string output, bool weldExactCoordinates = false)
    {
        SceneGeometryService.BakeMesh(path, null, null);
        using var r = new BinaryReader(File.OpenRead(path)); r.ReadBytes(4); uint meshes = r.ReadUInt32();
        if (selection.MeshIndex >= meshes) throw new InvalidDataException("Selected native submesh absent.");
        for (uint mesh = 0; mesh < meshes; ++mesh)
        {
            uint nv = r.ReadUInt32(), nt = r.ReadUInt32(); r.ReadUInt32();
            var vertices = new float[nv][];
            for (uint i = 0; i < nv; ++i) { vertices[i] = [r.ReadSingle(), r.ReadSingle(), r.ReadSingle()]; r.ReadSingle(); r.ReadSingle(); }
            var faces = new uint[nt][];
            for (uint i = 0; i < nt; ++i) { faces[i] = [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()]; r.ReadUInt32(); } r.ReadBytes(132);
            if (mesh != selection.MeshIndex) continue;
            if (selection.Triangles.Any(x => x >= nt)) throw new InvalidDataException("Selected triangle absent.");
            var triangles = selection.Triangles.Select(x => faces[x]).ToArray(); var used = triangles.SelectMany(x => x).Distinct().Order().ToArray();
            var index = new Dictionary<uint, uint>(); var writtenVertices = new List<float[]>();
            var coordinates = new Dictionary<(float, float, float), uint>();
            foreach (uint original in used)
            {
                var v = vertices[original]; var key = (v[0], v[1], v[2]);
                if (!weldExactCoordinates || !coordinates.TryGetValue(key, out uint mapped))
                {
                    mapped = (uint)writtenVertices.Count; writtenVertices.Add(v);
                    if (weldExactCoordinates) coordinates.Add(key, mapped);
                }
                index.Add(original, mapped);
            }
            if (weldExactCoordinates && triangles.Any(t => t.Select(i => index[i]).Distinct().Count() != 3)) throw new InvalidDataException("Coordinate welding produced a degenerate face.");
            using var w = new BinaryWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            w.Write(Encoding.ASCII.GetBytes("NCP1")); w.Write((uint)writtenVertices.Count); w.Write((uint)triangles.Length);
            foreach (var vertex in writtenVertices) foreach (float value in vertex) w.Write(value);
            foreach (var triangle in triangles) foreach (uint i in triangle) w.Write(index[i]);
            return;
        }
    }
}
