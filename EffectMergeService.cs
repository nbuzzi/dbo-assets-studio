using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Native version104 boundaries; append exact mesh/texture definitions. No guessed
// particle serializer, registry replacement, source writes, install or publish.
internal static partial class EffectMergeService
{
    private const int MaxBytes = 128 * 1024 * 1024;
    private sealed record Pin(string Path, string Sha256);
    private sealed record Mesh(string Name, Pin Bindings);
    private sealed record Texture(string Name, Pin Source);
    private sealed record Plan(int Version, string EffectHelperSha256, string MediaHelperSha256,
        Pin Target, Pin Companion, Pin Source, List<string> Keys, List<Mesh> Meshes, List<Texture>? Textures = null);
    private sealed record Row(int Offset, int Length, string Name);
    private sealed record Dependency(string Effect, string Kind, string Value);
    private sealed record MeshSystem(string Effect, int SystemIndex, string Mesh, string Animation, string Uv, string Spline);
    private sealed record NativeSystemMetadata(string Effect, int SystemIndex, int SystemType, string Name, bool SubSystem,
        uint EmitterFlags, int PathFlags, bool Eternity, float StartTime, float LifeTime, bool FadePresent, bool FadeFollow,
        string FadeTarget, string Mesh, string Animation, string Uv, string ParticleChild, string BeamHead);
    private sealed record SplinePlayback(string Name, int Controls, int Curves, float Duration, int Samples, bool MatrixChanged);
    private sealed record SplineObservation(string Name, int Controls, int Curves, string FirstTime, string LastTime,
        int DuplicateTimes, int DecreasingTimes, int NonFiniteValues, int InvalidQuaternions);
    private sealed record SplinePlan(int Version, string EffectHelperSha256, Pin Source, List<string> Splines);
    private sealed record ResourcePlan(int Version, string EffectHelperSha256, Pin Source, List<string> Keys);
    private sealed record ResourceField(string Effect, int SystemIndex, string Kind, int Slot, int Offset, int Bytes, string ValueHex);
    private sealed record Catalog(byte[] Raw, int CountOffset, List<Row> Splines, List<Row> Effects, List<Dependency> Dependencies,
        List<MeshSystem> MeshSystems, List<SplinePlayback> SplinePlayback, List<ResourceField> ResourceFields,
        List<SplineObservation> SplineObservations, int ResourceFieldVersion, List<NativeSystemMetadata> SystemMetadata);
    private sealed record Ready(Plan Plan, Catalog Target, Catalog Source, List<Row> Added,
        Dictionary<string, string> Pins, List<object> Bindings, byte[] Bytes);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Resolve(string path, string parent) => Path.GetFullPath(path, parent);
    private static int Integer(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    private static uint UnsignedInteger(string text) => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string SerializedResourceValue(byte[] bytes, ResourceField field)
    {
        bool fixedSpline = field.Kind == "spline";
        if (!fixedSpline && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(field.Offset, 4)) != field.Bytes)
            throw new InvalidDataException("Native resource-field length prefix differs.");
        var body = bytes.AsSpan(field.Offset + (fixedSpline ? 0 : 4), field.Bytes);
        int nul = body.IndexOf((byte)0);
        if (field.Bytes != 0 && nul < 0) throw new InvalidDataException("Native resource-field NUL missing.");
        var logical = field.Bytes == 0 ? ReadOnlySpan<byte>.Empty : body[..nul];
        if (!logical.SequenceEqual(Convert.FromHexString(field.ValueHex)))
            throw new InvalidDataException("Native resource-field logical value differs from serialized bytes.");
        return Encoding.Latin1.GetString(logical);
    }
    private static void Fields(JsonElement item, params string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Effect plan object required.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject())
            if (!allowed.Contains(field.Name) || !names.Add(field.Name)) throw new InvalidDataException("Unknown or duplicate effect plan field.");
    }
    private static byte[] Snapshot(Pin pin, Dictionary<string, string> pins)
    {
        ContentSafety.NoLinks(pin.Path);
        if (pin.Sha256 is null || pin.Sha256.Length != 64 || !pin.Sha256.All(Uri.IsHexDigit) || new FileInfo(pin.Path).Length is < 1 or > MaxBytes)
            throw new InvalidDataException("Pinned file/size invalid.");
        byte[] bytes = File.ReadAllBytes(pin.Path);
        if (!Equal(Hash(bytes), pin.Sha256) || !Equal(ContentSafety.Hash(pin.Path), pin.Sha256)) throw new IOException("Pinned source changed.");
        if (pins.TryGetValue(pin.Path, out string? previous) && !Equal(previous, pin.Sha256)) throw new IOException("Conflicting source pins.");
        pins[pin.Path] = Hash(bytes); return bytes;
    }
    private static async Task<Catalog> ReadNativeAsync(byte[] bytes, bool meshSystems = false, List<string>? splinePlayback = null, bool resourceFields = false,
        List<string>? splineObservations = null, bool observedOrderPlayback = false, List<string>? systemMetadata = null,
        bool literalSplinePlayback = false)
    {
        bool readMeshSystems = meshSystems || systemMetadata is { Count: > 0 };
        if (bytes.Length is < 18 or > MaxBytes || !bytes.AsSpan(0, 6).SequenceEqual("effect"u8) || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4)) != 104)
            throw new InvalidDataException("Only native EFF version104 supported.");
        string helper = Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"); ContentSafety.NoLinks(helper);
        string helperHash = ContentSafety.Hash(helper);
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsStudio", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        try
        {
            string input = Path.Combine(cache, "input.eff"); File.WriteAllBytes(input, bytes);
            var start = new ProcessStartInfo(helper) { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (systemMetadata is { Count: > 0 }) { start.ArgumentList.Add("--system-metadata"); start.ArgumentList.Add(string.Join(',', systemMetadata)); }
            else if (meshSystems) start.ArgumentList.Add("--mesh-systems");
            if (resourceFields) start.ArgumentList.Add("--resource-fields");
            if (splinePlayback is not null) { start.ArgumentList.Add(literalSplinePlayback ? "--spline-playback-literal" : observedOrderPlayback ? "--spline-playback-observed-order" : "--spline-playback"); start.ArgumentList.Add(string.Join(',', splinePlayback)); }
            if (splineObservations is not null) { start.ArgumentList.Add("--spline-observations"); start.ArgumentList.Add(string.Join(',', splineObservations)); }
            start.ArgumentList.Add(input);
            List<string>? observationSelection = observedOrderPlayback ? splinePlayback : splineObservations;
            using var process = Process.Start(start) ?? throw new IOException("Native effect reader unavailable.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Native effect reader timeout45s."); }
            string text = await stdout, diagnostic = await stderr;
            if (process.ExitCode != 0 || text.Length > 64 * 1024 * 1024 || !diagnostic.Contains("PASS native effect reader") || ContentSafety.Hash(helper) != helperHash)
                throw new IOException("Native effect reader rejected input or helper changed.");
            var splines = new List<Row>(); var effects = new List<Row>(); var dependencies = new List<Dependency>();
            var associations = new List<MeshSystem>();
            var playback = new List<SplinePlayback>();
            var fields = new List<ResourceField>();
            var observations = new List<SplineObservation>(); int observationHeaders = 0;
            var metadata = new List<NativeSystemMetadata>(); int metadataHeaders = 0;
            int countOffset = -1, count = -1, headers = 0, associationHeaders = 0, playbackHeaders = 0, fieldHeaders = 0, fieldVersion = 0;
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] columns = line.TrimEnd('\r').Split('\t');
                if (columns.Length == 2 && columns[0] == "H" && columns[1] == "104") { headers++; continue; }
                if (readMeshSystems && columns.Length == 2 && columns[0] == "B" && columns[1] == "1") { associationHeaders++; continue; }
                if (splinePlayback is not null && columns.Length == 2 && columns[0] == "P" && columns[1] == (observedOrderPlayback ? "2" : "1")) { playbackHeaders++; continue; }
                if (observationSelection is not null && columns.Length == 2 && columns[0] == "O" && columns[1] == "1") { observationHeaders++; continue; }
                if (observationSelection is not null && columns.Length == 10 && columns[0] == "O")
                { observations.Add(new(columns[1], Integer(columns[2]), Integer(columns[3]), columns[4], columns[5],
                    Integer(columns[6]), Integer(columns[7]), Integer(columns[8]), Integer(columns[9]))); continue; }
                if (resourceFields && columns.Length == 2 && columns[0] == "R" && columns[1] is "1" or "2") { fieldHeaders++; fieldVersion = Integer(columns[1]); continue; }
                if (resourceFields && columns.Length == 8 && columns[0] == "F")
                { fields.Add(new(columns[1], Integer(columns[2]), columns[3], int.Parse(columns[4], NumberStyles.Integer, CultureInfo.InvariantCulture), Integer(columns[5]), Integer(columns[6]), columns[7])); continue; }
                if (splinePlayback is not null && columns.Length == 7 && columns[0] == "V" && columns[6] is "0" or "1")
                { playback.Add(new(columns[1], Integer(columns[2]), Integer(columns[3]), float.Parse(columns[4], CultureInfo.InvariantCulture), Integer(columns[5]), columns[6] == "1")); continue; }
                if (columns.Length == 3 && columns[0] == "C" && countOffset == -1) { countOffset = Integer(columns[1]); count = Integer(columns[2]); continue; }
                if (columns.Length == 4 && columns[0] is "S" or "E")
                { var row = new Row(Integer(columns[1]), Integer(columns[2]), columns[3]); (columns[0] == "S" ? splines : effects).Add(row); continue; }
                if (columns.Length == 4 && columns[0] == "D") { dependencies.Add(new(columns[1], columns[2], columns[3])); continue; }
                if (readMeshSystems && columns.Length == 7 && columns[0] == "M")
                { associations.Add(new(columns[1], Integer(columns[2]), columns[3], columns[4], columns[5], columns[6])); continue; }
                if (systemMetadata is { Count: > 0 } && columns.Length == 2 && columns[0] == "Y" && columns[1] == "1") { metadataHeaders++; continue; }
                if (systemMetadata is { Count: > 0 } && columns.Length == 19 && columns[0] == "Y")
                { metadata.Add(new(columns[1], Integer(columns[2]), Integer(columns[3]), columns[4], Integer(columns[5]) != 0,
                    UnsignedInteger(columns[6]), Integer(columns[7]), Integer(columns[8]) != 0,
                    float.Parse(columns[9], CultureInfo.InvariantCulture), float.Parse(columns[10], CultureInfo.InvariantCulture),
                    Integer(columns[11]) != 0, Integer(columns[12]) != 0,
                    columns[13], columns[14], columns[15], columns[16], columns[17], columns[18])); continue; }
                throw new InvalidDataException("Unexpected native effect index record.");
            }
            if (headers != 1 || countOffset < 14 || countOffset > bytes.Length - 4 || count is < 0 or > 100000 || effects.Count != count ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(countOffset, 4)) != count ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4)) != splines.Count) throw new InvalidDataException("Native effect boundary count invalid.");
            int cursor = 14;
            void Check(Row row)
            {
                if (row.Offset != cursor || row.Length < 5 || row.Length > bytes.Length - cursor) throw new InvalidDataException("Noncontiguous native effect index.");
                int n = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
                if (n is < 1 or > 255 || n > row.Length - 4) throw new InvalidDataException("Native effect name size invalid.");
                var serializedName = bytes.AsSpan(cursor + 4, n);
                if (serializedName[^1] == 0) serializedName = serializedName[..^1];
                if (!serializedName.SequenceEqual(Encoding.UTF8.GetBytes(row.Name)))
                    throw new InvalidDataException("Native effect name/boundary mismatch.");
                cursor += row.Length;
            }
            foreach (var row in splines) Check(row);
            if (cursor != countOffset) throw new InvalidDataException("Native spline boundary mismatch."); cursor += 4;
            foreach (var row in effects) Check(row);
            if (cursor != bytes.Length || dependencies.Any(d => !effects.Any(e => e.Name == d.Effect))) throw new InvalidDataException("Native effect EOF/reference mismatch.");
            if (associationHeaders != (readMeshSystems ? 1 : 0) ||
                associations.Any(m => m.SystemIndex is < 0 or > 4095 || !effects.Any(e => e.Name == m.Effect)) ||
                associations.Select(m => (m.Effect, m.SystemIndex)).Distinct().Count() != associations.Count)
                throw new InvalidDataException("Native per-system association contract invalid.");
            if (metadataHeaders != (systemMetadata is { Count: > 0 } ? 1 : 0) ||
                metadata.Select(m => (m.Effect, m.SystemIndex)).Distinct().Count() != metadata.Count ||
                metadata.Any(m => !systemMetadata!.Contains(m.Effect, StringComparer.Ordinal) || m.SystemIndex is < 0 or > 4095 || m.Name.Length == 0 ||
                    !float.IsFinite(m.StartTime) || !float.IsFinite(m.LifeTime) || m.StartTime < 0 || m.LifeTime < 0) ||
                (systemMetadata is { Count: > 0 } && !systemMetadata.All(k => metadata.Any(m => m.Effect == k))))
                throw new InvalidDataException("Native selected system metadata contract invalid.");
            foreach (var m in associations)
                foreach (var (kind, value) in new[] { ("mesh", m.Mesh), ("animation", m.Animation), ("uv", m.Uv), ("spline", m.Spline) })
                    if (value.Length != 0 && !dependencies.Any(d => d.Effect == m.Effect && d.Kind == kind && d.Value == value))
                        throw new InvalidDataException("Native mesh-system association differs from dependency catalog.");
            if (playbackHeaders != (splinePlayback is null ? 0 : 1) ||
                playback.Any(p => p.Controls is < 4 or > 4096 || p.Curves is < 2 or > 1000000 || !float.IsFinite(p.Duration) || p.Duration <= 0 ||
                    (observedOrderPlayback ? p.Curves > 2048 || p.Samples < 164 || p.Samples > (41 + 3 * p.Curves) * 4 || p.Samples % 4 != 0 : p.Samples != 164) || splines.Count(s => s.Name == p.Name) != 1) ||
                (splinePlayback is not null && (playback.Count != splinePlayback.Count ||
                    !playback.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(splinePlayback))))
                throw new InvalidDataException("Native spline playback contract invalid.");
            if (fieldHeaders != (resourceFields ? 1 : 0)) throw new InvalidDataException("Native resource-field contract marker invalid.");
            if (observationHeaders != (observationSelection is null ? 0 : 1) ||
                observations.Any(o => o.Controls is < 4 or > 4096 || o.Curves is < 1 or > 1000000 || splines.Count(s => s.Name == o.Name) != 1) ||
                (observationSelection is not null && (observations.Count != observationSelection.Count ||
                    !observations.Select(o => o.Name).ToHashSet(StringComparer.Ordinal).SetEquals(observationSelection))))
                throw new InvalidDataException("Native spline observation contract invalid.");
            if (resourceFields)
            {
                if (effects.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != effects.Count || fields.Count > 1000000 ||
                    fields.Select(f => (f.Effect, f.SystemIndex, f.Kind, f.Slot)).Distinct().Count() != fields.Count)
                    throw new InvalidDataException("Ambiguous native resource-field identities.");
                var rows = effects.ToDictionary(e => e.Name, StringComparer.Ordinal);
                var references = dependencies.Select(d => (d.Effect, d.Kind, d.Value)).ToHashSet();
                var tracedReferences = new HashSet<(string Effect, string Kind, string Value)>();
                int previousEnd = 0;
                foreach (var field in fields.OrderBy(f => f.Offset))
                {
                    if (!rows.TryGetValue(field.Effect, out var row) || field.SystemIndex is < 0 or > 4095 ||
                        field.Kind is not ("mesh" or "animation" or "uv" or "texture" or "subsystem" or "spline") ||
                        (field.Kind == "spline" && field.Bytes != 260) ||
                        field.Slot < -1 || field.Slot > 99999 || (field.Kind != "texture" && field.Slot != -1) ||
                        field.Bytes is < 0 or > 512 || field.Offset < row.Offset + 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(row.Offset, 4)) || field.Offset > row.Offset + row.Length - 4 ||
                        field.Bytes > row.Offset + row.Length - field.Offset - (field.Kind == "spline" ? 0 : 4) || field.Offset < previousEnd ||
                        field.ValueHex.Length > 1022 || field.ValueHex.Length % 2 != 0 || !field.ValueHex.All(Uri.IsHexDigit))
                        throw new InvalidDataException("Native resource-field span/identity invalid.");
                    string value = SerializedResourceValue(bytes, field);
                    previousEnd = field.Offset + (field.Kind == "spline" ? 0 : 4) + field.Bytes;
                    if (value.Length > 0)
                    {
                        string kind = field.Kind == "subsystem" ? "effect" : field.Kind;
                        var reference = (field.Effect, kind, value);
                        if (!references.Contains(reference)) throw new InvalidDataException("Native resource field differs from dependency catalog.");
                        tracedReferences.Add(reference);
                    }
                }
                if (references.Any(r => ((r.Kind is "mesh" or "animation" or "uv" or "texture" or "effect") || (fieldVersion == 2 && r.Kind == "spline")) && !tracedReferences.Contains(r)))
                    throw new InvalidDataException("Native dependency lacks an exact resource field.");
                foreach (var system in fields.GroupBy(f => (f.Effect, f.SystemIndex)))
                {
                    var meshFields = system.Where(f => f.Kind is "mesh" or "animation" or "uv").Select(f => f.Kind).ToHashSet();
                    if (meshFields.Count != 0 && !meshFields.SetEquals(new[] { "mesh", "animation", "uv" }))
                        throw new InvalidDataException("Incomplete native mesh string fields.");
                    var frames = system.Where(f => f.Kind == "texture" && f.Slot >= 0).Select(f => f.Slot).Order().ToArray();
                    if (!frames.SequenceEqual(Enumerable.Range(0, frames.Length)))
                        throw new InvalidDataException("Noncontiguous native multi-texture slots.");
                }
            }
            return new(bytes, countOffset, splines, effects, dependencies, associations, playback, fields, observations, fieldVersion, metadata);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
    public static async Task<object> InspectAsync(string path)
    {
        path = Path.GetFullPath(path); var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalog = await ReadNativeAsync(Snapshot(new(path, ContentSafety.Hash(path)), pins), meshSystems: true);
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Source changed during inspection.");
        return new { source = path, sourceSha256 = pins[path], version = 104, nativeReaderPass = true, countOffset = catalog.CountOffset,
            splines = catalog.Splines, effects = catalog.Effects, dependencies = catalog.Dependencies,
            meshSystems = catalog.MeshSystems, meshSystemAssociationVersion = 1,
            effectHelperSha256 = ContentSafety.Hash(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe")), renderingTested = false, worldTested = false };
    }
    public static async Task<object> InspectSplinesAsync(string path, bool observations = false, bool observedOrder = false)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Spline binding plan maximum1MiB.");
        byte[] raw = File.ReadAllBytes(path); using var json = JsonDocument.Parse(raw);
        Fields(json.RootElement, "version", "effectHelperSha256", "source", "splines");
        Fields(json.RootElement.GetProperty("source"), "path", "sha256");
        var plan = JsonSerializer.Deserialize<SplinePlan>(raw, Options) ?? throw new InvalidDataException("Spline binding plan required.");
        if (plan.Version != 1 || plan.Source is null || plan.Splines is null || plan.Splines.Count is < 1 or > 64 ||
            plan.Splines.Distinct(StringComparer.Ordinal).Count() != plan.Splines.Count ||
            plan.Splines.Any(n => n is null || !Regex.IsMatch(n, "^[A-Za-z0-9_]{1,128}$")))
            throw new InvalidDataException("Select1..64 unique literal ASCII spline names, version1.");
        string source = Resolve(plan.Source.Path, Path.GetDirectoryName(path)!);
        if (Path.GetExtension(source).ToLowerInvariant() != ".eff") throw new InvalidDataException("Native EFF source required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Hash(raw) };
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"), plan.EffectHelperSha256), pins);
        var catalog = await ReadNativeAsync(Snapshot(plan.Source with { Path = source }, pins),
            splinePlayback: observations ? null : plan.Splines, splineObservations: observations ? plan.Splines : null,
            observedOrderPlayback: observedOrder);
        if (observations)
        {
            if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Spline/helper/plan changed during observation.");
            return new { plan = path, inputPins = pins, version = 104, selected = catalog.SplineObservations,
                nativeReaderPass = true, nativeSplinePlayback = false, renderingTested = false, worldTested = false, installed = false };
        }
        var selected = catalog.SplinePlayback.Select(p => {
            var row = catalog.Splines.Single(r => r.Name == p.Name);
            return new { p.Name, p.Controls, p.Curves, p.Duration, p.Samples, p.MatrixChanged, row.Offset, row.Length,
                sha256 = Hash(catalog.Raw.AsSpan(row.Offset, row.Length).ToArray()) };
        }).ToArray();
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Spline/helper/plan changed during inspection.");
        if (observedOrder)
            return new { plan = path, inputPins = pins, version = 104, selected, observations = catalog.SplineObservations,
                nativeSplinePlayback = true, diagnosticOnly = true, timelinePolicy = "native-first-crossing-observed-order",
                loopModes = new[] { false, true }, rotateModes = new[] { false, true }, scale = 1,
                renderingTested = false, eventsTested = false, worldTested = false, runtimeInstalled = false,
                mergeAllowed = false };
        return new { plan = path, inputPins = pins, version = 104, selected, nativeSplinePlayback = true,
            loopModes = new[] { false, true }, rotateModes = new[] { false, true }, scale = 1,
            renderingTested = false, eventsTested = false, worldTested = false, runtimeInstalled = false };
    }
    public static async Task<object> InspectResourceFieldsAsync(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Resource-field plan maximum1MiB.");
        byte[] raw = File.ReadAllBytes(path); using var json = JsonDocument.Parse(raw);
        Fields(json.RootElement, "version", "effectHelperSha256", "source", "keys");
        Fields(json.RootElement.GetProperty("source"), "path", "sha256");
        var plan = JsonSerializer.Deserialize<ResourcePlan>(raw, Options) ?? throw new InvalidDataException("Resource-field plan required.");
        if (plan.Version != 1 || plan.Source is null || plan.Keys is null || plan.Keys.Count is < 1 or > 64 ||
            plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count ||
            plan.Keys.Any(k => k is null || !Regex.IsMatch(k, "^[A-Za-z0-9_]{1,128}$")))
            throw new InvalidDataException("Select1..64 unique literal ASCII effect keys, version1.");
        string source = Resolve(plan.Source.Path, Path.GetDirectoryName(path)!);
        if (Path.GetExtension(source).ToLowerInvariant() != ".eff") throw new InvalidDataException("Native EFF source required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Hash(raw) };
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"), plan.EffectHelperSha256), pins);
        var catalog = await ReadNativeAsync(Snapshot(plan.Source with { Path = source }, pins), resourceFields: true);
        var selected = plan.Keys.Select(key => {
            var rows = catalog.Effects.Where(e => e.Name == key).ToArray();
            if (rows.Length != 1) throw new InvalidDataException("Effect key absent or ambiguous.");
            var row = rows[0];
            return new { key, row.Offset, row.Length, sha256 = Hash(catalog.Raw.AsSpan(row.Offset, row.Length).ToArray()),
                fields = catalog.ResourceFields.Where(f => f.Effect == key).OrderBy(f => f.Offset).ToArray() };
        }).ToArray();
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Resource-field/helper/plan changed during inspection.");
        return new { plan = path, inputPins = pins, version = 104, resourceFieldVersion = catalog.ResourceFieldVersion, selected, nativeReaderPass = true,
            adaptationTested = false, renderingTested = false, eventsTested = false, worldTested = false, runtimeInstalled = false };
    }
    private static async Task<Ready> PrepareAsync(string planPath)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Effect plan maximum1MiB.");
        byte[] planBytes = File.ReadAllBytes(planPath); using var json = JsonDocument.Parse(planBytes);
        int version = json.RootElement.GetProperty("version").GetInt32();
        if (version is not (1 or 2)) throw new InvalidDataException("Effect plan version1 or2 required.");
        if (version == 1) Fields(json.RootElement, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "meshes");
        else
        {
            Fields(json.RootElement, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "meshes", "textures");
            foreach (var texture in json.RootElement.GetProperty("textures").EnumerateArray())
            { Fields(texture, "name", "source"); Fields(texture.GetProperty("source"), "path", "sha256"); }
        }
        foreach (string field in new[] { "target", "companion", "source" }) Fields(json.RootElement.GetProperty(field), "path", "sha256");
        foreach (var mesh in json.RootElement.GetProperty("meshes").EnumerateArray())
        { Fields(mesh, "name", "bindings"); Fields(mesh.GetProperty("bindings"), "path", "sha256"); }
        Plan plan = JsonSerializer.Deserialize<Plan>(planBytes, Options) ?? throw new InvalidDataException("Effect plan required.");
        if (plan.Target is null || plan.Source is null || plan.Companion is null || plan.Keys is null || plan.Meshes is null ||
            plan.Keys.Count is < 1 or > 64 || plan.Meshes.Count > 64 || (version == 1 && plan.Meshes.Count < 1) ||
            (version == 2 && (plan.Textures is null || plan.Textures.Count > 512)) || plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count ||
            plan.Keys.Any(k => k is null || !Regex.IsMatch(k, "^[A-Za-z0-9_]{1,128}$"))) throw new InvalidDataException("Select1..64 unique ASCII effect keys and mesh bindings.");
        string root = Path.GetDirectoryName(planPath)!;
        plan = plan with { Target = plan.Target with { Path = Resolve(plan.Target.Path, root) }, Source = plan.Source with { Path = Resolve(plan.Source.Path, root) },
            Companion = plan.Companion with { Path = Resolve(plan.Companion.Path, root) } };
        if (!Path.GetFileName(plan.Target.Path).Equals("visualeffect_02.eff", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(plan.Companion.Path).Equals("visualeffect_01.eff", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(plan.Source.Path).ToLowerInvariant() != ".eff")
            throw new InvalidDataException("Append target EFF02 plus pinned baseline companion EFF01 required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [planPath] = Hash(planBytes) };
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"), plan.EffectHelperSha256), pins);
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "media-helper.exe"), plan.MediaHelperSha256), pins);
        var target = await ReadNativeAsync(Snapshot(plan.Target, pins)); var source = await ReadNativeAsync(Snapshot(plan.Source, pins));
        var companion = await ReadNativeAsync(Snapshot(plan.Companion, pins));
        var occupied = target.Effects.Concat(companion.Effects).Select(e => e.Name).ToHashSet(StringComparer.Ordinal); var added = new List<Row>();
        foreach (string key in plan.Keys)
        {
            var rows = source.Effects.Where(e => e.Name == key).ToArray();
            if (occupied.Contains(key) || rows.Length != 1) throw new InvalidDataException("Effect key exists, absent or ambiguous."); added.Add(rows[0]);
        }
        var selected = source.Dependencies.Where(d => plan.Keys.Contains(d.Effect)).Distinct().ToArray();
        if (plan.Keys.Any(k => !selected.Any(d => d.Effect == k)) || selected.Any(d => d.Kind != "mesh" && (version == 1 || d.Kind != "texture")))
            throw new InvalidDataException("Version1 mesh-only; version2 mesh/texture-only. ANM/UV/spline/child/audio require separate adapters.");
        var required = selected.Where(d => d.Kind == "mesh").Select(d => ContentSafety.ResourcePath(d.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = plan.Meshes.Select(m => ContentSafety.ResourcePath(m.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != plan.Meshes.Count || !names.SetEquals(required) || names.Any(n => n.Contains('/') || n.Contains('\\') || !n.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Every mesh reference needs one exact binding plan, with no unused meshes.");
        var bindings = new List<object>();
        foreach (var mesh in plan.Meshes)
        {
            if (mesh.Bindings is null) throw new InvalidDataException("Binding pin required.");
            var pin = mesh.Bindings with { Path = Resolve(mesh.Bindings.Path, root) }; byte[] raw = Snapshot(pin, pins);
            using var b = JsonDocument.Parse(raw); string folder = Path.GetDirectoryName(pin.Path)!;
            if (b.RootElement.TryGetProperty("animation", out var animation) && animation.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Mesh-only effects must not add unreferenced ANM.");
            if (b.RootElement.TryGetProperty("uvAnimation", out var uv) && uv.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Mesh/texture-only effects must not add unreferenced UV.");
            void BindingSource(JsonElement item) => Snapshot(new(Resolve(item.GetProperty("path").GetString()!, folder), item.GetProperty("sha256").GetString()!), pins);
            BindingSource(b.RootElement.GetProperty("model")); foreach (var texture in b.RootElement.GetProperty("textures").EnumerateArray()) BindingSource(texture);
            bindings.Add(new { mesh.Name, planSha256 = pins[pin.Path], native = await AssetBindingCatalog.InspectAsync(pin.Path) });
        }
        if (version == 2)
        {
            var textures = plan.Textures!;
            var textureNames = textures.Select(t => ContentSafety.ResourcePath(t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var textureRequired = selected.Where(d => d.Kind == "texture").Select(d => ContentSafety.ResourcePath(d.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (textureNames.Count != textures.Count || !textureNames.SetEquals(textureRequired) ||
                textureNames.Any(n => n.Contains('/') || n.Contains('\\') || Path.GetExtension(n).ToLowerInvariant() is not (".dds" or ".png" or ".bmp")))
                throw new InvalidDataException("Every literal texture reference requires exactly one pinned native raster, no extras.");
            foreach (var texture in textures)
            {
                if (texture.Source is null) throw new InvalidDataException("Texture source pin required.");
                var pin = texture.Source with { Path = Resolve(texture.Source.Path, root) };
                if (!Equal(Path.GetFileName(pin.Path), texture.Name)) throw new InvalidDataException("Texture binding must preserve literal filename.");
                Snapshot(pin, pins);
                bindings.Add(new { texture.Name, sourceSha256 = pins[pin.Path], native = await AssetDependencyCatalog.InspectAsync("texture", pin.Path) });
            }
        }
        if ((long)target.Raw.Length + added.Sum(r => (long)r.Length) > MaxBytes || target.Effects.Count + added.Count > 100000) throw new InvalidDataException("Merged EFF limit exceeded.");
        using var output = new MemoryStream(); output.Write(target.Raw);
        foreach (var row in added) output.Write(source.Raw.AsSpan(row.Offset, row.Length)); byte[] bytes = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(target.CountOffset, 4), target.Effects.Count + added.Count);
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Effect/helper/binding source changed.");
        return new(plan, target, source, added, pins, bindings, bytes);
    }
    public static async Task<object> PreviewAsync(string planPath)
    {
        if (IsAdvancedPlan(planPath)) return await PreviewAdvancedAsync(planPath);
        var ready = await PrepareAsync(planPath);
        return new { addedKeys = ready.Plan.Keys, effectsBefore = ready.Target.Effects.Count, effectsAfter = ready.Target.Effects.Count + ready.Added.Count,
            outputSha256 = Hash(ready.Bytes), bytes = ready.Bytes.Length, bindings = ready.Bindings, originalRecords = "byte-exact", renderingTested = false, installed = false };
    }
    public static async Task<object> ApplyAsync(string planPath, string destination)
    {
        if (IsAdvancedPlan(planPath)) return await ApplyAdvancedAsync(planPath, destination);
        string full = ContentSafety.SafeOutput(destination); var ready = await PrepareAsync(planPath);
        foreach (var path in ready.Pins.Keys)
        { string relative = Path.GetRelativePath(full, path); if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative)) throw new IOException("Output cannot contain a source."); }
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (var pin in ready.Pins)
            { var handle = new FileStream(pin.Key, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(handle); if (Convert.ToHexString(SHA256.HashData(handle)) != pin.Value) throw new IOException("Effect source changed before output."); }
            var final = await ReadNativeAsync(ready.Bytes);
            if (final.Effects.Count != ready.Target.Effects.Count + ready.Added.Count || final.CountOffset != ready.Target.CountOffset ||
                !ready.Bytes.AsSpan(0, ready.Target.CountOffset).SequenceEqual(ready.Target.Raw.AsSpan(0, ready.Target.CountOffset)) ||
                !ready.Bytes.AsSpan(ready.Target.CountOffset + 4, ready.Target.Raw.Length - ready.Target.CountOffset - 4).SequenceEqual(ready.Target.Raw.AsSpan(ready.Target.CountOffset + 4)) ||
                !final.Effects.Skip(ready.Target.Effects.Count).Select(e => e.Name).SequenceEqual(ready.Plan.Keys)) throw new IOException("Native append preservation failed.");
            pending = ContentSafety.FreshDirectory(full); string file = "visualeffect_02.eff";
            File.WriteAllBytes(Path.Combine(pending, file), ready.Bytes); File.WriteAllBytes(Path.Combine(pending, file + ".original.bak"), ready.Target.Raw);
            if (!File.ReadAllBytes(Path.Combine(pending, file)).AsSpan().SequenceEqual(ready.Bytes)) throw new IOException("EFF disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, planVersion = ready.Plan.Version, utc = DateTime.UtcNow,
                schema = ready.Plan.Version == 1 ? "Taiwan EFF104 / mesh-only append" : "Taiwan EFF104 / mesh+texture append",
                sourcePins = ready.Pins, outputSha256 = Hash(ready.Bytes), effectsBefore = ready.Target.Effects.Count, effectsAfter = final.Effects.Count,
                keys = ready.Plan.Keys, nativeReaderPass = true, baselineRecords = "byte-exact except effect count", bindings = ready.Bindings,
                renderingTested = false, eventsTested = false, worldTested = false, runtimeInstalled = false });
            Directory.Move(pending, full); pending = null;
            return new { output = Path.Combine(full, file), outputSha256 = Hash(ready.Bytes), keys = ready.Plan.Keys, nativeReaderPass = true,
                audit = Path.Combine(full, "audit.json"), renderingTested = false, worldTested = false, installed = false };
        }
        finally { foreach (var handle in locks) handle.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
