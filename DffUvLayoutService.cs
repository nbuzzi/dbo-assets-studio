using System.Buffers.Binary;
using System.Text;
namespace DboAssetsStudio;

// Read-only atlas evidence. Geometry/material IDs come from validated RW chunks;
// opaque plugins are never searched for strings or interpreted as geometry.
internal static class DffUvLayoutService
{
    private sealed record Node(uint Kind, int Header, int Start, int Bytes, List<Node>? Children);
    private static uint U(byte[] b,int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4));
    private static ushort H(byte[] b,int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p,2));
    private static float F(byte[] b,int p) => BitConverter.ToSingle(b,p);
    private static List<Node> Nodes(byte[] b,int start,int end,int depth=0)
    {
        if(depth>32) throw new InvalidDataException("UV chunk depth limit.");
        var nodes=new List<Node>();
        for(int p=start;p<end;)
        {
            if(end-p<12) throw new InvalidDataException("UV chunk header truncated.");
            uint kind=U(b,p);int size=checked((int)U(b,p+4)),next=checked(p+12+size);
            if(next>end) throw new InvalidDataException("UV chunk exceeds its parent.");
            bool container=kind is 16 or 26 or 15 or 8 or 7 or 6;
            nodes.Add(new(kind,p,p+12,size,container?Nodes(b,p+12,next,depth+1):null));p=next;
        }
        return nodes;
    }
    private static IEnumerable<Node> All(IEnumerable<Node> nodes)
    {
        foreach(var n in nodes){yield return n;if(n.Children!=null)foreach(var c in All(n.Children))yield return c;}
    }
    public static object Export(string source,string output)
    {
        ContentSafety.NoLinks(source);output=ContentSafety.SafeOutput(output);
        if(!Path.GetExtension(source).Equals(".dff",StringComparison.OrdinalIgnoreCase)||new FileInfo(source).Length is <24 or >128*1024*1024)
            throw new InvalidDataException("Bounded complete DFF required.");
        if(File.Exists(output)||Directory.Exists(output))throw new IOException("Fresh UV evidence required.");
        using var locked=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        byte[] raw=File.ReadAllBytes(source);
        var names=new List<DffRepairService.TextureName>();DffRepairService.Inspect(raw,false,true,names);
        var geometries=All(Nodes(raw,0,raw.Length)).Where(n=>n.Kind==15).ToArray();
        if(geometries.Length is <1 or >64)throw new InvalidDataException("Bounded geometry count required.");
        var result=new List<object>();int geometryIndex=0;
        foreach(var g in geometries)
        {
            var s=g.Children!.Single(n=>n.Kind==1);int p=s.Start;
            uint flags=U(raw,p);int nt=checked((int)U(raw,p+4)),nv=checked((int)U(raw,p+8));
            if((flags&0x01000000)!=0||U(raw,p+12)!=1||nv is <3 or >=65535||nt is <1 or >=65535)
                throw new InvalidDataException("Non-native bounded single-morph geometry required.");
            int sets=(int)((flags>>16)&255);if(sets==0)sets=(flags&128)!=0?2:(flags&4)!=0?1:0;
            if(sets is <1 or >8)throw new InvalidDataException("UV coordinates required.");
            int uv=checked(p+16+((flags&8)!=0?nv*4:0));
            var coords=new float[nv][];
            for(int i=0;i<nv;i++)
            {
                coords[i]=[F(raw,uv+i*8),F(raw,uv+i*8+4)];
                if(coords[i].Any(x=>!float.IsFinite(x)||Math.Abs(x)>100))throw new InvalidDataException("Finite bounded UV coordinates required.");
            }
            int triangles=checked(uv+sets*nv*8);
            if(triangles+nt*8+24>s.Start+s.Bytes)throw new InvalidDataException("UV geometry bounds mismatch.");
            var list=g.Children!.Single(n=>n.Kind==8);var mstruct=list.Children!.Single(n=>n.Kind==1);
            int count=checked((int)U(raw,mstruct.Start));
            if(count is <1 or >256||mstruct.Bytes!=4+count*4)throw new InvalidDataException("Bounded material list required.");
            var materialNodes=list.Children!.Where(n=>n.Kind==7).ToArray();
            var materials=new Node[count];int consumed=0;
            for(int i=0;i<count;i++)
            {
                int reference=unchecked((int)U(raw,mstruct.Start+4+i*4));
                if(reference==-1){if(consumed>=materialNodes.Length)throw new InvalidDataException("Missing serialized material.");materials[i]=materialNodes[consumed++];}
                else{if(reference<0||reference>=i)throw new InvalidDataException("Invalid reused material.");materials[i]=materials[reference];}
            }
            if(consumed!=materialNodes.Length)throw new InvalidDataException("Material count mismatch.");
            var faces=Enumerable.Range(0,count).Select(_=>new List<float[][]>()).ToArray();
            for(int i=0;i<nt;i++)
            {
                int t=triangles+i*8;ushort a=H(raw,t+2),b=H(raw,t),c=H(raw,t+6),m=H(raw,t+4);
                if(a>=nv||b>=nv||c>=nv||m>=count)throw new InvalidDataException("UV face index out of range.");
                faces[m].Add([coords[a],coords[b],coords[c]]);
            }
            for(int i=0;i<count;i++)
            {
                var texture=names.Where(n=>n.ParentHeaders.Contains(materials[i].Header)).ToArray();
                if(texture.Length>1)throw new InvalidDataException("Ambiguous material texture.");
                var sampler=materials[i].Children!.SingleOrDefault(n=>n.Kind==6)?.Children!.Single(n=>n.Kind==1);
                uint samplerFlags=sampler==null?0:U(raw,sampler.Start);
                result.Add(new{geometry=geometryIndex,material=i,texture=texture.Length==1?Encoding.Latin1.GetString(Convert.FromHexString(texture[0].NameHex)):null,
                    uvSet=0,samplerFlags,addressU=(samplerFlags>>8)&15,addressV=(samplerFlags>>12)&15,triangles=faces[i],vertexCount=nv,faceCount=faces[i].Count});
            }
            geometryIndex++;
        }
        string hash=ContentSafety.Hash(source);
        ContentSafety.WriteJson(output,new{profile="taiwan-dff-read-only-uv-layout-v1",source=Path.GetFullPath(source),sourceSha256=hash,surfaces=result,sourcePreserved=true,renderingTested=false,installed=false});
        return new{output,sourceSha256=hash,surfaces=result.Count,sourcePreserved=true,renderingTested=false};
    }
}
