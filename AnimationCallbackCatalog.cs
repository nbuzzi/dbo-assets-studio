using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal static class AnimationCallbackCatalog
{
    private const string Profile = "taiwan-native-animation-callbacks-v1";
    private sealed record Pin(string Path, string Sha256);
    private sealed record Plan(int Version, string Profile, Pin Character, Pin Model, Pin Animation,
        Pin ObjAudit, string ModelKey, uint AnimationKey, string HelperSha256);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public static async Task<object> InspectAsync(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1048576) throw new InvalidDataException("Bounded callback plan required.");
        string planHash = ContentSafety.Hash(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); TerrainGeometryService.NoDuplicateKeys(document.RootElement);
        var plan = JsonSerializer.Deserialize<Plan>(document.RootElement, Strict) ?? throw new InvalidDataException("Callback plan required.");
        if (plan.Version != 1 || plan.Profile != Profile || plan.AnimationKey >= 65535 ||
            plan.ModelKey is not { Length: > 5 and < 128 } ||
            !(plan.ModelKey.StartsWith("M_HF_", StringComparison.Ordinal) || plan.ModelKey.StartsWith("N_HF_", StringComparison.Ordinal)) ||
            !plan.ModelKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidDataException("Explicit own-character callback profile required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] Snapshot(Pin pin, string extension)
        {
            if (pin is null) throw new InvalidDataException("Missing callback input.");
            string input = Path.GetFullPath(pin.Path); ContentSafety.NoLinks(input);
            if (!input.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || new FileInfo(input).Length is < 1 or > 134217728 ||
                ContentSafety.Hash(input) != pin.Sha256) throw new InvalidDataException("Stale or unbounded callback input.");
            pins[input] = pin.Sha256; return File.ReadAllBytes(input);
        }
        byte[] character = Snapshot(plan.Character, ".rdf"), model = Snapshot(plan.Model, ".dff"), animation = Snapshot(plan.Animation, ".anm");
        byte[] auditBytes = Snapshot(plan.ObjAudit, ".json");
        if (auditBytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Bounded canonical OBJ audit required.");
        using var audit = JsonDocument.Parse(auditBytes); TerrainGeometryService.NoDuplicateKeys(audit.RootElement);
        var catalog = JsonSerializer.SerializeToElement(CharacterPropertyCatalog.Inspect(plan.Character.Path), ContentSafety.Json);
        var rows = catalog.GetProperty("records").EnumerateArray().Where(x => x.GetProperty("name").GetString() == plan.ModelKey).ToArray();
        if (rows.Length != 1) throw new InvalidDataException("Exact own native character required.");
        var row = rows[0];
        var animations = row.GetProperty("animations").EnumerateArray().Where(x => x.GetProperty("key").GetUInt32() == plan.AnimationKey).ToArray();
        if (animations.Length != 1 || row.GetProperty("mesh").GetString() != Path.GetFileName(plan.Model.Path) ||
            !animations[0].GetProperty("file").GetString()!.Equals(Path.GetFileName(plan.Animation.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Exact character mesh/key/animation identity required.");
        if (audit.RootElement.GetProperty("format").GetString() != "taiwan" || audit.RootElement.GetProperty("prefix").GetString() != "obj")
            throw new InvalidDataException("Canonical final Taiwan OBJ audit required.");
        foreach (var pair in new[] { (plan.Model, row.GetProperty("meshPath").GetString()! + row.GetProperty("mesh").GetString()),
            (plan.Animation, row.GetProperty("animationPath").GetString()! + animations[0].GetProperty("file").GetString()) })
        {
            string logical = ContentSafety.ResourcePath(pair.Item2);
            var resources = audit.RootElement.GetProperty("resources").EnumerateArray().Where(r => ContentSafety.ResourcePath(r.GetProperty("path").GetString()!).Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
            var operations = audit.RootElement.GetProperty("plan").GetProperty("operations").EnumerateArray().Where(r => ContentSafety.ResourcePath(r.GetProperty("path").GetString()!).Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (resources.Length != 1 || operations.Length != 1 || resources[0].GetProperty("sha256").GetString() != pair.Item1.Sha256 ||
                operations[0].GetProperty("sha256").GetString() != pair.Item1.Sha256 ||
                !Path.GetFullPath(operations[0].GetProperty("file").GetString()!).Equals(Path.GetFullPath(pair.Item1.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Callback assets must equal final packed descriptors and operations.");
        }
        string helper = Path.Combine(AppContext.BaseDirectory, "character-animation-callback-reader.exe"); ContentSafety.NoLinks(helper);
        if (ContentSafety.Hash(helper) != plan.HelperSha256) throw new InvalidDataException("Stale native callback observer.");
        string work = Path.Combine(Path.GetTempPath(), "DboAssetsAnimationCallbacks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            File.WriteAllBytes(Path.Combine(work, "characterproperty.rdf"), character);
            File.WriteAllBytes(Path.Combine(work, "model.dff"), model); File.WriteAllBytes(Path.Combine(work, "animation.anm"), animation);
            var start = new ProcessStartInfo(helper) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "characterproperty.rdf", "model.dff", "animation.anm", plan.ModelKey, plan.AnimationKey.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start native callback observer.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new IOException("Native callback observer timed out."); }
            if (process.ExitCode != 0) throw new InvalidDataException("Native callback observer rejected input: " + stderr.Result);
            using var result = JsonDocument.Parse(stdout.Result); var native = result.RootElement;
            if (native.GetProperty("profile").GetString() != Profile || !native.GetProperty("nativeCharacterEOF").GetBoolean() ||
                native.GetProperty("animationKey").GetUInt32() != plan.AnimationKey || native.GetProperty("cases").GetArrayLength() != 6 ||
                !native.GetProperty("productionLayerAndChannel").GetBoolean() || native.GetProperty("effectConsumersInvoked").GetBoolean() ||
                native.GetProperty("combatConsumersInvoked").GetBoolean()) throw new InvalidDataException("Production callback-only contract required.");
            if (ContentSafety.Hash(path) != planHash || ContentSafety.Hash(helper) != plan.HelperSha256 || pins.Any(p => ContentSafety.Hash(p.Key) != p.Value))
                throw new IOException("Callback plan/helper/source changed during observation.");
            return new { profile = Profile, planSha256 = planHash, helperSha256 = plan.HelperSha256, inputPins = pins,
                model = plan.ModelKey, animationKey = plan.AnimationKey, native = native.Clone(), callbacksDispatched = true,
                effectConsumersInvoked = false, combatConsumersInvoked = false, timingChanged = false, renderingTested = false, worldTested = false, installed = false };
        }
        finally { Directory.Delete(work, true); }
    }
}
