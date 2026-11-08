using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Native direct/multi texture string fields only. DFF materials, effect names,
// animation/UV/mesh/spline/event bytes remain outside this operation.
internal static partial class EffectMergeService
{
    private sealed record TextureAlias(string FromHex, string ToHex, Pin Raster, string TargetResource);
    private sealed record TextureAliasPlan(int Version, string Reason, string EffectHelperSha256,
        string MediaHelperSha256, Pin Source, List<string> Keys, List<TextureAlias> Aliases,
        string? TargetSha256 = null, int? TargetBytes = null);
    private sealed record TextureAliasChange(ResourceField Field, string ToHex, string TargetResource);
    private sealed record TextureAliasReady(TextureAliasPlan Plan, Catalog Source, Dictionary<string,string> Pins,
        List<TextureAliasChange> Changes, byte[] Bytes, List<object> Rasters);

    private static string TextureAliasName(string hex)
    {
        if (hex is null || hex.Length is < 10 or > 256 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException("Texture filename hex required; maximum128 bytes.");
        byte[] raw = Convert.FromHexString(hex);
        if (raw.Any(b => b < 33 || b > 126)) throw new InvalidDataException("Literal ASCII texture filename required.");
        string name = Encoding.ASCII.GetString(raw); ContentSafety.ResourcePath(name);
        if (!Regex.IsMatch(name,@"^[A-Za-z0-9_][A-Za-z0-9_.-]*\.(dds|png|bmp)$",RegexOptions.IgnoreCase) || name.Contains(".."))
            throw new InvalidDataException("Literal DDS/PNG/BMP basename only.");
        return name;
    }
    private static void TextureAliasFields(JsonElement json, bool sealedPlan)
    {
        AliasFields(json, sealedPlan
            ? ["version","reason","effectHelperSha256","mediaHelperSha256","source","keys","aliases","targetSha256","targetBytes"]
            : ["version","reason","effectHelperSha256","mediaHelperSha256","keys","aliases"]);
        if (sealedPlan) AliasFields(json.GetProperty("source"),"path","sha256");
        foreach (var alias in json.GetProperty("aliases").EnumerateArray())
        { AliasFields(alias,"fromHex","toHex","raster","targetResource"); AliasFields(alias.GetProperty("raster"),"path","sha256"); }
    }
    private static async Task<TextureAliasReady> PrepareTextureAliasesAsync(TextureAliasPlan plan,string folder,Dictionary<string,string> pins)
    {
        if (plan.Version is not (1 or 2) || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 2048 || plan.Source is null ||
            plan.Keys is null || plan.Keys.Count is < 1 or > 64 || plan.Keys.Any(k => k is null || !Regex.IsMatch(k,"^[A-Za-z0-9_]{1,128}$")) ||
            plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count || plan.Aliases is null || plan.Aliases.Count is < 1 or > 64)
            throw new InvalidDataException("Texture-alias v1 requires reason,1..64 effect keys and1..64 aliases.");
        plan = plan with { Source = plan.Source with { Path = Resolve(plan.Source.Path,folder) } };
        if (!Path.GetExtension(plan.Source.Path).Equals(".eff",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EFF source required.");
        Snapshot(new(Path.Combine(AppContext.BaseDirectory,"effect-reader.exe"),plan.EffectHelperSha256),pins);
        Snapshot(new(Path.Combine(AppContext.BaseDirectory,"media-helper.exe"),plan.MediaHelperSha256),pins);
        var catalog = await ReadNativeAsync(Snapshot(plan.Source,pins),resourceFields:true);
        foreach (string key in plan.Keys)
            if (catalog.Effects.Count(e => e.Name == key) != 1) throw new InvalidDataException("Effect key absent or ambiguous.");
        var aliases = new List<TextureAlias>(); var changes = new List<TextureAliasChange>(); var rasters = new List<object>();
        var from = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in plan.Aliases)
        {
            if (alias is null || alias.Raster is null) throw new InvalidDataException("Pinned native raster required.");
            string oldName = TextureAliasName(alias.FromHex), newName = TextureAliasName(alias.ToHex);
            if (!from.Add(oldName) || !to.Add(newName) || Equal(oldName,newName) || !Equal(Path.GetExtension(oldName),Path.GetExtension(newName)))
                throw new InvalidDataException("Duplicate/no-op aliases or extension conversion forbidden.");
            string resource = ContentSafety.ResourcePath(alias.TargetResource).Replace('\\','/');
            if (!Equal(resource,"texture/effect/"+newName)) throw new InvalidDataException("Texture targetResource must be texture/effect/new-name.");
            var raster = alias.Raster with { Path = Resolve(alias.Raster.Path,folder) };
            if (!Equal(Path.GetExtension(raster.Path),Path.GetExtension(newName))) throw new InvalidDataException("Pinned native raster extension differs.");
            Snapshot(raster,pins); rasters.Add(await AssetDependencyCatalog.InspectAsync("texture",raster.Path));
            if (catalog.ResourceFields.Any(f => f.Kind == "texture" && f.ValueHex.Length != 0 &&
                Equal(Encoding.Latin1.GetString(Convert.FromHexString(f.ValueHex)),newName)))
                throw new InvalidDataException("Alias target already referenced in source EFF.");
            var selected = catalog.ResourceFields.Where(f => plan.Keys.Contains(f.Effect) && f.Kind == "texture" && ResourceAliasMatches(plan.Version,f.ValueHex,alias.FromHex)).ToArray();
            if (selected.Length == 0) throw new InvalidDataException("Alias source absent from selected native texture fields.");
            foreach (var field in selected)
            {
                SerializedResourceValue(catalog.Raw,field);
                if (field.Bytes != field.ValueHex.Length/2+1) throw new InvalidDataException("Texture alias requires exact single-NUL string body.");
                changes.Add(new(field,Convert.ToHexString(Convert.FromHexString(alias.ToHex)),resource));
            }
            aliases.Add(alias with { Raster = raster with { Sha256 = pins[raster.Path] }, TargetResource = resource });
        }
        if (to.Overlaps(from)) throw new InvalidDataException("Alias swaps/chains forbidden.");
        plan = plan with { Aliases = aliases };
        long length = catalog.Raw.Length+changes.Sum(c => (long)c.ToHex.Length/2+1-c.Field.Bytes);
        if (length is < 18 or > MaxBytes) throw new InvalidDataException("Aliased EFF size limit.");
        using var output = new MemoryStream((int)length); int cursor = 0;
        foreach (var change in changes.OrderBy(c => c.Field.Offset))
        {
            if (change.Field.Offset < cursor) throw new InvalidDataException("Overlapping native alias spans.");
            output.Write(catalog.Raw.AsSpan(cursor,change.Field.Offset-cursor));
            byte[] value = Convert.FromHexString(change.ToHex), prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix,value.Length+1); output.Write(prefix); output.Write(value); output.WriteByte(0);
            cursor = change.Field.Offset+4+change.Field.Bytes;
        }
        output.Write(catalog.Raw.AsSpan(cursor)); byte[] bytes = output.ToArray();
        if (bytes.Length != length || (plan.TargetSha256 is not null && !Equal(Hash(bytes),plan.TargetSha256)) ||
            (plan.TargetBytes is not null && plan.TargetBytes != bytes.Length)) throw new InvalidDataException("Texture-alias output seal differs.");
        if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Texture-alias source/helper/raster changed.");
        return new(plan,catalog,pins,changes,bytes,rasters);
    }
    private static async Task<TextureAliasReady> PrepareTextureAliasPlanAsync(string path)
    {
        path = Path.GetFullPath(path); byte[] raw = AliasJson(path); using var json = JsonDocument.Parse(raw); TextureAliasFields(json.RootElement,true);
        var plan = JsonSerializer.Deserialize<TextureAliasPlan>(raw,Options) ?? throw new InvalidDataException("Texture alias plan required.");
        if (plan.TargetSha256 is null || plan.TargetSha256.Length != 64 || !plan.TargetSha256.All(Uri.IsHexDigit) || plan.TargetBytes is null)
            throw new InvalidDataException("Texture-alias output SHA/bytes seal required.");
        return await PrepareTextureAliasesAsync(plan,Path.GetDirectoryName(path)!,new(StringComparer.OrdinalIgnoreCase){[path]=Hash(raw)});
    }
    public static async Task<object> CreateTextureAliasPlanAsync(string source,string mapping,string destination)
    {
        source = Path.GetFullPath(source); mapping = Path.GetFullPath(mapping); destination = ContentSafety.SafeOutput(destination);
        byte[] raw = AliasJson(mapping); using var json = JsonDocument.Parse(raw); TextureAliasFields(json.RootElement,false);
        var value = JsonSerializer.Deserialize<TextureAliasPlan>(raw,Options) ?? throw new InvalidDataException("Texture alias mapping required.");
        var ready = await PrepareTextureAliasesAsync(value with { Source = new(source,ContentSafety.Hash(source)) },Path.GetDirectoryName(mapping)!,
            new(StringComparer.OrdinalIgnoreCase){[mapping]=Hash(raw)});
        var plan = ready.Plan with { TargetSha256 = Hash(ready.Bytes), TargetBytes = ready.Bytes.Length }; ContentSafety.WriteJson(destination,plan);
        return new { plan = destination, outputSha256 = plan.TargetSha256, bytes = plan.TargetBytes, changes = ready.Changes.Count, installed = false };
    }
    public static async Task<object> PreviewTextureAliasesAsync(string path)
    {
        var ready = await PrepareTextureAliasPlanAsync(path);
        return new { outputSha256 = Hash(ready.Bytes), bytes = ready.Bytes.Length, changes = ready.Changes, rasters = ready.Rasters,
            keys = ready.Plan.Keys, nativeCandidateReadback = false, renderingTested = false, installed = false };
    }
    public static async Task<object> ApplyTextureAliasesAsync(string path,string destination)
    {
        string full = ContentSafety.SafeOutput(destination); var ready = await PrepareTextureAliasPlanAsync(path);
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
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new { version = 1, utc = DateTime.UtcNow, profile = ready.Plan.Version == 1 ? "EFF104/native-texture-string-alias-v1" : "EFF104/native-texture-string-case-alias-v2",
                reason = ready.Plan.Reason, sourcePins = ready.Pins, sourceSha256 = Hash(ready.Source.Raw), outputSha256 = Hash(ready.Bytes),
                keys = ready.Plan.Keys, changes = ready.Changes, dependencies = ready.Plan.Aliases, nativeReaderPass = true,
                nonTextureStringBytesPreserved = true, renderingTested = false, eventsTested = false, worldTested = false, contentReady = false, installed = false });
            Directory.Move(pending,full); pending = null;
            return new { output = Path.Combine(full,file), outputSha256 = Hash(ready.Bytes), changes = ready.Changes.Count, nativeReaderPass = true, installed = false };
        }
        finally { foreach (var handle in locks) handle.Dispose(); if (pending is not null && Directory.Exists(pending)) Directory.Delete(pending,true); }
    }
}
