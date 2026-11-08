using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Explicit same-sector clone of a plain portal, or binding an existing portal.
// Never installs, authors navigation or certifies placement height/rendering.
internal static class ScenePortalService
{
    private const string Profile = "taiwan14-native-existing-portal-bind-v1";
    private const string CloneProfile = "taiwan14-native-same-sector-portal-clone-v1";
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record CloneOptions(uint TriggerId, uint ObjectId, float DeltaX, float DeltaY, float DeltaZ);
    internal sealed record Plan(string Profile, Pin Gwp, Pin Field, uint FieldId, uint SectorId,
        uint TriggerId, uint NameIndex, string HelperSha256, CloneOptions? Placement = null);
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "scene-portal-binder.exe");
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c < 32 || c > 127)) throw new InvalidDataException("Native ASCII path required.");
        long bytes = new FileInfo(path).Length;
        if (bytes is < 1 or > 128L * 1024 * 1024) throw new InvalidDataException("Bounded native input required.");
        return new(path, ContentSafety.Hash(path), bytes);
    }
    private static void Validate(Plan p)
    {
        if ((p.Profile != Profile && p.Profile != CloneProfile) || p.Gwp == null || p.Field == null || p.FieldId >= 4096 || p.SectorId >= 4 ||
            p.TriggerId is 0 or uint.MaxValue || p.NameIndex is 0 or uint.MaxValue ||
            Path.GetFileName(p.Gwp.Path) != "#######.gwp" || Path.GetFileName(p.Field.Path) != "wfif" + p.FieldId ||
            Path.GetFileName(Path.GetDirectoryName(p.Field.Path)) != "wfif" + p.FieldId ||
            p.Gwp.Path.Equals(p.Field.Path, StringComparison.OrdinalIgnoreCase) ||
            new[] { p.Gwp, p.Field }.Any(x => !Path.IsPathFullyQualified(x.Path) || x.Bytes is < 1 or > 128L * 1024 * 1024 ||
                x.Sha256 == null || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit)) ||
            p.HelperSha256 == null || p.HelperSha256.Length != 64 || !p.HelperSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Sealed literal native portal binding required.");
        if (p.Profile == Profile && p.Placement != null || p.Profile == CloneProfile && p.Placement == null)
            throw new InvalidDataException("Clone options must match the explicit clone profile.");
        if (p.Placement is { } c && (c.TriggerId is < 60000000 or > 60009999 || c.ObjectId != c.TriggerId ||
            c.TriggerId == p.TriggerId || !float.IsFinite(c.DeltaX) || !float.IsFinite(c.DeltaY) || !float.IsFinite(c.DeltaZ) ||
            Math.Abs(c.DeltaX) > 32 || Math.Abs(c.DeltaY) > 8 || Math.Abs(c.DeltaZ) > 32 || Math.Abs(c.DeltaX) + Math.Abs(c.DeltaZ) < 4))
            throw new InvalidDataException("Reserved distinct clone identity and bounded explicit displacement required.");
    }
    internal static object CreatePlan(string gwp, string field, uint fieldId, uint sector, uint sid, uint name, string output)
    {
        var plan = new Plan(Profile, Seal(gwp), Seal(field), fieldId, sector, sid, name, Seal(Helper).Sha256);
        Validate(plan); ContentSafety.WriteJson(output, plan);
        return new { plan = Path.GetFullPath(output), readyToEnable = false };
    }
    internal static object CreateClonePlan(string gwp, string field, uint fieldId, uint sector, uint sid,
        uint name, uint newSid, uint newObject, float dx, float dy, float dz, string output)
    {
        var plan = new Plan(CloneProfile, Seal(gwp), Seal(field), fieldId, sector, sid, name,
            Seal(Helper).Sha256, new(newSid, newObject, dx, dy, dz));
        Validate(plan); ContentSafety.WriteJson(output, plan);
        return new { plan = Path.GetFullPath(output), placementHeightVerified = false, readyToEnable = false };
    }
    internal static Task<object> PreviewAsync(string plan) => RunAsync(plan, null);
    internal static Task<object> ApplyAsync(string plan, string output) => RunAsync(plan, output);
    private static async Task<object> RunAsync(string path, string? output)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Bounded binding plan required.");
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            byte[] bytes = File.ReadAllBytes(path); using var json = JsonDocument.Parse(bytes); TerrainGeometryService.NoDuplicateKeys(json.RootElement);
            var p = JsonSerializer.Deserialize<Plan>(bytes, Strict) ?? throw new InvalidDataException("Null binding plan."); Validate(p);
            foreach (var pin in new[] { p.Gwp, p.Field })
            {
                ContentSafety.NoLinks(pin.Path); locks.Add(new(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Seal(pin.Path) != pin) throw new InvalidDataException("Stale GWP/field seal.");
            }
            ContentSafety.NoLinks(Helper); locks.Add(new(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Seal(Helper).Sha256 != p.HelperSha256) throw new InvalidDataException("Stale portal helper seal.");
            if (output != null)
            {
                output = ContentSafety.SafeOutput(output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (new[] { path, p.Gwp.Path, p.Field.Path, Helper }.Any(x => x.Equals(output, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Portal candidate overlaps source.");
                pending = ContentSafety.FreshDirectory(output);
            }
            else { pending = Path.Combine(Path.GetTempPath(), "DboAssetsPortal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending); }
            string sources = Path.Combine(pending, "sources"); Directory.CreateDirectory(sources);
            string gwp = Path.Combine(sources, "#######.gwp"), field = Path.Combine(sources, "wfif" + p.FieldId);
            File.Copy(p.Gwp.Path, gwp); File.Copy(p.Field.Path, field);
            if (ContentSafety.Hash(gwp) != p.Gwp.Sha256 || ContentSafety.Hash(field) != p.Field.Sha256) throw new IOException("Portal snapshot mismatch.");
            string target = Path.Combine(pending, "wfif" + p.FieldId);
            var start = new ProcessStartInfo(Helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = pending, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { gwp, field, p.FieldId.ToString(), p.SectorId.ToString(), p.TriggerId.ToString(), p.NameIndex.ToString(), target }) start.ArgumentList.Add(arg);
            if (p.Placement is { } clone)
                foreach (string arg in new[] { clone.TriggerId.ToString(CultureInfo.InvariantCulture), clone.ObjectId.ToString(CultureInfo.InvariantCulture),
                    clone.DeltaX.ToString("R", CultureInfo.InvariantCulture), clone.DeltaY.ToString("R", CultureInfo.InvariantCulture), clone.DeltaZ.ToString("R", CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Portal native helper missing.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); throw new IOException("Portal native timeout."); }
            string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, "native.log"), log);
            if (child.ExitCode != 0 || !log.Contains("PASS native portal binding") || !log.Contains("preservedOtherBytes=true")) throw new InvalidDataException("Native portal bind failed: " + log);
            long targetBytes = new FileInfo(target).Length;
            if (Seal(p.Gwp.Path) != p.Gwp || Seal(p.Field.Path) != p.Field || Seal(Helper).Sha256 != p.HelperSha256 ||
                (p.Placement == null ? targetBytes != p.Field.Bytes : targetBytes <= p.Field.Bytes || targetBytes > p.Field.Bytes + 1024 * 1024) ||
                !log.Contains(p.Placement == null ? "cloned=0 " : "cloned=1 ")) throw new IOException("Portal sources/output changed.");
            var audit = new { profile = p.Profile, at = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(path), sourcePins = new[] { p.Gwp, p.Field }, helperSha256 = p.HelperSha256,
                p.FieldId, p.SectorId, sourceTriggerId = p.TriggerId, triggerId = p.Placement?.TriggerId ?? p.TriggerId, p.NameIndex,
                placement = p.Placement, targetSha256 = ContentSafety.Hash(target), bytes = targetBytes,
                nativeCompleteFieldEof = true, otherBytesPreserved = true, selectionEnabled = true, portalEnabled = true,
                placementCreated = p.Placement != null, terrainHeightWithinOneMetre = p.Placement != null,
                placementHeightVerified = false, objectCollisionsVerified = false,
                navigationGenerated = false, readyToEnable = false, runtimeModified = false, installed = false, published = false };
            if (output != null) { File.Copy(path, Path.Combine(pending, "plan.json")); ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit); Directory.Move(pending, output); pending = null; }
            return audit;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null) Directory.Delete(pending, true); }
    }
}
