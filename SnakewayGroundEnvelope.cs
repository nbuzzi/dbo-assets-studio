using System.Text;
namespace DboAssetsStudio;

// Navigation-only upper surface of the pinned SnakeRun ground. The visual BSP
// and its collision triangles are never edited. No extrapolation into void.
internal static class SnakewayGroundEnvelope
{
    internal readonly record struct Point(float X, float Y, float Z);
    internal readonly record struct Face(Point A, Point B, Point C);
    private const double Step = .5, Error = .02;
    private const int BinSize = 4;
    private readonly record struct XZ(double X,double Z);
    private static List<XZ> Clip(IReadOnlyList<XZ> poly,Func<XZ,double> distance)
    {
        var result=new List<XZ>();if(poly.Count==0)return result;
        XZ a=poly[^1];double da=distance(a);
        foreach(var b in poly){double db=distance(b);bool ia=da>=0,ib=db>=0;if(ia!=ib){double t=da/(da-db);result.Add(new(a.X+t*(b.X-a.X),a.Z+t*(b.Z-a.Z)));}if(ib)result.Add(b);a=b;da=db;}
        // Clipping exactly on an existing vertex can emit it twice. A zero
        // length polygon edge would classify every point as both sides during
        // subtraction and falsely report the whole certified area uncovered.
        var clean=new List<XZ>();foreach(var p in result)if(clean.Count==0||Math.Abs(clean[^1].X-p.X)>1e-10||Math.Abs(clean[^1].Z-p.Z)>1e-10)clean.Add(p);
        if(clean.Count>1&&Math.Abs(clean[0].X-clean[^1].X)<=1e-10&&Math.Abs(clean[0].Z-clean[^1].Z)<=1e-10)clean.RemoveAt(clean.Count-1);
        return clean;
    }
    private static double Area(IReadOnlyList<XZ> poly)
    {double sum=0;for(int i=0;i<poly.Count;i++){var a=poly[i];var b=poly[(i+1)%poly.Count];sum+=a.X*b.Z-a.Z*b.X;}return Math.Abs(sum)*.5;}
    private static double HeightOn(Face f,XZ p)
    {
        double bx=f.B.X-f.A.X,bz=f.B.Z-f.A.Z,cx=f.C.X-f.A.X,cz=f.C.Z-f.A.Z;
        double det=bx*cz-bz*cx,px=p.X-f.A.X,pz=p.Z-f.A.Z;
        return f.A.Y+(px*cz-pz*cx)/det*(f.B.Y-f.A.Y)+(bx*pz-bz*px)/det*(f.C.Y-f.A.Y);
    }
    private static List<XZ> Intersection(IReadOnlyList<XZ> poly,IReadOnlyList<XZ> shape)
    {
        var result=poly.ToList();for(int i=0;i<shape.Count&&result.Count>=3;i++){var a=shape[i];var b=shape[(i+1)%shape.Count];result=Clip(result,p=>(b.Z-a.Z)*(p.X-a.X)-(b.X-a.X)*(p.Z-a.Z));}return result;
    }
    private static IEnumerable<List<XZ>> Subtract(IReadOnlyList<XZ> poly,IReadOnlyList<XZ> shape)
    {
        var inside=poly.ToList();for(int i=0;i<shape.Count&&inside.Count>=3;i++){
            var a=shape[i];var b=shape[(i+1)%shape.Count];double D(XZ p)=>(b.Z-a.Z)*(p.X-a.X)-(b.X-a.X)*(p.Z-a.Z);
            var outside=Clip(inside,p=>-D(p));if(outside.Count>=3&&Area(outside)>1e-10)yield return outside;
            inside=Clip(inside,D);
        }
    }
    internal static object Write(IReadOnlyList<Face> source, string output)
    {
        if (source.Count != 1239) throw new InvalidDataException("Pinned1239 source ground faces required.");
        return WriteCore(source,output);
    }
    internal static object WriteRampWalkSurface(IReadOnlyList<Face> source,string output){if(source.Count!=738)throw new InvalidDataException("Reviewed body690/terminal38/ramp10 required.");return WriteCore(source,output);}
    private static object WriteCore(IReadOnlyList<Face> source,string output)
    {
        var bins = new Dictionary<(int, int), List<Face>>();
        foreach (var f in source)
        {
            double minX=Math.Min(f.A.X,Math.Min(f.B.X,f.C.X)),maxX=Math.Max(f.A.X,Math.Max(f.B.X,f.C.X));
            double minZ=Math.Min(f.A.Z,Math.Min(f.B.Z,f.C.Z)),maxZ=Math.Max(f.A.Z,Math.Max(f.B.Z,f.C.Z));
            for(int x=(int)Math.Floor(minX/BinSize);x<=(int)Math.Floor(maxX/BinSize);x++)
                for(int z=(int)Math.Floor(minZ/BinSize);z<=(int)Math.Floor(maxZ/BinSize);z++)
                {var key=(x,z);if(!bins.TryGetValue(key,out var list))bins.Add(key,list=[]);list.Add(f);}
        }
        double? Height(double x,double z)
        {
            if(!bins.TryGetValue(((int)Math.Floor(x/BinSize),(int)Math.Floor(z/BinSize)),out var list))return null;
            double? top=null;
            foreach(var f in list)
            {
                double bx=f.B.X-f.A.X,bz=f.B.Z-f.A.Z,cx=f.C.X-f.A.X,cz=f.C.Z-f.A.Z;
                double det=bx*cz-bz*cx;if(Math.Abs(det)<1e-12)continue;
                double px=x-f.A.X,pz=z-f.A.Z,u=(px*cz-pz*cx)/det,v=(bx*pz-bz*px)/det;
                if(u< -1e-8||v< -1e-8||u+v>1+1e-8)continue;
                double y=f.A.Y+u*(f.B.Y-f.A.Y)+v*(f.C.Y-f.A.Y);
                if(top==null||y>top)top=y;
            }
            return top;
        }
        var samples=new Dictionary<(int,int),Point?>();
        Point? Sample(int x,int z)
        {
            if(samples.TryGetValue((x,z),out var value))return value;
            double wx=x*Step,wz=z*Step;double? y=Height(wx,wz);
            value=y==null?null:new Point((float)wx,(float)y,(float)wz);samples.Add((x,z),value);return value;
        }
        var vertices=new List<Point>();var indices=new Dictionary<Point,uint>();var faces=new List<uint[]>();
        var patches=new List<((int X,int Z) A,(int X,int Z) B,(int X,int Z) C)>();
        long uncovered=0,inaccurate=0;double worst=0;
        string lastReason="";
        bool Valid(Point A,Point B,Point C,double tolerance=Error)
        {
            double maximum=0;
            // Every triangle is inside source ground at corners, edges and
            // internal barycentric probes; no boundary chord across a void.
            foreach(var uv in new[]{(.5,0d),(0d,.5),(.5,.5),(1d/3,1d/3),(.2,.2),(.6,.2),(.2,.6)})
            {
                double x=A.X+uv.Item1*(B.X-A.X)+uv.Item2*(C.X-A.X),z=A.Z+uv.Item1*(B.Z-A.Z)+uv.Item2*(C.Z-A.Z);
                double? h=Height(x,z);if(h==null){lastReason="sample outside source";uncovered++;return false;}
                double y=A.Y+uv.Item1*(B.Y-A.Y)+uv.Item2*(C.Y-A.Y);maximum=Math.Max(maximum,Math.Abs(y-h.Value));
            }
            if(maximum>tolerance){lastReason="sample height "+maximum;inaccurate++;return false;}worst=Math.Max(worst,maximum);
            var nav=new Face(A,B,C);var footprint=new List<XZ>{new(A.X,A.Z),new(B.X,B.Z),new(C.X,C.Z)};
            var relevant=new HashSet<Face>();
            for(int bx=(int)Math.Floor(Math.Min(A.X,Math.Min(B.X,C.X))/BinSize);bx<=(int)Math.Floor(Math.Max(A.X,Math.Max(B.X,C.X))/BinSize);bx++)
                for(int bz=(int)Math.Floor(Math.Min(A.Z,Math.Min(B.Z,C.Z))/BinSize);bz<=(int)Math.Floor(Math.Max(A.Z,Math.Max(B.Z,C.Z))/BinSize);bz++)
                    if(bins.TryGetValue((bx,bz),out var list))foreach(var f in list)relevant.Add(f);
            var uncoveredPieces=new List<List<XZ>>{footprint};
            foreach(var f in relevant){
                var intersection=Intersection(footprint,new XZ[]{new(f.A.X,f.A.Z),new(f.B.X,f.B.Z),new(f.C.X,f.C.Z)});
                if(intersection.Count<3||Area(intersection)<1e-10)continue;
                // Affine extrema occur at the clipped polygon vertices. Never
                // place a navigation face below a higher physical source face.
                if(intersection.Any(p=>HeightOn(f,p)-HeightOn(nav,p)>tolerance+1e-7)){lastReason="affine upper height";inaccurate++;return false;}
                var acceptable=Clip(intersection,p=>HeightOn(f,p)-HeightOn(nav,p)+tolerance+1e-7);
                if(acceptable.Count<3||Area(acceptable)<1e-10)continue;
                uncoveredPieces=uncoveredPieces.SelectMany(poly=>Subtract(poly,acceptable)).ToList();
                if(uncoveredPieces.Count>256)throw new InvalidDataException("Bounded source coverage partitions.");
            }
            if(uncoveredPieces.Sum(Area)>1e-9){lastReason="coverage area "+uncoveredPieces.Sum(Area);uncovered++;return false;}
            return true;
        }
        bool Patch((int X,int Z) a,(int X,int Z) b,(int X,int Z) c,bool add)
        {
            var A=Sample(a.X,a.Z);var B=Sample(b.X,b.Z);var C=Sample(c.X,c.Z);
            bool valid=A!=null&&B!=null&&C!=null&&Valid(A.Value,B.Value,C.Value,Error/4);
            if(valid&&add)patches.Add((a,b,c));return valid;
        }
        void Visit(int x,int z,int size)
        {
            var a=(x,z);var b=(x,z+size);var c=(x+size,z);var d=(x+size,z+size);
            if(size>1&&Patch(a,b,c,false)&&Patch(c,b,d,false)){patches.Add((a,b,c));patches.Add((c,b,d));return;}
            if(size==1){Patch(a,b,c,true);Patch(c,b,d,true);return;}
            int half=size/2;Visit(x,z,half);Visit(x+half,z,half);Visit(x,z+half,half);Visit(x+half,z+half,half);
        }
        void Emit(Point A,Point B,Point C)
        {
            uint Index(Point value){if(!indices.TryGetValue(value,out uint index)){index=(uint)vertices.Count;indices.Add(value,index);vertices.Add(value);}return index;}
            faces.Add([Index(A),Index(B),Index(C)]);
            if(faces.Count>1000000||vertices.Count>1000000)throw new InvalidDataException("Bounded ground envelope counts.");
        }
        var points=source.SelectMany(f=>new[]{f.A,f.B,f.C}).ToArray();
        int left=(int)Math.Floor(points.Min(v=>v.X)/Step),right=(int)Math.Ceiling(points.Max(v=>v.X)/Step);
        int bottom=(int)Math.Floor(points.Min(v=>v.Z)/Step),top=(int)Math.Ceiling(points.Max(v=>v.Z)/Step);
        if((long)(right-left)*(top-bottom)>2000000)throw new InvalidDataException("Bounded SnakeRun sampling region.");
        const int block=16; // Eight metres maximum; every accepted triangle is certified.
        for(int x=(int)Math.Floor((double)left/block)*block;x<right;x+=block)
            for(int z=(int)Math.Floor((double)bottom/block)*block;z<top;z+=block)Visit(x,z,block);
        // Insert all neighbouring patch vertices into shared edges. Without
        // this, adaptive cells would create disconnected T junctions.
        var keys=patches.SelectMany(t=>new[]{t.A,t.B,t.C}).Distinct().ToArray();
        var horizontal=keys.GroupBy(p=>p.Z).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.X).ToArray());
        var vertical=keys.GroupBy(p=>p.X).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.Z).ToArray());
        var diagonal=keys.GroupBy(p=>p.X+p.Z).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.X).ToArray());
        (int X,int Z)[] Edge((int X,int Z) a,(int X,int Z) b)
        {
            var line=a.Z==b.Z?horizontal[a.Z]:a.X==b.X?vertical[a.X]:diagonal[a.X+a.Z];
            var points=line.Where(p=>p.X>=Math.Min(a.X,b.X)&&p.X<=Math.Max(a.X,b.X)&&p.Z>=Math.Min(a.Z,b.Z)&&p.Z<=Math.Max(a.Z,b.Z)).OrderBy(p=>(long)(p.X-a.X)*(b.X-a.X)+(long)(p.Z-a.Z)*(b.Z-a.Z)).ToArray();
            return points;
        }
        long splitPatches=0;
        foreach(var t in patches)
        {
            var ring=Edge(t.A,t.B).SkipLast(1).Concat(Edge(t.B,t.C).SkipLast(1)).Concat(Edge(t.C,t.A).SkipLast(1)).ToArray();
            if(ring.Length==3){Emit(Sample(t.A.X,t.A.Z)!.Value,Sample(t.B.X,t.B.Z)!.Value,Sample(t.C.X,t.C.Z)!.Value);continue;}
            splitPatches++;
            double cx=Math.Round((t.A.X+t.B.X+t.C.X)*Step/3*100)/100,cz=Math.Round((t.A.Z+t.B.Z+t.C.Z)*Step/3*100)/100;
            var patchPlane=new Face(Sample(t.A.X,t.A.Z)!.Value,Sample(t.B.X,t.B.Z)!.Value,Sample(t.C.X,t.C.Z)!.Value);
            // Subdivision must remain on the already certified affine patch.
            // Sampling the source again bends it and can exceed its certificate.
            double cy=HeightOn(patchPlane,new(cx,cz));Point center=new((float)cx,(float)cy,(float)cz);
            for(int i=0;i<ring.Length;i++){
                Point A=Sample(ring[i].X,ring[i].Z)!.Value,B=Sample(ring[(i+1)%ring.Length].X,ring[(i+1)%ring.Length].Z)!.Value;
                if(!Valid(center,A,B))throw new InvalidDataException($"Adaptive edge refinement lost source height/coverage certificate: {lastReason}; center={center}, A={A}, B={B}, patch={t}.");Emit(center,A,B);
            }
        }
        if(faces.Count<32)throw new InvalidDataException("Ground envelope unexpectedly incomplete.");
        using var w=new BinaryWriter(new FileStream(output,FileMode.CreateNew));w.Write(Encoding.ASCII.GetBytes("NCP1"));w.Write((uint)vertices.Count);w.Write((uint)faces.Count);
        foreach(var v in vertices){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);}foreach(var f in faces)foreach(uint i in f)w.Write(i);
        return new{vertices=vertices.Count,triangles=faces.Count,sourceFaces=source.Count,gridMetres=Step,adaptiveMaximumMetres=block*Step,conformingEdgeRefinement=true,splitPatches,upperEnvelope=true,noExtrapolation=true,completeProjectedSourceCoverage=true,affineHeightErrorCertified=true,maximumSampledHeightError=worst,heightErrorLimit=Error,uncoveredTrianglesRejected=uncovered,inaccurateTrianglesRejected=inaccurate,visualBspUnchanged=true,sourceTrianglesRetriangulated=true,gameplayTested=false};
    }
}
