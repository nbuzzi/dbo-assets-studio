using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// QuestTextDataTable.cpp uses the same margin1/id32/length16/UTF16 record
// codec as TextTable. Reconcile exact records, never a fixed std::wstring ABI.
internal static class QuestTextReconcileService
{
    private sealed record Input(string Path, string Sha256);
    private sealed record Choice(string Id, string Take);
    private sealed record PlanData(int Version, Input Base, Input Ours, Input Theirs, Choice[] Resolutions);
    private sealed record Decision(uint Id, string Take, bool Conflict);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static (byte[] Bytes, Dictionary<uint,byte[]> Rows) Read(string path)
    {
        ContentSafety.NoLinks(path);
        if (!Path.GetFileName(path).Equals("table_quest_text_data.rdf", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length is <1 or >128*1024*1024)
            throw new InvalidDataException("Native QuestText basename and maximum128MiB required.");
        byte[] bytes=File.ReadAllBytes(path);
        var rows=MapNameRdfService.ReadRecords(bytes, allowZero:true);
        foreach(var row in rows.Values) Utf16.GetString(row.AsSpan(6));
        return (bytes,rows);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static object Compare(string baseline,string ours,string theirs)
    {
        var b=Read(baseline);var a=Read(ours);var j=Read(theirs);
        bool Equal(byte[]? x,byte[]? y)=>x is null?y is null:y is not null&&x.AsSpan().SequenceEqual(y);
        var changes=new List<object>();int conflicts=0;
        foreach(uint id in b.Rows.Keys.Concat(a.Rows.Keys).Concat(j.Rows.Keys).Distinct().Order()){
            b.Rows.TryGetValue(id,out var bv);a.Rows.TryGetValue(id,out var av);j.Rows.TryGetValue(id,out var jv);
            if(Equal(bv,av)&&Equal(bv,jv))continue;
            string status=av is null?"restore-incoming-or-base":jv is null?"preserve-ours":Equal(av,jv)?"same-change":Equal(av,bv)?"theirs-only":Equal(jv,bv)?"ours-only":"conflict";
            if(status=="conflict")++conflicts;
            string? Text(byte[]? row)=>row is null?null:Utf16.GetString(row.AsSpan(6));
            changes.Add(new{id=id.ToString(),status,baseline=Text(bv),ours=Text(av),theirs=Text(jv)});
        }
        return new{schema="TaiwanQuestText",baselineSha256=Hash(b.Bytes),oursSha256=Hash(a.Bytes),theirsSha256=Hash(j.Bytes),baseRecords=b.Rows.Count,oursRecords=a.Rows.Count,theirsRecords=j.Rows.Count,conflicts,changes,installed=false};
    }
    private static Input Pin(string path){var r=Read(path);return new(Path.GetFullPath(path),Hash(r.Bytes));}
    private static Dictionary<uint,byte[]> Pinned(Input input)
    {
        var r=Read(input.Path);if(Hash(r.Bytes)!=input.Sha256)throw new IOException("QuestText input changed since reviewed plan.");return r.Rows;
    }
    private static (Dictionary<uint,byte[]> Rows, List<Decision> Decisions) Resolve(PlanData p)
    {
        if(p.Version!=1)throw new InvalidDataException("QuestText plan version1 required.");
        var b=Pinned(p.Base);var a=Pinned(p.Ours);var j=Pinned(p.Theirs);
        var resolutions=new Dictionary<uint,string>();
        foreach(var choice in p.Resolutions){
            if(!uint.TryParse(choice.Id,out uint id)||choice.Take is not("base" or "ours" or "theirs")||!resolutions.TryAdd(id,choice.Take))
                throw new InvalidDataException("Unique existing IDs and base/ours/theirs choices required; no deletion.");
        }
        bool Equal(byte[]? x,byte[]? y)=>x is null?y is null:y is not null&&x.AsSpan().SequenceEqual(y);
        var rows=new Dictionary<uint,byte[]>();var decisions=new List<Decision>();var used=new HashSet<uint>();
        foreach(uint id in b.Keys.Concat(a.Keys).Concat(j.Keys).Distinct().Order()){
            b.TryGetValue(id,out var bv);a.TryGetValue(id,out var av);j.TryGetValue(id,out var jv);
            string take;bool conflict=false;
            if(av is null)take=jv is null?"base":"theirs";
            else if(jv is null)take="ours";
            else if(Equal(av,jv)||Equal(jv,bv))take="ours";
            else if(Equal(av,bv))take="theirs";
            else {conflict=true;if(!resolutions.TryGetValue(id,out take!))throw new InvalidDataException($"QuestText content conflict ID{id}; explicit reviewed choice required.");used.Add(id);}
            byte[]? selected=take=="base"?bv:take=="ours"?av:jv;
            if(selected is null)throw new InvalidDataException("QuestText resolution cannot remove a base/current/incoming ID.");
            rows.Add(id,selected);decisions.Add(new(id,take,conflict));
        }
        if(resolutions.Keys.Any(id=>!used.Contains(id)))throw new InvalidDataException("QuestText resolution names a non-conflicting/absent ID.");
        return(rows,decisions);
    }
    public static object Plan(string baseline,string ours,string theirs,string decisions,string output)
    {
        ContentSafety.NoLinks(decisions);if(new FileInfo(decisions).Length>1024*1024)throw new InvalidDataException("QuestText decisions limit1MiB.");
        using var d=JsonDocument.Parse(File.ReadAllBytes(decisions));
        if(d.RootElement.EnumerateObject().Any(x=>x.Name!="resolutions"))throw new InvalidDataException("Only resolutions allowed.");
        foreach(var x in d.RootElement.GetProperty("resolutions").EnumerateArray())if(x.EnumerateObject().Any(f=>f.Name is not("id" or "take")))throw new InvalidDataException("Only id/take allowed.");
        var choices=d.RootElement.GetProperty("resolutions").Deserialize<Choice[]>(ContentSafety.Json)??[];
        var p=new PlanData(1,Pin(baseline),Pin(ours),Pin(theirs),choices);var result=Resolve(p);
        string path=ContentSafety.SafeOutput(output);if(File.Exists(path))throw new IOException("Fresh QuestText plan required.");
        ContentSafety.WriteJson(path,p);return new{plan=path,planSha256=ContentSafety.Hash(path),records=result.Rows.Count,conflictsResolved=result.Decisions.Count(x=>x.Conflict),installed=false};
    }
    private static PlanData Open(string path)
    {
        ContentSafety.NoLinks(path);if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("QuestText plan limit1MiB.");
        using var d=JsonDocument.Parse(File.ReadAllBytes(path));
        if(d.RootElement.EnumerateObject().Any(x=>x.Name is not("version" or "base" or "ours" or "theirs" or "resolutions")))throw new InvalidDataException("Unknown QuestText plan field.");
        return d.RootElement.Deserialize<PlanData>(ContentSafety.Json)??throw new InvalidDataException("QuestText plan required.");
    }
    public static object Preview(string path){var p=Open(path);var r=Resolve(p);return new{plan=p,planSha256=ContentSafety.Hash(path),records=r.Rows.Count,conflictsResolved=r.Decisions.Count(x=>x.Conflict),allBaseCurrentIncomingIDsPreserved=true,nativeReaderTested=false};}
    public static object Apply(string path,string output)
    {
        var p=Open(path);string planHash=ContentSafety.Hash(path);
        var locks=new[]{path,p.Base.Path,p.Ours.Path,p.Theirs.Path}.Distinct(StringComparer.OrdinalIgnoreCase).Select(x=>new FileStream(x,FileMode.Open,FileAccess.Read,FileShare.Read)).ToArray();
        string? pending=null;
        try{
            if(ContentSafety.Hash(path)!=planHash)throw new IOException("QuestText plan changed.");var r=Resolve(p);
            using var bytes=new MemoryStream();bytes.WriteByte(1);foreach(var row in r.Rows.OrderBy(x=>x.Key))bytes.Write(row.Value);
            byte[] candidate=bytes.ToArray();var readback=MapNameRdfService.ReadRecords(candidate,allowZero:true);
            if(readback.Count!=r.Rows.Count||r.Rows.Any(x=>!readback[x.Key].AsSpan().SequenceEqual(x.Value)))throw new InvalidDataException("QuestText record readback mismatch.");
            string destination=ContentSafety.SafeOutput(output);pending=ContentSafety.FreshDirectory(destination);
            string file=Path.Combine(pending,Path.GetFileName(p.Ours.Path));using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(candidate);stream.Flush(true);}
            foreach(var(role,input) in new[]{("base",p.Base),("ours",p.Ours),("theirs",p.Theirs)})File.Copy(input.Path,Path.Combine(pending,role+".rdf.bak"));
            File.Copy(path,Path.Combine(pending,"plan.json"));
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new{schema="TaiwanQuestText",version=1,planSha256=planHash,source=p.Ours.Path,sourceSha256=p.Ours.Sha256,outputSha256=Hash(candidate),records=r.Rows.Count,allBaseCurrentIncomingIDsPreserved=true,decisions=r.Decisions,nativeReaderTested=false,installed=false});
            if(ContentSafety.Hash(file)!=Hash(candidate))throw new IOException("QuestText disk readback mismatch.");Directory.Move(pending,destination);pending=null;return new{output=Path.Combine(destination,Path.GetFileName(p.Ours.Path)),outputSha256=Hash(candidate),records=r.Rows.Count,installed=false};
        }finally{foreach(var stream in locks)stream.Dispose();if(pending is not null&&Directory.Exists(pending))Directory.Delete(pending,true);}
    }
}
