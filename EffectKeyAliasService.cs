using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Changes only definition keys at boundaries reported by the native EFF104
// reader. Consumer references, dependencies and installation are separate work.
internal static partial class EffectMergeService
{
    private sealed record KeyAlias(string From, string To, string RecordSha256);
    private sealed record KeyAliasPlan(int Version, string Reason, string EffectHelperSha256,
        Pin Source, Pin Target, Pin Companion, List<KeyAlias> Aliases,
        string? TargetSha256 = null, int? TargetBytes = null);
    private sealed record KeyAliasReady(KeyAliasPlan Plan, Catalog Source,
        Dictionary<string, string> Pins, byte[] Bytes);

    private static void KeyAliasFields(JsonElement spec, bool sealedPlan)
    {
        var fields = new List<string> { "version", "reason", "effectHelperSha256", "target", "companion", "aliases" };
        if (sealedPlan) fields.AddRange(new[] { "source", "targetSha256", "targetBytes" });
        AliasFields(spec, fields.ToArray());
        foreach (string name in sealedPlan ? new[] { "source", "target", "companion" } : new[] { "target", "companion" })
            AliasFields(spec.GetProperty(name), "path", "sha256");
        if (spec.GetProperty("aliases").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Key aliases array required.");
        foreach (var alias in spec.GetProperty("aliases").EnumerateArray()) AliasFields(alias, "from", "to", "recordSha256");
    }

    private static async Task<KeyAliasReady> PrepareKeyAliasesAsync(KeyAliasPlan plan, string folder, Dictionary<string, string> pins)
    {
        bool Name(string? name) => name is not null && Regex.IsMatch(name, "^[A-Za-z0-9_]{1,128}$");
        if (plan.Version != 1 || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 2048 ||
            plan.Source is null || plan.Target is null || plan.Companion is null || plan.Aliases is not { Count: > 0 and <= 64 } ||
            plan.Aliases.Any(a => a is null || !Name(a.From) || !Name(a.To) || Equal(a.From, a.To) ||
                a.RecordSha256 is null || a.RecordSha256.Length != 64 || !a.RecordSha256.All(Uri.IsHexDigit)) ||
            plan.Aliases.Select(a => a.From).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Aliases.Count ||
            plan.Aliases.Select(a => a.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Aliases.Count)
            throw new InvalidDataException("Key-alias v1 requires reason and1..64 unique literal keys with exact record seals.");
        plan = plan with { Source = plan.Source with { Path = Resolve(plan.Source.Path, folder) },
            Target = plan.Target with { Path = Resolve(plan.Target.Path, folder) },
            Companion = plan.Companion with { Path = Resolve(plan.Companion.Path, folder) } };
        if (!Equal(Path.GetExtension(plan.Source.Path), ".eff") || !Equal(Path.GetFileName(plan.Target.Path), "visualeffect_02.eff") ||
            !Equal(Path.GetFileName(plan.Companion.Path), "visualeffect_01.eff"))
            throw new InvalidDataException("Source EFF and exact target EFF02/companion EFF01 required.");
        Snapshot(new(Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"), plan.EffectHelperSha256), pins);
        var source = await ReadNativeAsync(Snapshot(plan.Source, pins));
        var target = await ReadNativeAsync(Snapshot(plan.Target, pins));
        var companion = await ReadNativeAsync(Snapshot(plan.Companion, pins));
        if (new[] { source, target, companion }.Any(c => c.Effects.Select(e => e.Name).Distinct().Count() != c.Effects.Count))
            throw new InvalidDataException("Ambiguous native definition registry.");
        var occupied = source.Effects.Concat(target.Effects).Concat(companion.Effects).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldNames = plan.Aliases.Select(a => a.From).ToHashSet(StringComparer.Ordinal);
        if (plan.Aliases.Any(a => occupied.Contains(a.To) || source.Dependencies.Any(d => d.Kind == "effect" && Equal(d.Value, a.To))))
            throw new InvalidDataException("Alias destination exists or is already referenced; swaps/chains forbidden.");
        if (source.Dependencies.Any(d => oldNames.Contains(d.Effect) && d.Kind == "effect" && oldNames.Contains(d.Value)))
            throw new InvalidDataException("Selected child reference requires its own explicit retargeting plan.");
        var rows = source.Effects.ToDictionary(e => e.Name, StringComparer.Ordinal);
        foreach (var alias in plan.Aliases)
        {
            if (!rows.TryGetValue(alias.From, out var row) || !Equal(Hash(source.Raw.AsSpan(row.Offset, row.Length).ToArray()), alias.RecordSha256))
                throw new InvalidDataException("Selected native effect absent or record seal changed.");
            int size = BinaryPrimitives.ReadInt32LittleEndian(source.Raw.AsSpan(row.Offset, 4));
            if (size != alias.From.Length + 1 || !source.Raw.AsSpan(row.Offset + 4, size).SequenceEqual(Encoding.ASCII.GetBytes(alias.From + "\0")))
                throw new InvalidDataException("Definition key requires exact single-NUL serialized body.");
        }
        var mapping = plan.Aliases.ToDictionary(a => a.From, StringComparer.Ordinal);
        long length = source.Raw.Length + plan.Aliases.Sum(a => (long)a.To.Length - a.From.Length);
        if (length is < 18 or > MaxBytes) throw new InvalidDataException("Key-alias output size limit.");
        using var output = new MemoryStream((int)length);
        output.Write(source.Raw.AsSpan(0, source.CountOffset + 4));
        foreach (var row in source.Effects)
        {
            if (!mapping.TryGetValue(row.Name, out var alias)) { output.Write(source.Raw.AsSpan(row.Offset, row.Length)); continue; }
            byte[] prefix = new byte[4], body = Encoding.ASCII.GetBytes(alias.To + "\0");
            BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length); output.Write(prefix); output.Write(body);
            int oldSize = 4 + row.Name.Length + 1;
            output.Write(source.Raw.AsSpan(row.Offset + oldSize, row.Length - oldSize));
        }
        byte[] bytes = output.ToArray();
        if (bytes.Length != length || (plan.TargetSha256 is not null && !Equal(Hash(bytes), plan.TargetSha256)) ||
            (plan.TargetBytes is not null && bytes.Length != plan.TargetBytes)) throw new InvalidDataException("Key-alias output seal differs.");
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Key-alias pinned inputs changed.");
        return new(plan, source, pins, bytes);
    }

    private static async Task<KeyAliasReady> PrepareKeyAliasPlanAsync(string path)
    {
        path = Path.GetFullPath(path); byte[] raw = AliasJson(path);
        using var doc = JsonDocument.Parse(raw); KeyAliasFields(doc.RootElement, true);
        var plan = JsonSerializer.Deserialize<KeyAliasPlan>(raw, Options) ?? throw new InvalidDataException("Key-alias plan required.");
        if (plan.TargetSha256 is null || plan.TargetSha256.Length != 64 || !plan.TargetSha256.All(Uri.IsHexDigit) || plan.TargetBytes is null)
            throw new InvalidDataException("Key-alias output SHA/size seal required.");
        return await PrepareKeyAliasesAsync(plan, Path.GetDirectoryName(path)!, new(StringComparer.OrdinalIgnoreCase) { [path] = Hash(raw) });
    }

    public static async Task<object> CreateKeyAliasPlanAsync(string source, string mapping, string destination)
    {
        source = Path.GetFullPath(source); mapping = Path.GetFullPath(mapping); destination = ContentSafety.SafeOutput(destination);
        byte[] raw = AliasJson(mapping); using var doc = JsonDocument.Parse(raw); KeyAliasFields(doc.RootElement, false);
        var spec = JsonSerializer.Deserialize<KeyAliasPlan>(raw, Options) ?? throw new InvalidDataException("Key-alias mapping required.");
        var ready = await PrepareKeyAliasesAsync(spec with { Source = new(source, ContentSafety.Hash(source)) }, Path.GetDirectoryName(mapping)!,
            new(StringComparer.OrdinalIgnoreCase) { [mapping] = Hash(raw) });
        var plan = ready.Plan with { TargetSha256 = Hash(ready.Bytes), TargetBytes = ready.Bytes.Length };
        ContentSafety.WriteJson(destination, plan);
        return new { plan = destination, outputSha256 = plan.TargetSha256, bytes = plan.TargetBytes, changes = plan.Aliases.Count, installed = false };
    }

    public static async Task<object> PreviewKeyAliasesAsync(string path)
    {
        var ready = await PrepareKeyAliasPlanAsync(path);
        return new { outputSha256 = Hash(ready.Bytes), bytes = ready.Bytes.Length, aliases = ready.Plan.Aliases,
            definitionCount = ready.Source.Effects.Count, nativeCandidateReadback = false, consumerReferencesAdapted = false,
            dependencyClosureVerified = false, renderingTested = false, installed = false };
    }

    public static async Task<object> ApplyKeyAliasesAsync(string path, string destination)
    {
        string full = ContentSafety.SafeOutput(destination); var ready = await PrepareKeyAliasPlanAsync(path);
        foreach (string input in new[] { ready.Plan.Source.Path, ready.Plan.Target.Path, ready.Plan.Companion.Path })
        {
            string parent = Path.GetDirectoryName(input)!;
            if (full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Key-alias output must be outside every EFF input folder.");
        }
        foreach (string input in ready.Pins.Keys)
        {
            string rel = Path.GetRelativePath(full, input);
            if (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(rel)) throw new IOException("Output cannot contain an input.");
        }
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (var pin in ready.Pins)
            { var handle = new FileStream(pin.Key, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(handle); if (Convert.ToHexString(SHA256.HashData(handle)) != pin.Value) throw new IOException("Key-alias source changed before output."); }
            var final = await ReadNativeAsync(ready.Bytes);
            var mapping = ready.Plan.Aliases.ToDictionary(a => a.From, a => a.To, StringComparer.Ordinal);
            string Rename(string key) => mapping.GetValueOrDefault(key, key);
            if (final.CountOffset != ready.Source.CountOffset || final.Effects.Count != ready.Source.Effects.Count ||
                !final.Effects.Select(e => e.Name).SequenceEqual(ready.Source.Effects.Select(e => Rename(e.Name))) ||
                !ready.Bytes.AsSpan(0, final.CountOffset + 4).SequenceEqual(ready.Source.Raw.AsSpan(0, ready.Source.CountOffset + 4)) ||
                !final.Dependencies.SequenceEqual(ready.Source.Dependencies.Select(d => d with { Effect = Rename(d.Effect) })))
                throw new IOException("Key-alias native registry/dependency preservation failed.");
            for (int i = 0; i < final.Effects.Count; ++i)
            {
                var before = ready.Source.Effects[i]; var after = final.Effects[i];
                int oldPrefix = 4 + BinaryPrimitives.ReadInt32LittleEndian(ready.Source.Raw.AsSpan(before.Offset, 4));
                int newPrefix = 4 + BinaryPrimitives.ReadInt32LittleEndian(ready.Bytes.AsSpan(after.Offset, 4));
                if (!ready.Source.Raw.AsSpan(before.Offset + oldPrefix, before.Length - oldPrefix).SequenceEqual(ready.Bytes.AsSpan(after.Offset + newPrefix, after.Length - newPrefix)) ||
                    (!mapping.ContainsKey(before.Name) && !ready.Source.Raw.AsSpan(before.Offset, before.Length).SequenceEqual(ready.Bytes.AsSpan(after.Offset, after.Length))))
                    throw new IOException("Only selected native definition key bytes may change.");
            }
            pending = ContentSafety.FreshDirectory(full); const string file = "adapted-source.eff";
            File.WriteAllBytes(Path.Combine(pending, file), ready.Bytes);
            File.WriteAllBytes(Path.Combine(pending, "original.eff.bak"), ready.Source.Raw);
            if (!File.ReadAllBytes(Path.Combine(pending, file)).AsSpan().SequenceEqual(ready.Bytes)) throw new IOException("Key-alias disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, utc = DateTime.UtcNow, profile = "EFF104/native-definition-key-alias-v1",
                reason = ready.Plan.Reason, sourcePins = ready.Pins, sourceSha256 = Hash(ready.Source.Raw), outputSha256 = Hash(ready.Bytes),
                aliases = ready.Plan.Aliases, definitionCount = final.Effects.Count, nativeReaderPass = true,
                nonDefinitionKeyBytesPreserved = true, consumerReferencesAdapted = false, dependencyClosureVerified = false,
                renderingTested = false, eventsTested = false, worldTested = false, contentReady = false, installed = false });
            Directory.Move(pending, full); pending = null;
            return new { output = Path.Combine(full, file), outputSha256 = Hash(ready.Bytes), changes = mapping.Count, nativeReaderPass = true, installed = false };
        }
        finally { foreach (var handle in locks) handle.Dispose(); if (pending is not null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
