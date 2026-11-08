using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal static class MapNavigationService
{
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, Pin Audit, Pin Recipe, Pin Mesh, Pin Ground, Pin ChunkHelper, Pin ExportHelper, Pin PathEngine, Pin? Identity = null);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c < 32 || c > 127)) throw new IOException("Native ASCII path required.");
        long size = new FileInfo(path).Length; if (size is < 1 or > 32 * 1024 * 1024) throw new IOException("Bounded map navigation input required.");
        return new(path, ContentSafety.Hash(path), size);
    }
    private static Pin[] Inputs(Plan p) => p.Identity==null?[p.Audit, p.Recipe, p.Mesh, p.Ground, p.ChunkHelper, p.ExportHelper, p.PathEngine]:[p.Audit, p.Recipe, p.Mesh, p.Ground, p.ChunkHelper, p.ExportHelper, p.PathEngine,p.Identity];
    private static void Validate(Plan p)
    {
        if(p.Identity!=null)NewMapTableService.ReadIdentity(p.Identity.Path);
        var recipe=JsonSerializer.Deserialize<MapWorkshopService.Recipe>(File.ReadAllBytes(p.Recipe.Path),Strict)??throw new InvalidDataException("Null navigation recipe.");
        if(!recipe.Stands)throw new InvalidDataException("Reviewed tournament navigation requires a spectator balcony.");
        if (p.Profile != "assets-power-arena-navigation-plan-v1" || Path.GetFileName(p.ChunkHelper.Path) != "scene-navi-piece-builder.exe" || Path.GetFileName(p.ExportHelper.Path) != "map-workshop-navigation-builder.exe" || Path.GetFileName(p.PathEngine.Path) != "PathEngine_x64.dll") throw new InvalidDataException("Reviewed map navigation profile/helpers required.");
        using var audit = JsonDocument.Parse(File.ReadAllBytes(p.Audit.Path)); TerrainGeometryService.NoDuplicateKeys(audit.RootElement);
        var a = audit.RootElement;
        if (a.GetProperty("profile").GetString() != "assets-power-arena-candidate-v1" || !a.GetProperty("nativeDffBspRoundtrip").GetBoolean()) throw new InvalidDataException("Native original map candidate required.");
        foreach (var pin in new[] { p.Ground, p.Mesh, p.Recipe })
        {
            string rel = Path.GetRelativePath(Path.GetDirectoryName(p.Audit.Path)!, pin.Path);
            if (!a.GetProperty("resources").EnumerateArray().Any(x => x.GetProperty("path").GetString() == rel && x.GetProperty("sha256").GetString() == pin.Sha256 && x.GetProperty("size").GetInt64() == pin.Bytes)) throw new InvalidDataException("Map resource differs from original audit.");
        }
    }
    internal static object CreatePlan(string candidate, string chunk, string exporter, string dll, string output,string? identityPath=null)
    {
        candidate = Path.GetFullPath(candidate); var p = new Plan("assets-power-arena-navigation-plan-v1", Seal(Path.Combine(candidate, "audit.json")), Seal(Path.Combine(candidate, "snapshots", "recipe.json")), Seal(Path.Combine(candidate, "arena.mesh")), Seal(Path.Combine(candidate, "ground.ncp")), Seal(chunk), Seal(exporter), Seal(dll),identityPath==null?null:Seal(identityPath));
        Validate(p); output = ContentSafety.SafeOutput(output); if (Inputs(p).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Navigation plan overlaps input.");
        ContentSafety.WriteJson(output, p); return new { plan = output, provisionalResourceId = p.Identity==null?6010:NewMapTableService.ReadIdentity(p.Identity.Path).ResourceId, readyToInstall = false };
    }
    internal static async Task<object> RunAsync(string path, string? output)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path); var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (new FileInfo(path).Length > 16384) throw new IOException("Bounded navigation plan required.");
            byte[] bytes = File.ReadAllBytes(path); using var doc = JsonDocument.Parse(bytes); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
            var p = JsonSerializer.Deserialize<Plan>(bytes, Strict) ?? throw new InvalidDataException("Null navigation plan.");
            foreach (var pin in Inputs(p)) { ContentSafety.NoLinks(pin.Path); locks.Add(new(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read)); if (Seal(pin.Path) != pin) throw new IOException("Stale navigation input: " + pin.Path); }
            Validate(p);
            var identity=p.Identity==null?null:NewMapTableService.ReadIdentity(p.Identity.Path);uint resource=identity?.ResourceId??6010,label=identity?.MapNameId??600100010;string folder=identity?.Folder??"dbo_assets_power_arena_001";
            if (output == null) return new { pinsValid = true, provisionalResourceId = resource, nativeExportTested = false, readyToInstall = false };
            output = ContentSafety.SafeOutput(output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Inputs(p).Select(x => x.Path).Append(path).Any(x => x.Equals(output, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) throw new IOException("Navigation candidate overlaps sources.");
            pending = ContentSafety.FreshDirectory(output); string snapshots = Path.Combine(pending, "snapshots"); Directory.CreateDirectory(snapshots);
            foreach (var pin in new[] { p.Recipe, p.Ground, p.PathEngine }) File.Copy(pin.Path, Path.Combine(snapshots, Path.GetFileName(pin.Path)));
            string dll = Path.Combine(snapshots, "PathEngine_x64.dll"), ground = Path.Combine(snapshots, "ground.ncp"), tok = Path.Combine(pending, "ground.tok");
            string chunkLog = await Run(p.ChunkHelper.Path, pending, dll, ground, tok); File.WriteAllText(Path.Combine(pending, "chunk-native.log"), chunkLog);
            if (!chunkLog.Contains("PASS native selected-ground TOK")) throw new InvalidDataException("Native ground chunk proof absent.");
            var recipe = JsonSerializer.Deserialize<MapWorkshopService.Recipe>(File.ReadAllBytes(p.Recipe.Path), Strict) ?? throw new InvalidDataException("Null map recipe.");
            string Float(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            string nativeRoot = Path.Combine(pending, "native"); string exportLog = await Run(p.ExportHelper.Path, pending, tok, dll, nativeRoot, Float(recipe.Radius), Float(recipe.DeckHeight), Float(recipe.PillarRadius), resource.ToString(CultureInfo.InvariantCulture), label.ToString(CultureInfo.InvariantCulture),folder);
            File.WriteAllText(Path.Combine(pending, "export-native.log"), exportLog);
            if (!exportLog.Contains($"PASS original arena production Navi resource{resource} blocks1024 agents5")) throw new InvalidDataException("Full production navigation proof absent.");
            if (Inputs(p).Any(x => Seal(x.Path) != x)) throw new IOException("Navigation sources changed.");
            File.Copy(path, Path.Combine(pending, "plan.json"));
            var resources = Directory.GetFiles(pending, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(pending, x), sha256 = ContentSafety.Hash(x), size = new FileInfo(x).Length }).ToArray();
            bool firstCollisionCertified=exportLog.Split("first-collision-certified=true").Length==6;
            if(!firstCollisionCertified)throw new InvalidDataException("Native first collision proof required for all five radii.");
            var audit = new { profile = "assets-power-arena-navigation-candidate-v1", utc = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(path), inputs = Inputs(p), provisionalResourceId = resource, provisionalMapNameId = label, worldFolder=folder, nativeExportReload = true, taiwanAgentRadii = new[] { .3f, .5f, 1f, 2f, 3f }, pillarBlocked = true, bidirectionalRoutes = true, outsideDeckDestinationRejected = true, spectatorBalconyIsolated = true, firstCollisionBoundaryCertified = firstCollisionCertified, all1024BlockDefaults = "gameProp=0; private new-world label", worldMetadataPrepared = false, tablesPrepared = false, clientRenderingTested = false, gameplayTested = false, readyToInstall = false, resources };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit); Directory.Move(pending, output); pending = null; return new { output, audit };
        }
        finally { foreach (var s in locks) s.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
    private static async Task<string> Run(string helper, string cwd, params string[] args)
    {
        var start = new ProcessStartInfo(helper) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (string arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Navigation helper unavailable."); var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new IOException("Navigation helper deadline60s."); }
        string log = await stdout + await stderr; if (child.ExitCode != 0) throw new InvalidDataException("Native map navigation failed: " + log); return log;
    }
}
