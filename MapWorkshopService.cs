using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Deterministic original scene authoring; no RDF/PAK, runtime or paid API writes.
// OSM1 is the existing Studio geometry interchange. Native export is optional
// and proves serialization only; world metadata, tables and gameplay are separate.
internal static partial class MapWorkshopService
{
    internal sealed record Recipe(string Profile, string Name, float Radius, int Segments,
        float DeckHeight, float DeckThickness, float UndersideDepth, float PillarRadius,
        float PillarHeight, bool Stands, string SurfaceStyle = "legacy");
    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, Pin Recipe, Pin PreviewTemplate, Pin? NativeHelper, Pin[] PreviewAssets);
    private sealed record Vertex(float X, float Y, float Z, float U = 0, float V = 0);
    private sealed class Mesh(string name, byte[] rgba)
    {
        public string Name = name;
        public byte[] Rgba = rgba;
        public string Texture = "orig_power_white";
        public List<Vertex> Vertices = [];
        public List<uint[]> Faces = [];
        public void Triangle(Vertex a, Vertex b, Vertex c)
        { uint start = (uint)Vertices.Count; Vertices.AddRange([a, b, c]); Faces.Add([start, start + 1, start + 2]); }
        public void Quad(Vertex a, Vertex b, Vertex c, Vertex d)
        { Triangle(a, b, c); Triangle(a, c, d); }
    }
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static Pin Seal(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        long bytes = new FileInfo(path).Length;
        if (bytes is < 1 or > 32 * 1024 * 1024) throw new IOException("Bounded map input required.");
        return new(path, ContentSafety.Hash(path), bytes);
    }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new IOException("Bounded map JSON required.");
        byte[] bytes = File.ReadAllBytes(path); using var doc = JsonDocument.Parse(bytes);
        TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new InvalidDataException("Null map input.");
    }
    private static Recipe Validate(string path)
    {
        var r = Read<Recipe>(path);
        if (r.Profile != "assets-power-arena-v1" || r.SurfaceStyle is not ("legacy" or "apocalypse-stone-v1") || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 ||
            r.Name.Any(char.IsControl) || r.Radius is < 25 or > 150 || r.Segments is < 48 or > 192 || r.Segments % 12 != 0 ||
            r.DeckHeight is < -100 or > 100 || r.DeckThickness is < 2 or > 15 || r.UndersideDepth is < 5 or > 70 ||
            r.PillarRadius is < 1 or > 5 || r.PillarHeight is < 10 or > 90 || r.PillarRadius >= r.Radius * .07f ||
            new[] { r.Radius, r.DeckHeight, r.DeckThickness, r.UndersideDepth, r.PillarRadius, r.PillarHeight }.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Unsupported or unsafe power arena dimensions.");
        return r;
    }
    internal static object CreatePlan(string recipe, string preview, string native, string output)
    {
        if (IsTerrain(recipe)) ValidateTerrain(recipe); else Validate(recipe);
        var assets = new[] { "three.module.js", "three.core.js", "OrbitControls.js", "LICENSE" }.Select(name => Seal(Path.Combine(AppContext.BaseDirectory, "web", "vendor", "three", name))).ToArray();
        var p = new Plan("assets-map-workshop-plan-v1", Seal(recipe), Seal(preview), native == "none" ? null : Seal(native), assets);
        output = ContentSafety.SafeOutput(output);
        if (Inputs(p).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new IOException("Plan overlaps input.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); ContentSafety.WriteJson(output, p);
        return new { plan = output, nativeExportRequested = p.NativeHelper != null, readyToInstall = false };
    }
    private static Pin[] Inputs(Plan p) => (p.NativeHelper == null ? new[] { p.Recipe, p.PreviewTemplate } : new[] { p.Recipe, p.PreviewTemplate, p.NativeHelper }).Concat(p.PreviewAssets).ToArray();
    internal static async Task<object> RunAsync(string planPath, string? output)
    {
        planPath = Path.GetFullPath(planPath); ContentSafety.NoLinks(planPath);
        var locks = new List<FileStream>(); string? pending = null;
        try
        {
            locks.Add(new(planPath, FileMode.Open, FileAccess.Read, FileShare.Read)); var p = Read<Plan>(planPath);
            if (p.Profile != "assets-map-workshop-plan-v1") throw new InvalidDataException("Map plan profile mismatch.");
            foreach (var pin in Inputs(p))
            { ContentSafety.NoLinks(pin.Path); locks.Add(new(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read)); if (Seal(pin.Path) != pin) throw new IOException("Stale map input: " + pin.Path); }
            bool terrain = IsTerrain(p.Recipe.Path);
            var r = terrain ? null : Validate(p.Recipe.Path); var t = terrain ? ValidateTerrain(p.Recipe.Path) : null;
            var meshes = terrain ? GenerateTerrain(t!) : Generate(r!); var proof = terrain ? TerrainProof(meshes, t!) : GeometryProof(meshes, r!);
            if (output == null) return new { recipe = terrain ? (object)t! : r!, geometry = proof, nativeExportTested = false, readyToInstall = false };
            output = ContentSafety.SafeOutput(output); string prefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Inputs(p).Select(x => x.Path).Append(planPath).Any(x => x.Equals(output, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) throw new IOException("Map output overlaps sources.");
            pending = ContentSafety.FreshDirectory(output);
            Directory.CreateDirectory(Path.Combine(pending, "snapshots"));
            File.Copy(p.Recipe.Path, Path.Combine(pending, "snapshots", "recipe.json"));
            File.Copy(planPath, Path.Combine(pending, "plan.json"));
            WriteMesh(Path.Combine(pending, "arena.mesh"), meshes);
            if (terrain) WriteTerrainGround(Path.Combine(pending, "ground.ncp"), t!); else WriteGround(Path.Combine(pending, "ground.ncp"), r!);
            WriteObj(pending, meshes);
            WriteWhiteBmp(Path.Combine(pending, "orig_power_white.bmp"));
            if (terrain) WriteTerrainTextures(pending, t!);
            else if (r!.SurfaceStyle == "apocalypse-stone-v1")
            {
                WriteSurfaceBmp(Path.Combine(pending, "orig_power_stone_v2.bmp"), false);
                WriteSurfaceBmp(Path.Combine(pending, "orig_power_metal_v2.bmp"), true);
            }
            // The viewer reads recipe.radius/deckHeight; a terrain exposes its half size and arrival height there.
            object recipe = terrain ? new { t!.Profile, t.Name, t.Size, t.Cell, t.Seed, t.HillHeight, t.WaterLevel, t.Spires, t.DyingRadius, t.LavaLevel, Radius = t.Size / 2, DeckHeight = Height(t, 0, 0) } : r!;
            var scene = new { profile = "assets-map-preview-scene-v1", recipe, axes = "Taiwan X/Y-up/Z; metres", geometry = proof,
                navigationGround = ReadGroundPreview(Path.Combine(pending,"ground.ncp")),
                meshes = meshes.Select(m => new { name = m.Name, texture = m.Texture, rgba = m.Rgba.Select(x => (int)x), positions = m.Vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }), uv = m.Vertices.SelectMany(v => new[] { v.U, v.V }), indices = m.Faces.SelectMany(f => f) }),
                markers = terrain ? TerrainMarkers(t!) : new object[] { new { name = "Entrada propuesta", x = -r!.Radius * .55f, y = r!.DeckHeight, z = 0f }, new { name = "Rival propuesto", x = r!.Radius * .55f, y = r!.DeckHeight, z = 0f },new{name="Gradas propuestas",x=(r!.Radius+9)*MathF.Cos(MathF.PI*(1.17f+1f/3)),y=r!.DeckHeight+8,z=(r!.Radius+9)*MathF.Sin(MathF.PI*(1.17f+1f/3))} },
                navigationPolicy = terrain ? "Height grid ground; cells under water or lava and the spire footprints are excluded; water, lava and spires are decorative" :
                    "Visible combat deck and isolated spectator balcony; pillar footprint excluded; seating tiers, lamp and underside are decorative", readyToInstall = false };
            ContentSafety.WriteJson(Path.Combine(pending, "scene.json"), scene);
            string template = File.ReadAllText(p.PreviewTemplate.Path);
            if (!template.Contains("/*MAP_SCENE*/")) throw new InvalidDataException("Map preview scene slot absent.");
            File.WriteAllText(Path.Combine(pending, "preview.html"), template.Replace("/*MAP_SCENE*/", JsonSerializer.Serialize(scene, ContentSafety.Json)), new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(pending, "vendor"));
            foreach (var pin in p.PreviewAssets) File.Copy(pin.Path, Path.Combine(pending, "vendor", Path.GetFileName(pin.Path)));
            bool native = false;
            if (p.NativeHelper != null)
            {
                var start = new ProcessStartInfo(p.NativeHelper.Path) { WorkingDirectory = pending, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "map-workshop", Path.Combine(pending, "arena.mesh"), Path.Combine(pending, "scene.sbp") }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start) ?? throw new IOException("Map native helper unavailable.");
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await child.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new IOException("Map native export exceeded 60 seconds."); }
                string log = await stdout + await stderr; File.WriteAllText(Path.Combine(pending, "native.log"), log);
                if (child.ExitCode != 0 || !log.Contains("PASS map workshop native DFF/BSP")) throw new InvalidDataException("Native map export failed: " + log);
                using var reader = new BinaryReader(File.OpenRead(Path.Combine(pending, "scene.sbp")));
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "SBP1") throw new InvalidDataException("Native BSP proof envelope absent.");
                var fields = Enumerable.Range(0, 7).Select(_ => reader.ReadUInt32()).ToArray();
                if (fields[0] != meshes.Count || fields[2] != meshes.Sum(m => m.Faces.Count) || fields[4] != fields[2] || reader.BaseStream.Length != fields[6] + 32) throw new InvalidDataException("Native BSP source/readback count mismatch.");
                File.WriteAllBytes(Path.Combine(pending, "arena.bsp"), reader.ReadBytes(checked((int)fields[6]))); native = true;
            }
            if (Inputs(p).Any(x => Seal(x.Path) != x)) throw new IOException("Map inputs changed.");
            var resources = Directory.GetFiles(pending, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(pending, x), sha256 = ContentSafety.Hash(x), size = new FileInfo(x).Length }).ToArray();
            var audit = new { profile = terrain ? "assets-namek-terrain-candidate-v1" : "assets-power-arena-candidate-v1", status = "candidate_not_installed", utc = DateTimeOffset.UtcNow, planSha256 = ContentSafety.Hash(planPath), inputs = Inputs(p), geometry = proof,
                nativeDffBspRoundtrip = native, nativeCollisionTreeBuilt = native, nativeNavigationGenerated = false, tablesPrepared = false, worldMetadataPrepared = false, clientRenderingTested = false, worldTested = false, readyToInstall = false, resources };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            Directory.Move(pending, output); pending = null; return new { output, audit };
        }
        finally { foreach (var stream in locks) stream.Dispose(); if (pending != null && Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
    private static Vertex Polar(float radius, float angle, float y) => new(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle), (MathF.Cos(angle) + 1) / 2, (MathF.Sin(angle) + 1) / 2);
    private static List<Mesh> Generate(Recipe r)
    {
        byte[][] palette = [[225,213,172,255], [170,113,61,255], [34,65,73,255], [204,142,55,255], [87,76,63,255], [249,226,126,255]];
        var meshes = new List<Mesh>(); var cache = new Dictionary<string, Mesh>();
        Mesh Part(string part, int color) { string key = part + "_" + color; if (!cache.TryGetValue(key, out var m)) { m = new Mesh(key, palette[color]); cache.Add(key, m); meshes.Add(m); } return m; }
        float y = r.DeckHeight; int n = r.Segments;
        // Exact annular tiling: no invisible floor, overlapping motif decals or holes.
        float[] radii = [r.PillarRadius, r.Radius * .115f, r.Radius * .14f, r.Radius * .38f, r.Radius * .40f, r.Radius * .65f, r.Radius * .68f, r.Radius * .96f, r.Radius];
        for (int ring = 0; ring < radii.Length - 1; ring++) for (int i = 0; i < n; i++)
        {
            float a = i * MathF.Tau / n, b = (i + 1) * MathF.Tau / n;
            var p0 = Polar(radii[ring], a, y); var p1 = Polar(radii[ring + 1], a, y); var p2 = Polar(radii[ring + 1], b, y); var p3 = Polar(radii[ring], b, y);
            int color = ring switch { 0 => 2, 1 => 3, 3 => 3, 4 => i % 2 == 0 ? 2 : 1, 5 => 3, 7 => 2, _ => 0 };
            if (ring is 2 or 6)
            { Part("deck", 0).Triangle(p0, p3, p1); Part("deck", 1).Triangle(p1, p3, p2); }
            else Part("deck", color).Quad(p0, p3, p2, p1);
            if (ring == 0) Part("underside", i % 2 == 0 ? 1 : 2).Triangle(Polar(r.Radius, a, y - r.DeckThickness), Polar(r.Radius, b, y - r.DeckThickness), new(0, y - r.UndersideDepth, 0));
        }
        for (int i = 0; i < n; i++)
        {
            float a = i * MathF.Tau / n, b = (i + 1) * MathF.Tau / n;
            Part("rim", i % 4 < 2 ? 2 : 3).Quad(Polar(r.Radius, a, y), Polar(r.Radius, b, y), Polar(r.Radius, b, y - r.DeckThickness), Polar(r.Radius, a, y - r.DeckThickness));
            for (int band = 0; band < 10; band++)
            {
                float bottom = y + band * r.PillarHeight / 10, top = y + (band + 1) * r.PillarHeight / 10;
                Part("pillar", band % 3 == 0 ? 0 : band % 3 == 1 ? 2 : 3).Quad(Polar(r.PillarRadius, a, bottom), Polar(r.PillarRadius, a, top), Polar(r.PillarRadius, b, top), Polar(r.PillarRadius, b, bottom));
            }
            Part("pillar", 3).Triangle(new(0, y + r.PillarHeight, 0), Polar(r.PillarRadius, b, y + r.PillarHeight), Polar(r.PillarRadius, a, y + r.PillarHeight));
        }
        if (r.Stands) for (int step = 0; step < 5; step++) for (int i = 0; i < n / 3; i++)
        {
            float a = MathF.PI * 1.17f + i * MathF.Tau / n, b = a + MathF.Tau / n;
            float inner = r.Radius + 14 + step * 3, outer = inner + 3, h = y + 8 + step * 2;
            Part("stands", 0).Quad(Polar(inner, a, h), Polar(inner, b, h), Polar(outer, b, h), Polar(outer, a, h));
            Part("stands", 1).Quad(Polar(inner, b, h - 2), Polar(inner, a, h - 2), Polar(inner, a, h), Polar(inner, b, h));
            Part("stands", 4).Quad(Polar(outer, a, y + 5), Polar(outer, b, y + 5), Polar(outer, b, h), Polar(outer, a, h));
        }
        // New walkable balcony in front of the existing seating tiers. The
        // four-metre gap keeps the combat deck separate from spectators.
        if(r.Stands)for(int i=0;i<n/3;i++){
            float a=MathF.PI*1.17f+i*MathF.Tau/n,b=a+MathF.Tau/n,inner=r.Radius+4,outer=r.Radius+14,h=y+8;
            Part("spectator_deck",0).Quad(Polar(inner,a,h),Polar(inner,b,h),Polar(outer,b,h),Polar(outer,a,h));
            Part("spectator_deck",1).Quad(Polar(inner,b,y+5),Polar(inner,a,y+5),Polar(inner,a,h),Polar(inner,b,h));
        }
        // Suspended ceremonial lamp: original geometry, excluded from walk surface.
        for (int lat = 0; lat < 12; lat++) for (int i = 0; i < 32; i++)
        {
            Vertex Sphere(int t, int s) { float v = t * MathF.PI / 12, a = s * MathF.Tau / 32; return new(-r.Radius * .75f + 7 * MathF.Sin(v) * MathF.Cos(a), y + 40 + 7 * MathF.Cos(v), -r.Radius * 1.18f + 7 * MathF.Sin(v) * MathF.Sin(a)); }
            if (lat == 0) Part("lamp", 2).Triangle(Sphere(0, i), Sphere(1, i + 1), Sphere(1, i));
            else if (lat == 11) Part("lamp", 5).Triangle(Sphere(11, i), Sphere(11, i + 1), Sphere(12, i));
            else Part("lamp", lat < 4 ? 2 : 5).Quad(Sphere(lat, i), Sphere(lat, i + 1), Sphere(lat + 1, i + 1), Sphere(lat + 1, i));
        }
        if (r.SurfaceStyle == "apocalypse-stone-v1") ApplyStoneSurfaces(meshes);
        return meshes;
    }
    private static void ApplyStoneSurfaces(List<Mesh> meshes)
    {
        // Appearance only: positions, face order, winding and NCP remain exact.
        byte[][] palette = [[197,190,178,255], [151,139,126,255], [115,129,137,255],
            [196,157,105,255], [130,125,122,255], [249,216,154,255]];
        foreach (var m in meshes)
        {
            int color = int.Parse(m.Name[(m.Name.LastIndexOf('_') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            m.Rgba = palette[color];
            m.Texture = color is 3 or 5 ? "orig_power_metal_v2" : "orig_power_stone_v2";
            bool wall = m.Name.StartsWith("pillar_") || m.Name.StartsWith("rim_") || m.Name.StartsWith("underside_");
            for (int i = 0; i < m.Vertices.Count; i++)
            {
                var v = m.Vertices[i];
                m.Vertices[i] = v with { U = wall ? MathF.Atan2(v.Z, v.X) / MathF.Tau : v.X / 12,
                    V = wall ? v.Y / 8 : v.Z / 12 };
            }
            if (wall) foreach (var f in m.Faces)
            {
                float min = f.Min(i => m.Vertices[(int)i].U), max = f.Max(i => m.Vertices[(int)i].U);
                if (max - min > .5f) foreach (var i in f)
                { var v = m.Vertices[(int)i]; if (v.U < 0) m.Vertices[(int)i] = v with { U = v.U + 1 }; }
            }
        }
    }
    private static object GeometryProof(List<Mesh> meshes, Recipe r)
    {
        int triangles = 0; double deckArea = 0;
        foreach (var m in meshes) foreach (var f in m.Faces)
        {
            var a = m.Vertices[(int)f[0]]; var b = m.Vertices[(int)f[1]]; var c = m.Vertices[(int)f[2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (nx * nx + ny * ny + nz * nz < 1e-10 || new[] { a, b, c }.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) throw new InvalidDataException("Degenerate or nonfinite authored triangle.");
            if (m.Name.StartsWith("deck_")) { if (ny <= 0) throw new InvalidDataException("Deck winding must face Taiwan +Y."); deckArea += ny / 2; }
            triangles++;
        }
        double expected = r.Segments / 2d * Math.Sin(2 * Math.PI / r.Segments) * (r.Radius * r.Radius - r.PillarRadius * r.PillarRadius);
        if (Math.Abs(deckArea - expected) > expected * 1e-5) throw new InvalidDataException("Visible deck area mismatch.");
        return new { meshes = meshes.Count, vertices = meshes.Sum(m => m.Vertices.Count), triangles, deckTriangles = 16 * r.Segments, diameterMetres = 2 * r.Radius, visibleDeckArea = deckArea, expectedPolygonArea = expected, deckUpwardWinding = true, pillarFootprintExcluded = true };
    }
    private static void WriteMesh(string path, List<Mesh> meshes)
    {
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew)); w.Write(Encoding.ASCII.GetBytes("OSM1")); w.Write((uint)meshes.Count);
        foreach (var m in meshes)
        {
            w.Write((uint)m.Vertices.Count); w.Write((uint)m.Faces.Count); w.Write(1u);
            foreach (var v in m.Vertices) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.U); w.Write(v.V); }
            foreach (var f in m.Faces) { foreach (var i in f) w.Write(i); w.Write(0u); }
            w.Write(m.Rgba); byte[] name = new byte[128]; Encoding.ASCII.GetBytes(m.Texture).CopyTo(name, 0); w.Write(name);
        }
    }
    private static void WriteGround(string path, Recipe r)
    {
        int n = r.Segments;
        int stands=r.Stands?n/3:0,standV=r.Stands?2*(stands+1):0;
        int seatingV=r.Stands?5*2*(stands+1):0,seatingT=r.Stands?5*2*stands:0;
        uint vertexCount=checked((uint)(2*n+standV+seatingV));
        uint originalTriangleCount=checked((uint)(2*n+2*stands)); // unchanged deck plus spectator balcony
        uint seatingTriangleCount=checked((uint)seatingT);
        uint triangleCount=checked(originalTriangleCount+seatingTriangleCount);
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew)); w.Write(Encoding.ASCII.GetBytes("NCP1")); w.Write(vertexCount); w.Write(triangleCount);
        uint verticesWritten=0,trianglesWritten=0;
        void WritePoint(Vertex v){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);verticesWritten++;}
        void WriteTriangle(uint a,uint b,uint c){if(a>=vertexCount||b>=vertexCount||c>=vertexCount)throw new InvalidDataException("Generated NCP index exceeds declared vertex count.");w.Write(a);w.Write(b);w.Write(c);trianglesWritten++;}
        // Native PathEngine truncates inverse-Z centimetres. Keep an inward
        // physical margin so quantized vertices never enter the pillar/void.
        const float margin=.04f;
        foreach (float radius in new[] { r.PillarRadius+margin, r.Radius-margin }) for (int i = 0; i < n; i++) { var v = Polar(radius, i * MathF.Tau / n, r.DeckHeight); WritePoint(v); }
        if(r.Stands)foreach(float radius in new[]{r.Radius+4+margin,r.Radius+14-margin})for(int i=0;i<=stands;i++){float angle=MathF.PI*1.17f+i*MathF.Tau/n;if(i==0)angle+=margin/r.Radius;if(i==stands)angle-=margin/r.Radius;var v=Polar(radius,angle,r.DeckHeight+8);WritePoint(v);}
        // Preserve each authored horizontal tread as its own walk surface.
        // No riser faces are written: the two-metre elevation changes must
        // stay discontinuities, never sloped bridges through the empty space.
        if(r.Stands)for(int step=0;step<5;step++)foreach(float radius in new[]{r.Radius+14+step*3+margin,r.Radius+17+step*3-margin})for(int i=0;i<=stands;i++){
            float angle=MathF.PI*1.17f+i*MathF.Tau/n;
            if(i==0)angle+=margin/(r.Radius+14+step*3);
            if(i==stands)angle-=margin/(r.Radius+17+step*3);
            var v=Polar(radius,angle,r.DeckHeight+8+step*2);WritePoint(v);
        }
        for (int i = 0; i < n; i++) { uint a = (uint)i, b = (uint)((i + 1) % n), c = b + (uint)n, d = a + (uint)n; WriteTriangle(a,b,c);WriteTriangle(a,c,d); }
        for(int i=0;i<stands;i++){uint a=(uint)(2*n+i),b=a+1,c=b+(uint)(stands+1),d=a+(uint)(stands+1);WriteTriangle(a,b,c);WriteTriangle(a,c,d);}
        uint seatingStart=(uint)(2*n+standV);
        for(int step=0;step<5&&r.Stands;step++)for(int i=0;i<stands;i++){
            uint a=seatingStart+(uint)(step*2*(stands+1)+i),b=a+1,c=b+(uint)(stands+1),d=a+(uint)(stands+1);
            WriteTriangle(a,b,c);WriteTriangle(a,c,d);
        }
        if(verticesWritten!=vertexCount||trianglesWritten!=triangleCount||triangleCount-seatingTriangleCount!=originalTriangleCount||
            w.BaseStream.Length!=12L+12L*vertexCount+12L*triangleCount)
            throw new InvalidDataException("Generated NCP counts do not preserve original deck/balcony triangles and append all five stair surfaces.");
    }
    private static object ReadGroundPreview(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path));
        if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="NCP1")throw new InvalidDataException("Generated ground header differs.");
        uint vertices=reader.ReadUInt32(),triangles=reader.ReadUInt32();
        if(vertices>10000||triangles>10000)throw new InvalidDataException("Bounded authored preview ground required.");
        var positions=new float[vertices*3];var indices=new uint[triangles*3];
        for(int i=0;i<positions.Length;i++)positions[i]=reader.ReadSingle();
        for(int i=0;i<indices.Length;i++)indices[i]=reader.ReadUInt32();
        if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Ground exact EOF required.");
        return new{positions,indices,marginMetres=.04f};
    }
    private static void WriteObj(string dir, List<Mesh> meshes)
    {
        using var obj = new StreamWriter(Path.Combine(dir, "arena.obj"), false, new UTF8Encoding(false)); using var mtl = new StreamWriter(Path.Combine(dir, "arena.mtl"), false, new UTF8Encoding(false));
        obj.WriteLine("# Taiwan Y-up, metres. Import with Blender forward=-Z, up=Y.\nmtllib arena.mtl"); int offset = 1;
        foreach (var m in meshes)
        {
            obj.WriteLine("o " + m.Name); obj.WriteLine("usemtl " + m.Name); mtl.WriteLine("newmtl " + m.Name); mtl.WriteLine(FormattableString.Invariant($"Kd {m.Rgba[0] / 255f} {m.Rgba[1] / 255f} {m.Rgba[2] / 255f}\nKa 0.2 0.2 0.2\nKs 0 0 0\nd 1"));
            mtl.WriteLine("map_Kd " + m.Texture + ".bmp");
            foreach (var v in m.Vertices) obj.WriteLine(FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}"));
            foreach (var v in m.Vertices) obj.WriteLine(FormattableString.Invariant($"vt {v.U:R} {v.V:R}"));
            foreach (var f in m.Faces) obj.WriteLine($"f {f[0] + offset}/{f[0] + offset} {f[1] + offset}/{f[1] + offset} {f[2] + offset}/{f[2] + offset}"); offset += m.Vertices.Count;
        }
    }
    private static void WriteWhiteBmp(string path)
    {
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew)); w.Write((ushort)0x4d42); w.Write(102u); w.Write(0u); w.Write(54u); w.Write(40u); w.Write(4); w.Write(4); w.Write((ushort)1); w.Write((ushort)24); w.Write(0u); w.Write(48u); w.Write(2835); w.Write(2835); w.Write(0u); w.Write(0u); w.Write(Enumerable.Repeat((byte)255, 48).ToArray());
    }
    private static void WriteSurfaceBmp(string path, bool metal)
    {
        const int size = 512, bytes = size * size * 3;
        using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew));
        w.Write((ushort)0x4d42); w.Write((uint)(54 + bytes)); w.Write(0u); w.Write(54u); w.Write(40u);
        w.Write(size); w.Write(size); w.Write((ushort)1); w.Write((ushort)24); w.Write(0u); w.Write((uint)bytes);
        w.Write(2835); w.Write(2835); w.Write(0u); w.Write(0u);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            uint n = unchecked((uint)(x * 374761393 + y * 668265263));
            n = unchecked((n ^ (n >> 13)) * 1274126177u); n ^= n >> 16;
            double grain = (n & 255) / 255d;
            double cloud = Math.Sin(x * Math.Tau / 128 + Math.Sin(y * Math.Tau / 256)) * 5 + Math.Cos(y * Math.Tau / 64) * 3;
            int row = y / 128, shifted = (x + (row % 2) * 64) % 128;
            int seam = Math.Min(Math.Min(shifted, 128 - shifted), Math.Min(y % 128, 128 - y % 128));
            double value = (metal ? 193 : 204) + cloud + (grain - .5) * (metal ? 22 : 30);
            if (!metal && seam < 3) value -= 65 - seam * 10;
            if (metal) value += Math.Sin((x + 2 * y) * Math.Tau / 32) * 5;
            // Sparse dark mineral veins, not luminous gameplay markings.
            if (!metal && Math.Abs(Math.Sin(x * Math.Tau / 256) * 16 + y % 128 - 62) < 1.2) value -= 24;
            byte channel = (byte)Math.Clamp((int)value, 55, 242);
            w.Write(channel); w.Write(channel); w.Write(channel);
        }
    }
}
