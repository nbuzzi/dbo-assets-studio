using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Additive content names only. Boundaries come from DffRepairService; never
// search strings in geometry/plugin blobs or mutate material UV dictionaries.
internal static class DffTextureAliasService
{
    private const int Limit = 128 * 1024 * 1024;
    private sealed record Texture(string Path, string Sha256);
    private sealed record Alias(string FromHex, string ToHex, Texture Texture, string TargetResource);
    private sealed record Mapping(int Version, string Reason, List<Alias> Aliases);
    private sealed record Plan(int Version, string Source, string SourceSha256, string TargetSha256,
        int TargetBytes, string Reason, List<Alias> Aliases);
    private sealed record Change(int Offset, int OldBytes, int NewBytes, string FromHex, string ToHex);
    private sealed record Evidence(List<Change> Changes, bool NonNamePayloadsPreserved = true,
        bool NativeReaderTested = false, bool RenderingTested = false, bool Installed = false);
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSON object required.");
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidDataException("Unknown/duplicate alias field.");
        if (seen.Count != fields.Length) throw new InvalidDataException("Required alias field missing.");
    }
    private static JsonDocument Document(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Alias JSON limit1MiB.");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
    private static byte[] Name(string value, bool sourceName = false, bool legacySuffix = false)
    {
        if (value is null || value.Length is < 2 or > 62 || value.Length % 2 != 0 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("RenderWare name hex required.");
        byte[] name = Convert.FromHexString(value);
        if (name.Any(c => c < (sourceName ? 32 : 33) || c > 126 || c is (byte)'/' or (byte)'\\') ||
            (name.Contains((byte)'.') && (!sourceName || !legacySuffix || name.Count(c => c == (byte)'.') != 1 || !Encoding.ASCII.GetString(name).EndsWith(".1", StringComparison.Ordinal))))
            throw new InvalidDataException("ASCII basename without extension/path required.");
        if (!sourceName) ContentSafety.ResourcePath(Encoding.ASCII.GetString(name));
        // Existing RenderWare callback names may end in a space. They remain bounded
        // hex metadata; only the new name is an installable filesystem/resource key.
        return name;
    }
    private static List<Alias> Aliases(JsonElement json, string folder, bool legacySuffix = false)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() is < 1 or > 32)
            throw new InvalidDataException("1..32 aliases required.");
        var result = new List<Alias>(); var from = new HashSet<string>();
        var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in json.EnumerateArray())
        {
            Fields(value, "fromHex", "toHex", "texture", "targetResource");
            Fields(value.GetProperty("texture"), "path", "sha256");
            var alias = JsonSerializer.Deserialize<Alias>(value, ContentSafety.Json) ?? throw new InvalidDataException("Alias required.");
            byte[] original = Name(alias.FromHex, true, legacySuffix), renamed = Name(alias.ToHex);
            string first = Convert.ToHexString(original), second = Convert.ToHexString(renamed);
            if (!from.Add(first) || !to.Add(Encoding.ASCII.GetString(renamed)) || first == second)
                throw new InvalidDataException("Duplicate/no-op texture alias.");
            string resource = ContentSafety.ResourcePath(alias.TargetResource).Replace('\\', '/');
            string path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(alias.Texture.Path)
                ? alias.Texture.Path : System.IO.Path.Combine(folder, alias.Texture.Path));
            ContentSafety.NoLinks(path); string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string pin = alias.Texture.Sha256;
            if (ext is not (".dds" or ".png" or ".bmp") || new FileInfo(path).Length is < 1 or > Limit ||
                pin is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit) || !ContentSafety.Hash(path).Equals(pin, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(resource).Equals(Encoding.ASCII.GetString(renamed) + ext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Texture pin/extension/targetResource basename mismatch.");
            result.Add(alias with { FromHex = first, ToHex = second, Texture = new(path, pin.ToUpperInvariant()), TargetResource = resource });
        }
        // No simultaneous swaps/chains: each alias introduces an independent name.
        if (result.Any(a => result.Any(b => Encoding.ASCII.GetString(Name(a.ToHex)).Equals(Encoding.ASCII.GetString(Name(b.FromHex, true, legacySuffix)), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Alias swaps/chains not supported.");
        return result;
    }
    private static byte[] Read(string source)
    {
        ContentSafety.NoLinks(source);
        if (!System.IO.Path.GetExtension(source).Equals(".dff", StringComparison.OrdinalIgnoreCase) || new FileInfo(source).Length is < 24 or > Limit)
            throw new InvalidDataException("Complete bounded DFF required.");
        return File.ReadAllBytes(source);
    }
    private static (byte[] bytes, Evidence evidence) Transform(byte[] raw, List<Alias> aliases)
    {
        var names = new List<DffRepairService.TextureName>();
        var before = DffRepairService.Inspect(raw, false, true, names);
        foreach (var alias in aliases)
        {
            if (!names.Any(n => n.NameHex == alias.FromHex)) throw new InvalidDataException("Alias source name absent from material Texture/String boundaries.");
            string target = Encoding.ASCII.GetString(Name(alias.ToHex));
            if (names.Any(n => Encoding.Latin1.GetString(Convert.FromHexString(n.NameHex)).Equals(target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Alias target already exists in model.");
        }
        var edits = new List<(DffRepairService.TextureName field, Alias alias, byte[] body)>();
        var deltas = new Dictionary<int, int>();
        foreach (var name in names)
        {
            var alias = aliases.SingleOrDefault(a => a.FromHex == name.NameHex); if (alias is null) continue;
            byte[] value = Name(alias.ToHex), body = new byte[(value.Length + 4) & ~3]; value.CopyTo(body, 0);
            int delta = body.Length - name.Bytes;
            foreach (int header in name.ParentHeaders) deltas[header] = deltas.GetValueOrDefault(header) + delta;
            edits.Add((name, alias, body));
        }
        long length = raw.Length + edits.Sum(e => (long)e.body.Length - e.field.Bytes);
        if (length is < 24 or > Limit) throw new InvalidDataException("Aliased DFF size limit.");
        byte[] patched = (byte[])raw.Clone();
        foreach (var (header, delta) in deltas)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(header + 4, 4));
            long changed = size + (long)delta;
            if (changed is < 0 or > Limit) throw new InvalidDataException("Aliased parent size overflow.");
            BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(header + 4, 4), (uint)changed);
        }
        foreach (var edit in edits) BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(edit.field.Offset - 8, 4), (uint)edit.body.Length);
        using var stream = new MemoryStream((int)length); int cursor = 0;
        foreach (var edit in edits.OrderBy(e => e.field.Offset))
        {
            stream.Write(patched.AsSpan(cursor, edit.field.Offset - cursor)); stream.Write(edit.body);
            cursor = edit.field.Offset + edit.field.Bytes;
        }
        stream.Write(patched.AsSpan(cursor)); byte[] bytes = stream.ToArray();
        var afterNames = new List<DffRepairService.TextureName>(); var after = DffRepairService.Inspect(bytes, false, true, afterNames);
        string[] expected = names.Select(n => aliases.SingleOrDefault(a => a.FromHex == n.NameHex)?.ToHex ?? n.NameHex).ToArray();
        if (bytes.Length != length || before.Geometries != after.Geometries || before.Materials != after.Materials ||
            before.Chunks != after.Chunks || !afterNames.Select(n => n.NameHex).SequenceEqual(expected))
            throw new InvalidDataException("Aliased DFF structure/readback mismatch.");
        return (bytes, new(edits.Select(e => new Change(e.field.Offset, e.field.Bytes, e.body.Length, e.alias.FromHex, e.alias.ToHex)).ToList()));
    }
    public static object CreatePlan(string source, string mapping, string output)
    {
        source = System.IO.Path.GetFullPath(source); mapping = System.IO.Path.GetFullPath(mapping);
        using var document = Document(mapping); Fields(document.RootElement, "version", "reason", "aliases");
        var spec = JsonSerializer.Deserialize<Mapping>(document, ContentSafety.Json) ?? throw new InvalidDataException("Mapping required.");
        if (spec.Version is not (1 or 2) || string.IsNullOrWhiteSpace(spec.Reason) || spec.Reason.Length > 4096) throw new InvalidDataException("Reviewed v1/v2 alias mapping/reason required.");
        // Explicit v2 permits only a source .1 suffix; targets remain extensionless
        // resource basenames. V1 retains its prior source-name restrictions.
        var aliases = Aliases(document.RootElement.GetProperty("aliases"), System.IO.Path.GetDirectoryName(mapping)!, spec.Version == 2);
        byte[] raw = Read(source); var (bytes, evidence) = Transform(raw, aliases);
        var plan = new Plan(spec.Version, source, Hash(raw), Hash(bytes), bytes.Length, spec.Reason, aliases);
        ContentSafety.WriteJson(output, plan); return new { plan = System.IO.Path.GetFullPath(output), evidence, sourcePreserved = true, installed = false };
    }
    private static (Plan plan, byte[] raw, byte[] bytes, Evidence evidence) Resolve(string path)
    {
        path = System.IO.Path.GetFullPath(path); using var document = Document(path);
        Fields(document.RootElement, "version", "source", "sourceSha256", "targetSha256", "targetBytes", "reason", "aliases");
        var plan = JsonSerializer.Deserialize<Plan>(document, ContentSafety.Json) ?? throw new InvalidDataException("Sealed plan required.");
        if (plan.Version is not (1 or 2) || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 4096) throw new InvalidDataException("Alias plan v1/v2/reason required.");
        var aliases = Aliases(document.RootElement.GetProperty("aliases"), System.IO.Path.GetDirectoryName(path)!, plan.Version == 2);
        plan = plan with { Source = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(plan.Source) ? plan.Source : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, plan.Source)), Aliases = aliases };
        byte[] raw = Read(plan.Source); if (Hash(raw) != plan.SourceSha256) throw new IOException("Alias model source changed.");
        var (bytes, evidence) = Transform(raw, aliases);
        if (Hash(bytes) != plan.TargetSha256 || bytes.Length != plan.TargetBytes) throw new IOException("Alias target differs from sealed plan.");
        return (plan, raw, bytes, evidence);
    }
    public static object Preview(string path)
    {
        var (plan, _, _, evidence) = Resolve(path); return new { plan, evidence, installed = false };
    }
    public static object Apply(string path, string output)
    {
        path = System.IO.Path.GetFullPath(path); ContentSafety.NoLinks(path); string planHash = ContentSafety.Hash(path);
        output = ContentSafety.SafeOutput(output); var (plan, raw, bytes, evidence) = Resolve(path);
        if (output.StartsWith(System.IO.Path.GetDirectoryName(plan.Source)! + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Alias output outside model source folder required.");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = planHash, [plan.Source] = plan.SourceSha256 };
        foreach (var alias in plan.Aliases) pins[alias.Texture.Path] = alias.Texture.Sha256;
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (var (file, hash) in pins)
            {
                ContentSafety.NoLinks(file); var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(locked);
                if (Convert.ToHexString(SHA256.HashData(locked)) != hash) throw new IOException("Alias input changed before apply.");
            }
            pending = ContentSafety.FreshDirectory(output);
            File.WriteAllBytes(System.IO.Path.Combine(pending, "original.dff.bak"), raw);
            File.WriteAllBytes(System.IO.Path.Combine(pending, "model.dff"), bytes);
            if (Hash(File.ReadAllBytes(System.IO.Path.Combine(pending, "model.dff"))) != plan.TargetSha256) throw new IOException("Alias disk readback mismatch.");
            ContentSafety.WriteJson(System.IO.Path.Combine(pending, "audit.json"), new { version = 1, planHash, plan, evidence,
                dependencyPayloadsCopied = false, nativeReaderTested = false, renderingTested = false, installed = false, contentReady = false });
            Directory.Move(pending, output); pending = null;
            return new { output, sha256 = plan.TargetSha256, evidence, installed = false, contentReady = false };
        }
        finally { foreach (var locked in locks) locked.Dispose(); if (pending != null) Directory.Delete(pending, true); }
    }
}
