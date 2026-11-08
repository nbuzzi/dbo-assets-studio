using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Explicit new Snake Way world. All template resources remain at their original
// paths; these outputs only occupy a newly reserved world folder.
internal static class StandaloneBspWorldService
{
    private const string Profile = "assets-snakeway-static-bsp-wrapper-v1";
    private const string PowerProfile = "assets-power-arena-static-bsp-wrapper-v1";
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, Pin Gwp, Pin Wfp, Pin EmptyField, Pin Bsp, Pin BspAudit, string HelperSha256, Pin? Identity = null);
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "gwp-resource-reader.exe");
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c > 127 || c < 32)) throw new InvalidDataException("Native ASCII paths required.");
        long size = new FileInfo(path).Length;
        if (size is < 1 or > 128L * 1024 * 1024) throw new InvalidDataException("Bounded world input required.");
        return new(path, ContentSafety.Hash(path), size);
    }
    private static void Validate(Plan p)
    {
        if(p.Identity!=null){if(p.Profile!=PowerProfile)throw new InvalidDataException("Identity applies only to authored original maps.");NewMapTableService.ReadIdentity(p.Identity.Path);}
        if ((p.Profile != Profile&&p.Profile!=PowerProfile) || p.Gwp.Sha256 != "6BB04087FE8865050AAC6FBE29608953BDA7E853ADB9CDC3DCD755E17436F952" || p.Gwp.Bytes != 250 ||
            p.EmptyField.Sha256 != "E2FC162ED9124452D23C85E81D60A0C228F414C3214A5DE635737E25FBD29AC1" || p.EmptyField.Bytes != 304 ||
            p.Wfp.Bytes != 443392 || Path.GetFileName(p.Gwp.Path) != "#######.gwp" || Path.GetFileName(p.Wfp.Path) != "wfp")
            throw new InvalidDataException("Reviewed Snake Way BSP template/empty field required.");
        using var audit = JsonDocument.Parse(File.ReadAllBytes(p.BspAudit.Path)); var a = audit.RootElement;
        if(p.Profile==PowerProfile){
            if(a.GetProperty("profile").GetString()!="assets-power-arena-candidate-v1"||!a.GetProperty("nativeDffBspRoundtrip").GetBoolean()||
                !a.GetProperty("resources").EnumerateArray().Any(x=>x.GetProperty("path").GetString()=="arena.bsp"&&x.GetProperty("sha256").GetString()==p.Bsp.Sha256))throw new InvalidDataException("Canonical original power arena BSP audit required.");return;
        }
        bool additive=a.GetProperty("profile").GetString()=="taiwan-native-static-clump-additive-bsp-v2";
        if ((a.GetProperty("profile").GetString() != "taiwan-native-static-clump-bsp-v1"&&!additive) ||
            a.GetProperty("bspSha256").GetString() != p.Bsp.Sha256 || (additive? a.GetProperty("originalSourceTriangles").GetInt32()!=5736||a.GetProperty("additionalTriangles").GetInt32() is <1 or >200||a.GetProperty("sourceTriangles").GetInt32()!=5736+a.GetProperty("additionalTriangles").GetInt32():a.GetProperty("sourceTriangles").GetInt32()!=5736) ||
            !a.GetProperty("allOrientedTrianglesPositionsUvPrelightsMaterialsPreserved").GetBoolean() ||
            !a.GetProperty("nativeBspStreamReadback").GetBoolean() || !a.GetProperty("realTextureBindings").GetBoolean() ||
            a.GetProperty("texturesSubstituted").GetBoolean())
            throw new InvalidDataException("Canonical fully preserved Snake Way BSP audit required.");
        var snapshots = a.GetProperty("snapshots").EnumerateArray().ToArray();
        if (!snapshots.Any(x => x.GetProperty("sha256").GetString() == "7CCD9380EA84D8C41889EA17149B2394EA0F4D9FFC2D838E186886897E900968"))
            throw new InvalidDataException("Audited original SnakeRun source required.");
    }
    internal static object CreatePlan(string gwp, string wfp, string emptyField, string bspDirectory, string output)
    {
        bspDirectory = Path.GetFullPath(bspDirectory); ContentSafety.NoLinks(bspDirectory);
        var p = new Plan(Profile, Seal(gwp), Seal(wfp), Seal(emptyField), Seal(Path.Combine(bspDirectory, "scene.bsp")),
            Seal(Path.Combine(bspDirectory, "audit.json")), Seal(Helper).Sha256);
        Validate(p); output = ContentSafety.SafeOutput(output);
        if (Inputs(p).Append(Seal(Helper)).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("World plan overlaps input.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Fresh world plan path required.");
        ContentSafety.WriteJson(output, p); return new { plan = output, folder = "dbo_assets_snakeway_001", resourceId = 6009, mapNameId = 600090010, readyToInstall = false };
    }
    internal static object CreatePowerPlan(string gwp,string wfp,string emptyField,string mapDirectory,string output,string? identityPath=null){
        mapDirectory=Path.GetFullPath(mapDirectory);ContentSafety.NoLinks(mapDirectory);
        var p=new Plan(PowerProfile,Seal(gwp),Seal(wfp),Seal(emptyField),Seal(Path.Combine(mapDirectory,"arena.bsp")),Seal(Path.Combine(mapDirectory,"audit.json")),Seal(Helper).Sha256,identityPath==null?null:Seal(identityPath));
        Validate(p);output=ContentSafety.SafeOutput(output);if(File.Exists(output)||Directory.Exists(output)||Inputs(p).Append(Seal(Helper)).Any(x=>x.Path.Equals(output,StringComparison.OrdinalIgnoreCase)))throw new IOException("Fresh power arena metadata plan required.");
        var id=p.Identity==null?null:NewMapTableService.ReadIdentity(p.Identity.Path);
        ContentSafety.WriteJson(output,p);return new{plan=output,folder=id?.Folder??"dbo_assets_power_arena_001",resourceId=id?.ResourceId??6010,mapNameId=id?.MapNameId??600100010,readyToInstall=false};
    }
    private static Pin[] Inputs(Plan p) => p.Identity==null?[p.Gwp, p.Wfp, p.EmptyField, p.Bsp, p.BspAudit]:[p.Gwp, p.Wfp, p.EmptyField, p.Bsp, p.BspAudit,p.Identity];
    internal static async Task<object> RunAsync(string planPath, string? output)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 16384) throw new InvalidDataException("Bounded world plan required.");
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(planPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            byte[] planBytes = File.ReadAllBytes(planPath); using var doc = JsonDocument.Parse(planBytes); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
            var p = JsonSerializer.Deserialize<Plan>(planBytes, Strict) ?? throw new InvalidDataException("Null world plan.");
            foreach (var pin in Inputs(p))
            {
                ContentSafety.NoLinks(pin.Path); locks.Add(new(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Seal(pin.Path) != pin) throw new IOException("Stale world input: " + pin.Path);
            }
            Validate(p); ContentSafety.NoLinks(Helper); locks.Add(new(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            bool power=p.Profile==PowerProfile;string folder=power?"dbo_assets_power_arena_001":"dbo_assets_snakeway_001";uint resource=power?6010u:6009u,mapName=power?600100010u:600090010u;
            if(p.Identity!=null){var id=NewMapTableService.ReadIdentity(p.Identity.Path);folder=id.Folder;resource=id.ResourceId;mapName=id.MapNameId;}
            if (Seal(Helper).Sha256 != p.HelperSha256) throw new IOException("Stale world helper.");
            if (output != null)
            {
                output = ContentSafety.SafeOutput(output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Inputs(p).Append(Seal(Helper)).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase) || x.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("World output overlaps source.");
                pending = ContentSafety.FreshDirectory(output);
            }
            else { pending = Path.Combine(Path.GetTempPath(), "AssetsSnakeWorld", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending); }
            string snapshots = Path.Combine(pending, "snapshots"); Directory.CreateDirectory(snapshots);
            string sg = Path.Combine(snapshots, "#######.gwp"), sw = Path.Combine(snapshots, "wfp"), sf = Path.Combine(snapshots, "empty-field.bin");
            File.Copy(p.Gwp.Path, sg); File.Copy(p.Wfp.Path, sw); File.Copy(p.EmptyField.Path, sf);
            string root = Path.Combine(pending, "world", folder); Directory.CreateDirectory(root);
            string targetGwp = Path.Combine(root, "#######.gwp"), targetWfp = Path.Combine(root, "wfp");
            var start = new ProcessStartInfo(Helper) { WorkingDirectory = pending, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { p.Identity!=null?"--standalone-authored-bsp":power?"--standalone-power-bsp":"--standalone-snake-bsp", sg, sw, sf, targetGwp, targetWfp }) start.ArgumentList.Add(arg);
            if(p.Identity!=null){start.ArgumentList.Add(folder);start.ArgumentList.Add(resource.ToString(System.Globalization.CultureInfo.InvariantCulture));start.ArgumentList.Add(mapName.ToString(System.Globalization.CultureInfo.InvariantCulture));}
            using var child = Process.Start(start) ?? throw new IOException("World helper unavailable.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); throw new IOException("World helper deadline45s."); }
            string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, "native.log"), log);
            if (child.ExitCode != 0 || !log.Contains($"PASS standalone BSP world resource{resource} map{mapName} fields64 blocks1024 nativeGWP250 WFP433 names-only emptyField304;"))
                throw new InvalidDataException("Native Snake Way world wrapper failed (" + child.ExitCode + "): " + log);
            byte[] before = File.ReadAllBytes(sg), after = File.ReadAllBytes(targetGwp);
            if (after.Length != 250 || !before.AsSpan(0, 178).SequenceEqual(after.AsSpan(0, 178))) throw new InvalidDataException("Unrelated GWP fields changed.");
            byte[] oldWfp = File.ReadAllBytes(sw), newWfp = File.ReadAllBytes(targetWfp);
            if (newWfp.Length != oldWfp.Length) throw new InvalidDataException("Block property length changed.");
            for (int i = 0; i < 1024; i++)
                if (!oldWfp.AsSpan(i * 433 + 64, 369).SequenceEqual(newWfp.AsSpan(i * 433 + 64, 369))) throw new InvalidDataException("Unrelated climate/game block bytes changed.");
            string targetBsp = Path.Combine(root, folder+".bsp"); File.Copy(p.Bsp.Path, targetBsp);
            for (int field = 0; field < 64; field++)
            { string dir = Path.Combine(root, "fields", "wfif" + field); Directory.CreateDirectory(dir); File.Copy(sf, Path.Combine(dir, "wfif" + field)); }
            if (Inputs(p).Any(x => Seal(x.Path) != x) || Seal(Helper).Sha256 != p.HelperSha256 || ContentSafety.Hash(targetBsp) != p.Bsp.Sha256)
                throw new IOException("World sources changed.");
            var resources = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(x => new { path = Path.GetRelativePath(pending, x), sha256 = ContentSafety.Hash(x), size = new FileInfo(x).Length }).ToArray();
            var audit = new { profile = p.Profile, status = "candidate_not_installed", utc = DateTimeOffset.UtcNow,
                planSha256 = ContentSafety.Hash(planPath), helperSha256 = p.HelperSha256, resourceId = resource, mapNameId = mapName,
                nativeGwpRoundtrip = true, nativeEmptyFieldRead = true, nativeBlockPropertiesRoundtrip = true,
                originalTemplateUnchanged = true, wfpChanges = "new-world map label only; all climate/game bytes preserved", resources,
                originalBspAuditSha256 = p.BspAudit.Sha256, navigationGenerated = false, tablesPrepared = false, renderingTested = false, worldTested = false, readyToInstall = false };
            if (output != null)
            {
                File.Copy(planPath, Path.Combine(pending, "plan.json")); ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
                Directory.Move(pending, output); pending = null; return new { output, resources = resources.Length, audit };
            }
            return new { resources = resources.Length, audit };
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
