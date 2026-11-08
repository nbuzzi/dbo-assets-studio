using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DboAssetsStudio;

// Reviewed ground selection for the original SnakeRun mesh. This operation
// authors private navigation; it never installs or changes existing worlds.
internal static class StaticBspNavigationService
{
    private const string Profile = "assets-snakerun-native-indoor-navigation-v1";
    private const string Rule = "shedao01/shedao_head/shedao_platform; original winding; upward normalY/length>0.20; navigation-only XYZ weld at1mm; exclude decorative materials";
    private const string EnvelopeProfile="assets-snakerun-native-indoor-navigation-v2";
    private const string EnvelopeRule="1239 original upward ground faces; exact local terminal upper-surface union; original navigation elsewhere; conforming XYZ weld1mm; visual BSP unchanged";
    private const string RampProfile="assets-snakerun-visible-ramp-navigation-v3";
    private const string RampRule="source body690/terminal38 plus visible ramp10; certified conforming walk surface; other source ground retained; no invisible bridge";
    private const string RampCentreProfile="assets-snakerun-visible-ramp-navigation-v4";
    private const string RampCentreRule="source body690/terminal38 plus visible ramp20 with terminal centre anchor; certified conforming walk surface; other source ground retained; no invisible bridge";
    private static bool IsRamp(Plan p)=>p.Profile==RampProfile||p.Profile==RampCentreProfile;
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, string GroundRule, Pin Mesh, Pin WorldAudit, Pin Bsp, Pin Wfp, Pin PathEngine, Pin ChunkHelper, Pin NavigationHelper,Pin? RampMesh=null,Pin? RampAudit=null,Pin? RampFaces=null,Pin? WorldPlan=null,Pin? BspAudit=null);
    private static string ChunkHelper => Path.Combine(AppContext.BaseDirectory, "scene-navi-piece-builder.exe");
    private static string NavigationHelper => Path.Combine(AppContext.BaseDirectory, "static-bsp-navigation-builder.exe");
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        if (path.Any(c => c < 32 || c > 127)) throw new InvalidDataException("Native ASCII paths required.");
        long length = new FileInfo(path).Length;
        if (length is < 1 or > 128L * 1024 * 1024) throw new InvalidDataException("Bounded navigation input required.");
        return new(path, ContentSafety.Hash(path), length);
    }
    private static Pin[] Inputs(Plan p) => new[]{p.Mesh,p.WorldAudit,p.Bsp,p.Wfp,p.PathEngine,p.ChunkHelper,p.NavigationHelper,p.RampMesh,p.RampAudit,p.RampFaces,p.WorldPlan,p.BspAudit}.Where(x=>x!=null).Select(x=>x!).ToArray();
    private static void Validate(Plan p)
    {
        if (!((p.Profile==Profile&&p.GroundRule==Rule)||(p.Profile==EnvelopeProfile&&p.GroundRule==EnvelopeRule)||(p.Profile==RampProfile&&p.GroundRule==RampRule)||(p.Profile==RampCentreProfile&&p.GroundRule==RampCentreRule)) || p.Mesh.Sha256 != "2B1A245AD29A07F4F91DD2A485D36C787ABAC8D1F1D4CFF87F6A47B9F2A7CF6B" || p.Wfp.Bytes != 443392 ||
            p.ChunkHelper.Path != Path.GetFullPath(ChunkHelper) || p.NavigationHelper.Path != Path.GetFullPath(NavigationHelper)) throw new InvalidDataException("Reviewed SnakeRun mesh/rule/helper required.");
        using var d = JsonDocument.Parse(File.ReadAllBytes(p.WorldAudit.Path)); var a = d.RootElement;
        if (a.GetProperty("profile").GetString() != "assets-snakeway-static-bsp-wrapper-v1" || a.GetProperty("resourceId").GetInt32() != 6009 ||
            !a.GetProperty("nativeGwpRoundtrip").GetBoolean() || !a.GetProperty("nativeEmptyFieldRead").GetBoolean() || !a.GetProperty("nativeBlockPropertiesRoundtrip").GetBoolean())
            throw new InvalidDataException("Canonical native world wrapper required.");
        foreach (var pin in new[] { p.Wfp, p.Bsp })
            if (!a.GetProperty("resources").EnumerateArray().Any(x => x.GetProperty("sha256").GetString() == pin.Sha256 && x.GetProperty("size").GetInt64() == pin.Bytes)) throw new InvalidDataException("World resource pin absent from audit.");
        SceneGeometryService.BakeMesh(p.Mesh.Path, null, null);
        if(IsRamp(p)){
            bool centre=p.Profile==RampCentreProfile;int additions=centre?88:44,walk=centre?20:10;
            if(p.RampMesh==null||p.RampAudit==null||p.RampFaces==null||p.WorldPlan==null||p.BspAudit==null)throw new InvalidDataException("Complete visible ramp/world/BSP pins required.");
            using var ramp=JsonDocument.Parse(File.ReadAllBytes(p.RampAudit.Path));var rr=ramp.RootElement;
            string? rampProfile=rr.GetProperty("profile").GetString();
            if((centre?rampProfile!="assets-snakeway-visible-terminal-ramp-v4":rampProfile is not ("assets-snakeway-visible-terminal-ramp-v1" or "assets-snakeway-visible-terminal-ramp-v2" or "assets-snakeway-visible-terminal-ramp-v3"))||rr.GetProperty("meshSha256").GetString()!=p.RampMesh.Sha256||rr.GetProperty("additionalTriangles").GetInt32()!=additions||rr.GetProperty("walkTriangles").GetInt32()!=walk||!rr.GetProperty("originalGeometryUnchanged").GetBoolean())throw new InvalidDataException("Canonical visible ramp required.");
            var faces=JsonSerializer.Deserialize<SnakewayGroundEnvelope.Face[]>(File.ReadAllBytes(p.RampFaces.Path),Strict);
            if(faces==null||!faces.SequenceEqual(SnakewayRampService.ReadRampTop(p.RampMesh.Path)))throw new InvalidDataException("Navigation faces must equal the visible ramp mesh.");
            using var worldPlan=JsonDocument.Parse(File.ReadAllBytes(p.WorldPlan.Path));var wp=worldPlan.RootElement;
            var linkedAudit=JsonSerializer.Deserialize<Pin>(wp.GetProperty("bspAudit"),Strict);
            if(linkedAudit!=p.BspAudit||a.GetProperty("planSha256").GetString()!=p.WorldPlan.Sha256||a.GetProperty("originalBspAuditSha256").GetString()!=p.BspAudit.Sha256)throw new InvalidDataException("Exact world plan/BSP audit chain required.");
            using var bspAudit=JsonDocument.Parse(File.ReadAllBytes(p.BspAudit.Path));var ba=bspAudit.RootElement;
            if(ba.GetProperty("profile").GetString()!="taiwan-native-static-clump-additive-bsp-v2"||ba.GetProperty("bspSha256").GetString()!=p.Bsp.Sha256||ba.GetProperty("originalSourceTriangles").GetInt32()!=5736||ba.GetProperty("additionalTriangles").GetInt32()!=additions||!ba.GetProperty("allOrientedTrianglesPositionsUvPrelightsMaterialsPreserved").GetBoolean()||!ba.GetProperty("snapshots").EnumerateArray().Any(x=>x.GetProperty("sha256").GetString()==p.RampMesh.Sha256))throw new InvalidDataException("Visible BSP must contain the same audited ramp faces and preserve5736 originals.");
        }else if(p.RampMesh!=null||p.RampAudit!=null||p.RampFaces!=null||p.WorldPlan!=null||p.BspAudit!=null)throw new InvalidDataException("Ramp pins require explicit ramp profile.");
    }
    internal static object CreateRampPlan(string mesh,string world,string dll,string ramp,string output){
        world=Path.GetFullPath(world);string root=Path.Combine(world,"world","dbo_assets_snakeway_001");
        var worldPlan=Seal(Path.Combine(world,"plan.json"));using var wp=JsonDocument.Parse(File.ReadAllBytes(worldPlan.Path));
        var bspAudit=JsonSerializer.Deserialize<Pin>(wp.RootElement.GetProperty("bspAudit"),Strict)??throw new InvalidDataException("BSP audit pin.");
        if(Seal(bspAudit.Path)!=bspAudit)throw new IOException("Stale BSP audit.");
        using var rampAudit=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ramp,"audit.json")));
        bool centre=rampAudit.RootElement.GetProperty("profile").GetString()=="assets-snakeway-visible-terminal-ramp-v4";
        var p=new Plan(centre?RampCentreProfile:RampProfile,centre?RampCentreRule:RampRule,Seal(mesh),Seal(Path.Combine(world,"audit.json")),Seal(Path.Combine(root,"dbo_assets_snakeway_001.bsp")),Seal(Path.Combine(root,"wfp")),Seal(dll),Seal(ChunkHelper),Seal(NavigationHelper),Seal(Path.Combine(ramp,"ramp.mesh")),Seal(Path.Combine(ramp,"audit.json")),Seal(Path.Combine(ramp,"top-faces.json")),worldPlan,bspAudit);
        Validate(p);output=ContentSafety.SafeOutput(output);
        if(File.Exists(output)||Directory.Exists(output)||Inputs(p).Any(x=>x.Path.Equals(output,StringComparison.OrdinalIgnoreCase)))throw new IOException("Fresh ramp navigation plan required.");
        ContentSafety.WriteJson(output,p);return new{plan=output,readyToInstall=false};
    }
    private static object RampGround(Plan p,string ncp){
        var source=SnakewayRampService.ReadGround(p.Mesh.Path);
        var ramp=SnakewayRampService.ReadRampTop(p.RampMesh!.Path);
        return SnakewayTerminalUnion.WriteVisibleRamp(source,ramp,ncp);
        /* Experimental full-grid retessellation is retained in source history;
           the reviewed ramp uses a local union, preserving the original road.
        string walk=Path.Combine(Path.GetDirectoryName(ncp)!,"walksurface.ncp");var primary=source.Where((_,i)=>components.Components[i]==components.Main||components.Components[i]==components.Terminal).Concat(ramp).ToArray();var proof=SnakewayGroundEnvelope.WriteRampWalkSurface(primary,walk);
        var vertices=new List<SnakewayGroundEnvelope.Point>();var triangles=new List<uint[]>();var map=new Dictionary<SnakewayGroundEnvelope.Point,uint>();
        uint Index(SnakewayGroundEnvelope.Point p){static float S(float x)=>(float)(Math.Round(x*1000d,MidpointRounding.AwayFromZero)/1000);p=new(S(p.X),S(p.Y),S(p.Z));if(!map.TryGetValue(p,out uint i)){i=(uint)vertices.Count;map.Add(p,i);vertices.Add(p);}return i;}
        using(var r=new BinaryReader(File.OpenRead(walk))){r.ReadBytes(4);uint nv=r.ReadUInt32(),nt=r.ReadUInt32();var v=new SnakewayGroundEnvelope.Point[nv];for(int i=0;i<nv;i++)v[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());for(int i=0;i<nt;i++)triangles.Add([Index(v[r.ReadUInt32()]),Index(v[r.ReadUInt32()]),Index(v[r.ReadUInt32()])]);}
        var rest=source.Where((_,i)=>components.Components[i]!=components.Main&&components.Components[i]!=components.Terminal).ToArray();foreach(var f in rest)triangles.Add([Index(f.A),Index(f.B),Index(f.C)]);
        using(var w=new BinaryWriter(new FileStream(ncp,FileMode.CreateNew))){w.Write(Encoding.ASCII.GetBytes("NCP1"));w.Write((uint)vertices.Count);w.Write((uint)triangles.Count);foreach(var v in vertices){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);}foreach(var t in triangles)foreach(uint i in t)w.Write(i);}
        return new{vertices=vertices.Count,triangles=triangles.Count,walkSurface=proof,unchangedOtherSourceFaces=rest.Length,visibleRampTriangles=10,noInvisibleBridge=true,worldTested=false};*/
    }
    internal static object CreatePlan(string mesh, string world, string dll, string output,bool envelope=false)
    {
        world = Path.GetFullPath(world); string root = Path.Combine(world, "world", "dbo_assets_snakeway_001");
        var p = new Plan(envelope?EnvelopeProfile:Profile, envelope?EnvelopeRule:Rule, Seal(mesh), Seal(Path.Combine(world, "audit.json")), Seal(Path.Combine(root, "dbo_assets_snakeway_001.bsp")), Seal(Path.Combine(root, "wfp")), Seal(dll), Seal(ChunkHelper), Seal(NavigationHelper));
        Validate(p); output = ContentSafety.SafeOutput(output); if (File.Exists(output) || Directory.Exists(output) || Inputs(p).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Fresh navigation plan required.");
        ContentSafety.WriteJson(output, p); return new { plan = output, resourceId = 6009, groundRule = Rule, readyToInstall = false };
    }
    private record Vertex(float X, float Y, float Z);
    private static object Ground(string mesh, string ncp,bool envelope)
    {
        using var r = new BinaryReader(File.OpenRead(mesh)); if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "OSM1" || r.ReadUInt32() != 9) throw new InvalidDataException("SnakeRun nine material meshes required.");
        var vertices = new List<Vertex>(); var indices = new Dictionary<Vertex, uint>(); var faces = new List<uint[]>(); var selected = new List<object>();
        var envelopeFaces=new List<SnakewayGroundEnvelope.Face>();
        for (uint mi = 0; mi < 9; mi++)
        {
            uint nv = r.ReadUInt32(), nt = r.ReadUInt32(), nm = r.ReadUInt32(); if (nv != 5874 || nm != 1 || nt > 6000) throw new InvalidDataException("Reviewed native mesh counts.");
            var v = new Vertex[nv]; for (int i = 0; i < nv; i++) { v[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); r.ReadSingle(); r.ReadSingle(); }
            var t = new uint[nt][]; for (int i = 0; i < nt; i++) t[i] = [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()];
            byte[] mat = r.ReadBytes(132); string name = Encoding.ASCII.GetString(mat[4..]).Split('\0')[0];
            if (mi is < 2 or > 4) continue;
            if (name != new[] { "shedao01", "shedao_head", "shedao_platform" }[mi - 2]) throw new InvalidDataException("Ground material identity changed.");
            var selections = new List<uint>();
            for (uint ti = 0; ti < nt; ti++)
            {
                var a = v[t[ti][0]]; var b = v[t[ti][1]]; var c = v[t[ti][2]];
                double ux = b.X-a.X, uy=b.Y-a.Y, uz=b.Z-a.Z, vx=c.X-a.X, vy=c.Y-a.Y, vz=c.Z-a.Z;
                double nx=uy*vz-uz*vy, ny=uz*vx-ux*vz, nz=ux*vy-uy*vx, length=Math.Sqrt(nx*nx+ny*ny+nz*nz);
                if (!(ny > .20 * length)) continue; selections.Add(ti);
                envelopeFaces.Add(new(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z),new(c.X,c.Y,c.Z)));
                var face = new uint[3]; for (int k = 0; k < 3; k++) { var original=v[t[ti][k]];
                    static float Snap(float x) => (float)(Math.Round((double)x * 1000, MidpointRounding.AwayFromZero) / 1000);
                    var point=new Vertex(Snap(original.X),Snap(original.Y),Snap(original.Z));
                    if(Math.Abs(point.X-original.X)>.00051f||Math.Abs(point.Y-original.Y)>.00051f||Math.Abs(point.Z-original.Z)>.00051f)throw new InvalidDataException("Navigation seam weld deviation bound.");
                    if (!indices.TryGetValue(point, out uint index)) { index=(uint)vertices.Count; indices.Add(point,index);vertices.Add(point); } face[k]=index; }
                faces.Add(face);
            }
            selected.Add(new { meshIndex = mi, material = name, triangles = selections, reason = Rule });
        }
        if (r.BaseStream.Position != r.BaseStream.Length || faces.Count != 1239) throw new InvalidDataException("Reviewed complete1239 ground triangles required.");
        if(envelope)return new{sourceSelection=selected,envelope=SnakewayTerminalUnion.Write(envelopeFaces,ncp)};
        using var w = new BinaryWriter(new FileStream(ncp, FileMode.CreateNew)); w.Write(Encoding.ASCII.GetBytes("NCP1")); w.Write((uint)vertices.Count); w.Write((uint)faces.Count);
        foreach(var v in vertices){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);} foreach(var f in faces)foreach(uint i in f)w.Write(i);
        return new { vertices=vertices.Count, triangles=faces.Count, selected, positionWeld="navigation-only1mm grid; per-axis deviation<=0.00051m; visual BSP unchanged", sourceTriangleSelectionAndWindingPreserved=true, excludesCloudsSkyControlGeometry=true };
    }
    private static async Task<string> Native(string helper, string cwd, string marker, params string[] args)
    {
        var start = new ProcessStartInfo(helper) { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=cwd, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach(string a in args)start.ArgumentList.Add(a);using var child=Process.Start(start)??throw new IOException("Native helper unavailable.");
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try{await child.WaitForExitAsync(deadline.Token);}catch(OperationCanceledException){if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();File.WriteAllText(Path.Combine(cwd,Path.GetFileNameWithoutExtension(helper)+".timeout.log"),await stdout+await stderr);throw new IOException("Bounded native navigation deadline; captured partial log retained.");}
        string log=await stdout+await stderr;File.WriteAllText(Path.Combine(cwd,Path.GetFileNameWithoutExtension(helper)+".log"),log);
        if(child.ExitCode!=0||!log.Contains(marker))throw new InvalidDataException("Native navigation failed ("+child.ExitCode+"): "+log);return log;
    }
    internal static async Task<object> RunAsync(string path,string? output)
    {
        path=Path.GetFullPath(path);ContentSafety.NoLinks(path);if(new FileInfo(path).Length is < 1 or > 16384)throw new InvalidDataException("Bounded plan.");
        var locks=new List<FileStream>();string? pending=null;
        try
        {
            locks.Add(new(path,FileMode.Open,FileAccess.Read,FileShare.Read));byte[] bytes=File.ReadAllBytes(path);using var d=JsonDocument.Parse(bytes);TerrainGeometryService.NoDuplicateKeys(d.RootElement);
            var p=JsonSerializer.Deserialize<Plan>(bytes,Strict)??throw new InvalidDataException("Null navigation plan.");
            foreach(var pin in Inputs(p)){ContentSafety.NoLinks(pin.Path);locks.Add(new(pin.Path,FileMode.Open,FileAccess.Read,FileShare.Read));if(Seal(pin.Path)!=pin)throw new IOException("Stale input: "+pin.Path);}Validate(p);
            if(output!=null){output=ContentSafety.SafeOutput(output);string prefix=output.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(Inputs(p).Select(x=>x.Path).Append(path).Any(x=>x.Equals(output,StringComparison.OrdinalIgnoreCase)||x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new IOException("Navigation candidate overlaps source.");pending=ContentSafety.FreshDirectory(output);}
            else{pending=Path.Combine(Path.GetTempPath(),"AssetsSnakeNavigation",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(pending);}
            string sources=Path.Combine(pending,"snapshots");Directory.CreateDirectory(sources);string mesh=Path.Combine(sources,"snakerun.mesh"),dll=Path.Combine(sources,"PathEngine_x64.dll"),wfp=Path.Combine(sources,"wfp");File.Copy(p.Mesh.Path,mesh);File.Copy(p.PathEngine.Path,dll);File.Copy(p.Wfp.Path,wfp);File.Copy(p.WorldAudit.Path,Path.Combine(sources,"world-audit.json"));
            // Retain all ramp/world/helper preimages, including those that were
            // previously only pinned in the plan. No mutable input is its backup.
            int snapshotIndex=0;foreach(var pin in Inputs(p)){string target=Path.Combine(sources,$"input-{snapshotIndex++:D2}-{Path.GetFileName(pin.Path)}");File.Copy(pin.Path,target);if(ContentSafety.Hash(target)!=pin.Sha256)throw new IOException("Navigation snapshot differs from input.");}
            bool rampProfile=IsRamp(p);bool envelope=p.Profile==EnvelopeProfile||rampProfile;
            string ncp=Path.Combine(pending,"ground.ncp"),tok=Path.Combine(pending,"ASSETS_SNAKEWAY_PE_Nav_000.tok");var ground=rampProfile?RampGround(p,ncp):Ground(mesh,ncp,envelope);
            await Native(p.ChunkHelper.Path,pending,"PASS native selected-ground TOK",dll,ncp,tok);var nativeArgs=new List<string>{tok,wfp,dll,Path.Combine(pending,"native")};if(envelope)nativeArgs.Add("--upper-envelope");
            double? terminalHeight=null;
            if(rampProfile){terminalHeight=SnakewayRampService.ReadGround(p.Mesh.Path).Concat(SnakewayRampService.ReadRampTop(p.RampMesh!.Path)).Select(f=>SnakewayRampService.Height(f,-146,40)).Where(y=>y!=null).Select(y=>y!.Value).Max();if(!double.IsFinite(terminalHeight.Value)||terminalHeight<49.56||terminalHeight>51)throw new InvalidDataException("Pinned visible terminal height required.");nativeArgs.Add("--visible-terminal-height");nativeArgs.Add(terminalHeight.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));}
            string log=await Native(p.NavigationHelper.Path,pending,"PASS native Snake Way indoor export/reload",nativeArgs.ToArray());
            if(Inputs(p).Any(x=>Seal(x.Path)!=x))throw new IOException("Navigation sources changed.");
            var resources=Directory.GetFiles(Path.Combine(pending,"native","navi"),"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(x=>new{path=Path.GetRelativePath(pending,x),sha256=ContentSafety.Hash(x),size=new FileInfo(x).Length}).ToArray();
            var audit=new{profile=p.Profile,status="candidate_not_installed",utc=DateTimeOffset.UtcNow,planSha256=ContentSafety.Hash(path),sourcePins=Inputs(p),ground,resources,terminalHeight,nativeContentChunkRoundtrip=true,nativeIndoorExportReload=true,taiwanSupportedAgents=new[]{.3,.5,1.0,2.0,3.0},testedBidirectionalBodyRouteAgents=new[]{.3,.5,1.0},bidirectionalBodyRoute=true,headDeckLocalRouteTested=envelope,headDeckToBodyRouteTested=false,terminalPlatformTraversalTested=envelope,clientSlopeAcceptanceTested=false,renderingTested=false,worldTested=false,readyToInstall=false};
            if(output!=null){File.Copy(path,Path.Combine(pending,"plan.json"));ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),audit);Directory.Move(pending,output);pending=null;return new{output,audit};}return new{audit};
        }
        finally{foreach(var l in locks)l.Dispose();if(output==null&&pending!=null&&Directory.Exists(pending))Directory.Delete(pending,true);}
    }
}
