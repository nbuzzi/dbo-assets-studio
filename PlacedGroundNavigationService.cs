using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// A single explicitly selected placed floor. This never classifies surrounding
// scene objects, rewrites a client project, installs a cache or enables a world.
internal static class PlacedGroundNavigationService
{
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Point(string Name, float X, float Y, float Z);
    internal sealed record Request(uint ResourceId, uint Label, string Folder, uint Field, uint Sector,
        uint ObjectIndex, string Model, uint MeshIndex, uint[] Triangles, string Reason, Point[] Points, Point[] Outside);
    internal sealed record Plan(string Profile, Pin Observation, Pin Audit, Pin Mesh, Pin Request, Pin PathEngine, Pin Chunk, Pin Exporter);
    private const string Profile = "taiwan-explicit-observed-floor-navigation-v1";
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Bounded observed floor metadata required.");
        byte[] bytes = File.ReadAllBytes(path); using var doc = JsonDocument.Parse(bytes); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new InvalidDataException("Null observed floor metadata.");
    }
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c < 32 || c > 127)) throw new IOException("Native ASCII path required.");
        long size = new FileInfo(path).Length;
        if (size is < 1 or > 128 * 1024 * 1024) throw new IOException("Bounded navigation source required.");
        return new(path, ContentSafety.Hash(path), size);
    }
    private static Pin[] Pins(Plan p) => [p.Observation, p.Audit, p.Mesh, p.Request, p.PathEngine, p.Chunk, p.Exporter];
    private static string PrivateOutput(Plan p, string output)
    {
        output = ContentSafety.SafeOutput(output);
        return output;
    }
    private static SceneGeometryService.Placement Validate(Plan p)
    {
        if (p.Profile != Profile || Path.GetFileName(p.Chunk.Path) != "scene-navi-piece-builder.exe" ||
            Path.GetFileName(p.Exporter.Path) != "placed-floor-navigation-builder.exe") throw new InvalidDataException("Explicit observed-floor helpers/profile required.");
        var q = Read<Request>(p.Request.Path);
        if (q.ResourceId is < 6000 or > 999999 || q.Label is 0 or uint.MaxValue || q.Folder == null || q.Folder.Length is < 1 or > 40 ||
            q.Folder.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) || q.Sector > 3 ||
            string.IsNullOrWhiteSpace(q.Reason) || q.Reason.Length > 1000 || q.Model == null ||
            q.Triangles == null || q.Triangles.Length is < 1 or > 100000 || !q.Triangles.SequenceEqual(q.Triangles.Distinct().Order()) ||
            q.Points == null || q.Points.Length is < 2 or > 16 || q.Outside == null || q.Outside.Length is < 1 or > 16)
            throw new InvalidDataException("Bounded explicit floor selection and route/outside probes required.");
        var points = q.Points.Concat(q.Outside).ToArray();
        if (points.Select(x => x.Name).Distinct().Count() != points.Length || points.Any(x => x.Name == null || x.Name.Length is < 1 or > 48 ||
            x.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || new[] { x.X, x.Y, x.Z }.Any(f => !float.IsFinite(f) || Math.Abs(f) >= 1024)))
            throw new InvalidDataException("Unique finite floor probe points inside private navigation bounds required.");
        using var observation = JsonDocument.Parse(File.ReadAllBytes(p.Observation.Path));
        using var audit = JsonDocument.Parse(File.ReadAllBytes(p.Audit.Path));
        TerrainGeometryService.NoDuplicateKeys(observation.RootElement); TerrainGeometryService.NoDuplicateKeys(audit.RootElement);
        if (observation.RootElement.GetProperty("profile").GetString() != "taiwan14-placed-object-observation-v1" ||
            audit.RootElement.GetProperty("profile").GetString() != "taiwan14-placed-object-observation-v1" ||
            !audit.RootElement.GetProperty("nativeCompleteEof").GetBoolean() || !audit.RootElement.GetProperty("nativeRenderWareMatrices").GetBoolean() ||
            audit.RootElement.GetProperty("observationSha256").GetString() != p.Observation.Sha256)
            throw new InvalidDataException("Canonical complete native placement observation/audit required.");
        var rows = observation.RootElement.GetProperty("objects");
        if (rows.GetArrayLength() != audit.RootElement.GetProperty("placedObjects").GetInt32()) throw new InvalidDataException("Placement count differs from audit.");
        var matches = rows.EnumerateArray().Where(x => x.GetProperty("field").GetUInt32() == q.Field &&
            x.GetProperty("sector").GetUInt32() == q.Sector && x.GetProperty("objectIndex").GetUInt32() == q.ObjectIndex).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Explicit placement absent or duplicated.");
        var placement = matches[0].Deserialize<SceneGeometryService.Placement>(Strict) ?? throw new InvalidDataException("Null placement.");
        if (placement.Model != q.Model || placement.Matrix.Length != 12 || placement.Matrix.Any(f => !float.IsFinite(f)))
            throw new InvalidDataException("Exact source placement/model/matrix required.");
        SceneGeometryService.BakeMesh(p.Mesh.Path, null, null);
        return placement;
    }
    internal static object CreatePlan(string observation, string mesh, string request, string dll, string chunk, string exporter, string output)
    {
        var p = new Plan(Profile, Seal(observation), Seal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(observation))!, "audit.json")),
            Seal(mesh), Seal(request), Seal(dll), Seal(chunk), Seal(exporter));
        Validate(p); output = ContentSafety.SafeOutput(output);
        if (Pins(p).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Plan overlaps source.");
        ContentSafety.WriteJson(output, p); return new { plan = output, installed = false, clientProjectBindingPrepared = false };
    }
    internal static async Task<object> RunAsync(string path, string? output)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path); var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read)); var p = Read<Plan>(path);
            foreach (var pin in Pins(p))
            {
                ContentSafety.NoLinks(pin.Path); locks.Add(new(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Seal(pin.Path) != pin) throw new IOException("Stale observed floor source: " + pin.Path);
            }
            var placement = Validate(p); var q = Read<Request>(p.Request.Path);
            if (output == null) return new { pinsValid = true, placement, selectionCount = q.Triangles.Length, navigationGenerated = false };
            output = PrivateOutput(p, output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Pins(p).Select(x => x.Path).Append(path).Any(x => x.Equals(output, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Navigation output overlaps inputs.");
            pending = ContentSafety.FreshDirectory(output); string sources = Path.Combine(pending, "sources"); Directory.CreateDirectory(sources);
            foreach (var pin in new[] { p.Observation, p.Audit, p.Mesh, p.Request, p.PathEngine })
            {
                string target = Path.Combine(sources, pin == p.Observation ? "placements.json" : pin == p.Audit ? "placement-audit.json" : pin == p.Mesh ? "source.mesh" : pin == p.Request ? "request.json" : "PathEngine_x64.dll");
                File.Copy(pin.Path, target); if (ContentSafety.Hash(target) != pin.Sha256) throw new IOException("Snapshot mismatch.");
            }
            string baked = Path.Combine(pending, "placed.mesh"), ncp = Path.Combine(pending, "floor.ncp"), tok = Path.Combine(pending, "floor.tok");
            var geometry = SceneGeometryService.BakeMesh(p.Mesh.Path, baked, placement.Matrix);
            // UV seams duplicate XYZ in render meshes. Connect only equal XYZ
            // coordinates in the derived navigation piece; no epsilon snapping,
            // hull, new faces, material/UV edits or source-model mutation.
            SceneNaviPieceService.WriteSnapshot(baked, new("FLOOR", "placed.mesh", q.MeshIndex, q.Triangles, q.Reason), ncp, true);
            string dll = Path.Combine(sources, "PathEngine_x64.dll");
            string chunkLog = await Run(p.Chunk.Path, pending, dll, ncp, tok); File.WriteAllText(Path.Combine(pending, "chunk.log"), chunkLog);
            if (!chunkLog.Contains("PASS native selected-ground TOK") || !chunkLog.Contains("production-importer=true")) throw new IOException("Native floor TOK proof absent.");
            string settings = Path.Combine(pending, "native-request.txt");
            var lines = new List<string> { $"FLOORWORLD1 {q.ResourceId} {q.Label} {q.Folder} {q.Points.Length} {q.Outside.Length}" };
            foreach (var point in q.Points.Concat(q.Outside)) lines.Add(string.Join(" ", point.Name, point.X.ToString("R", CultureInfo.InvariantCulture), point.Y.ToString("R", CultureInfo.InvariantCulture), point.Z.ToString("R", CultureInfo.InvariantCulture)));
            File.WriteAllLines(settings, lines, Encoding.ASCII);
            string log = await Run(p.Exporter.Path, pending, tok, dll, settings, Path.Combine(pending, "native"));
            File.WriteAllText(Path.Combine(pending, "native.log"), log);
            if (!log.Contains($"PASS placed floor production Navi resource{q.ResourceId} agents5") || !log.Contains("all-probes-bidirectional=true outside-rejected=true"))
                throw new IOException("Complete native floor navigation proof absent.");
            foreach (var pin in Pins(p)) if (Seal(pin.Path) != pin) throw new IOException("Navigation source changed.");
            File.Copy(path, Path.Combine(pending, "plan.json"));
            var resources = Directory.GetFiles(pending, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(pending, x), sha256 = ContentSafety.Hash(x), size = new FileInfo(x).Length }).ToArray();
            var audit = new { profile = Profile, utc = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(path), sourcePins = Pins(p), placement, geometry,
                request = q, exactCoordinateWelding = true, floorChunkNativeRoundtrip = true, nativeNavigationExportReload = true, allFiveAgentRadii = true,
                allConfiguredSpawnRoutesBidirectional = true, outsideDestinationRejected = true,
                scope = "Explicit selected triangles of one native placed object only; surrounding scene/obstacles not classified",
                clientProjectBindingPrepared = false, clientCollisionTested = false, gameplayTested = false, readyToInstall = false, installed = false, published = false, resources };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit); Directory.Move(pending, output); pending = null;
            return new { output, audit };
        }
        catch (Exception ex)
        {
            if (pending != null && Directory.Exists(pending))
            {
                ContentSafety.WriteJson(Path.Combine(pending, "failure.json"), new { profile = Profile, failed = true, error = ex.Message, installed = false, readyToInstall = false });
                // Keep diagnostic preimages/partial native logs. A failed
                // directory is never the requested successful candidate.
                string failed = output + ".failed-" + Guid.NewGuid().ToString("N");
                Directory.Move(pending, failed); pending = null;
            }
            throw;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
    private static async Task<string> Run(string helper, string cwd, params string[] args)
    {
        var start = new ProcessStartInfo(helper) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Native helper unavailable.");
        async Task<string> Capture(StreamReader reader, string suffix)
        {
            using var writer = new StreamWriter(new FileStream(Path.Combine(cwd, Path.GetFileNameWithoutExtension(helper) + suffix), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            writer.AutoFlush = true; var text = new StringBuilder();
            while (await reader.ReadLineAsync() is { } line) { text.AppendLine(line); await writer.WriteLineAsync(line); }
            return text.ToString();
        }
        var stdout = Capture(child.StandardOutput, ".stdout.log"); var stderr = Capture(child.StandardError, ".stderr.log");
        // Native DLL cold starts can take longer while the disk is busy.
        // The asynchronous deadline bounds the process; callers can yield.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new IOException("Native floor navigation deadline180s: " + Path.GetFileName(helper) + "\n" + await stdout + await stderr); }
        string log = await stdout + await stderr;
        if (child.ExitCode != 0) throw new IOException("Native floor navigation failed: " + log);
        return log;
    }
}
