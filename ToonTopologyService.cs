using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// Narrow static-geometry adapter: uniform thickness/ink, no crease edges.
// Preserve all DFF payloads except Toon and enclosing chunk byte lengths.
internal static partial class ToonTopologyService
{
    private const string Profile = "uniform-no-crease-exact-position-weld-v1";
    private sealed record Plan(int Version, string Profile, string Source, string SourceSha256, string TargetSha256, int TargetBytes);
    private sealed record Node(uint Kind, uint Stamp, byte[] Body, List<Node>? Children);
    private static string Hash(byte[] b) => System.Convert.ToHexString(SHA256.HashData(b));
    private static uint U(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
    private static ushort H(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private static float F(byte[] b, int p) => BitConverter.ToSingle(b, p);
    private static byte[] Read(string path, bool legacyToonRebuild = false)
    {
        ContentSafety.NoLinks(path);
        if (Path.GetExtension(path).ToLowerInvariant() != ".dff" || new FileInfo(path).Length is < 24 or > 128*1024*1024)
            throw new InvalidDataException("Bounded DFF required.");
        byte[] raw = File.ReadAllBytes(path); DffRepairService.Inspect(raw, false, true, textureNames: legacyToonRebuild ? new List<DffRepairService.TextureName>() : null, allowLegacyToon: legacyToonRebuild); return raw;
    }
    private static List<Node> Nodes(byte[] raw, uint? parent = null)
    {
        var result = new List<Node>(); int cursor = 0;
        while (cursor < raw.Length)
        {
            uint kind = U(raw, cursor), size = U(raw, cursor+4), stamp = U(raw, cursor+8);
            byte[] body = raw.AsSpan(cursor+12, checked((int)size)).ToArray();
            bool recurse = (parent == null && kind == 16) || (parent == 16 && kind == 26) ||
                (parent == 26 && kind == 15) || (parent == 15 && kind == 3);
            result.Add(new(kind, stamp, body, recurse ? Nodes(body, kind) : null)); cursor += checked(12+(int)size);
        }
        return result;
    }
    private static IEnumerable<Node> All(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes) { yield return node; if (node.Children != null) foreach (var child in All(node.Children)) yield return child; }
    }
    private static byte[] Encode(IEnumerable<Node> nodes)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var node in nodes)
        {
            byte[] body = node.Children == null ? node.Body : Encode(node.Children);
            writer.Write(node.Kind); writer.Write(body.Length); writer.Write(node.Stamp); writer.Write(body);
        }
        return stream.ToArray();
    }
    private static (byte[] bytes, object evidence) Convert(byte[] raw, int? selectedGeometry = null, byte[]? compatibleToon = null, bool indexedTopology = false, bool retainDegenerateFaces = false, bool splitNonManifoldEdges = false)
    {
        var tree = Nodes(raw); var geometries = All(tree).Where(n => n.Kind == 15 && n.Children != null).ToArray();
        if (selectedGeometry == null && geometries.Length != 1) throw new InvalidDataException("Exactly one complete static geometry required.");
        if (selectedGeometry is < 0 || selectedGeometry >= geometries.Length) throw new InvalidDataException("Explicit geometry index out of range.");
        var geometry = geometries[selectedGeometry ?? 0]; var structures = geometry.Children!.Where(n => n.Kind == 1).ToArray();
        var extensions = geometry.Children!.Where(n => n.Kind == 3).ToArray();
        if (structures.Length != 1 || extensions.Length != 1) throw new InvalidDataException("One geometry Struct/Extension required.");
        var extension = extensions[0]; var toons = extension.Children!.Where(n => n.Kind == 0x12e).ToArray();
        if (toons.Length != 1) throw new InvalidDataException("One existing Toon plugin required.");
        byte[] b = structures[0].Body, old = compatibleToon ?? toons[0].Body;
        int nv = checked((int)U(b,8)), nt = checked((int)U(b,4)); uint flags = U(b,0);
        if ((flags & 0x01000000) != 0 || U(b,12) != 1 || nv is < 3 or >= 65535 || nt is < 1 or >= 65535)
            throw new InvalidDataException("Non-native single morph target with bounded vertices/faces required.");
        int sets = (int)((flags >> 16) & 255); if (sets == 0) sets = (flags & 128) != 0 ? 2 : (flags & 4) != 0 ? 1 : 0;
        if (sets > 8) throw new InvalidDataException("Unregistered texture-coordinate count.");
        int cursor = checked(16 + ((flags & 8) != 0 ? nv*4 : 0) + sets*nv*8);
        var triangles = new (ushort a, ushort b, ushort c)[nt];
        for (int i=0; i<nt; i++,cursor+=8)
        {
            triangles[i] = (H(b,cursor+2), H(b,cursor), H(b,cursor+6));
            if (triangles[i].a >= nv || triangles[i].b >= nv || triangles[i].c >= nv) throw new InvalidDataException("Geometry triangle index out of range.");
        }
        cursor += 16; if (U(b,cursor) != 1 || U(b,cursor+4) != 1) throw new InvalidDataException("Explicit positions/normals required."); cursor += 8;
        Vector3[] Vectors()
        {
            var values = new Vector3[nv];
            for (int i=0; i<nv; i++,cursor+=12)
            {
                values[i] = new(F(b,cursor),F(b,cursor+4),F(b,cursor+8));
                if (!float.IsFinite(values[i].X) || !float.IsFinite(values[i].Y) || !float.IsFinite(values[i].Z)) throw new InvalidDataException("Non-finite geometry vector.");
            }
            return values;
        }
        var positions = Vectors(); var normals = Vectors(); if (cursor != b.Length) throw new InvalidDataException("Geometry Struct EOF mismatch.");
        if (U(old,0) != 0xabcd0002) throw new InvalidDataException("Toon stream version not registered.");
        int ov = checked((int)U(old,4)), ot = checked((int)U(old,8));
        if (ov is < 1 or >= 65535 || ot is < 1 or >= 65535 || (compatibleToon == null && ov == nv && ot == nt)) throw new InvalidDataException("Bounded mismatched Toon topology required; compatible data is not rewritten.");
        cursor = checked(12 + ot*6); float thickness = F(old,cursor);
        if (!float.IsFinite(thickness) || thickness <= 0) throw new InvalidDataException("Positive uniform thickness required.");
        for (int i=0; i<ov; i++) if (F(old,cursor+i*4) != thickness) throw new InvalidDataException("Non-uniform thickness needs a separate adapter.");
        cursor += ov*4; int oe = checked((int)U(old,cursor));
        if (oe is < 1 or >= 65535 || U(old,cursor+4) != 0) throw new InvalidDataException("No crease edges supported in this profile.");
        cursor = checked(cursor+8+oe*16+ot*12+ov*12); int inks = checked((int)U(old,cursor)); cursor+=4;
        if (inks is < 1 or > 256) throw new InvalidDataException("Bounded ink IDs required.");
        byte[] inkNames = old.AsSpan(cursor,inks*32).ToArray(); cursor+=inks*32;
        byte id0=old[cursor], id1=old[cursor+1];
        if (id0 >= inks || id1 >= inks) throw new InvalidDataException("Invalid edge ink ID.");
        for (int i=0; i<oe; i++) if (old[cursor+i*2] != id0 || old[cursor+i*2+1] != id1) throw new InvalidDataException("Non-uniform edge ink IDs require another adapter.");
        cursor+=oe*2; byte[] paint = old.AsSpan(cursor,32).ToArray(); cursor+=32;
        if (cursor != old.Length) throw new InvalidDataException("Toon EOF mismatch.");
        var weld = new Dictionary<Vector3,ushort>(); var canonical = new ushort[nv];
        for (int i=0; i<nv; i++) { if (indexedTopology) { canonical[i]=(ushort)i; continue; } if (!weld.TryGetValue(positions[i],out ushort index)) weld.Add(positions[i],index=(ushort)i); canonical[i]=index; }
        var edges = new List<(ushort a, ushort b, List<ushort> faces)>(); var lookup = new Dictionary<(ushort,ushort),int>(); var faceEdges = new int[nt][]; var faceNormals = new Vector3[nt];
        for (int face=0; face<nt; face++)
        {
            var tri = triangles[face]; ushort[] corners = [canonical[tri.a],canonical[tri.b],canonical[tri.c]];
            if (!retainDegenerateFaces && corners.Distinct().Count() != 3) throw new InvalidDataException("Degenerate position triangle.");
            var normal = Vector3.Cross(positions[tri.b]-positions[tri.a], positions[tri.c]-positions[tri.a]); float length = normal.Length();
            if (!float.IsFinite(length)) throw new InvalidDataException("Invalid triangle normal.");
            if (length == 0) {
                if (!retainDegenerateFaces) throw new InvalidDataException("Invalid triangle normal.");
                normal = normals[tri.a]+normals[tri.b]+normals[tri.c]; length=normal.Length();
                if (!float.IsFinite(length)) throw new InvalidDataException("Non-finite authored normal.");
                if (length == 0) { normal=Vector3.UnitY; length=1; }
            }
            faceNormals[face]=normal/length;
            faceEdges[face] = new int[3];
            for (int j=0; j<3; j++)
            {
                ushort a=corners[j], c=corners[(j+1)%3]; if (retainDegenerateFaces && a==c) { faceEdges[face][j]=-1; continue; } if (a>c) (a,c)=(c,a);
                if (!lookup.TryGetValue((a,c),out int index)) { lookup.Add((a,c),index=edges.Count); edges.Add((a,c,new())); }
                if (retainDegenerateFaces && edges[index].faces.Contains((ushort)face)) { faceEdges[face][j]=index; continue; }
                if (edges[index].faces.Count >= 2) {
                    if (!splitNonManifoldEdges) throw new InvalidDataException("Non-manifold exact-weld edge requires another adapter.");
                    index=edges.Count; edges.Add((a,c,new())); lookup[(a,c)]=index;
                }
                edges[index].faces.Add((ushort)face); faceEdges[face][j]=index;
            }
        }
        if (edges.Count >= 65535) throw new InvalidDataException("Toon edge index limit.");
        using var output = new MemoryStream(); using (var w = new BinaryWriter(output,System.Text.Encoding.UTF8,true))
        {
            w.Write(0xabcd0002u); w.Write(nv); w.Write(nt);
            foreach (var t in triangles) { w.Write(t.a); w.Write(t.b); w.Write(t.c); }
            for (int i=0; i<nv; i++) w.Write(thickness); w.Write(edges.Count); w.Write(0);
            for (int i=0; i<edges.Count; i++)
            {
                var e=edges[i]; w.Write(e.a);w.Write(e.b);w.Write(e.faces[0]);w.Write(e.faces.Count==2?e.faces[1]:ushort.MaxValue);
                for (int side=0;side<2;side++) foreach (ushort vertex in new[]{e.a,e.b})
                {
                    int neighbor = -1;
                    if (side<e.faces.Count)
                    {
                        var matches=faceEdges[e.faces[side]].Distinct().Where(n=>n>=0 && n!=i && (edges[n].a==vertex || edges[n].b==vertex)).ToArray();
                        if (matches.Length!=1 && !(retainDegenerateFaces && matches.Length==0)) throw new InvalidDataException("Ambiguous edge connectivity.");neighbor=matches.Length==0?-1:matches[0];
                    }
                    w.Write(neighbor<0?ushort.MaxValue:(ushort)neighbor);
                }
            }
            foreach (var vectors in new[]{faceNormals,normals}) foreach (var v in vectors) {w.Write(v.X);w.Write(v.Y);w.Write(v.Z);}
            w.Write(inks);w.Write(inkNames);for(int i=0;i<edges.Count;i++){w.Write(id0);w.Write(id1);}w.Write(paint);
        }
        byte[] toon=output.ToArray(); var plugins=extension.Children!; plugins[plugins.IndexOf(toons[0])] = toons[0] with {Body=toon}; byte[] bytes=Encode(tree);
        if (bytes.Length>128*1024*1024) throw new InvalidDataException("Repaired DFF limit."); DffRepairService.Inspect(bytes,false,true, textureNames: compatibleToon != null ? new List<DffRepairService.TextureName>() : null, allowLegacyToon: compatibleToon != null);
        return (bytes,new {vertices=nv,triangles=nt,originalToonVertices=ov,originalToonTriangles=ot,exactPositionGroups=weld.Count,indexedTopology,retainDegenerateFaces,splitNonManifoldEdges,edges=edges.Count,creaseEdges=0,uniformThickness=thickness,inkNamesPreserved=compatibleToon == null,paintNamePreserved=compatibleToon == null,geometryMaterialsRigAndOtherPluginsPreserved=true,nativeReaderTested=false,renderingTested=false});
    }
    public static object CreatePlan(string source,string destination)
    {
        source=Path.GetFullPath(source); byte[] raw=Read(source); var (bytes,evidence)=Convert(raw);
        var plan=new Plan(1,Profile,source,Hash(raw),Hash(bytes),bytes.Length); ContentSafety.WriteJson(destination,plan);
        return new {plan=Path.GetFullPath(destination),evidence,sourcePreserved=true,installed=false};
    }
    private static (Plan plan,byte[] raw,byte[] bytes,object evidence) Resolve(string path)
    {
        ContentSafety.NoLinks(path); if(new FileInfo(path).Length is <1 or >1024*1024)throw new InvalidDataException("Bounded Toon plan required.");
        using var json=JsonDocument.Parse(File.ReadAllBytes(path)); var seen=new HashSet<string>();
        foreach(var field in json.RootElement.EnumerateObject()) if(!new[]{"version","profile","source","sourceSha256","targetSha256","targetBytes"}.Contains(field.Name)||!seen.Add(field.Name))throw new InvalidDataException("Unknown/duplicate Toon plan field.");
        var plan=JsonSerializer.Deserialize<Plan>(json,ContentSafety.Json)??throw new InvalidDataException("Toon plan required.");
        if(plan.Version!=1||plan.Profile!=Profile)throw new InvalidDataException("Registered Toon profile required.");
        byte[] raw=Read(plan.Source); if(Hash(raw)!=plan.SourceSha256)throw new IOException("Toon source changed.");var (bytes,evidence)=Convert(raw);
        if(Hash(bytes)!=plan.TargetSha256||bytes.Length!=plan.TargetBytes)throw new IOException("Toon target differs from sealed plan.");return(plan,raw,bytes,evidence);
    }
    public static object Preview(string path) {var (plan,_,_,evidence)=Resolve(path);return new {plan,evidence,installed=false};}
    public static object Apply(string path,string destination)
    {
        destination=ContentSafety.SafeOutput(destination);var (plan,raw,bytes,evidence)=Resolve(path);
        if(destination.StartsWith(Path.GetDirectoryName(Path.GetFullPath(plan.Source))!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Output outside source directory required.");
        using var source=new FileStream(plan.Source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(ConvertHex(SHA256.HashData(source))!=plan.SourceSha256)throw new IOException("Toon source changed before write.");
        string pending=ContentSafety.FreshDirectory(destination);
        try
        {
            File.WriteAllBytes(Path.Combine(pending,"original.dff.bak"),raw);File.WriteAllBytes(Path.Combine(pending,"model.dff"),bytes);
            if(Hash(File.ReadAllBytes(Path.Combine(pending,"model.dff")))!=plan.TargetSha256)throw new IOException("Toon disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),new {plan,evidence,installed=false,nativeReaderTested=false,renderingTested=false});Directory.Move(pending,destination);
            return new {output=Path.Combine(destination,"model.dff"),sha256=plan.TargetSha256,evidence,installed=false};
        }
        catch{Directory.Delete(pending,true);throw;}
    }
    private static string ConvertHex(byte[] bytes)=>System.Convert.ToHexString(bytes);
}
