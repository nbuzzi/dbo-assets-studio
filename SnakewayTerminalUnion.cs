using System.Text;
namespace DboAssetsStudio;

// Exact local union of overlapping terminal floors. Outside this source-defined
// region the original navigation faces remain; no new visual/collision geometry.
internal static class SnakewayTerminalUnion
{
    private readonly record struct XY(double X,double Z);
    private readonly record struct P(float X,float Y,float Z,int Identity=0);
    private sealed record Face(P A,P B,P C);
    private static double Area(IReadOnlyList<XY> p){double s=0;for(int i=0;i<p.Count;i++){var a=p[i];var b=p[(i+1)%p.Count];s+=a.X*b.Z-a.Z*b.X;}return Math.Abs(s)*.5;}
    private static List<XY> Clip(IReadOnlyList<XY> p,Func<XY,double>d){var q=new List<XY>();if(p.Count==0)return q;var a=p[^1];double da=d(a);foreach(var b in p){double db=d(b);if((da>=0)!=(db>=0)){double t=da/(da-db);q.Add(new(a.X+t*(b.X-a.X),a.Z+t*(b.Z-a.Z)));}if(db>=0)q.Add(b);a=b;da=db;}var clean=new List<XY>();foreach(var v in q)if(clean.Count==0||Math.Abs(clean[^1].X-v.X)>1e-10||Math.Abs(clean[^1].Z-v.Z)>1e-10)clean.Add(v);if(clean.Count>1&&Math.Abs(clean[0].X-clean[^1].X)<1e-10&&Math.Abs(clean[0].Z-clean[^1].Z)<1e-10)clean.RemoveAt(clean.Count-1);return clean;}
    private static List<List<XY>> Subtract(IReadOnlyList<XY> p,IReadOnlyList<XY> cut){var q=p.ToList();var outside=new List<List<XY>>();for(int i=0;i<cut.Count&&q.Count>=3;i++){var a=cut[i];var b=cut[(i+1)%cut.Count];double D(XY v)=>(b.Z-a.Z)*(v.X-a.X)-(b.X-a.X)*(v.Z-a.Z);var part=Clip(q,v=>-D(v));if(part.Count>=3&&Area(part)>1e-8)outside.Add(part);q=Clip(q,D);}return outside;}
    private static bool Intersects(IReadOnlyList<XY> p,IReadOnlyList<XY> cut){var q=p.ToList();for(int i=0;i<cut.Count&&q.Count>=3;i++){var a=cut[i];var b=cut[(i+1)%cut.Count];q=Clip(q,v=>(b.Z-a.Z)*(v.X-a.X)-(b.X-a.X)*(v.Z-a.Z));}return q.Count>=3&&Area(q)>1e-8;}
    private static double Height(Face f,XY p){double bx=f.B.X-f.A.X,bz=f.B.Z-f.A.Z,cx=f.C.X-f.A.X,cz=f.C.Z-f.A.Z,det=bx*cz-bz*cx;return f.A.Y+((p.X-f.A.X)*cz-(p.Z-f.A.Z)*cx)/det*(f.B.Y-f.A.Y)+(bx*(p.Z-f.A.Z)-bz*(p.X-f.A.X))/det*(f.C.Y-f.A.Y);}
    private static float Coordinate(int units){
        // Both the TOK writer and the production FVMesh divide/multiply by100
        // in single precision then truncate. Use a fixed point of that real
        // roundtrip, so adjacent centimetre edges cannot collapse on import.
        for(int delta=0;delta<=2;delta++)foreach(int candidate in new[]{units-delta,units+delta})if((int)((candidate/100f)*100f)==candidate)return candidate/100f;
        throw new InvalidDataException("Bounded production centimetre roundtrip.");
    }
    private static P Snap(P p)=>new(Coordinate((int)Math.Round(p.X*100d,MidpointRounding.AwayFromZero)),(float)(Math.Round(p.Y*1000d,MidpointRounding.AwayFromZero)/1000),Coordinate((int)Math.Round(p.Z*100d,MidpointRounding.AwayFromZero)));
    private static P NativeSource(P p)=>new(Coordinate((int)((float)(Math.Round(p.X*1000d,MidpointRounding.AwayFromZero)/1000)*100f)),(float)(Math.Round(p.Y*1000d,MidpointRounding.AwayFromZero)/1000),Coordinate((int)((float)(Math.Round(p.Z*1000d,MidpointRounding.AwayFromZero)/1000)*100f)));
    private static bool Local(Face f)=>new[]{f.A,f.B,f.C}.Any(p=>p.X< -134&&p.Z>25&&p.Z<65);
    private static bool Overlap(Face a,Face b)=>Math.Min(a.A.X,Math.Min(a.B.X,a.C.X))<=Math.Max(b.A.X,Math.Max(b.B.X,b.C.X))&&Math.Min(b.A.X,Math.Min(b.B.X,b.C.X))<=Math.Max(a.A.X,Math.Max(a.B.X,a.C.X))&&Math.Min(a.A.Z,Math.Min(a.B.Z,a.C.Z))<=Math.Max(b.A.Z,Math.Max(b.B.Z,b.C.Z))&&Math.Min(b.A.Z,Math.Min(b.B.Z,b.C.Z))<=Math.Max(a.A.Z,Math.Max(a.B.Z,a.C.Z));
    internal static object Write(IReadOnlyList<SnakewayGroundEnvelope.Face> input,string path)
        =>WriteCore(input,path,false);
    internal static object WriteVisibleRamp(IReadOnlyList<SnakewayGroundEnvelope.Face> source,IReadOnlyList<SnakewayGroundEnvelope.Face> ramp,string path){if(source.Count!=1239||ramp.Count is not (10 or 20))throw new InvalidDataException("Original1239 plus canonical visible ramp10/20 required.");return WriteCore(source.Concat(ramp).ToArray(),path,true);}
    private static object WriteCore(IReadOnlyList<SnakewayGroundEnvelope.Face> input,string path,bool visibleRamp)
    {
        if(visibleRamp?input.Count is not (1249 or 1259):input.Count!=1239)throw new InvalidDataException("Pinned source ground face count required.");
        var original=input.Select(f=>new Face(new(f.A.X,f.A.Y,f.A.Z),new(f.B.X,f.B.Y,f.B.Z),new(f.C.X,f.C.Y,f.C.Z))).ToArray();
        P WeldKey(P p)=>new((float)(Math.Round(p.X*1000d,MidpointRounding.AwayFromZero)/1000),(float)(Math.Round(p.Y*1000d,MidpointRounding.AwayFromZero)/1000),(float)(Math.Round(p.Z*1000d,MidpointRounding.AwayFromZero)/1000));
        var parents=Enumerable.Range(0,input.Count).ToArray();int Find(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        var edges=new Dictionary<(P,P),int>();
        int Compare(P a,P b){int x=a.X.CompareTo(b.X);if(x!=0)return x;int y=a.Y.CompareTo(b.Y);if(y!=0)return y;int z=a.Z.CompareTo(b.Z);return z!=0?z:a.Identity.CompareTo(b.Identity);}
        for(int i=0;i<original.Length;i++){var v=new[]{WeldKey(original[i].A),WeldKey(original[i].B),WeldKey(original[i].C)};for(int k=0;k<3;k++){var a=v[k];var b=v[(k+1)%3];var key=Compare(a,b)<0?(a,b):(b,a);if(edges.TryGetValue(key,out int other))parents[Find(i)]=Find(other);else edges.Add(key,i);}}
        int main=Enumerable.Range(0,input.Count).GroupBy(Find).OrderByDescending(g=>g.Count()).First().Key;
        int terminal=-1;var point=new XY(-146,40);
        for(int i=0;i<original.Length;i++){var f=original[i];var triangle=new[]{new XY(f.A.X,f.A.Z),new XY(f.B.X,f.B.Z),new XY(f.C.X,f.C.Z)};bool inside=true;for(int k=0;k<3;k++){var a=triangle[k];var b=triangle[(k+1)%3];if((b.Z-a.Z)*(point.X-a.X)-(b.X-a.X)*(point.Z-a.Z)<-1e-7)inside=false;}if(inside&&Math.Abs(Height(f,point)-49.564596)<.03){terminal=Find(i);break;}}
        if(terminal<0||terminal==main)throw new InvalidDataException("Reviewed separate terminal source component required.");
        // The indoor point reader can choose a lower overlapping floor. Inside
        // the ramp region retain the physical upper walk surface of every
        // selected source component, including the small floor below the ramp.
        // Visual/collision geometry remains fully preserved in the BSP.
        var union=Enumerable.Range(0,input.Count).Select(i=>visibleRamp||Find(i)==main||Find(i)==terminal).ToArray();
        bool InRegion(P p)=>p.X< -133&&p.Z>24&&p.Z<66;
        var sourceIds=new Dictionary<P,int>();
        P Prepare(P p){if(!visibleRamp)return NativeSource(p);var key=WeldKey(p);if(!sourceIds.TryGetValue(key,out int id)){id=sourceIds.Count+1;sourceIds.Add(key,id);}int Raw(float x){int first=(int)(x*100f);return(int)((first/100f)*100f);}return new(Coordinate(Raw(key.X)),key.Y,Coordinate(Raw(key.Z)),id);}
        var source=original.Select(f=>new Face(Prepare(f.A),Prepare(f.B),Prepare(f.C))).ToArray();
        var sourcePositions=source.SelectMany(f=>new[]{f.A,f.B,f.C}).Distinct().GroupBy(p=>(p.X,p.Y,p.Z)).ToDictionary(g=>g.Key,g=>g.ToArray());
        P Finish(Face f,XY p){if(visibleRamp)foreach(var c in new[]{f.A,f.B,f.C})if(Math.Abs(p.X-c.X)<1e-9&&Math.Abs(p.Z-c.Z)<1e-9)return c;var q=Snap(new((float)p.X,(float)Height(f,p),(float)p.Z));if(visibleRamp&&sourcePositions.TryGetValue((q.X,q.Y,q.Z),out var matches)&&matches.Length==1)return matches[0];return q;}
        var mainEdges=new Dictionary<(P,P),int>();
        foreach(int i in Enumerable.Range(0,input.Count).Where(i=>Find(i)==main)){
            var f=source[i];var vv=new[]{f.A,f.B,f.C};for(int k=0;k<3;k++){var a=vv[k];var b=vv[(k+1)%3];var key=Compare(a,b)<0?(a,b):(b,a);mainEdges[key]=mainEdges.GetValueOrDefault(key)+1;}
        }
        var polygons=new List<P[]>();var polygonComponents=new List<int>();var edgeOwners=new List<(P,P)?[]>();var originalSplits=new Dictionary<(P,P),HashSet<P>>();long changed=0;
        for(int i=0;i<source.Length;i++){
            var f=source[i];var pieces=new List<List<XY>>{new(){new(f.A.X,f.A.Z),new(f.B.X,f.B.Z),new(f.C.X,f.C.Z)}};
            if(union[i]&&Local(f))for(int j=0;j<source.Length&&pieces.Count>0;j++){
                var other=source[j];if(i==j||!union[j]||!Local(other)||!Overlap(f,other))continue;
                var triangle=new List<XY>{new(other.A.X,other.A.Z),new(other.B.X,other.B.Z),new(other.C.X,other.C.Z)};
                double[] differences=triangle.Select(p=>Height(other,p)-Height(f,p)).ToArray();
                List<XY> cut;
                if(differences.All(d=>Math.Abs(d)<1e-7)){if(j>i)continue;cut=triangle;}
                else cut=Clip(triangle,p=>Height(other,p)-Height(f,p));
                // A long source road triangle can reach the terminal while
                // starting many metres away. Bound the actual cut polygon,
                // not merely the triangle's vertex test, so existing road
                // overlaps and connections outside the ramp remain untouched.
                if(visibleRamp){cut=Clip(cut,p=>-133-p.X);cut=Clip(cut,p=>p.Z-24);cut=Clip(cut,p=>66-p.Z);}
                if(cut.Count<3||Area(cut)<1e-8)continue;
                pieces=pieces.SelectMany(p=>Intersects(p,cut)?Subtract(p,cut):new List<List<XY>>{p}).ToList();if(pieces.Count>128)throw new InvalidDataException("Bounded terminal fragments.");
            }
            if(pieces.Count!=1||pieces[0].Count!=3)changed++;
            foreach(var piece in pieces){
                var points=piece.Select(p=>Finish(f,p)).ToArray();var owners=new (P,P)?[piece.Count];var corners=new[]{f.A,f.B,f.C};
                bool On(XY p,P a,P b){double dx=(double)b.X-a.X,dz=(double)b.Z-a.Z,t=((p.X-a.X)*dx+(p.Z-a.Z)*dz)/(dx*dx+dz*dz);return t>=-1e-8&&t<=1+1e-8&&Math.Abs((p.X-a.X)*dz-(p.Z-a.Z)*dx)<=1e-7*Math.Sqrt(dx*dx+dz*dz);}
                for(int e=0;e<3;e++){var a=corners[e];var b=corners[(e+1)%3];var key=Compare(a,b)<0?(a,b):(b,a);if(!originalSplits.TryGetValue(key,out var split)){split=new();originalSplits.Add(key,split);}for(int k=0;k<piece.Count;k++){if(On(piece[k],a,b))split.Add(points[k]);if(On(piece[k],a,b)&&On(piece[(k+1)%piece.Count],a,b))owners[k]=key;}}
                polygons.Add(points);edgeOwners.Add(owners);polygonComponents.Add(Find(i));
            }
        }
        var levels=polygons.SelectMany(p=>p).GroupBy(p=>((int)(p.X*100f),(int)(p.Z*100f))).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Y).Distinct().OrderBy(y=>y).ToArray());
        float maxWeld=0;
        for(int i=0;i<polygons.Count;i++)for(int k=0;k<polygons[i].Length;k++){
            var p=polygons[i][k];if(p.X>=-133||p.Z<=24||p.Z>=66)continue;
            float y=levels[((int)(p.X*100f),(int)(p.Z*100f))].Where(y=>Math.Abs(y-p.Y)<=.020001f).Max();maxWeld=Math.Max(maxWeld,y-p.Y);polygons[i][k]=new(p.X,y,p.Z,p.Identity);
        }
        // Split every affected shared edge at all collinear endpoints, including
        // intersections introduced by subtraction. Equal XYZ uses one vertex.
        var localVertices=polygons.SelectMany(p=>p).Where(p=>p.X< -133&&p.Z>24&&p.Z<66).Distinct().ToArray();
        var terminalVertices=polygons.Where((_,i)=>polygonComponents[i]==terminal).SelectMany(p=>p).Distinct().ToHashSet();
        var vertices=new List<P>();var indices=new Dictionary<P,uint>();var triangles=new List<uint[]>();
        uint Index(P p){
            // Cut points and original corners can describe the same seam.
            // In the bounded union use one XYZ vertex, while retaining source
            // identities elsewhere where centimetre projection overlaps roads.
            if(visibleRamp&&p.X<=-133&&p.Z>=24&&p.Z<=66)p=new(p.X,p.Y,p.Z);
            if(!indices.TryGetValue(p,out uint i)){i=(uint)vertices.Count;indices.Add(p,i);vertices.Add(p);}return i;
        }
        long splits=0,collapsed=0,duplicates=0,quantisedSlivers=0,sliverDoubleArea=0;var emitted=new HashSet<(uint,uint,uint)>();var edgeDebug=new List<object>();
        void Emit(P a,P b,P c){long area=(long)((int)(b.Z*100f)-(int)(a.Z*100f))*((int)(c.X*100f)-(int)(a.X*100f))-(long)((int)(b.X*100f)-(int)(a.X*100f))*((int)(c.Z*100f)-(int)(a.Z*100f));if(area<=0){collapsed++;return;}if(area<=8&&new[]{a,b,c}.All(p=>p.X< -134&&p.Z>25&&p.Z<65)){quantisedSlivers++;sliverDoubleArea+=area;return;}uint A=Index(a),B=Index(b),C=Index(c);var key=new[]{A,B,C}.Order().ToArray();if(!emitted.Add((key[0],key[1],key[2]))){duplicates++;return;}triangles.Add([A,B,C]);}
        for(int polygonIndex=0;polygonIndex<polygons.Count;polygonIndex++){var polygon=polygons[polygonIndex];
            var ring=new List<P>();for(int k=0;k<polygon.Length;k++){
                var a=polygon[k];var b=polygon[(k+1)%polygon.Length];double dx=b.X-a.X,dy=b.Y-a.Y,dz=b.Z-a.Z,length=dx*dx+dy*dy+dz*dz;if(length<1e-10)continue;
                var candidates=new List<(double T,P Point)>{(0,a)};
                var edgeKey=Compare(a,b)<0?(a,b):(b,a);
                bool terminalSeam=polygonComponents[polygonIndex]==main&&mainEdges.GetValueOrDefault(edgeKey)==1;
                var owner=edgeOwners[polygonIndex][k];
                if(a.X< -133||b.X< -133)foreach(var p in localVertices){long ax=(int)(a.X*100f),az=(int)(a.Z*100f),bx=(int)(b.X*100f),bz=(int)(b.Z*100f),px=(int)(p.X*100f),pz=(int)(p.Z*100f);long cross=(px-ax)*(bz-az)-(pz-az)*(bx-ax);bool ownSplit=visibleRamp&&owner.HasValue&&originalSplits[owner.Value].Any(v=>v.X==p.X&&v.Z==p.Z&&Math.Abs(v.Y-p.Y)<=.020001);if(cross!=0&&!((ownSplit||!visibleRamp&&terminalSeam&&terminalVertices.Contains(p))&&cross*cross<=4d*((bx-ax)*(bx-ax)+(bz-az)*(bz-az))))continue;double t=((p.X-a.X)*dx+(p.Z-a.Z)*dz)/(dx*dx+dz*dz);if(t<=1e-6||t>=1-1e-6)continue;if(Math.Abs(p.Y-a.Y-t*dy)<=.020001)candidates.Add((t,p));}
                if(visibleRamp&&Math.Abs(a.X-b.X)>80&&a.Y>49.73f&&b.Y>49.73f)edgeDebug.Add(new{a,b,owner,originalPoints=owner.HasValue?originalSplits[owner.Value].ToArray():[],candidates});
                ring.AddRange(candidates.OrderBy(v=>v.T).Select(v=>v.Point).DistinctBy(p=>(p.X,p.Y,p.Z)));
            }
            if(ring.Count<3)continue;
            if(ring.Count==3){Emit(ring[0],ring[1],ring[2]);continue;}
            // An integer-rounded centroid can leave a narrow clipped polygon
            // and create overlapping triangles. Triangulate only existing ring
            // vertices; retain collinear boundary points for shared-edge joins.
            splits++;
            var remaining=ring.DistinctBy(p=>(p.X,p.Y,p.Z)).ToList();
            long Turn(P a,P b,P c)=>(long)((int)(b.Z*100f)-(int)(a.Z*100f))*((int)(c.X*100f)-(int)(a.X*100f))-(long)((int)(b.X*100f)-(int)(a.X*100f))*((int)(c.Z*100f)-(int)(a.Z*100f));
            while(remaining.Count>3){
                bool found=false;
                for(int k=0;k<remaining.Count;k++){
                    var a=remaining[(k+remaining.Count-1)%remaining.Count];var b=remaining[k];var c=remaining[(k+1)%remaining.Count];
                    if(Turn(a,b,c)<=0)continue;
                    if(remaining.Where((_,j)=>j!=k&&j!=(k+1)%remaining.Count&&j!=(k+remaining.Count-1)%remaining.Count).Any(p=>Turn(a,b,p)>=0&&Turn(b,c,p)>=0&&Turn(c,a,p)>=0))continue;
                    Emit(a,b,c);remaining.RemoveAt(k);found=true;break;
                }
                if(!found)throw new InvalidDataException("Quantised terminal polygon cannot be triangulated without overlap.");
            }
            if(remaining.Count==3)Emit(remaining[0],remaining[1],remaining[2]);
        }
        if(triangles.Count<1000||triangles.Count>10000)throw new InvalidDataException("Bounded local union triangle count.");
        if(visibleRamp)ContentSafety.WriteJson(Path.Combine(Path.GetDirectoryName(path)!,"edge-debug.json"),edgeDebug);
        using var w=new BinaryWriter(new FileStream(path,FileMode.CreateNew));w.Write(Encoding.ASCII.GetBytes("NCP1"));w.Write((uint)vertices.Count);w.Write((uint)triangles.Count);foreach(var p in vertices){w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}foreach(var t in triangles)foreach(uint i in t)w.Write(i);
        return new{vertices=vertices.Count,triangles=triangles.Count,sourceFaces=input.Count,terminalUnion=true,changedSourceFaces=changed,conformingSplits=splits,collapsedSubCentimetreFragments=collapsed,duplicateFragmentsRemoved=duplicates,quantisedTerminalSlivers=quantisedSlivers,quantisedSliverAreaSquareMetres=sliverDoubleArea*.00005,visualBspUnchanged=true,horizontalNativeCentimetres=true,heightSnapMillimetres=1,maximumTerminalHeightWeld=maxWeld,gameplayTested=false};
    }
}
