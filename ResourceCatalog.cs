using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed record ResourceReference(string Kind, string Id, string Name, string Key, string Table, string Field);
internal sealed class ResourceCatalog
{
    private readonly Dictionary<string, List<ResourceReference>> _keys = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = [];
    public int Count { get; private set; }
    public static async Task<ResourceCatalog> LoadPakAsync(PakArchive archive, IReadOnlyList<RdfSchema> schemas)
    {
        string directory = Path.Combine(Path.GetTempPath(), "DboAssetsStudioRefs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var names = new HashSet<string>(schemas.Select(s => s.FileName), StringComparer.OrdinalIgnoreCase)
                { "Table_Mob_Data.rdf", "Table_NPC_Data.rdf", "Table_Object_Data.rdf" };
            long budget = 128L * 1024 * 1024;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                string leaf = Path.GetFileName(entry.Name);
                if (!names.Contains(leaf) && !(leaf.StartsWith("spawn_object_", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(".rdf", StringComparison.OrdinalIgnoreCase))) continue;
                if (!seen.Add(leaf)) throw new InvalidDataException("El PAK contiene más de una tabla con el nombre " + leaf);
                if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Size > budget) throw new InvalidDataException("Las referencias RDF exceden los límites de importación.");
                budget -= entry.Size;
                await File.WriteAllBytesAsync(Path.Combine(directory, leaf), await archive.ReadAsync(entry));
            }
            if (seen.Count == 0) throw new InvalidDataException("El PAK no contiene tablas RDF compatibles para relacionar recursos.");
            return await Task.Run(() => Load(directory, schemas));
        }
        finally { Directory.Delete(directory, true); }
    }
    private void Add(ResourceReference row)
    {
        string key = Path.GetFileNameWithoutExtension(row.Key.Trim()).ToLowerInvariant(); if (key.Length == 0) return;
        if (!_keys.TryGetValue(key, out var list)) _keys[key] = list = [];
        list.Add(row); ++Count;
    }
    public static ResourceCatalog Load(string root, IReadOnlyList<RdfSchema> schemas)
    {
        var catalog = new ResourceCatalog();
        foreach (var schema in schemas)
        {
            var fields = schema.Fields.Where(f => (f.Type is "string" or "wstring") && (f.Name.Contains("model", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("szIcon_Name", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (fields.Length == 0) continue;
            string path = Path.Combine(root, schema.FileName); if (!File.Exists(path)) continue;
            try
            {
                var doc = RdfDocument.Open(path, schema); var id = schema.Fields.First(f => f.Name == "tblidx");
                var name = schema.Fields.FirstOrDefault(f => f.Name is "wszNameText" or "wszName");
                foreach (var row in doc.Rows)
                    foreach (var field in fields) catalog.Add(new ResourceReference(schema.Name, schema.Decode(id, row.Current), name == null ? "" : schema.Decode(name, row.Current), schema.Decode(field, row.Current), Path.GetFileName(path), field.Name));
            }
            catch (Exception ex) { catalog.Notes.Add(schema.Name + ": " + ex.Message); }
        }
        string layoutPath = Path.Combine(AppContext.BaseDirectory, "resource-layout.json");
        if (!File.Exists(layoutPath)) { catalog.Notes.Add("No se encontró el catálogo nativo de Mob/NPC/Object."); return catalog; }
        using var layouts = JsonDocument.Parse(File.ReadAllText(layoutPath));
        foreach (var layout in layouts.RootElement.EnumerateArray())
        {
            string table = layout.GetProperty("table").GetString()!, kind = layout.GetProperty("kind").GetString()!;
            var candidates = kind == "Object" ? Directory.EnumerateFiles(root, "*object*.rdf", SearchOption.TopDirectoryOnly) : [Path.Combine(root, table)];
            foreach (string path in candidates.Where(File.Exists))
            {
                try
                {
                    if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("Tabla demasiado grande.");
                    byte[] bytes = File.ReadAllBytes(path); int stride = layout.GetProperty("rowSize").GetInt32();
                    if (stride <= 0 || bytes.Length < 1 || (bytes.Length - 1) % stride != 0) throw new InvalidDataException("La tabla no coincide con la ABI Taiwan.");
                    int idOffset = layout.GetProperty("idOffset").GetInt32(), modelOffset = layout.GetProperty("modelOffset").GetInt32(), modelLength = layout.GetProperty("modelLength").GetInt32();
                    int nameOffset = layout.GetProperty("nameOffset").GetInt32(), nameLength = layout.GetProperty("nameLength").GetInt32(); bool wide = layout.GetProperty("nameWide").GetBoolean();
                    if (modelOffset < 0 || modelOffset + modelLength > stride || idOffset < 0 || idOffset + 4 > stride || nameOffset < 0 || nameOffset + nameLength * (wide ? 2 : 1) > stride) throw new InvalidDataException("Layout nativo inválido.");
                    for (int o = 1; o < bytes.Length; o += stride)
                    {
                        string model = Encoding.Latin1.GetString(bytes, o + modelOffset, modelLength).Split('\0')[0];
                        string name = nameLength == 0 ? "" : (wide ? Encoding.Unicode : Encoding.Latin1).GetString(bytes, o + nameOffset, nameLength * (wide ? 2 : 1)).Split('\0')[0];
                        catalog.Add(new ResourceReference(kind, BitConverter.ToUInt32(bytes, o + idOffset).ToString(), name, model, Path.GetFileName(path), "szModel"));
                    }
                }
                catch (Exception ex) { catalog.Notes.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
        }
        return catalog;
    }

    public string Describe(string resource)
    {
        string key = Path.GetFileNameWithoutExtension(resource).ToLowerInvariant();
        if (_keys.TryGetValue(key, out var direct)) return Format(direct, "REFERENCIAS DIRECTAS EN RDF");
        var candidates = _keys.Where(k => key.StartsWith(k.Key + "_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Key.Length).Take(3).SelectMany(k => k.Value).ToList();
        return candidates.Count == 0 ? "Sin referencia encontrada en las tablas cargadas. El nombre del archivo no es un ID de juego." : Format(candidates, "COINCIDENCIAS POR PREFIJO — variantes posibles, requieren confirmar");
    }
    private static string Format(IEnumerable<ResourceReference> rows, string heading) => heading + "\r\n" + string.Join("\r\n", rows.Take(30).Select(r => $"{r.Kind} · ID {r.Id} · {r.Name}\r\n  {r.Table} / {r.Field} = {r.Key}"));
}
