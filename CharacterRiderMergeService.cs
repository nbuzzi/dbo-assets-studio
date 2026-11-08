using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Taiwan-only, eventless SRP1 -> new SRP3 keys. Native ANM supplies duration;
// original records/events/flags/bones and unrelated models stay byte-exact.
internal static partial class CharacterAnimationMergeService
{
    private sealed record RiderPin(string Path, string Sha256);
    private sealed record RiderOperation(string Model, uint TemplateKey, uint TargetKey, string File, RiderPin Bindings);
    private sealed record RiderPlan(int Version, string Profile, string Reason, string MediaHelperSha256, RiderPin Target, List<RiderOperation> Operations);
    private sealed record RiderReady(RiderPlan Plan, byte[] Original, byte[] Candidate, Dictionary<string,string> Pins, List<object> Native, int Models);
    private static readonly JsonSerializerOptions RiderOptions = new(ContentSafety.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] RiderPoses = ["idle", "run", "start", "idle", "run", "run", "run", "run"];

    private static void RiderFields(JsonElement node, params string[] allowed)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Rider plan object required.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in node.EnumerateObject())
            if (!seen.Add(p.Name) || !allowed.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unknown/duplicate rider field: " + p.Name);
        if (seen.Count != allowed.Length) throw new InvalidDataException("Incomplete rider plan fields.");
    }
    private static string RiderResolve(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Rider source path required.");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder,path));
    }
    private static byte[] RiderSnapshot(RiderPin pin, Dictionary<string,string> pins, long maximum = 128L*1024*1024)
    {
        ContentSafety.NoLinks(pin.Path);
        if (pin.Sha256 is null || !Regex.IsMatch(pin.Sha256,"^[0-9a-fA-F]{64}$") || new FileInfo(pin.Path).Length is < 1 || new FileInfo(pin.Path).Length > maximum)
            throw new InvalidDataException("Rider pin/size invalid.");
        byte[] bytes = File.ReadAllBytes(pin.Path); string actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(pin.Sha256,StringComparison.OrdinalIgnoreCase) || ContentSafety.Hash(pin.Path) != actual) throw new IOException("Pinned rider source changed.");
        if (pins.TryGetValue(pin.Path,out var old) && old != actual) throw new IOException("Conflicting rider pin.");
        pins[pin.Path] = actual; return bytes;
    }
    private static void RiderUnchanged(Dictionary<string,string> pins)
    { if (pins.Any(p => ContentSafety.Hash(p.Key) != p.Value)) throw new IOException("Rider inputs changed."); }

    private static async Task<RiderReady> PrepareRiderAsync(string planPath)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 1024*1024) throw new InvalidDataException("Rider plan maximum1MiB.");
        byte[] raw = File.ReadAllBytes(planPath); using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        RiderFields(root,"version","profile","reason","mediaHelperSha256","target","operations");
        RiderFields(root.GetProperty("target"),"path","sha256");
        foreach (var op in root.GetProperty("operations").EnumerateArray())
        { RiderFields(op,"model","templateKey","targetKey","file","bindings"); RiderFields(op.GetProperty("bindings"),"path","sha256"); }
        var plan = JsonSerializer.Deserialize<RiderPlan>(raw,RiderOptions) ?? throw new InvalidDataException("Rider plan required.");
        if (plan.Version != 1 || plan.Profile != "vehicle-srp3-eventless-v1" || string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 2048 ||
            plan.Target is null || plan.Operations is null || plan.Operations.Count is < 8 or > 192)
            throw new InvalidDataException("Explicit bounded eventless SRP3 profile required.");
        string folder = Path.GetDirectoryName(planPath)!;
        plan = plan with { Target = plan.Target with { Path = RiderResolve(plan.Target.Path,folder) } };
        if (!Path.GetFileName(plan.Target.Path).Equals("characterproperty.rdf",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Taiwan CharacterProperty required.");
        var pins = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { [planPath] = Convert.ToHexString(SHA256.HashData(raw)) };
        RiderSnapshot(new(Path.Combine(AppContext.BaseDirectory,"media-helper.exe"),plan.MediaHelperSha256),pins);
        byte[] original = RiderSnapshot(plan.Target,pins);
        var catalog = JsonSerializer.SerializeToElement(CharacterPropertyCatalog.Inspect(plan.Target.Path),ContentSafety.Json);
        var rows = catalog.GetProperty("records").EnumerateArray().ToDictionary(r=>r.GetProperty("name").GetString()!,StringComparer.Ordinal);
        var groups = plan.Operations.GroupBy(o=>o.Model).ToArray(); var changed = new Dictionary<int,byte[]>(); var native = new List<object>();
        var cache = new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        var animationPins = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Key is null || !Regex.IsMatch(group.Key,"^A_((HEN|HFI|HMY|MMI|MWO)_[MF]|NFI|NMY)_(AD|CH)$") || !rows.TryGetValue(group.Key,out var row) ||
                group.Count() != 8 || !group.Select(o=>o.TargetKey).Order().SequenceEqual(Enumerable.Range(5200,8).Select(k=>(uint)k)))
                throw new InvalidDataException("Exact PC actor/eight distinct SRP3 keys required.");
            var animations = row.GetProperty("animations").EnumerateArray().ToDictionary(a=>a.GetProperty("key").GetUInt32());
            if (!animations.TryGetValue(5000,out var template) || template.GetProperty("flags").GetUInt32() != 0 || template.GetProperty("eventTypes").GetArrayLength() != 0 ||
                group.Any(o=>o.TemplateKey != 5000 || animations.ContainsKey(o.TargetKey)))
                throw new InvalidDataException("Eventless flags0 SRP1 template and unused SRP3 keys required.");
            using var record = new MemoryStream(); int start = row.GetProperty("start").GetInt32(), countAt = row.GetProperty("animationCountOffset").GetInt32();
            int animEnd = row.GetProperty("animationEnd").GetInt32(), end = row.GetProperty("end").GetInt32();
            record.Write(original,start,countAt-start); byte[] count = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(count,checked((uint)(animations.Count+8))); record.Write(count);
            record.Write(original,countAt+4,animEnd-countAt-4); string? actorModelHash = null;
            foreach (var op in group)
            {
                int pose = checked((int)(op.TargetKey-5200));
                if (op.File is null || !Regex.IsMatch(op.File,"^A_[A-Z0-9_]+_BIKE_"+RiderPoses[pose]+"\\.ANM$",RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Literal bike ANM must match SRP3 key pose.");
                ContentSafety.ResourcePath(op.File);
                var bp = op.Bindings with { Path = RiderResolve(op.Bindings.Path,folder) };
                byte[] bindingRaw = RiderSnapshot(bp,pins,1024*1024); using var bindingDoc = JsonDocument.Parse(bindingRaw); var b = bindingDoc.RootElement;
                RiderFields(b,"version","model","textures","animation");
                if (b.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("ANM-only native binding v1 required.");
                string bf = Path.GetDirectoryName(bp.Path)!;
                RiderPin Asset(JsonElement a)
                { RiderFields(a,"path","sha256"); return new(RiderResolve(a.GetProperty("path").GetString()!,bf),a.GetProperty("sha256").GetString()!); }
                var model = Asset(b.GetProperty("model")); var anm = Asset(b.GetProperty("animation"));
                if (!Path.GetFileName(model.Path).Equals(row.GetProperty("mesh").GetString(),StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetExtension(model.Path).Equals(".dff",StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(anm.Path).Equals(op.File,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Exact actor mesh and literal ANM filename binding required.");
                RiderSnapshot(model,pins); RiderSnapshot(anm,pins);
                if (actorModelHash is not null && actorModelHash != pins[model.Path]) throw new InvalidDataException("Actor has conflicting DFF payloads.");
                actorModelHash = pins[model.Path];
                if (animationPins.TryGetValue(op.File,out var same) && same != pins[anm.Path]) throw new InvalidDataException("One ANM name has conflicting payloads.");
                animationPins[op.File] = pins[anm.Path];
                foreach (var tex in b.GetProperty("textures").EnumerateArray())
                {
                    RiderFields(tex,"nameHex","maskHex","path","sha256");
                    RiderSnapshot(new(RiderResolve(tex.GetProperty("path").GetString()!,bf),tex.GetProperty("sha256").GetString()!),pins);
                }
                if (!cache.TryGetValue(bp.Path,out var result))
                { result = JsonSerializer.SerializeToElement(await AssetBindingCatalog.InspectAsync(bp.Path),ContentSafety.Json); cache[bp.Path] = result; }
                if (!result.GetProperty("nativeHierarchyPlayback").GetBoolean() || !result.GetProperty("realTextureBindings").GetBoolean() || result.GetProperty("texturesSubstituted").GetBoolean())
                    throw new InvalidDataException("Native model/ANM/real raster playback required.");
                float duration = result.GetProperty("native").GetProperty("animationBinding").GetProperty("duration").GetSingle();
                if (!float.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Positive native ANM duration required.");
                int ts = template.GetProperty("start").GetInt32(), te = template.GetProperty("end").GetInt32(); int nul = Array.IndexOf(original,(byte)0,ts,te-ts);
                byte[] payload = original.AsSpan(nul+1,te-nul-1).ToArray();
                if (payload.Length != 16) throw new InvalidDataException("Eventless template payload must be16bytes.");
                BinaryPrimitives.WriteUInt32LittleEndian(payload,op.TargetKey); BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4),duration);
                record.Write(Encoding.ASCII.GetBytes(op.File)); record.WriteByte(0); record.Write(payload);
                native.Add(new { op.Model,op.TemplateKey,op.TargetKey,op.File,duration,bindingsSha256=pins[bp.Path],result });
            }
            record.Write(original,animEnd,end-animEnd); changed.Add(start,record.ToArray());
        }
        using var output = new MemoryStream(); int cursor = 0;
        foreach (var row in catalog.GetProperty("records").EnumerateArray())
        {
            int start = row.GetProperty("start").GetInt32(), end = row.GetProperty("end").GetInt32(); output.Write(original,cursor,start-cursor);
            if (changed.TryGetValue(start,out var replacement)) output.Write(replacement); else output.Write(original,start,end-start); cursor = end;
        }
        output.Write(original,cursor,original.Length-cursor); RiderUnchanged(pins);
        return new(plan,original,output.ToArray(),pins,native,catalog.GetProperty("count").GetInt32());
    }
    public static async Task<object> PreviewRiderAsync(string planPath)
    {
        var ready = await PrepareRiderAsync(planPath);
        return new { outputSha256=Convert.ToHexString(SHA256.HashData(ready.Candidate)),bytes=ready.Candidate.Length,actors=ready.Plan.Operations.Select(o=>o.Model).Distinct().Count(),
            operations=ready.Plan.Operations.Count,bindings=ready.Native,existingEntriesPreserved=true,nativeModelAnimationPlayback=true,nativePropertyReaderPass=false,
            renderingTested=false,eventsTested=false,worldTested=false,contentReady=false,runtimeInstalled=false };
    }
    public static async Task<object> ApplyRiderAsync(string planPath, string output)
    {
        var ready = await PrepareRiderAsync(planPath); output = ContentSafety.SafeOutput(output);
        if (Directory.Exists(output) || File.Exists(output) || ready.Pins.Keys.Any(p=>Path.GetFullPath(p).StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) ||
            output.StartsWith(Path.GetDirectoryName(ready.Plan.Target.Path)!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Fresh rider output outside inputs required.");
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            foreach (string pinnedFile in ready.Pins.Keys) locks.Add(new(pinnedFile,FileMode.Open,FileAccess.Read,FileShare.Read)); RiderUnchanged(ready.Pins);
            pending = ContentSafety.FreshDirectory(output); string fileName = Path.GetFileName(ready.Plan.Target.Path), file = Path.Combine(pending,fileName); File.WriteAllBytes(file,ready.Candidate);
            var readback = JsonSerializer.SerializeToElement(CharacterPropertyCatalog.Inspect(file),ContentSafety.Json);
            if (readback.GetProperty("count").GetInt32() != ready.Models) throw new IOException("Rider output model count changed.");
            File.WriteAllBytes(Path.Combine(pending,fileName+".original.bak"),ready.Original);
            var audit = new { profile=ready.Plan.Profile,reason=ready.Plan.Reason,target=ready.Plan.Target,inputPins=ready.Pins,operations=ready.Plan.Operations,bindings=ready.Native,
                output=Path.Combine(output,fileName),outputSha256=ContentSafety.Hash(file),models=ready.Models,existingEntriesPreserved=true,structuralReaderPass=true,
                nativeModelAnimationPlayback=true,nativePropertyReaderPass=false,renderingTested=false,eventsTested=false,worldTested=false,contentReady=false,runtimeInstalled=false };
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),audit); RiderUnchanged(ready.Pins); Directory.Move(pending,output); pending = null; return audit;
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending is not null && Directory.Exists(pending)) Directory.Delete(pending,true); }
    }
}
