using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DboAssetsStudio;

// Authored, visible terminal ramp. This creates private additive geometry only.
internal static class SnakewayRampService
{
    private const string Profile="assets-snakeway-visible-terminal-ramp-v4";
    private const string SourceHash="2B1A245AD29A07F4F91DD2A485D36C787ABAC8D1F1D4CFF87F6A47B9F2A7CF6B";
    internal sealed record Pin(string Path,string Sha256,long Bytes);
    internal sealed record Plan(string Profile,Pin Source,float Width,float Thickness,float PeakHeight,string Authorization);
    internal readonly record struct V(float X,float Y,float Z);
    private static readonly JsonSerializerOptions Strict=new(ContentSafety.Json){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private static Pin Seal(string path){path=Path.GetFullPath(path);ContentSafety.NoLinks(path);return new(path,ContentSafety.Hash(path),new FileInfo(path).Length);}
    private static void Validate(Plan p){if(!(((p.Profile==Profile||p.Profile=="assets-snakeway-visible-terminal-ramp-v3")&&p.PeakHeight==51f&&p.Width==4)||(p.Profile=="assets-snakeway-visible-terminal-ramp-v2"&&p.PeakHeight==51f&&p.Width==3)||(p.Profile=="assets-snakeway-visible-terminal-ramp-v1"&&p.PeakHeight==50.1f&&p.Width==3))||p.Source.Sha256!=SourceHash||p.Thickness!=.2f||p.Authorization!="Nico20261013: agregar una rampa visible para caminar")throw new InvalidDataException("Pinned source and explicit reviewed ramp profile required.");}
    internal static object CreatePlan(string source,string output){var p=new Plan(Profile,Seal(source),4,.2f,51f,"Nico20261013: agregar una rampa visible para caminar");Validate(p);output=ContentSafety.SafeOutput(output);if(File.Exists(output)||Directory.Exists(output)||output.Equals(p.Source.Path,StringComparison.OrdinalIgnoreCase))throw new IOException("Fresh ramp plan required.");ContentSafety.WriteJson(output,p);return new{plan=output,readyToInstall=false};}
    internal static SnakewayGroundEnvelope.Face[] ReadGround(string path){
        using var r=new BinaryReader(File.OpenRead(path));if(Encoding.ASCII.GetString(r.ReadBytes(4))!="OSM1"||r.ReadUInt32()!=9)throw new InvalidDataException("Pinned nine original meshes required.");var result=new List<SnakewayGroundEnvelope.Face>();
        for(int mi=0;mi<9;mi++){uint nv=r.ReadUInt32(),nt=r.ReadUInt32(),nm=r.ReadUInt32();if(nv!=5874||nt>6000||nm!=1)throw new InvalidDataException("Original mesh counts.");var v=new V[nv];for(int i=0;i<nv;i++){v[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());r.ReadSingle();r.ReadSingle();}var t=new uint[nt][];for(int i=0;i<nt;i++)t[i]=[r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32()];var mat=r.ReadBytes(132);if(mi<2||mi>4)continue;if(Encoding.ASCII.GetString(mat[4..]).Split('\0')[0]!=new[]{"shedao01","shedao_head","shedao_platform"}[mi-2])throw new InvalidDataException("Original ground material.");foreach(var f in t){var a=v[f[0]];var b=v[f[1]];var c=v[f[2]];double ux=b.X-a.X,uy=b.Y-a.Y,uz=b.Z-a.Z,vx=c.X-a.X,vy=c.Y-a.Y,vz=c.Z-a.Z,nx=uy*vz-uz*vy,ny=uz*vx-ux*vz,nz=ux*vy-uy*vx;if(ny>.2*Math.Sqrt(nx*nx+ny*ny+nz*nz))result.Add(new(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z),new(c.X,c.Y,c.Z)));}}
        if(r.BaseStream.Position!=r.BaseStream.Length||result.Count!=1239)throw new InvalidDataException("Complete original ground selection.");return result.ToArray();
    }
    internal static (int[] Components,int Main,int Terminal) Components(SnakewayGroundEnvelope.Face[] faces){
        var ids=new Dictionary<V,int>();var edges=new Dictionary<(int,int),int>();var parents=Enumerable.Range(0,faces.Length).ToArray();int Find(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        int Index(SnakewayGroundEnvelope.Point p){V k=new((float)(Math.Round(p.X*1000d,MidpointRounding.AwayFromZero)/1000),(float)(Math.Round(p.Y*1000d,MidpointRounding.AwayFromZero)/1000),(float)(Math.Round(p.Z*1000d,MidpointRounding.AwayFromZero)/1000));if(!ids.TryGetValue(k,out int i)){i=ids.Count;ids.Add(k,i);}return i;}
        for(int i=0;i<faces.Length;i++){var f=faces[i];int[] v=[Index(f.A),Index(f.B),Index(f.C)];for(int k=0;k<3;k++){int a=v[k],b=v[(k+1)%3];var e=a<b?(a,b):(b,a);if(edges.TryGetValue(e,out int j))parents[Find(i)]=Find(j);else edges.Add(e,i);}}
        var components=Enumerable.Range(0,faces.Length).Select(Find).ToArray();int main=components.GroupBy(x=>x).OrderByDescending(g=>g.Count()).First().Key;int terminal=-1;
        for(int i=0;i<faces.Length;i++)if(Height(faces[i],-146,40) is double h&&Math.Abs(h-49.564596)<.03){terminal=components[i];break;}
        if(components.Count(x=>x==main)!=690||terminal<0||components.Count(x=>x==terminal)!=38)throw new InvalidDataException("Reviewed body690/terminal38 source topology required.");return(components,main,terminal);
    }
    internal static SnakewayGroundEnvelope.Face[] ReadRampTop(string path){
        using var r=new BinaryReader(File.OpenRead(path));
        if(Encoding.ASCII.GetString(r.ReadBytes(4))!="OSM1"||r.ReadUInt32()!=1)throw new InvalidDataException("Canonical ramp mesh required.");
        uint nv=r.ReadUInt32(),nt=r.ReadUInt32();
        if(!((nv==132&&nt==44)||(nv==264&&nt==88))||r.ReadUInt32()!=1)
            throw new InvalidDataException("Bounded canonical44/88-face ramp mesh required.");
        var vertices=new SnakewayGroundEnvelope.Point[nv];
        for(int i=0;i<vertices.Length;i++){vertices[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());r.ReadSingle();r.ReadSingle();}
        var top=new List<SnakewayGroundEnvelope.Face>();
        for(int i=0;i<nt;i++){uint a=r.ReadUInt32(),b=r.ReadUInt32(),c=r.ReadUInt32(),material=r.ReadUInt32();
            if(a>=nv||b>=nv||c>=nv||material!=0)throw new InvalidDataException("Ramp indices/material.");
            if(i<(nt==44?40:80)&&i%8<2)top.Add(new(vertices[a],vertices[b],vertices[c]));
        }
        if(r.ReadUInt32()!=0xff8cb4dcu||Encoding.ASCII.GetString(r.ReadBytes(128)).TrimEnd('\0')!="orig_sw_shedao_platform"||r.BaseStream.Position!=r.BaseStream.Length)
            throw new InvalidDataException("Complete canonical ramp material/EOF required.");
        SceneGeometryService.BakeMesh(path,null,null);return top.ToArray();
    }
    internal static double? Height(SnakewayGroundEnvelope.Face f,double x,double z){double bx=f.B.X-f.A.X,bz=f.B.Z-f.A.Z,cx=f.C.X-f.A.X,cz=f.C.Z-f.A.Z,det=bx*cz-bz*cx,u=((x-f.A.X)*cz-(z-f.A.Z)*cx)/det,v=(bx*(z-f.A.Z)-bz*(x-f.A.X))/det;if(u< -1e-7||v< -1e-7||u+v>1+1e-7)return null;return f.A.Y+u*(f.B.Y-f.A.Y)+v*(f.C.Y-f.A.Y);}
    internal static object Apply(string planPath,string output){
        planPath=Path.GetFullPath(planPath);ContentSafety.NoLinks(planPath);byte[] bytes=File.ReadAllBytes(planPath);using var doc=JsonDocument.Parse(bytes);TerrainGeometryService.NoDuplicateKeys(doc.RootElement);var p=JsonSerializer.Deserialize<Plan>(bytes,Strict)??throw new InvalidDataException("Ramp plan.");Validate(p);using var locked=new FileStream(p.Source.Path,FileMode.Open,FileAccess.Read,FileShare.Read);if(Seal(p.Source.Path)!=p.Source)throw new IOException("Stale ramp source.");output=ContentSafety.SafeOutput(output);if(p.Source.Path.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||planPath.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Source/output overlap.");string pending=ContentSafety.FreshDirectory(output);
        var source=ReadGround(p.Source.Path);var topology=Components(source);var terminal=source.Where((_,i)=>topology.Components[i]==topology.Terminal).ToArray();
        float Floor(float z){var h=terminal.Select(f=>Height(f,-147,z)).Where(h=>h!=null).Select(h=>h!.Value).ToArray();if(h.Length==0)throw new InvalidDataException("Ramp endpoint requires real terminal floor.");return(float)h.Max();}
        // v4 samples the actual terminal height at its centre as well as both
        // edges. A straight edge-to-edge chord cuts below the curved floor.
        int sections=p.Profile==Profile?3:2;
        float[] xs=[-132,-135,-138,-141,-144,-147];var ring=new V[6*sections];for(int i=0;i<6;i++)for(int side=0;side<sections;side++){float z=40-p.Width/2+side*p.Width/(sections-1);float y=i<2?49.7398071f:i<4?p.PeakHeight:i==4?49.95f:Floor(z);ring[sections*i+side]=new(xs[i],y,z);}
        var triangles=new List<V[]>();var top=new List<SnakewayGroundEnvelope.Face>();
        void Add(V a,V b,V c,bool walk=false){triangles.Add([a,b,c]);if(walk)top.Add(new(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z),new(c.X,c.Y,c.Z)));}
        V Lower(V v)=>new(v.X,v.Y-p.Thickness,v.Z);
        for(int i=0;i<5;i++)for(int side=0;side<sections-1;side++){var a=ring[sections*i+side];var b=ring[sections*i+side+1];var c=ring[sections*(i+1)+side];var d=ring[sections*(i+1)+side+1];Add(a,c,b,true);Add(b,c,d,true);Add(Lower(a),Lower(b),Lower(c));Add(Lower(b),Lower(d),Lower(c));Add(a,Lower(a),c);Add(c,Lower(a),Lower(c));Add(b,d,Lower(b));Add(d,Lower(d),Lower(b));}
        foreach(int i in new[]{0,5})for(int side=0;side<sections-1;side++){var a=ring[sections*i+side];var b=ring[sections*i+side+1];if(i==0){Add(a,b,Lower(a));Add(b,Lower(b),Lower(a));}else{Add(a,Lower(a),b);Add(b,Lower(a),Lower(b));}}
        string mesh=Path.Combine(pending,"ramp.mesh");using(var w=new BinaryWriter(new FileStream(mesh,FileMode.CreateNew))){w.Write(Encoding.ASCII.GetBytes("OSM1"));w.Write(1u);w.Write((uint)triangles.Count*3);w.Write((uint)triangles.Count);w.Write(1u);foreach(var t in triangles)foreach(var v in t){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);w.Write((v.X+132)/3);w.Write((v.Z-38.5f)/3);}for(uint i=0;i<triangles.Count;i++){w.Write(i*3);w.Write(i*3+1);w.Write(i*3+2);w.Write(0u);}w.Write(0xff8cb4dcu);var name=new byte[128];Encoding.ASCII.GetBytes("orig_sw_shedao_platform").CopyTo(name,0);w.Write(name);}
        SceneGeometryService.BakeMesh(mesh,null,null);ContentSafety.WriteJson(Path.Combine(pending,"top-faces.json"),top);File.Copy(p.Source.Path,Path.Combine(pending,"source.original.mesh"));File.Copy(planPath,Path.Combine(pending,"plan.json"));
        var audit=new{profile=p.Profile,status="candidate_not_installed",planSha256=ContentSafety.Hash(planPath),source=p.Source,originalGeometryUnchanged=true,additionalTriangles=triangles.Count,walkTriangles=top.Count,width=p.Width,thickness=p.Thickness,anchors=ring,meshSha256=ContentSafety.Hash(mesh),authorization=p.Authorization,worldTested=false,readyToInstall=false};ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),audit);if(Seal(p.Source.Path)!=p.Source)throw new IOException("Ramp source changed.");Directory.Move(pending,output);return new{output,audit};
    }
}
