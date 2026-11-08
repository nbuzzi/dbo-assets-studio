using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Same engine/catalog as EFF inspection and merge. Only explicitly selected
// native mesh-string spans; no whole EFF serializer or opaque-byte searches.
internal static partial class EffectMergeService
{
    private sealed record MeshAlias(string FromHex, string ToHex, Pin Model, string TargetResource);
    private sealed record MeshAliasPlan(int Version, string Reason, string EffectHelperSha256, Pin Source,
        List<string> Keys, List<MeshAlias> Aliases, string? TargetSha256 = null, int? TargetBytes = null);
    private sealed record MeshAliasChange(ResourceField Field, string ToHex, string TargetResource);
    private sealed record MeshAliasReady(MeshAliasPlan Plan, Catalog Source, Dictionary<string,string> Pins,
        List<MeshAliasChange> Changes, byte[] Bytes);
    private static void AliasFields(JsonElement value, params string[] allowed)
    {
        Fields(value, allowed);
        if (value.EnumerateObject().Count() != allowed.Length) throw new InvalidDataException("Required mesh-alias field missing.");
    }
    private static byte[] AliasJson(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Mesh-alias JSON maximum1MiB.");
        return File.ReadAllBytes(path);
    }
    private static string MeshAliasName(string hex)
    {
        if (hex is null || hex.Length is < 10 or > 256 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException("Mesh filename hex required.");
        byte[] raw = Convert.FromHexString(hex);
        if (raw.Any(b => b < 33 || b > 126)) throw new InvalidDataException("Literal ASCII mesh filename required.");
        string name = Encoding.ASCII.GetString(raw); ContentSafety.ResourcePath(name);
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_][A-Za-z0-9_.-]*\.dff$", RegexOptions.IgnoreCase) || name.Contains(".."))
            throw new InvalidDataException("Mesh basename.dff only; no paths/traversal.");
        return name;
    }
    private static async Task<MeshAliasReady> PrepareMeshAliasesAsync(MeshAliasPlan plan, string folder, Dictionary<string,string> pins)
    {
        if (plan.Version is not (1 or 2) || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 2048 || plan.Source is null ||
            plan.Keys is null || plan.Keys.Count is < 1 or > 64 || plan.Keys.Any(k => k is null || !Regex.IsMatch(k,"^[A-Za-z0-9_]{1,128}$")) ||
            plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count || plan.Aliases is null || plan.Aliases.Count is < 1 or > 64)
            throw new InvalidDataException("Mesh-alias v1 requires reason,1..64 effect keys and1..64 aliases.");
        string source = Resolve(plan.Source.Path, folder);
        if (!Path.GetExtension(source).Equals(".eff", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native EFF source required.");
        plan = plan with { Source = plan.Source with { Path = source } };
        Snapshot(new(Path.Combine(AppContext.BaseDirectory,"effect-reader.exe"),plan.EffectHelperSha256),pins);
        var aliases = new List<MeshAlias>();
        var from = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in plan.Aliases)
        {
            if (alias is null || alias.Model is null) throw new InvalidDataException("Pinned alias model required.");
            string oldName = MeshAliasName(alias.FromHex), newName = MeshAliasName(alias.ToHex);
            if (!from.Add(oldName) || !to.Add(newName) || Equal(oldName,newName)) throw new InvalidDataException("Duplicate/no-op mesh alias.");
            string resource = ContentSafety.ResourcePath(alias.TargetResource).Replace('\\','/');
            if (!Equal(resource,"effect/"+newName)) throw new InvalidDataException("Mesh targetResource must be effect/new-name.dff.");
            var model = alias.Model with { Path = Resolve(alias.Model.Path,folder) };
            if (!Path.GetExtension(model.Path).Equals(".dff",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DFF dependency required.");
            byte[] raw = Snapshot(model,pins);
            DffRepairService.Inspect(raw,false,true,new List<DffRepairService.TextureName>());
            aliases.Add(new(Convert.ToHexString(Convert.FromHexString(alias.FromHex)),Convert.ToHexString(Convert.FromHexString(alias.ToHex)),
                model with { Sha256 = pins[model.Path] },resource));
        }
        if (to.Overlaps(from)) throw new InvalidDataException("Alias swaps/chains not supported.");
        plan = plan with { Aliases = aliases };
        var catalog = await ReadNativeAsync(Snapshot(plan.Source,pins),resourceFields:true);
        foreach (string key in plan.Keys)
            if (catalog.Effects.Count(e => e.Name == key) != 1) throw new InvalidDataException("Effect key absent or ambiguous.");
        var selected = catalog.ResourceFields.Where(f => plan.Keys.Contains(f.Effect) && f.Kind == "mesh").ToArray();
        var changes = new List<MeshAliasChange>();
        foreach (var alias in aliases)
        {
            if (catalog.ResourceFields.Any(f => f.Kind == "mesh" && f.ValueHex.Length != 0 &&
                Equal(Encoding.Latin1.GetString(Convert.FromHexString(f.ValueHex)),MeshAliasName(alias.ToHex))))
                throw new InvalidDataException("Alias target already referenced in source EFF.");
            var fields = selected.Where(f => ResourceAliasMatches(plan.Version,f.ValueHex,alias.FromHex)).ToArray();
            if (fields.Length == 0) throw new InvalidDataException("Alias source mesh absent from selected native fields.");
            foreach (var field in fields)
            {
                // Reviewed native string profile: literal bytes plus exactly one
                // terminal NUL, not a guessed padded/opaque serialized payload.
                if (field.Bytes != field.ValueHex.Length / 2 + 1) throw new InvalidDataException("Mesh alias requires exact single-NUL string body.");
                changes.Add(new(field,alias.ToHex,alias.TargetResource));
            }
        }
        long length = catalog.Raw.Length + changes.Sum(c => (long)c.ToHex.Length/2 + 1 - c.Field.Bytes);
        if (length is < 18 or > MaxBytes) throw new InvalidDataException("Aliased EFF size limit.");
        using var output = new MemoryStream((int)length); int cursor = 0;
        foreach (var change in changes.OrderBy(c => c.Field.Offset))
        {
            output.Write(catalog.Raw.AsSpan(cursor,change.Field.Offset-cursor));
            byte[] value = Convert.FromHexString(change.ToHex), prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix,value.Length+1); output.Write(prefix); output.Write(value); output.WriteByte(0);
            cursor = change.Field.Offset + 4 + change.Field.Bytes;
        }
        output.Write(catalog.Raw.AsSpan(cursor)); byte[] bytes = output.ToArray();
        if (bytes.Length != length || (plan.TargetSha256 is not null && !Equal(Hash(bytes),plan.TargetSha256)) ||
            (plan.TargetBytes is not null && plan.TargetBytes != bytes.Length)) throw new InvalidDataException("Mesh-alias target seal differs.");
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Mesh-alias source/helper/dependency changed.");
        return new(plan,catalog,pins,changes,bytes);
    }
    private static async Task<MeshAliasReady> PrepareMeshAliasPlanAsync(string path)
    {
        path = Path.GetFullPath(path); byte[] raw = AliasJson(path); using var json = JsonDocument.Parse(raw);
        AliasFields(json.RootElement,"version","reason","effectHelperSha256","source","keys","aliases","targetSha256","targetBytes");
        AliasFields(json.RootElement.GetProperty("source"),"path","sha256");
        foreach (var alias in json.RootElement.GetProperty("aliases").EnumerateArray())
        { AliasFields(alias,"fromHex","toHex","model","targetResource"); AliasFields(alias.GetProperty("model"),"path","sha256"); }
        var plan = JsonSerializer.Deserialize<MeshAliasPlan>(raw,Options) ?? throw new InvalidDataException("Mesh-alias plan required.");
        if (plan.TargetSha256 is null || plan.TargetSha256.Length != 64 || !plan.TargetSha256.All(Uri.IsHexDigit) || plan.TargetBytes is null)
            throw new InvalidDataException("Mesh-alias target SHA/bytes seal required.");
        return await PrepareMeshAliasesAsync(plan,Path.GetDirectoryName(path)!,new(StringComparer.OrdinalIgnoreCase){[path]=Hash(raw)});
    }
    public static async Task<object> CreateMeshAliasPlanAsync(string source, string mapping, string destination)
    {
        source = Path.GetFullPath(source); mapping = Path.GetFullPath(mapping); destination = ContentSafety.SafeOutput(destination);
        byte[] raw = AliasJson(mapping); using var json = JsonDocument.Parse(raw);
        AliasFields(json.RootElement,"version","reason","effectHelperSha256","keys","aliases");
        foreach (var alias in json.RootElement.GetProperty("aliases").EnumerateArray())
        { AliasFields(alias,"fromHex","toHex","model","targetResource"); AliasFields(alias.GetProperty("model"),"path","sha256"); }
        var value = JsonSerializer.Deserialize<MeshAliasPlan>(raw,Options) ?? throw new InvalidDataException("Mesh-alias mapping required.");
        var ready = await PrepareMeshAliasesAsync(value with { Source = new(source,ContentSafety.Hash(source)) },Path.GetDirectoryName(mapping)!,
            new(StringComparer.OrdinalIgnoreCase){[mapping]=Hash(raw)});
        var plan = ready.Plan with { TargetSha256 = Hash(ready.Bytes), TargetBytes = ready.Bytes.Length };
        ContentSafety.WriteJson(destination,plan);
        return new { plan = destination, sourceSha256 = ready.Pins[source], outputSha256 = plan.TargetSha256, bytes = plan.TargetBytes,
            changes = ready.Changes.Count, keys = plan.Keys, modelBindingsTested = false, installed = false };
    }
    public static async Task<object> PreviewMeshAliasesAsync(string path)
    {
        var ready = await PrepareMeshAliasPlanAsync(path);
        return new { outputSha256 = Hash(ready.Bytes), bytes = ready.Bytes.Length, changes = ready.Changes, dependencies = ready.Plan.Aliases,
            keys = ready.Plan.Keys, changedKeys = ready.Changes.Select(c => c.Field.Effect).Distinct().ToArray(),
            nativeFieldCatalogPass = true, nativeCandidateReadback = false, modelBindingsTested = false, installed = false };
    }
    public static async Task<object> ApplyMeshAliasesAsync(string path, string destination)
    {
        string full = ContentSafety.SafeOutput(destination); var ready = await PrepareMeshAliasPlanAsync(path);
        string sourceFolder = Path.GetDirectoryName(ready.Plan.Source.Path)!;
        if (full.Equals(sourceFolder,StringComparison.OrdinalIgnoreCase) || full.StartsWith(sourceFolder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Alias output must be outside the EFF source folder.");
        foreach (string source in ready.Pins.Keys)
        { string rel = Path.GetRelativePath(full,source); if (rel != ".." && !rel.StartsWith(".."+Path.DirectorySeparatorChar) && !Path.IsPathRooted(rel)) throw new IOException("Alias output cannot contain a source."); }
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (var pin in ready.Pins)
            { var handle = new FileStream(pin.Key,FileMode.Open,FileAccess.Read,FileShare.Read); locks.Add(handle); if (Convert.ToHexString(SHA256.HashData(handle)) != pin.Value) throw new IOException("Alias source changed before output."); }
            var final = await ReadNativeAsync(ready.Bytes,resourceFields:true);
            if (final.CountOffset != ready.Source.CountOffset || !final.Effects.Select(e => e.Name).SequenceEqual(ready.Source.Effects.Select(e => e.Name)) ||
                !ready.Bytes.AsSpan(0,final.CountOffset+4).SequenceEqual(ready.Source.Raw.AsSpan(0,ready.Source.CountOffset+4)) ||
                final.ResourceFields.Count != ready.Source.ResourceFields.Count) throw new IOException("Aliased EFF catalog preservation failed.");
            var fields = final.ResourceFields.ToDictionary(f => (f.Effect,f.SystemIndex,f.Kind,f.Slot));
            var edits = ready.Changes.ToDictionary(c => (c.Field.Effect,c.Field.SystemIndex,c.Field.Kind,c.Field.Slot));
            foreach (var field in ready.Source.ResourceFields)
            {
                var key = (field.Effect,field.SystemIndex,field.Kind,field.Slot);
                string expected = edits.TryGetValue(key,out var edit) ? edit.ToHex : field.ValueHex;
                if (!fields.TryGetValue(key,out var actual) || !Equal(expected,actual.ValueHex)) throw new IOException("Aliased native field identity/value differs.");
            }
            foreach (var row in ready.Source.Effects.Where(e => !ready.Plan.Keys.Contains(e.Name)))
            { var after = final.Effects.Single(e => e.Name == row.Name); if (row.Length != after.Length || !ready.Source.Raw.AsSpan(row.Offset,row.Length).SequenceEqual(ready.Bytes.AsSpan(after.Offset,after.Length))) throw new IOException("Unselected effect changed."); }
            pending = ContentSafety.FreshDirectory(full); const string file = "adapted-source.eff";
            File.WriteAllBytes(Path.Combine(pending,file),ready.Bytes); File.WriteAllBytes(Path.Combine(pending,"original.eff.bak"),ready.Source.Raw);
            if (!File.ReadAllBytes(Path.Combine(pending,file)).AsSpan().SequenceEqual(ready.Bytes)) throw new IOException("Aliased EFF disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new { version = 1, utc = DateTime.UtcNow, profile = ready.Plan.Version == 1 ? "EFF104/native-mesh-string-alias-v1" : "EFF104/native-mesh-string-case-alias-v2",
                reason = ready.Plan.Reason,
                sourcePins = ready.Pins, sourceSha256 = Hash(ready.Source.Raw), outputSha256 = Hash(ready.Bytes), effects = final.Effects.Count,
                keys = ready.Plan.Keys, changes = ready.Changes, dependencies = ready.Plan.Aliases, nativeReaderPass = true,
                changedKeys = ready.Changes.Select(c => c.Field.Effect).Distinct().ToArray(),
                nonMeshStringBytesPreserved = true, modelBindingsTested = false, renderingTested = false, eventsTested = false, worldTested = false, contentReady = false, installed = false });
            Directory.Move(pending,full); pending = null;
            return new { output = Path.Combine(full,file), outputSha256 = Hash(ready.Bytes), changes = ready.Changes.Count,
                nativeReaderPass = true, modelBindingsTested = false, contentReady = false, installed = false };
        }
        finally { foreach (var handle in locks) handle.Dispose(); if (pending is not null && Directory.Exists(pending)) Directory.Delete(pending,true); }
    }
    // v1 compares literal filename bytes. v2 explicitly opts into Windows
    // filename case matching; only native selected spans are still rewritten.
    private static bool ResourceAliasMatches(int version, string observedHex, string fromHex) => version == 1
        ? Equal(observedHex,fromHex)
        : StringComparer.OrdinalIgnoreCase.Equals(Encoding.Latin1.GetString(Convert.FromHexString(observedHex)),Encoding.ASCII.GetString(Convert.FromHexString(fromHex)));
}
