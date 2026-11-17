using System.Globalization;

namespace DboAssetsStudio;

/// <summary>
/// World Editor phase 5: read-only list of portal points (Table_Portal_Data.rdf) so the editor can draw them over
/// the map and flag spawns placed on top of a portal. Never writes anything.
/// </summary>
internal static class PortalReadService
{
    internal sealed record Portal(uint Id, string Name, uint WorldId, int Grade, float X, float Y, float Z, float DirX, float DirZ);

    public const float NearSpawnMetres = 10f;

    // The schema decoder returns the raw cp949 bytes as Latin-1 characters; rebuild the Korean text for display only.
    private static string Korean(string raw)
    {
        raw = raw.Trim('\0', ' ');
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(949).GetString(System.Text.Encoding.Latin1.GetBytes(raw)).Trim('\0', ' ');
        }
        catch { return raw; }
    }

    public static IReadOnlyList<Portal> Load(string folder, out string? note)
    {
        note = null;
        string path = Path.Combine(folder, "Table_Portal_Data.rdf");
        if (!File.Exists(path)) { note = "Falta Table_Portal_Data.rdf junto al archivo de spawns; no hay portales para mostrar."; return []; }
        var schema = RdfSchema.LoadCatalog(RdfCli.LocateCatalog()).FirstOrDefault(s => s.Name == "Portal");
        if (schema == null) { note = "No hay esquema registrado para Portal."; return []; }
        RdfField F(string n) => schema.Fields.First(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        var id = F("tblidx"); var name = F("szPointNameText"); var world = F("worldId"); var grade = F("byGrade"); var loc = F("vLoc"); var dir = F("vDir");
        var doc = RdfDocument.Open(path, schema);
        var list = new List<Portal>(doc.Rows.Count);
        foreach (var row in doc.Rows)
        {
            ReadOnlySpan<byte> r = row.Original;
            float x = BitConverter.ToSingle(r.Slice(loc.Offset, 4)), y = BitConverter.ToSingle(r.Slice(loc.Offset + 4, 4)), z = BitConverter.ToSingle(r.Slice(loc.Offset + 8, 4));
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            float dx = BitConverter.ToSingle(r.Slice(dir.Offset, 4)), dz = BitConverter.ToSingle(r.Slice(dir.Offset + 8, 4));
            if (!float.IsFinite(dx) || !float.IsFinite(dz)) { dx = 1; dz = 0; }
            list.Add(new Portal(BitConverter.ToUInt32(r.Slice(id.Offset, 4)), Korean(schema.Decode(name, r)), BitConverter.ToUInt32(r.Slice(world.Offset, 4)),
                int.Parse(schema.Decode(grade, r), CultureInfo.InvariantCulture), x, y, z, dx, dz));
        }
        return list;
    }

    public static object ToResult(IReadOnlyList<Portal> portals, string? note, uint? worldId, Func<float, float, int> spawnsNear)
    {
        var rows = portals.Where(p => worldId == null || p.WorldId == worldId).ToArray();
        return new
        {
            note, total = portals.Count, nearMetres = NearSpawnMetres,
            worlds = portals.GroupBy(p => p.WorldId).OrderBy(g => g.Key).Select(g => new { worldId = g.Key, count = g.Count() }).ToArray(),
            rows = rows.Select(p => new { id = p.Id, name = p.Name, worldId = p.WorldId, grade = p.Grade, x = p.X, y = p.Y, z = p.Z, dirX = p.DirX, dirZ = p.DirZ, nearSpawns = spawnsNear(p.X, p.Z) }).ToArray()
        };
    }
}
