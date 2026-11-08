using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal static class CharacterToonCatalog
{
    private const string Profile = "taiwan-native-character-toon-v1";
    private sealed record Pin(string Path, string Sha256);
    private sealed record Plan(int Version, string Profile, Pin Character, Pin Model, Pin ObjAudit,
        Pin Gradient, Pin GradientFingerprint, Pin FinalGradientFingerprint, Pin TexAudit,
        Pin BaselineManifest, string ModelKey, string HelperSha256);
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public static async Task<object> InspectAsync(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1048576) throw new InvalidDataException("Bounded Toon plan required.");
        string planHash = ContentSafety.Hash(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); TerrainGeometryService.NoDuplicateKeys(document.RootElement);
        var plan = JsonSerializer.Deserialize<Plan>(document.RootElement, Strict) ?? throw new InvalidDataException("Toon plan required.");
        if (plan.Version != 1 || plan.Profile != Profile || plan.ModelKey is not { Length: > 5 and < 128 } ||
            !(plan.ModelKey.StartsWith("M_HF_", StringComparison.Ordinal) || plan.ModelKey.StartsWith("N_HF_", StringComparison.Ordinal)) ||
            !plan.ModelKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidDataException("Explicit own-character Toon profile required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] Snapshot(Pin pin, string extension, long limit = 134217728)
        {
            if (pin is null) throw new InvalidDataException("Missing Toon input.");
            string input = Path.GetFullPath(pin.Path); ContentSafety.NoLinks(input);
            long size = new FileInfo(input).Length;
            if (!input.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || size < 1 || size > limit || ContentSafety.Hash(input) != pin.Sha256)
                throw new InvalidDataException("Stale or unbounded Toon input.");
            pins[input] = pin.Sha256; return File.ReadAllBytes(input);
        }
        JsonDocument Metadata(Pin pin)
        {
            var result = JsonDocument.Parse(Snapshot(pin, ".json", 8 * 1024 * 1024));
            TerrainGeometryService.NoDuplicateKeys(result.RootElement); return result;
        }
        _ = Snapshot(plan.Character, ".rdf"); byte[] model = Snapshot(plan.Model, ".dff"), gradient = Snapshot(plan.Gradient, ".png", 1048576);
        using var obj = Metadata(plan.ObjAudit); using var tex = Metadata(plan.TexAudit);
        using var baseline = Metadata(plan.BaselineManifest); using var original = Metadata(plan.GradientFingerprint);
        using var final = Metadata(plan.FinalGradientFingerprint);
        var catalog = JsonSerializer.SerializeToElement(CharacterPropertyCatalog.Inspect(plan.Character.Path), ContentSafety.Json);
        var rows = catalog.GetProperty("records").EnumerateArray().Where(r => r.GetProperty("name").GetString() == plan.ModelKey).ToArray();
        if (rows.Length != 1 || rows[0].GetProperty("mesh").GetString() != Path.GetFileName(plan.Model.Path))
            throw new InvalidDataException("Exact character model identity required.");
        var row = rows[0];
        string logical = ContentSafety.ResourcePath(row.GetProperty("meshPath").GetString()! + row.GetProperty("mesh").GetString());
        if (obj.RootElement.GetProperty("format").GetString() != "taiwan" || obj.RootElement.GetProperty("prefix").GetString() != "obj" ||
            tex.RootElement.GetProperty("format").GetString() != "taiwan" || tex.RootElement.GetProperty("prefix").GetString() != "tex")
            throw new InvalidDataException("Exact final Taiwan OBJ/TEX audits required.");
        var resources = obj.RootElement.GetProperty("resources").EnumerateArray().Where(r => ContentSafety.ResourcePath(r.GetProperty("path").GetString()!).Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
        var operations = obj.RootElement.GetProperty("plan").GetProperty("operations").EnumerateArray().Where(r => ContentSafety.ResourcePath(r.GetProperty("path").GetString()!).Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (resources.Length != 1 || operations.Length != 1 || resources[0].GetProperty("sha256").GetString() != plan.Model.Sha256 ||
            operations[0].GetProperty("sha256").GetString() != plan.Model.Sha256 ||
            !Path.GetFullPath(operations[0].GetProperty("file").GetString()!).Equals(Path.GetFullPath(plan.Model.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model must match packed descriptors/operation.");
        void ValidateGradient(JsonElement fingerprint, string indexHash)
        {
            if (fingerprint.GetProperty("ok").GetBoolean() != true) throw new InvalidDataException("Canonical gradient fingerprint required.");
            var value = fingerprint.GetProperty("result"); var entries = value.GetProperty("resources").EnumerateArray().ToArray();
            if (value.GetProperty("schema").GetString() != "dbo-pak-resource-fingerprints-v1" || value.GetProperty("format").GetString() != "taiwan" ||
                value.GetProperty("indexHash").GetString() != indexHash || !value.GetProperty("readOnly").GetBoolean() || entries.Length != 1 ||
                ContentSafety.ResourcePath(entries[0].GetProperty("path").GetString()!) != "texture\\toon\\smooth.png" ||
                entries[0].GetProperty("sha256").GetString() != plan.Gradient.Sha256 || entries[0].GetProperty("size").GetInt64() != gradient.Length)
                throw new InvalidDataException("Baseline/final smooth PNG must match exact canonical fingerprints.");
        }
        ValidateGradient(original.RootElement, baseline.RootElement.GetProperty("files").GetProperty("pack/tex.pak").GetProperty("sha256").GetString()!.ToUpperInvariant());
        ValidateGradient(final.RootElement, tex.RootElement.GetProperty("index").GetProperty("sha256").GetString()!);
        string helper = Path.Combine(AppContext.BaseDirectory, "character-toon-reader.exe"); ContentSafety.NoLinks(helper);
        if (ContentSafety.Hash(helper) != plan.HelperSha256) throw new InvalidDataException("Stale native Toon observer.");
        string work = Path.Combine(Path.GetTempPath(), "DboAssetsCharacterToon", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            File.WriteAllBytes(Path.Combine(work, "model.dff"), model); File.WriteAllBytes(Path.Combine(work, "smooth.png"), gradient);
            var start = new ProcessStartInfo(helper) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("model.dff"); start.ArgumentList.Add("smooth.png");
            using var process = Process.Start(start) ?? throw new IOException("Cannot start native Toon observer.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new IOException("Native Toon observer timed out."); }
            if (process.ExitCode != 0) throw new InvalidDataException($"Native Toon observer rejected input (exit0x{unchecked((uint)process.ExitCode):X8}): " + stderr.Result);
            using var result = JsonDocument.Parse(stdout.Result); var native = result.RootElement;
            if (native.GetProperty("profile").GetString() != Profile || !native.GetProperty("nativeDffEOF").GetBoolean() ||
                !native.GetProperty("productionToonSetupCalled").GetBoolean() || native.GetProperty("geometryGenerated").GetBoolean() ||
                native.GetProperty("renderingTested").GetBoolean() || native.GetProperty("worldTested").GetBoolean())
                throw new InvalidDataException("Native metadata/setup-only contract required.");
            if (ContentSafety.Hash(path) != planHash || ContentSafety.Hash(helper) != plan.HelperSha256 || pins.Any(p => ContentSafety.Hash(p.Key) != p.Value))
                throw new IOException("Toon plan/helper/input changed during observation.");
            return new { profile = Profile, planSha256 = planHash, helperSha256 = plan.HelperSha256, inputPins = pins,
                model = plan.ModelKey, characterFlags = row.GetProperty("flags").GetUInt32(), native = native.Clone(),
                geometryChanged = false, animationChanged = false, texturesSubstituted = true, renderingTested = false, worldTested = false, installed = false };
        }
        finally { Directory.Delete(work, true); }
    }
}
