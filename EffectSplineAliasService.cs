using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Clone native spline records under owned keys, retaining every original spline.
// Retarget only selected systems at fixed spans traced by the native loader.
internal static partial class EffectMergeService
{
    private sealed record SplineAlias(string From, string To, string RecordSha256);
    private sealed record SplineAliasPlan(int Version, string Reason, string EffectHelperSha256,
        Pin Source, Pin Target, Pin Companion, List<string> Keys, List<SplineAlias> Aliases,
        string? TargetSha256 = null, int? TargetBytes = null);
    private sealed record SplineAliasReady(SplineAliasPlan Plan, Catalog Source,
        Dictionary<string,string> Pins, List<ResourceField> Changes, byte[] Bytes);
    private static void SplineAliasFields(JsonElement value, bool sealedPlan)
    {
        var fields = new List<string>{ "version", "reason", "effectHelperSha256", "target", "companion", "keys", "aliases" };
        if (sealedPlan) fields.AddRange(new[]{ "source", "targetSha256", "targetBytes" });
        AliasFields(value,fields.ToArray());
        foreach (string name in sealedPlan ? new[]{"source","target","companion"} : new[]{"target","companion"})
            AliasFields(value.GetProperty(name),"path","sha256");
        foreach (var alias in value.GetProperty("aliases").EnumerateArray()) AliasFields(alias,"from","to","recordSha256");
    }
    private static async Task<SplineAliasReady> PrepareSplineAliasesAsync(SplineAliasPlan plan, string folder, Dictionary<string,string> pins)
    {
        bool Name(string? value) => value is not null && Regex.IsMatch(value,"^[A-Za-z0-9_][A-Za-z0-9_-]{0,127}$");
        if (plan.Version != 1 || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 2048 ||
            plan.Source is null || plan.Target is null || plan.Companion is null || plan.Keys is not {Count: >0 and <=64} ||
            plan.Keys.Any(k => !Name(k)) || plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count ||
            plan.Aliases is not {Count: >0 and <=64} || plan.Aliases.Any(a => a is null || !Name(a.From) ||
                a.To is null || !Regex.IsMatch(a.To,"^[A-Za-z0-9_]{1,128}$") || Equal(a.From,a.To) ||
                a.RecordSha256 is null || a.RecordSha256.Length != 64 || !a.RecordSha256.All(Uri.IsHexDigit)) ||
            plan.Aliases.Select(a => a.From).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Aliases.Count ||
            plan.Aliases.Select(a => a.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Aliases.Count)
            throw new InvalidDataException("Spline alias v1 requires unique selected effects and exact sealed source records; owned ASCII destinations.");
        plan = plan with {Source=plan.Source with {Path=Resolve(plan.Source.Path,folder)},
            Target=plan.Target with {Path=Resolve(plan.Target.Path,folder)},Companion=plan.Companion with {Path=Resolve(plan.Companion.Path,folder)}};
        if (!Equal(Path.GetExtension(plan.Source.Path),".eff") || !Equal(Path.GetFileName(plan.Target.Path),"visualeffect_02.eff") ||
            !Equal(Path.GetFileName(plan.Companion.Path),"visualeffect_01.eff")) throw new InvalidDataException("EFF source and exact target02/companion01 required.");
        Snapshot(new(Path.Combine(AppContext.BaseDirectory,"effect-reader.exe"),plan.EffectHelperSha256),pins);
        var source = await ReadNativeAsync(Snapshot(plan.Source,pins),resourceFields:true);
        if(source.ResourceFieldVersion != 2)throw new InvalidDataException("Native R2 fixed spline span reader required.");
        var target = await ReadNativeAsync(Snapshot(plan.Target,pins)); var companion = await ReadNativeAsync(Snapshot(plan.Companion,pins));
        foreach (var catalog in new[]{source,target,companion})
            if (catalog.Splines.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Splines.Count)
                throw new InvalidDataException("Ambiguous spline registry.");
        if (plan.Keys.Any(k => source.Effects.Count(r => r.Name == k) != 1)) throw new InvalidDataException("Selected effect absent or ambiguous.");
        var occupied = source.Splines.Concat(target.Splines).Concat(companion.Splines).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapping = plan.Aliases.ToDictionary(a => a.From,StringComparer.Ordinal);
        if (plan.Aliases.Any(a => occupied.Contains(a.To) || source.Dependencies.Any(d => d.Kind == "spline" && Equal(d.Value,a.To))))
            throw new InvalidDataException("Owned spline key already exists or is referenced; swaps/chains forbidden.");
        var changes = new List<ResourceField>(); var clones = new List<byte[]>();
        foreach (var alias in plan.Aliases)
        {
            var row = source.Splines.SingleOrDefault(r => r.Name == alias.From) ?? throw new InvalidDataException("Source spline missing.");
            if (!Equal(Hash(source.Raw.AsSpan(row.Offset,row.Length).ToArray()),alias.RecordSha256)) throw new IOException("Source spline seal changed.");
            int prefix = 4 + BinaryPrimitives.ReadInt32LittleEndian(source.Raw.AsSpan(row.Offset,4));
            if (prefix != 4+alias.From.Length+1 || !source.Raw.AsSpan(row.Offset+4,prefix-4).SequenceEqual(Encoding.ASCII.GetBytes(alias.From+"\0")))
                throw new InvalidDataException("Spline definition requires exact single NUL name.");
            var fields = source.ResourceFields.Where(f => plan.Keys.Contains(f.Effect) && f.Kind == "spline" &&
                Equal(f.ValueHex,Convert.ToHexString(Encoding.ASCII.GetBytes(alias.From)))).ToArray();
            var refs = source.Dependencies.Where(d => plan.Keys.Contains(d.Effect) && d.Kind == "spline" && d.Value == alias.From).Select(d => d.Effect).ToHashSet();
            if (fields.Length == 0 || !fields.Select(f => f.Effect).ToHashSet().SetEquals(refs)) throw new InvalidDataException("Native fixed spline spans missing or incomplete.");
            changes.AddRange(fields);
            using var clone = new MemoryStream(); byte[] name=Encoding.ASCII.GetBytes(alias.To+"\0"), length=new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length,name.Length);clone.Write(length);clone.Write(name);
            clone.Write(source.Raw.AsSpan(row.Offset+prefix,row.Length-prefix));clones.Add(clone.ToArray());
        }
        long total = (long)source.Raw.Length+clones.Sum(c => (long)c.Length);
        if (total > MaxBytes || source.Splines.Count+clones.Count >= 20000) throw new InvalidDataException("Spline alias output bound.");
        using var output = new MemoryStream((int)total);
        output.Write(source.Raw.AsSpan(0,10)); byte[] count=new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count,source.Splines.Count+clones.Count);output.Write(count);
        output.Write(source.Raw.AsSpan(14,source.CountOffset-14));foreach(var clone in clones)output.Write(clone);
        output.Write(source.Raw.AsSpan(source.CountOffset,4));
        foreach(var row in source.Effects)
        {
            byte[] bytes=source.Raw.AsSpan(row.Offset,row.Length).ToArray();
            foreach(var field in changes.Where(f => f.Effect==row.Name))
            {
                string from=Encoding.ASCII.GetString(Convert.FromHexString(field.ValueHex));
                byte[] to=Encoding.ASCII.GetBytes(mapping[from].To+"\0");
                if(field.Bytes!=260 || to.Length>field.Bytes)throw new InvalidDataException("Fixed spline name ABI differs.");
                // Keep opaque padding after both logical strings exactly as received.
                int modified=Math.Max(from.Length+1,to.Length);bytes.AsSpan(field.Offset-row.Offset,modified).Clear();
                to.CopyTo(bytes,field.Offset-row.Offset);
            }
            output.Write(bytes);
        }
        byte[] result=output.ToArray();
        if(result.Length!=total || (plan.TargetSha256 is not null&&!Equal(Hash(result),plan.TargetSha256)) ||
            (plan.TargetBytes is not null&&result.Length!=plan.TargetBytes))throw new IOException("Spline alias target seal differs.");
        if(pins.Any(p=>ContentSafety.Hash(p.Key)!=p.Value))throw new IOException("Spline alias inputs changed.");
        return new(plan,source,pins,changes,result);
    }
    private static async Task<SplineAliasReady> PrepareSplineAliasPlanAsync(string path)
    {
        path=Path.GetFullPath(path);byte[] raw=AliasJson(path);using var json=JsonDocument.Parse(raw);SplineAliasFields(json.RootElement,true);
        var plan=JsonSerializer.Deserialize<SplineAliasPlan>(raw,Options)??throw new InvalidDataException("Spline plan required.");
        if(plan.TargetSha256 is null||plan.TargetSha256.Length!=64||!plan.TargetSha256.All(Uri.IsHexDigit)||plan.TargetBytes is null)
            throw new InvalidDataException("Spline output SHA/size required.");
        return await PrepareSplineAliasesAsync(plan,Path.GetDirectoryName(path)!,new(StringComparer.OrdinalIgnoreCase){[path]=Hash(raw)});
    }
    public static async Task<object> CreateSplineAliasPlanAsync(string source,string mapping,string destination)
    {
        source=Path.GetFullPath(source);mapping=Path.GetFullPath(mapping);destination=ContentSafety.SafeOutput(destination);
        byte[] raw=AliasJson(mapping);using var json=JsonDocument.Parse(raw);SplineAliasFields(json.RootElement,false);
        var spec=JsonSerializer.Deserialize<SplineAliasPlan>(raw,Options)??throw new InvalidDataException("Spline mapping required.");
        var ready=await PrepareSplineAliasesAsync(spec with{Source=new(source,ContentSafety.Hash(source))},Path.GetDirectoryName(mapping)!,
            new(StringComparer.OrdinalIgnoreCase){[mapping]=Hash(raw)});
        ContentSafety.WriteJson(destination,ready.Plan with{TargetSha256=Hash(ready.Bytes),TargetBytes=ready.Bytes.Length});
        return new{plan=destination,sha256=Hash(ready.Bytes),bytes=ready.Bytes.Length,clones=ready.Plan.Aliases.Count,retargetedFields=ready.Changes.Count,installed=false};
    }
    public static async Task<object> PreviewSplineAliasesAsync(string path)
    {
        var ready=await PrepareSplineAliasPlanAsync(path);
        return new{sha256=Hash(ready.Bytes),bytes=ready.Bytes.Length,clones=ready.Plan.Aliases,retargetedFields=ready.Changes,
            nativeCandidateReadback=false,renderingTested=false,installed=false};
    }
    public static async Task<object> ApplySplineAliasesAsync(string path,string destination)
    {
        string full=ContentSafety.SafeOutput(destination);var ready=await PrepareSplineAliasPlanAsync(path);
        foreach(string input in ready.Pins.Keys)
        {
            string relative=Path.GetRelativePath(full,input);
            if(relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar)&&!Path.IsPathRooted(relative))throw new IOException("Output cannot contain an input.");
            if(full.Equals(Path.GetDirectoryName(input),StringComparison.OrdinalIgnoreCase)||full.StartsWith(Path.GetDirectoryName(input)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output must be outside input folders.");
        }
        var locks=new List<FileStream>();string? pending=null;
        try
        {
            foreach(var pin in ready.Pins){var handle=new FileStream(pin.Key,FileMode.Open,FileAccess.Read,FileShare.Read);locks.Add(handle);
                if(Convert.ToHexString(SHA256.HashData(handle))!=pin.Value)throw new IOException("Spline input changed before output.");}
            var final=await ReadNativeAsync(ready.Bytes,resourceFields:true);
            var mapping=ready.Plan.Aliases.ToDictionary(a=>a.From,a=>a.To,StringComparer.Ordinal);
            if(final.Effects.Count!=ready.Source.Effects.Count||final.Splines.Count!=ready.Source.Splines.Count+mapping.Count||
                !final.Effects.Select(r=>r.Name).SequenceEqual(ready.Source.Effects.Select(r=>r.Name))||
                !final.Splines.Select(r=>r.Name).SequenceEqual(ready.Source.Splines.Select(r=>r.Name).Concat(ready.Plan.Aliases.Select(a=>a.To)))||
                !ready.Bytes.AsSpan(0,10).SequenceEqual(ready.Source.Raw.AsSpan(0,10)))throw new IOException("Spline catalog preservation failed.");
            for(int i=0;i<ready.Source.Splines.Count;i++)
            {
                var a=ready.Source.Splines[i];var b=final.Splines[i];
                if(a.Length!=b.Length||!ready.Source.Raw.AsSpan(a.Offset,a.Length).SequenceEqual(ready.Bytes.AsSpan(b.Offset,b.Length)))throw new IOException("Existing spline changed.");
            }
            foreach(var alias in ready.Plan.Aliases)
            {
                var a=ready.Source.Splines.Single(r=>r.Name==alias.From);var b=final.Splines.Single(r=>r.Name==alias.To);
                var oldBody=ready.Source.Raw.AsSpan(a.Offset+5+alias.From.Length,a.Length-5-alias.From.Length).ToArray();
                if(!oldBody.AsSpan().SequenceEqual(ready.Bytes.AsSpan(b.Offset+5+alias.To.Length,b.Length-5-alias.To.Length)))throw new IOException("Spline clone payload changed.");
            }
            var edits=ready.Changes.ToDictionary(f=>(f.Effect,f.SystemIndex,f.Kind,f.Slot));
            if(final.ResourceFields.Count!=ready.Source.ResourceFields.Count)throw new IOException("Spline resource field count changed.");
            var finalFields=final.ResourceFields.ToDictionary(f=>(f.Effect,f.SystemIndex,f.Kind,f.Slot));
            foreach(var field in ready.Source.ResourceFields)
            {
                string expected=edits.ContainsKey((field.Effect,field.SystemIndex,field.Kind,field.Slot))?
                    Convert.ToHexString(Encoding.ASCII.GetBytes(mapping[Encoding.ASCII.GetString(Convert.FromHexString(field.ValueHex))])):field.ValueHex;
                if(!finalFields.TryGetValue((field.Effect,field.SystemIndex,field.Kind,field.Slot),out var after)||!Equal(expected,after.ValueHex)||field.Bytes!=after.Bytes)
                    throw new IOException("Spline retarget native field differs.");
            }
            if(!final.Dependencies.SequenceEqual(ready.Source.Dependencies.Select(d=>d.Kind=="spline"&&ready.Plan.Keys.Contains(d.Effect)&&mapping.ContainsKey(d.Value)?d with{Value=mapping[d.Value]}:d)))
                throw new IOException("Spline dependency preservation failed.");
            foreach(var before in ready.Source.Effects)
            {
                var after=final.Effects.Single(r=>r.Name==before.Name);byte[] expected=ready.Source.Raw.AsSpan(before.Offset,before.Length).ToArray();
                foreach(var field in ready.Changes.Where(f=>f.Effect==before.Name))
                {
                    string from=Encoding.ASCII.GetString(Convert.FromHexString(field.ValueHex));byte[] to=Encoding.ASCII.GetBytes(mapping[from]+"\0");
                    expected.AsSpan(field.Offset-before.Offset,Math.Max(from.Length+1,to.Length)).Clear();to.CopyTo(expected,field.Offset-before.Offset);
                }
                if(expected.Length!=after.Length||!expected.AsSpan().SequenceEqual(ready.Bytes.AsSpan(after.Offset,after.Length)))throw new IOException("Nonselected effect bytes changed.");
            }
            pending=ContentSafety.FreshDirectory(full);File.WriteAllBytes(Path.Combine(pending,"adapted-source.eff"),ready.Bytes);
            File.WriteAllBytes(Path.Combine(pending,"original.eff.bak"),ready.Source.Raw);
            if(!File.ReadAllBytes(Path.Combine(pending,"adapted-source.eff")).AsSpan().SequenceEqual(ready.Bytes))throw new IOException("Spline disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new{version=1,profile="EFF104/native-fixed-spline-reference-and-owned-clone-v1",
                utc=DateTime.UtcNow,reason=ready.Plan.Reason,sourcePins=ready.Pins,sourceSha256=Hash(ready.Source.Raw),outputSha256=Hash(ready.Bytes),
                keys=ready.Plan.Keys,clones=ready.Plan.Aliases,retargetedFields=ready.Changes,originalSplinesPreserved=true,
                unselectedEffectBytesPreserved=true,nativeReaderPass=true,splinePlaybackTested=false,renderingTested=false,worldTested=false,installed=false});
            Directory.Move(pending,full);pending=null;return new{output=Path.Combine(full,"adapted-source.eff"),sha256=Hash(ready.Bytes),
                clones=mapping.Count,retargetedFields=ready.Changes.Count,nativeReaderPass=true,installed=false};
        }
        finally{foreach(var handle in locks)handle.Dispose();if(pending is not null&&Directory.Exists(pending))Directory.Delete(pending,true);}
    }
}
