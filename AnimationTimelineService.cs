using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Narrow standard HAnim repair: discard a complete negative pre-roll group.
// No pose interpolation, skeleton remapping or duration changes are attempted.
internal static class AnimationTimelineService
{
    private sealed record Plan(string SourceSha256,int Nodes);
    public static object Trim(string source,string planPath,string destination)
    {
        source=Path.GetFullPath(source);ContentSafety.NoLinks(source);ContentSafety.NoLinks(planPath);
        byte[] planBytes=File.ReadAllBytes(planPath);
        if(planBytes.Length>4096)throw new InvalidDataException("Bounded timeline plan required.");
        using(var document=JsonDocument.Parse(planBytes)){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var field in document.RootElement.EnumerateObject())if(!names.Add(field.Name))throw new InvalidDataException("Duplicate timeline plan property.");}
        var options=new JsonSerializerOptions(ContentSafety.Json){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        var plan=JsonSerializer.Deserialize<Plan>(planBytes,options)??throw new InvalidDataException("Missing plan.");
        if(plan.Nodes<1||plan.Nodes>4096||plan.SourceSha256 is null||ContentSafety.Hash(source)!=plan.SourceSha256.ToUpperInvariant())throw new InvalidDataException("Stale/invalid timeline plan.");
        if(new FileInfo(source).Length>128L*1024*1024)throw new InvalidDataException("ANM exceeds limit.");
        byte[] raw=File.ReadAllBytes(source);if(Convert.ToHexString(SHA256.HashData(raw))!=plan.SourceSha256.ToUpperInvariant())throw new IOException("ANM changed during read.");
        uint Word(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at,4));
        float Float(int at)=>BitConverter.Int32BitsToSingle(unchecked((int)Word(at)));
        if(raw.Length<32||Word(0)!=27||Word(4)!=raw.Length-12||Word(12)!=256||Word(16)!=1||Word(20)>1000000||raw.Length!=32L+36L*Word(20)||!float.IsFinite(Float(28))||Float(28)<=0)throw new InvalidDataException("Exact standard ANM required.");
        int frames=checked((int)Word(20)),nodes=plan.Nodes;
        if(frames<nodes*3||frames%nodes!=0)throw new InvalidDataException("Whole bounded node groups required.");
        float negative=Float(32);if(!float.IsFinite(negative)||negative>=0)throw new InvalidDataException("One negative pre-roll group required.");
        for(int i=0;i<nodes;i++)if(Float(32+i*36)!=negative||Float(32+(i+nodes)*36)!=0)throw new InvalidDataException("Negative/zero groups do not match node count.");
        for(int i=nodes;i<frames;i++)
        {
            float time=Float(32+i*36);if(!float.IsFinite(time)||time<0||time>Float(28))throw new InvalidDataException("Remaining timeline invalid.");
            for(int f=1;f<8;f++)if(!float.IsFinite(Float(32+i*36+f*4)))throw new InvalidDataException("Nonfinite pose.");
            if(i>=nodes*2){uint previous=Word(32+i*36+32);if(previous<nodes*36||previous>=frames*36||previous%36!=0)throw new InvalidDataException("Noninitial previous-frame reference invalid.");}
        }
        byte[] result=new byte[raw.Length-nodes*36];raw.AsSpan(0,32).CopyTo(result);raw.AsSpan(32+nodes*36).CopyTo(result.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),checked((uint)(result.Length-12)));BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20),checked((uint)(frames-nodes)));
        for(int i=0;i<frames-nodes;i++)BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32+i*36+32),i<nodes?0:checked(Word(32+(i+nodes)*36+32)-(uint)(nodes*36)));
        destination=ContentSafety.SafeOutput(destination);
        if(destination.StartsWith(Path.GetDirectoryName(source)!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Output outside source directory required.");
        string pending=ContentSafety.FreshDirectory(destination);
        try
        {
            string file=Path.Combine(pending,Path.GetFileName(source));File.WriteAllBytes(file,result);
            if(!File.ReadAllBytes(file).SequenceEqual(result)||ContentSafety.Hash(source)!=plan.SourceSha256.ToUpperInvariant()||!File.ReadAllBytes(planPath).SequenceEqual(planBytes))throw new IOException("Timeline inputs/readback changed.");
            var audit=new{source,sourceSha256=plan.SourceSha256,plan.Nodes,removedFrames=nodes,framesBefore=frames,framesAfter=frames-nodes,duration=Float(28),posesPreserved=true,previousReferencesAdjusted=true,outputSha256=ContentSafety.Hash(file),nativeReaderTested=false,worldTested=false};
            File.Copy(source,Path.Combine(pending,Path.GetFileName(source)+".original.bak"));ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),audit);Directory.Move(pending,destination);return audit;
        }
        finally{if(Directory.Exists(pending))Directory.Delete(pending,true);}
    }
}
