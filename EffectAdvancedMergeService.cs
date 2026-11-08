using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// EFF104 v3 uses literal per-system associations, not a Cartesian dependency
// product. Splines and effects retain native record bytes. No install or draw.
internal static partial class EffectMergeService
{
    private sealed record SystemBinding(string Effect, int SystemIndex, Pin Bindings);
    private sealed record ObservedSplinePolicy(string Name, Pin PlaybackPlan, string RecordSha256);
    private sealed record SoundDependency(string Name, Pin Source);
    private sealed record EmptyMeshPolicy(string Effect, int SystemIndex, int SystemType, string SystemName,
        string Mesh, string Animation, string Uv, string Spline, bool SubSystem, string EffectRecordSha256);
    private sealed record LiteralSplinePolicy(string Name, string RecordSha256);
    private sealed record EmbeddedTrailRecord(string Effect, string RecordSha256);
    private sealed record AdvancedPlan(int Version, string EffectHelperSha256, string MediaHelperSha256,
        Pin Target, Pin Companion, Pin Source, List<string> Keys, List<SystemBinding> Systems,
        List<Texture> Textures, List<string> Splines, List<ObservedSplinePolicy>? ObservedOrderSplines = null,
        List<SoundDependency>? Sounds = null, List<EmptyMeshPolicy>? InactiveMeshSystems = null,
        List<LiteralSplinePolicy>? LiteralSplineNames = null, List<Texture>? EmbeddedTrailTextures = null,
        List<EmbeddedTrailRecord>? EmbeddedTrailRecords = null);
    private sealed record AdvancedReady(AdvancedPlan Plan, Catalog Target, Catalog Companion, Catalog Source,
        List<Row> AddedEffects, List<Row> AddedSplines, Dictionary<string, string> Pins,
        List<object> Bindings, byte[] Bytes, int CountOffset);

    private static bool IsAdvancedPlan(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Effect plan maximum1MiB.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("version").GetInt32() is 3 or 4 or 5 or 6 or 7 or 8 or 9;
    }

    private static async Task AdvancedSplinePlaybackAsync(AdvancedPlan plan, byte[] bytes)
    {
        var observed = plan.ObservedOrderSplines?.Select(p => p.Name).ToList() ?? new List<string>();
        var strict = plan.Splines.Except(observed, StringComparer.Ordinal).ToList();
        if (strict.Count != 0) await ReadNativeAsync(bytes, splinePlayback: strict, literalSplinePlayback: plan.Version == 7);
        if (observed.Count != 0) await ReadNativeAsync(bytes, splinePlayback: observed, observedOrderPlayback: true);
    }

    private static string LiteralResource(string name, params string[] extensions)
    {
        ContentSafety.ResourcePath(name);
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_ .-]{1,128}$") || name.Contains('/') || name.Contains('\\') ||
            !extensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            throw new InvalidDataException("Literal resource basename/extension required.");
        return name;
    }

    private static async Task<AdvancedReady> PrepareAdvancedAsync(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Effect plan maximum1MiB.");
        byte[] raw = File.ReadAllBytes(path); using var doc = JsonDocument.Parse(raw); var spec = doc.RootElement;
        int version = spec.GetProperty("version").GetInt32();
        bool soundPolicy = version is 5 or 6 or 7 or 8 or 9;
        bool observedPolicy = version is 4 or 5 or 6 or 7 or 8 or 9;
        bool emptyMeshPolicy = version == 6;
        bool particleTrailPolicy = version is 8 or 9;
        bool literalSplinePolicy = version == 7;
        if (version == 9)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines", "sounds", "embeddedTrailTextures", "embeddedTrailRecords");
        else if (particleTrailPolicy)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines", "sounds", "embeddedTrailTextures");
        else if (literalSplinePolicy)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines", "sounds", "literalSplineNames");
        else if (emptyMeshPolicy)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines", "sounds", "inactiveMeshSystems");
        else if (soundPolicy)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines", "sounds");
        else if (observedPolicy)
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines", "observedOrderSplines");
        else
            Fields(spec, "version", "effectHelperSha256", "mediaHelperSha256", "target", "companion", "source", "keys", "systems", "textures", "splines");
        foreach (string key in new[] { "target", "companion", "source" }) Fields(spec.GetProperty(key), "path", "sha256");
        foreach (var s in spec.GetProperty("systems").EnumerateArray())
        { Fields(s, "effect", "systemIndex", "bindings"); Fields(s.GetProperty("bindings"), "path", "sha256"); }
        foreach (var t in spec.GetProperty("textures").EnumerateArray())
        { Fields(t, "name", "source"); Fields(t.GetProperty("source"), "path", "sha256"); }
        if (soundPolicy)
            foreach (var s in spec.GetProperty("sounds").EnumerateArray())
            { Fields(s, "name", "source"); Fields(s.GetProperty("source"), "path", "sha256"); }
        if (emptyMeshPolicy)
            foreach (var p in spec.GetProperty("inactiveMeshSystems").EnumerateArray())
                Fields(p, "effect", "systemIndex", "systemType", "systemName", "mesh", "animation", "uv", "spline", "subSystem", "effectRecordSha256");
        if (literalSplinePolicy)
            foreach (var p in spec.GetProperty("literalSplineNames").EnumerateArray())
                Fields(p, "name", "recordSha256");
        if (particleTrailPolicy)
            foreach (var t in spec.GetProperty("embeddedTrailTextures").EnumerateArray())
            { Fields(t, "name", "source"); Fields(t.GetProperty("source"), "path", "sha256"); }
        if (version == 9)
            foreach (var r in spec.GetProperty("embeddedTrailRecords").EnumerateArray())
                Fields(r, "effect", "recordSha256");
        if (observedPolicy)
            foreach (var p in spec.GetProperty("observedOrderSplines").EnumerateArray())
            { Fields(p, "name", "playbackPlan", "recordSha256"); Fields(p.GetProperty("playbackPlan"), "path", "sha256"); }
        var plan = JsonSerializer.Deserialize<AdvancedPlan>(raw, Options) ?? throw new InvalidDataException("EFF v3 plan required.");
        if (plan.Version is not (3 or 4 or 5 or 6 or 7 or 8 or 9) || plan.Target is null || plan.Companion is null || plan.Source is null || plan.Keys is null ||
            plan.Systems is null || plan.Textures is null || plan.Splines is null || plan.Keys.Count is < 1 or > 64 ||
            plan.Systems.Count > 4096 || plan.Textures.Count > 512 || plan.Splines.Count > 64 ||
            plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count ||
            plan.Keys.Any(k => k is null || !Regex.IsMatch(k, "^[A-Za-z0-9_]{1,128}$")) ||
            plan.Splines.Any(k => k is null || !Regex.IsMatch(k, "^[A-Za-z0-9_]{1,128}$") &&
                !(literalSplinePolicy && plan.LiteralSplineNames is not null && plan.LiteralSplineNames.Any(p => p is not null && p.Name == k))) ||
            plan.Splines.Distinct(StringComparer.Ordinal).Count() != plan.Splines.Count ||
            plan.Systems.Any(s => s.Effect is null || s.Bindings is null) ||
            plan.Systems.Select(s => (s.Effect, s.SystemIndex)).Distinct().Count() != plan.Systems.Count ||
            plan.Textures.Any(t => t.Name is null || t.Source is null))
            throw new InvalidDataException("EFF v3 selections/bounds/identities invalid.");
        if (observedPolicy && (plan.ObservedOrderSplines is null || plan.ObservedOrderSplines.Count > 8 ||
            (plan.Version == 4 && plan.ObservedOrderSplines.Count == 0) ||
            plan.ObservedOrderSplines.Any(p => p is null || p.PlaybackPlan is null || p.Name is null ||
                !plan.Splines.Contains(p.Name) || p.RecordSha256 is null || p.RecordSha256.Length != 64 || !p.RecordSha256.All(Uri.IsHexDigit)) ||
            plan.ObservedOrderSplines.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != plan.ObservedOrderSplines.Count))
            throw new InvalidDataException("EFF v4 requires1..8 unique referenced observed-order splines with exact playback-plan/record seals.");
        int minimumSounds = plan.Version == 5 ? 1 : 0;
        if (soundPolicy && (plan.Sounds is null || plan.Sounds.Count < minimumSounds || plan.Sounds.Count > 64 || plan.Sounds.Any(s => s is null || s.Name is null || s.Source is null) ||
            plan.Sounds.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Sounds.Count))
            throw new InvalidDataException("EFF v5 requires1..64 unique pinned WAV dependencies and an explicit observed-order policy list.");
        if (emptyMeshPolicy && (plan.InactiveMeshSystems is not { Count: 1 } || plan.InactiveMeshSystems.Any(p => p is null ||
            p.Effect is null || p.SystemName is null || p.Mesh is null || p.Animation is null || p.Uv is null || p.Spline is null ||
            p.EffectRecordSha256 is null || p.EffectRecordSha256.Length != 64 || !p.EffectRecordSha256.All(Uri.IsHexDigit))))
            throw new InvalidDataException("EFF v6 requires one exact inactive empty-mesh system policy.");
        if (!emptyMeshPolicy && plan.InactiveMeshSystems is not null)
            throw new InvalidDataException("Inactive empty-mesh policies are exclusive to EFF v6.");
        if (!particleTrailPolicy && plan.EmbeddedTrailTextures is not null)
            throw new InvalidDataException("Embedded particle trail policies require EFF v8/v9.");
        if (plan.Version != 9 && plan.EmbeddedTrailRecords is not null)
            throw new InvalidDataException("Explicit embedded trail record pins require EFF v9.");
        ValidateLiteralSplinePolicyShape(plan);
        string folder = Path.GetDirectoryName(path)!;
        plan = plan with { Target = plan.Target with { Path = Resolve(plan.Target.Path, folder) },
            Source = plan.Source with { Path = Resolve(plan.Source.Path, folder) },
            Companion = plan.Companion with { Path = Resolve(plan.Companion.Path, folder) } };
        if (!Equal(Path.GetFileName(plan.Target.Path), "visualeffect_02.eff") ||
            !Equal(Path.GetFileName(plan.Companion.Path), "visualeffect_01.eff") || !Equal(Path.GetExtension(plan.Source.Path), ".eff"))
            throw new InvalidDataException("Target EFF02/companion EFF01/native source required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Hash(raw) };
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"), plan.EffectHelperSha256), pins);
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "media-helper.exe"), plan.MediaHelperSha256), pins);
        var target = await ReadNativeAsync(Snapshot(plan.Target, pins));
        var companion = await ReadNativeAsync(Snapshot(plan.Companion, pins));
        var source = await ReadNativeAsync(Snapshot(plan.Source, pins), meshSystems: true,
            systemMetadata: emptyMeshPolicy || particleTrailPolicy ? plan.Keys : null);
        ValidateLiteralSplinePolicyRecords(plan, source);
        // The reviewed native executable intentionally has separate modes.
        // Reuse both existing contracts on the same immutable byte snapshot.
        await AdvancedSplinePlaybackAsync(plan, source.Raw);
        if (source.Effects.Select(r => r.Name).Distinct().Count() != source.Effects.Count ||
            new[] { target, companion, source }.Any(c => c.Splines.Select(r => r.Name).Distinct().Count() != c.Splines.Count))
            throw new InvalidDataException("Ambiguous effect/spline registry.");
        var occupied = target.Effects.Concat(companion.Effects).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var effects = plan.Keys.Select(k => !occupied.Contains(k) && source.Effects.Count(r => r.Name == k) == 1
            ? source.Effects.Single(r => r.Name == k) : throw new InvalidDataException("Effect key exists/absent/ambiguous.")).ToList();
        var deps = source.Dependencies.Where(d => plan.Keys.Contains(d.Effect)).Distinct().ToArray();
        if ((!particleTrailPolicy && plan.Keys.Any(k => !deps.Any(d => d.Effect == k))) ||
            deps.Any(d => d.Kind is not ("mesh" or "texture" or "animation" or "uv" or "spline") && !(soundPolicy && d.Kind == "sound")))
            throw new InvalidDataException("EFF v3 rejects child effects/audio/unsupported dependencies.");
        var systems = source.MeshSystems.Where(s => plan.Keys.Contains(s.Effect)).ToDictionary(s => (s.Effect, s.SystemIndex));
        var inactiveSystemKeys = emptyMeshPolicy
            ? plan.InactiveMeshSystems!.Select(s => (s.Effect, s.SystemIndex)).ToHashSet()
            : new HashSet<(string Effect, int SystemIndex)>();
        if (!systems.Keys.ToHashSet().SetEquals(plan.Systems.Select(s => (s.Effect, s.SystemIndex)).Concat(inactiveSystemKeys)) ||
            plan.Systems.Any(s => inactiveSystemKeys.Contains((s.Effect, s.SystemIndex))) ||
            deps.Where(d => d.Kind is "mesh" or "animation" or "uv").Any(d => !systems.Values.Any(s => s.Effect == d.Effect &&
                (d.Kind == "mesh" ? s.Mesh : d.Kind == "animation" ? s.Animation : s.Uv) == d.Value)))
            throw new InvalidDataException("Every native mesh system needs one exact binding; no missing/extras or non-mesh ANM/UV.");
        var bindings = new List<object>(); var inspected = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (emptyMeshPolicy) bindings.AddRange(ValidateEmptyMeshPolicies(plan, source));
        if (plan.Version == 8) bindings.AddRange(await ValidateParticleTrailPolicyAsync(plan, source, folder, pins));
        if (plan.Version == 9) bindings.AddRange(await ValidateMobParticleTrailPolicyAsync(plan, source, folder, pins));
        foreach (var policy in plan.ObservedOrderSplines ?? new List<ObservedSplinePolicy>())
        {
            var pin = policy.PlaybackPlan with { Path = Resolve(policy.PlaybackPlan.Path, folder) };
            Snapshot(pin, pins);
            var proof = JsonSerializer.SerializeToElement(await InspectSplinesAsync(pin.Path, observedOrder: true), ContentSafety.Json);
            var selected = proof.GetProperty("selected").EnumerateArray().ToArray();
            var rows = source.Splines.Where(r => r.Name == policy.Name).ToArray();
            if (selected.Length != 1 || rows.Length != 1 || selected[0].GetProperty("name").GetString() != policy.Name ||
                !Equal(selected[0].GetProperty("sha256").GetString()!, policy.RecordSha256) ||
                !Equal(Hash(source.Raw.AsSpan(rows[0].Offset, rows[0].Length).ToArray()), policy.RecordSha256) ||
                !proof.GetProperty("nativeSplinePlayback").GetBoolean() || !proof.GetProperty("diagnosticOnly").GetBoolean())
                throw new InvalidDataException("Observed-order proof must select this exact serialized source spline, without retiming or substitution.");
            foreach (var input in proof.GetProperty("inputPins").EnumerateObject())
            {
                string digest = input.Value.GetString()!;
                if (pins.TryGetValue(input.Name, out string? previous) && !Equal(previous, digest)) throw new IOException("Conflicting observed-order input seals.");
                pins[input.Name] = digest;
            }
            bindings.Add(new { kind = "observed-order-spline", policy.Name, policy.RecordSha256,
                playbackPlanSha256 = pins[pin.Path], timelinePolicy = "native-first-crossing-observed-order", native = proof });
        }
        var meshPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var associatedPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in plan.Systems)
        {
            var pair = systems[(system.Effect, system.SystemIndex)]; LiteralResource(pair.Mesh, ".dff");
            var pin = system.Bindings with { Path = Resolve(system.Bindings.Path, folder) }; byte[] braw = Snapshot(pin, pins);
            using var bdoc = JsonDocument.Parse(braw); var b = bdoc.RootElement; string bf = Path.GetDirectoryName(pin.Path)!;
            if (b.GetProperty("version").GetInt32() == 6) Fields(b, "version", "model", "textures", "consumerProfile", "consumerSources");
            else if (b.GetProperty("version").GetInt32() is 3 or 4 or 5 or 7 or 8) Fields(b, "version", "model", "textures", "animation", "uvAnimation", "uvProfile");
            else Fields(b, "version", "model", "textures", "animation", "uvAnimation");
            Fields(b.GetProperty("model"), "path", "sha256");
            var model = new Pin(Resolve(b.GetProperty("model").GetProperty("path").GetString()!, bf), b.GetProperty("model").GetProperty("sha256").GetString()!);
            if (!Equal(Path.GetExtension(model.Path), ".dff")) throw new InvalidDataException("DFF binding required.");
            Snapshot(model, pins);
            if(b.GetProperty("version").GetInt32()==6)
                foreach(var consumer in b.GetProperty("consumerSources").EnumerateArray()){
                    Fields(consumer,"path","sha256");
                    Snapshot(new(Resolve(consumer.GetProperty("path").GetString()!,bf),consumer.GetProperty("sha256").GetString()!),pins);
                }
            if (meshPins.TryGetValue(pair.Mesh, out var previous) && !Equal(previous, pins[model.Path]))
                throw new InvalidDataException("One EFF mesh cannot resolve to different DFF payloads across systems.");
            meshPins[pair.Mesh] = pins[model.Path];
            foreach (var t in b.GetProperty("textures").EnumerateArray())
            {
                Fields(t, "nameHex", "maskHex", "path", "sha256");
                Snapshot(new(Resolve(t.GetProperty("path").GetString()!, bf), t.GetProperty("sha256").GetString()!), pins);
            }
            void Associated(string field, string literal, string ext)
            {
                bool has = b.TryGetProperty(field, out var item) && item.ValueKind != JsonValueKind.Null;
                if (has != (literal.Length != 0)) throw new InvalidDataException("Binding ANM/UV presence differs from its exact native mesh system.");
                if (!has) return;
                Fields(item, "path", "sha256"); LiteralResource(literal, ext);
                var asset = new Pin(Resolve(item.GetProperty("path").GetString()!, bf), item.GetProperty("sha256").GetString()!);
                if (!Equal(Path.GetFileName(asset.Path), literal)) throw new InvalidDataException("Binding ANM/UV literal filename mismatch.");
                Snapshot(asset, pins);
                string identity = field + ":" + literal;
                if (associatedPins.TryGetValue(identity, out var previousPin) && !Equal(previousPin, pins[asset.Path]))
                    throw new InvalidDataException("One ANM/UV reference cannot resolve to different payloads across systems.");
                associatedPins[identity] = pins[asset.Path];
            }
            Associated("animation", pair.Animation, ".anm"); Associated("uvAnimation", pair.Uv, ".uva");
            if (!inspected.TryGetValue(pin.Path, out var result))
            { result = await AssetBindingCatalog.InspectAsync(pin.Path); inspected[pin.Path] = result; }
            bindings.Add(new { pair.Effect, pair.SystemIndex, pair.Mesh, pair.Animation, pair.Uv, pair.Spline, bindingSha256 = pins[pin.Path], native = result });
        }
        var textures = plan.Textures.Select(t => LiteralResource(t.Name, ".png", ".bmp", ".dds")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (textures.Count != plan.Textures.Count || !textures.SetEquals(deps.Where(d => d.Kind == "texture").Select(d => LiteralResource(d.Value, ".png", ".bmp", ".dds"))))
            throw new InvalidDataException("Every direct raster needs one pin, no missing/extras.");
        foreach (var texture in plan.Textures)
        {
            var pin = texture.Source with { Path = Resolve(texture.Source.Path, folder) };
            if (!Equal(Path.GetFileName(pin.Path), texture.Name)) throw new InvalidDataException("Direct raster literal filename mismatch.");
            Snapshot(pin, pins); bindings.Add(new { texture.Name, sourceSha256 = pins[pin.Path], native = await AssetDependencyCatalog.InspectAsync("texture", pin.Path) });
        }
        if (soundPolicy)
        {
            var names = plan.Sounds!.Select(s => LiteralResource(s.Name, ".wav")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.SetEquals(deps.Where(d => d.Kind == "sound").Select(d => LiteralResource(d.Value, ".wav"))))
                throw new InvalidDataException("Every native sound reference requires one exact WAV pin, no missing/extras.");
            foreach (var sound in plan.Sounds!)
            {
                var pin = sound.Source with { Path = Resolve(sound.Source.Path, folder) };
                if (!Equal(Path.GetFileName(pin.Path), sound.Name)) throw new InvalidDataException("Sound literal filename mismatch.");
                Snapshot(pin, pins);
                bindings.Add(new { kind = "sound", sound.Name, sourceSha256 = pins[pin.Path],
                    native = await AssetDependencyCatalog.InspectAsync("sound", pin.Path) });
            }
        }
        if (!plan.Splines.ToHashSet(StringComparer.Ordinal).SetEquals(deps.Where(d => d.Kind == "spline").Select(d => d.Value)))
            throw new InvalidDataException("Exact referenced spline selection required, no missing/extras.");
        var addedSplines = new List<Row>();
        foreach (string name in plan.Splines)
        {
            var row = source.Splines.Single(r => r.Name == name);
            var existing = target.Splines.Select(r => (Catalog: target, Row: r)).Concat(companion.Splines.Select(r => (Catalog: companion, Row: r))).Where(x => x.Row.Name == name).ToArray();
            if (existing.Any(x => !source.Raw.AsSpan(row.Offset, row.Length).SequenceEqual(x.Catalog.Raw.AsSpan(x.Row.Offset, x.Row.Length))))
                throw new InvalidDataException("Existing global spline differs; preserve baseline and adapt explicitly.");
            if (existing.Length == 0) addedSplines.Add(row);
        }
        long size = (long)target.Raw.Length + effects.Sum(r => (long)r.Length) + addedSplines.Sum(r => (long)r.Length);
        if (size > MaxBytes || target.Effects.Count + effects.Count > 100000 || target.Splines.Count + addedSplines.Count > 100000)
            throw new InvalidDataException("EFF v3 combined limits exceeded.");
        using var output = new MemoryStream(); output.Write(target.Raw.AsSpan(0, target.CountOffset));
        foreach (var row in addedSplines) output.Write(source.Raw.AsSpan(row.Offset, row.Length));
        int countOffset = checked((int)output.Position); output.Write(target.Raw.AsSpan(target.CountOffset));
        foreach (var row in effects) output.Write(source.Raw.AsSpan(row.Offset, row.Length));
        byte[] bytes = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), target.Splines.Count + addedSplines.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(countOffset, 4), target.Effects.Count + effects.Count);
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("EFF v3 input/helper/binding changed.");
        return new(plan, target, companion, source, effects, addedSplines, pins, bindings, bytes, countOffset);
    }

    private static async Task<object> PreviewAdvancedAsync(string path)
    {
        var r = await PrepareAdvancedAsync(path);
        return new { planVersion = r.Plan.Version, outputSha256 = Hash(r.Bytes), bytes = r.Bytes.Length, addedKeys = r.Plan.Keys,
            addedSplines = r.AddedSplines.Select(s => s.Name), effectsBefore = r.Target.Effects.Count,
            effectsAfter = r.Target.Effects.Count + r.AddedEffects.Count, bindings = r.Bindings,
            originalRecords = "byte-exact except counts", renderingTested = false, installed = false };
    }

    private static async Task<object> ApplyAdvancedAsync(string path, string destination)
    {
        string full = ContentSafety.SafeOutput(destination); var r = await PrepareAdvancedAsync(path);
        foreach (var p in r.Pins.Keys)
        { string rel = Path.GetRelativePath(full, p); if (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(rel)) throw new IOException("Output cannot contain a source."); }
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (var pin in r.Pins)
            { var handle = new FileStream(pin.Key, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(handle); if (Convert.ToHexString(SHA256.HashData(handle)) != pin.Value) throw new IOException("EFF v3 source changed before output."); }
            var final = await ReadNativeAsync(r.Bytes, meshSystems: true,
                systemMetadata: r.Plan.Version is 6 or 8 or 9 ? r.Plan.Keys : null);
            await AdvancedSplinePlaybackAsync(r.Plan, r.Bytes);
            // Records are compared at native boundaries; both shifted counts are excluded.
            void Same(Row row, Row previous, Catalog original)
            { if (row.Name != previous.Name || !r.Bytes.AsSpan(row.Offset, row.Length).SequenceEqual(original.Raw.AsSpan(previous.Offset, previous.Length))) throw new IOException("Native EFF v3 record preservation failed."); }
            if (final.CountOffset != r.CountOffset || final.Effects.Count != r.Target.Effects.Count + r.AddedEffects.Count ||
                final.Splines.Count != r.Target.Splines.Count + r.AddedSplines.Count ||
                !r.Bytes.AsSpan(0, 10).SequenceEqual(r.Target.Raw.AsSpan(0, 10))) throw new IOException("EFF v3 header/count preservation failed.");
            for (int i = 0; i < r.Target.Effects.Count; i++) Same(final.Effects[i], r.Target.Effects[i], r.Target);
            for (int i = 0; i < r.AddedEffects.Count; i++) Same(final.Effects[r.Target.Effects.Count + i], r.AddedEffects[i], r.Source);
            for (int i = 0; i < r.Target.Splines.Count; i++) Same(final.Splines[i], r.Target.Splines[i], r.Target);
            for (int i = 0; i < r.AddedSplines.Count; i++) Same(final.Splines[r.Target.Splines.Count + i], r.AddedSplines[i], r.Source);
            var expected = r.Plan.Keys.SelectMany(k => r.Source.MeshSystems.Where(s => s.Effect == k));
            if (!expected.SequenceEqual(final.MeshSystems.Where(s => r.Plan.Keys.Contains(s.Effect))) ||
                !r.Plan.Keys.SelectMany(k => r.Source.Dependencies.Where(d => d.Effect == k)).SequenceEqual(final.Dependencies.Where(d => r.Plan.Keys.Contains(d.Effect))))
                throw new IOException("EFF v3 system/dependency preservation failed.");
            // New records are appended in the explicit key order, which may
            // differ from their positions in the foreign registry. Compare
            // every native system in that same order without dropping fields.
            var expectedMetadata = r.Plan.Version == 9
                ? r.Plan.Keys.SelectMany(k => r.Source.SystemMetadata.Where(s => s.Effect == k))
                : r.Source.SystemMetadata;
            if (r.Plan.Version is 6 or 8 or 9 && !expectedMetadata.SequenceEqual(final.SystemMetadata))
                throw new IOException("EFF native per-system metadata preservation failed.");
            pending = ContentSafety.FreshDirectory(full); string filename = "visualeffect_02.eff";
            File.WriteAllBytes(Path.Combine(pending, filename), r.Bytes);
            File.WriteAllBytes(Path.Combine(pending, filename + ".original.bak"), r.Target.Raw);
            if (!File.ReadAllBytes(Path.Combine(pending, filename)).AsSpan().SequenceEqual(r.Bytes)) throw new IOException("EFF v3 disk readback failed.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, planVersion = r.Plan.Version, utc = DateTime.UtcNow,
                schema = "Taiwan EFF104 / per-system mesh ANM UV raster spline append", sourcePins = r.Pins,
                outputSha256 = Hash(r.Bytes), effectsBefore = r.Target.Effects.Count, effectsAfter = final.Effects.Count,
                keys = r.Plan.Keys, addedSplines = r.AddedSplines.Select(s => s.Name), nativeReaderPass = true,
                baselineRecords = "byte-exact except spline/effect counts", bindings = r.Bindings,
                inactiveMeshSystems = r.Plan.InactiveMeshSystems ?? new List<EmptyMeshPolicy>(),
                inactiveMeshRecordsPreserved = r.Plan.Version == 6,
                literalSplineNames = r.Plan.LiteralSplineNames ?? new List<LiteralSplinePolicy>(),
                embeddedTrailTextures = r.Plan.EmbeddedTrailTextures ?? new List<Texture>(),
                embeddedTrailRecordsPreserved = r.Plan.Version is 8 or 9,
                renderingTested = false, eventsTested = false, worldTested = false, runtimeInstalled = false });
            Directory.Move(pending, full); pending = null;
            return new { output = Path.Combine(full, filename), outputSha256 = Hash(r.Bytes), keys = r.Plan.Keys,
                addedSplines = r.AddedSplines.Select(s => s.Name), nativeReaderPass = true,
                audit = Path.Combine(full, "audit.json"), renderingTested = false, worldTested = false, installed = false };
        }
        finally { foreach (var handle in locks) handle.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
