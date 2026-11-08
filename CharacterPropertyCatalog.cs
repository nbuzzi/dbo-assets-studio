using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Read-only metadata audit. Layout: CNtlPLCharacterProperty/TypeAnimTable,
// SEventAnimHit and TypeBoneData serializers in the Taiwan x86 client.
internal static class CharacterPropertyCatalog
{
    private static readonly Dictionary<uint, int> EventBytes = new()
    {
        [3]=108,[4]=292,[5]=16,[6]=88,[7]=16,[8]=176,[9]=12,
        [10]=96,[11]=8,[12]=12,[13]=32,[14]=12,[15]=12,[16]=24,
        [17]=188,[18]=8,[19]=8
    };
    public static object Inspect(string path, string? selectedModel=null)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 12 || stream.Length > 128L*1024*1024) throw new InvalidDataException("CharacterProperty size limit.");
        byte[] data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data);
        return InspectData(data, path, selectedModel);
    }
    internal static object InspectTaiwanKeyCatalog(string path)
    {
        path=Path.GetFullPath(path);ContentSafety.NoLinks(path);
        if(new FileInfo(path).Length is <12 or >128*1024*1024)throw new InvalidDataException("Bounded Taiwan property required.");
        return InspectData(File.ReadAllBytes(path),path,null,keyCatalogOnly:true);
    }
    private static object InspectData(byte[] data, string path, string? selectedModel, bool keyCatalogOnly=false)
    {
        if(selectedModel is not null && (selectedModel.Length is <1 or >128 || selectedModel.Any(c=>c<32||c>126||c is '/' or '\\')))
            throw new InvalidDataException("Exact bounded model key required.");
        var r = new Reader(data);
        uint version = r.UInt();
        if (version != 1u) throw new InvalidDataException("Explicit Taiwan v1 CharacterProperty required.");
        string header = r.String(); int count = r.Count(10000);
        var records = new List<object>(); var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i=0;i<count;i++)
        {
            var stringFields = new List<object>();
            string Field(string role)
            {
                int offset = r.Pos; string value = r.String();
                stringFields.Add(new { role, value, offset, bytes = r.Pos - offset }); return value;
            }
            int start=r.Pos; string name=Field("name");
            if (!names.Add(name)) throw new InvalidDataException("Duplicate character property: "+name);
            uint flags=r.UInt(); string texture=Field("texture"), sound=Field("sound"), mesh=Field("mesh"), meshPath=Field("meshPath");
            r.Take(4); int linksCount=r.Count(1000);var linkedEffects=new List<object>();
            for(int n=0;n<linksCount;n++)
            {
                int offset=r.Pos;var link=r.Take(88);
                linkedEffects.Add(new {offset,payloadSha256=Hash(link),time=BitConverter.ToSingle(link,0),
                    effect=Reference(link,8,32),bone=Reference(link,40,32),
                    eventType=BinaryPrimitives.ReadUInt32LittleEndian(link.AsSpan(4))});
            }
            string animationPath=Field("animationPath"); int animationCountOffset=r.Pos; int animationsCount=r.Count(5000);
            var animations=new List<object>(); var keys=new HashSet<uint>();
            for (int j=0;j<animationsCount;j++)
            {
                int animationStart=r.Pos; string file=r.String(); uint key=r.UInt();
                float duration=BitConverter.Int32BitsToSingle(unchecked((int)r.UInt())); uint animationFlags=r.UInt();
                if (!keys.Add(key) || !float.IsFinite(duration) || duration<0) throw new InvalidDataException("Invalid animation key/duration: "+name);
                int events=r.Count(1000); var eventTypes=new List<uint>(); var hitEvents=new List<object>();var assetEvents=new List<object>();
                for (int k=0;k<events;k++)
                {
                    uint type=r.UInt();eventTypes.Add(type);
                    if (type==2)
                    {
                        int eventOffset=r.Pos;var hit=r.Take(264);uint projectile=BinaryPrimitives.ReadUInt32LittleEndian(hit.AsSpan(32));
                        uint shake=BinaryPrimitives.ReadUInt32LittleEndian(hit.AsSpan(228));
                        if (projectile is not (0 or 2000 or 2001 or 2002 or 2003 or 2004 or 2005) || shake>1) throw new InvalidDataException("Unknown hit event layout.");
                        // SEventAnimHit fields, before its variable projectile extras.
                        // An energy HIT event launches a projectile; it is not arrival.
                        float time=BitConverter.ToSingle(hit,0), speed=BitConverter.ToSingle(hit,108);
                        if(!float.IsFinite(time)||!float.IsFinite(speed))throw new InvalidDataException("Non-finite animation hit metadata.");
                        hitEvents.Add(new {time,attackType=BinaryPrimitives.ReadUInt32LittleEndian(hit.AsSpan(16)),
                            targetBehavior=BinaryPrimitives.ReadUInt32LittleEndian(hit.AsSpan(20)),projectileType=projectile,
                            shotType=BinaryPrimitives.ReadUInt32LittleEndian(hit.AsSpan(36)),projectileSpeed=speed,
                            bone=Encoding.Latin1.GetString(hit,44,32).TrimEnd('\0'),targetHeight=BitConverter.ToSingle(hit,116),
                            offset=eventOffset,payloadSha256=Hash(hit),
                            assetReferences=new {projectileEffect=Reference(hit,76,32),targetEffect=Reference(hit,120,32),
                                subTargetEffect=Reference(hit,156,32),targetSound=Reference(hit,188,32),wordEffect=Reference(hit,232,32)}});
                        if (projectile==2002)r.Take(12);
                        else if(projectile==2003)r.Take(20);
                        else if(projectile==2004)r.Take(checked(r.Count(100)*8));
                        if(shake==1)r.Take(8);
                    }
                    else
                    {
                        if(!EventBytes.TryGetValue(type,out int size))throw new InvalidDataException("Unknown animation event: "+type);
                        int eventOffset=r.Pos;var payload=r.Take(size);
                        if(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4))!=type)throw new InvalidDataException("Event payload type mismatch.");
                        // Fixed names observed from NtlAnimEventData.h. Preserve raw
                        // bytes and termination status: this is not path resolution,
                        // a runtime compatibility claim, or an asset substitution.
                        if(type==3)assetEvents.Add(new {type,offset=eventOffset,payloadSha256=Hash(payload),time=BitConverter.ToSingle(payload,0),
                            effect=Reference(payload,8,32),bone=Reference(payload,44,32)});
                        else if(type==4)assetEvents.Add(new {type,offset=eventOffset,payloadSha256=Hash(payload),time=BitConverter.ToSingle(payload,0),
                            sounds=Enumerable.Range(0,4).Select(n=>Reference(payload,8+n*64,64)).ToArray(),
                            soundGroup=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(264))});
                        else if(type==6)assetEvents.Add(new {type,offset=eventOffset,payloadSha256=Hash(payload),time=BitConverter.ToSingle(payload,0),
                            effect=Reference(payload,8,32),bone=Reference(payload,40,32)});
                    }
                }
                animations.Add(new { key,file,duration,flags=animationFlags,eventTypes,hitEvents,assetEvents,start=animationStart,end=r.Pos,sha256=Hash(data.AsSpan(animationStart,r.Pos-animationStart)) });
            }
            int animationEnd=r.Pos;
            int boneScale=r.Count(1),baseScale=r.Count(1),bones=r.Count(1000);r.Take(12);
            if(boneScale==1)r.Take(checked(bones*20));if(baseScale==1)r.Take(12);
            r.Take(24);r.Take(checked(r.Count(1000)*28));r.Take(32);
            if(!keyCatalogOnly && (selectedModel is null || selectedModel==name))
                records.Add(new {name,flags,texture,sound,mesh,meshPath,animationPath,stringFields,linkedEffects,animations,start,end=r.Pos,animationCountOffset,animationEnd,sha256=Hash(data.AsSpan(start,r.Pos-start))});
        }
        int padding = data.Length-r.Pos;
        if (padding!=0)
            throw new InvalidDataException("CharacterProperty has trailing bytes or invalid padding.");
        if(selectedModel is not null && records.Count!=1)throw new InvalidDataException("Selected model missing from complete catalog.");
        if(keyCatalogOnly)return new {source=path,sha256=Hash(data),version,header,count,modelKeys=names.Order(StringComparer.Ordinal).ToArray(),serializedBytes=r.Pos};
        return new {source=path,sha256=Hash(data),version,header,count,records,structuralOnly=true,animationTested=false,worldTested=false,
            sourceFormat="taiwan-property-v1",
            serializedBytes=r.Pos,desPaddingBytes=padding,nativeReaderPass=false,selectedModel};
    }
    private static string Hash(ReadOnlySpan<byte> data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static object Reference(byte[] payload,int offset,int size)
    {
        ReadOnlySpan<byte> bytes=payload.AsSpan(offset,size);int nul=bytes.IndexOf((byte)0);
        var name=nul<0?bytes:bytes[..nul];
        return new {value=Encoding.Latin1.GetString(name),bytesHex=Convert.ToHexString(name),terminated=nul>=0};
    }
    private sealed class Reader(byte[] data)
    {
        public int Pos {get;private set;}
        public byte[] Take(int count)
        {
            if(count<0||count>data.Length-Pos)throw new InvalidDataException("Truncated CharacterProperty at "+Pos);
            byte[] value=data.AsSpan(Pos,count).ToArray();Pos+=count;return value;
        }
        public uint UInt()=>BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Count(int limit){uint count=UInt();if(count>limit)throw new InvalidDataException("CharacterProperty count exceeds limit.");return (int)count;}
        public string String()
        {
            int end=Array.IndexOf(data,(byte)0,Pos,Math.Min(512,data.Length-Pos));
            if(end<0)throw new InvalidDataException("Unterminated CharacterProperty string.");
            var value=Take(end-Pos+1);return Encoding.Latin1.GetString(value,0,value.Length-1);
        }
    }
}
