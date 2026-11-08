using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Add named copies of existing native definitions; never replace or rename a
// definition. A fallback is an authored adaptation, not recovered source data.
internal static partial class EffectMergeService
{
    private sealed record Fallback(string From, string To, string RecordSha256);
    private sealed record FallbackPlan(int Version, string Reason, string EffectHelperSha256,
        Pin Source, Pin Companion, List<Fallback> Copies, string? OutputSha256 = null);

    private static FallbackPlan ReadFallback(string path, bool sealedPlan)
    {
        byte[] raw = AliasJson(Path.GetFullPath(path));
        using var doc = JsonDocument.Parse(raw);
        Fields(doc.RootElement, sealedPlan
            ? ["version", "reason", "effectHelperSha256", "source", "companion", "copies", "outputSha256"]
            : ["version", "reason", "effectHelperSha256", "source", "companion", "copies"]);
        foreach (string name in new[] { "source", "companion" }) Fields(doc.RootElement.GetProperty(name), "path", "sha256");
        foreach (var row in doc.RootElement.GetProperty("copies").EnumerateArray()) Fields(row, "from", "to", "recordSha256");
        return JsonSerializer.Deserialize<FallbackPlan>(raw, Options) ?? throw new InvalidDataException("Fallback plan required.");
    }

    private static async Task<(FallbackPlan Plan, Catalog Before, Catalog After, byte[] Bytes)> PrepareFallback(string path, bool sealedPlan)
    {
        var p = ReadFallback(path, sealedPlan); string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        bool Name(string? n) => n is not null && System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9_]{1,128}$");
        if (p.Version != 1 || string.IsNullOrWhiteSpace(p.Reason) || p.Reason.Length > 2048 ||
            p.Copies is not { Count: > 0 and <= 64 } || p.Copies.Any(c => c is null || !Name(c.From) || !Name(c.To) || Equal(c.From,c.To)) ||
            p.Copies.Select(c => c.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Copies.Count)
            throw new InvalidDataException("Explicit bounded additive fallback copies required.");
        p = p with { Source = p.Source with { Path = Resolve(p.Source.Path,parent) }, Companion = p.Companion with { Path = Resolve(p.Companion.Path,parent) } };
        if (!Equal(Path.GetFileName(p.Source.Path), "visualeffect_02.eff") || !Equal(Path.GetFileName(p.Companion.Path), "visualeffect_01.eff"))
            throw new InvalidDataException("Native EFF02 plus companion EFF01 required.");
        var pins = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        Snapshot(new(Path.Combine(AppContext.BaseDirectory,"effect-reader.exe"),p.EffectHelperSha256),pins);
        var source = await ReadNativeAsync(Snapshot(p.Source,pins));
        var companion = await ReadNativeAsync(Snapshot(p.Companion,pins));
        var occupied = source.Effects.Concat(companion.Effects).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Published catalogs can intentionally share native ResourceEffect keys.
        // Preserve that registry unchanged; reject ambiguity within either file.
        if (new[] { source, companion }.Any(c => c.Effects.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Effects.Count) || p.Copies.Any(c => occupied.Contains(c.To)) ||
            source.Dependencies.Concat(companion.Dependencies).Any(d => d.Kind == "effect" && p.Copies.Any(c => Equal(c.To,d.Value))))
            throw new InvalidDataException("Ambiguous registry or fallback destination occupied/referenced.");
        var rows = source.Effects.ToDictionary(e => e.Name,StringComparer.Ordinal);
        long totalBytes = source.Raw.Length;
        foreach (var c in p.Copies)
        {
            if (!rows.TryGetValue(c.From,out var row)) throw new InvalidDataException("Fallback source absent.");
            totalBytes += row.Length + c.To.Length - c.From.Length;
        }
        if (totalBytes > MaxBytes) throw new InvalidDataException("Fallback output too large.");
        using var output = new MemoryStream((int)totalBytes); output.Write(source.Raw);
        foreach (var c in p.Copies)
        {
            if (!rows.TryGetValue(c.From,out var row) || !Equal(Hash(source.Raw.AsSpan(row.Offset,row.Length).ToArray()),c.RecordSha256))
                throw new InvalidDataException("Fallback source record seal differs.");
            int prefix = BinaryPrimitives.ReadInt32LittleEndian(source.Raw.AsSpan(row.Offset,4));
            if (prefix != c.From.Length + 1 || !source.Raw.AsSpan(row.Offset+4,prefix).SequenceEqual(Encoding.ASCII.GetBytes(c.From+"\0")))
                throw new InvalidDataException("Native single-NUL definition prefix required.");
            byte[] name = Encoding.ASCII.GetBytes(c.To+"\0"), size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size,name.Length); output.Write(size); output.Write(name);
            output.Write(source.Raw.AsSpan(row.Offset+4+prefix,row.Length-4-prefix));
        }
        if (output.Length > MaxBytes) throw new InvalidDataException("Fallback output too large.");
        byte[] bytes = output.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(source.CountOffset,4),source.Effects.Count+p.Copies.Count);
        if (sealedPlan && (p.OutputSha256 is null || !Equal(Hash(bytes),p.OutputSha256))) throw new InvalidDataException("Fallback output seal differs.");
        var final = await ReadNativeAsync(bytes);
        if (final.CountOffset != source.CountOffset || final.Effects.Count != source.Effects.Count+p.Copies.Count ||
            !bytes.AsSpan(0,source.CountOffset).SequenceEqual(source.Raw.AsSpan(0,source.CountOffset)) ||
            !bytes.AsSpan(source.CountOffset+4,source.Raw.Length-source.CountOffset-4).SequenceEqual(source.Raw.AsSpan(source.CountOffset+4)) ||
            !final.Effects.Select(e=>e.Name).SequenceEqual(source.Effects.Select(e=>e.Name).Concat(p.Copies.Select(c=>c.To))))
            throw new IOException("Fallback baseline preservation failed.");
        var dependencies = source.Dependencies.Concat(p.Copies.SelectMany(c => source.Dependencies.Where(d=>d.Effect==c.From).Select(d=>d with { Effect=c.To })));
        if (!final.Dependencies.SequenceEqual(dependencies)) throw new IOException("Fallback dependency identity failed.");
        if (pins.Any(pin=>ContentSafety.Hash(pin.Key)!=pin.Value)) throw new IOException("Fallback input changed.");
        return (p,source,final,bytes);
    }

    internal static async Task<object> CreateFallbackAsync(string mapping,string output)
    {
        string before = ContentSafety.Hash(mapping); var ready = await PrepareFallback(mapping,false);
        output = ContentSafety.SafeOutput(output);
        if (ContentSafety.Hash(mapping)!=before) throw new IOException("Fallback mapping changed.");
        ContentSafety.WriteJson(output,ready.Plan with { OutputSha256=Hash(ready.Bytes) });
        return new { plan=output, outputSha256=Hash(ready.Bytes), added=ready.Plan.Copies.Count, nativeReaderPass=true, installed=false };
    }
    internal static async Task<object> PreviewFallbackAsync(string plan)
    {
        var r=await PrepareFallback(plan,true);
        return new { outputSha256=Hash(r.Bytes), copies=r.Plan.Copies, baselineDefinitionsPreserved=true, nativeReaderPass=true, renderingTested=false };
    }
    internal static async Task<object> ApplyFallbackAsync(string plan,string destination)
    {
        string seal=ContentSafety.Hash(plan); var r=await PrepareFallback(plan,true); destination=ContentSafety.SafeOutput(destination);
        foreach(string input in new[]{Path.GetFullPath(plan),r.Plan.Source.Path,r.Plan.Companion.Path})
            if (input.Equals(destination,StringComparison.OrdinalIgnoreCase) || input.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                (input != Path.GetFullPath(plan) && destination.StartsWith(Path.GetDirectoryName(input)!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Fallback output overlaps input folder.");
        string pending=ContentSafety.FreshDirectory(destination);
        try
        {
            string file=Path.Combine(pending,"visualeffect_02.eff"); File.WriteAllBytes(file,r.Bytes);
            File.WriteAllBytes(Path.Combine(pending,"original.eff.bak"),r.Before.Raw);
            if(ContentSafety.Hash(plan)!=seal || ContentSafety.Hash(r.Plan.Source.Path)!=r.Plan.Source.Sha256 || ContentSafety.Hash(r.Plan.Companion.Path)!=r.Plan.Companion.Sha256 || ContentSafety.Hash(file)!=Hash(r.Bytes))
                throw new IOException("Fallback source/output changed.");
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new{profile="EFF104/additive-native-fallback-v1",utc=DateTime.UtcNow,plan=r.Plan,planSha256=seal,
                outputSha256=Hash(r.Bytes),baselineDefinitions=r.Before.Effects.Count,definitions=r.After.Effects.Count,baselineDefinitionsPreserved=true,
                dependencyReferencesPreserved=true,nativeReaderPass=true,renderingTested=false,installed=false,published=false});
            Directory.Move(pending,destination);
            return new{output=Path.Combine(destination,"visualeffect_02.eff"),outputSha256=Hash(r.Bytes),nativeReaderPass=true,installed=false};
        }
        finally{if(Directory.Exists(pending))Directory.Delete(pending,true);}
    }
}
