using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

/// <summary>A read-only lookup row used by the WPS editor's field picker.</summary>
internal sealed record WpsReferenceRecord(
    string Kind,
    string Id,
    string Name,
    string Model,
    string Icon,
    string Table,
    string Source,
    string? Context,
    IReadOnlyList<WpsGroupMember> Members,
    string Key);

/// <summary>One spawn-table row contributing a mob and quantity to a contextual group.</summary>
internal sealed record WpsGroupMember(string MobId, string Quantity, string SpawnRowId);

/// <summary>
/// Read-only catalog for WPS item, mob, and world-scoped spawn-group references.
/// It consumes Taiwan RDF through RdfSchema/RdfDocument; it never writes to source tables.
/// </summary>
internal sealed class WpsReferenceCatalog
{
    private readonly List<WpsReferenceRecord> _rows = [];
    private readonly Dictionary<string, WpsReferenceRecord> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, string Model)> _mobDetails = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<uint, string> _itemNames = new Dictionary<uint, string>(), _mobNames = new Dictionary<uint, string>();

    public IReadOnlyList<WpsReferenceRecord> Rows => _rows;
    public List<string> Notes { get; } = [];

    public static WpsReferenceCatalog LoadFolder(string rdfFolder, IReadOnlyList<RdfSchema> schemas, string? serverSpawnRoot = null)
    {
        if (!Directory.Exists(rdfFolder)) throw new DirectoryNotFoundException(rdfFolder);
        ContentSafety.NoLinks(rdfFolder);
        var catalog = new WpsReferenceCatalog();
        catalog.LoadClientRows(rdfFolder, schemas);
        foreach (string kind in new[] { "item", "mob" }) if (!catalog._rows.Any(r => r.Kind == kind)) catalog.Notes.Add("No se cargaron referencias " + kind + "; revisar tablas/esquemas vinculados.");
        string provenance = Path.Combine(rdfFolder, "provenance.json");
        if (File.Exists(provenance) && new FileInfo(provenance).Length < 4 * 1024 * 1024)
        {
            try { using var document = JsonDocument.Parse(File.ReadAllBytes(provenance)); if (document.RootElement.TryGetProperty("capturedAt", out var captured)) catalog.Notes.Add("Snapshot de tablas: " + captured.GetString() + ". Vincular fuentes actuales si la versión cambió."); }
            catch (JsonException) { catalog.Notes.Add("No se pudo leer la procedencia del snapshot."); }
        }
        if (!string.IsNullOrWhiteSpace(serverSpawnRoot)) catalog.LoadSpawnGroups(serverSpawnRoot, schemas, rdfFolder);
        catalog.Notes.Add("Los grupos listados provienen de tablas estáticas de spawn. Los grupos creados dinámicamente por WPS/CC no se deducen de esas tablas. Elegir un grupo no cambia el mundo del WPS.");
        return catalog;
    }

    public static async Task<WpsReferenceCatalog> LoadPakAsync(PakArchive archive, IReadOnlyList<RdfSchema> schemas, string? serverSpawnRoot = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DboAssetsWpsRefs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wanted = new HashSet<string>(new[] { "Table_Item_Data.rdf", "Table_Mob_Data.rdf", "Table_World_Data.rdf", "Table_Text_All_Data.rdf" }, StringComparer.OrdinalIgnoreCase);
            long remaining = 512L * 1024 * 1024;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                string leaf = Path.GetFileName(entry.Name);
                if (!wanted.Contains(leaf)) continue;
                if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Nombre RDF inválido.");
                if (!seen.Add(leaf)) throw new InvalidDataException("El PAK contiene tablas ambiguas con el nombre " + leaf);
                if (entry.Size > remaining) throw new InvalidDataException("Las tablas RDF exceden el límite de importación.");
                remaining -= entry.Size;
                await File.WriteAllBytesAsync(Path.Combine(dir, leaf), await archive.ReadAsync(entry));
            }
            if (seen.Count == 0) throw new InvalidDataException("El PAK no contiene tablas RDF Taiwan reconocidas.");
            var catalog = await Task.Run(() => LoadFolder(dir, schemas, serverSpawnRoot));
            for (int i = 0; i < catalog._rows.Count; i++)
            {
                var row = catalog._rows[i];
                if (row.Kind == "group") continue;
                var corrected = row with { Source = archive.IndexPath + " :: " + row.Table };
                catalog._rows[i] = corrected; catalog._byKey[row.Key] = corrected;
            }
            return catalog;
        }
        finally { Directory.Delete(dir, true); }
    }

    public WpsReferenceRecord Get(string key) => _byKey.TryGetValue(key, out var row)
        ? row : throw new KeyNotFoundException("No existe referencia WPS con key " + key);

    /// <param name="context">For groups, exact spawn-table filename or world table ID/name. Required when results span contexts.</param>
    public IReadOnlyList<WpsReferenceRecord> Query(string kind, string query, string? worldId, int page, int pageSize)
    {
        if (page < 0 || pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        string normalizedKind = kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("item" or "mob" or "group" or "all")) return [];
        if (normalizedKind == "group" && string.IsNullOrWhiteSpace(worldId)) return [];
        IEnumerable<WpsReferenceRecord> rows = _rows;
        if (normalizedKind != "all") rows = rows.Where(r => r.Kind == normalizedKind);
        if (!string.IsNullOrWhiteSpace(worldId)) rows = rows.Where(r => r.Kind != "group" || string.Equals(r.Context, worldId, StringComparison.OrdinalIgnoreCase));
        var matches = rows.Where(r => Matches(r, query)).OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Context, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase);
        // Never silently turn per-world spawn groups into a global pool.
        var pageRows = matches.Skip(checked(page * pageSize)).Take(pageSize).ToArray();
        return pageRows;
    }

    /// <summary>Returns the WPS source category expected for a node parameter, or null when it is not a table reference.</summary>
    public static string? FieldKind(string nodeName, string paramKey)
    {
        string node = nodeName.Trim().ToLowerInvariant().Replace('_', ' '), key = paramKey.Trim().ToLowerInvariant();
        node = string.Join(' ', node.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (node == "add mob" && key == "index") return "mob";
        if ((node is "add mob" or "add mobgroup" or "add mob group" or "check mobgroup" or "check mob group" or "remove mobgroup") && key == "group") return "group";
        if (node == "mob list" && key.StartsWith("mob", StringComparison.Ordinal) && int.TryParse(key.AsSpan(3), out int n) && n is >= 1 and <= 10) return "mob-list";
        if ((node == "ccbd reward" && key == "item tblidx") || (node == "add mob" && key == "drop item") || ((node is "add mobgroup" or "add mob group") && (key is "drop item custom" or "custom drop item"))) return "item";
        return null;
    }

    private void LoadClientRows(string root, IReadOnlyList<RdfSchema> schemas)
    {
        string textAll = Path.Combine(root, "table_text_all_data.rdf");
        if (File.Exists(textAll))
        {
            try { _itemNames = MapNameRdfService.ReadTextCategory(textAll, 3); _mobNames = MapNameRdfService.ReadTextCategory(textAll, 5); }
            catch (Exception ex) { Notes.Add("TextAll: " + ex.Message + "; se usan nombres embebidos en RDF."); }
        }
        foreach (var schema in schemas)
        {
            string path = Path.Combine(root, schema.FileName);
            if (!File.Exists(path)) continue;
            // HLSItem, UseItem and SetItemTable contain "item_data" too, but
            // their IDs belong to different tables and cannot share this picker.
            bool item = schema.Name.Equals("ItemData", StringComparison.OrdinalIgnoreCase);
            bool mob = schema.Name.Equals("MobData", StringComparison.OrdinalIgnoreCase);
            if (!item && !mob) continue;
            try
            {
                var id = Field(schema, "tblidx") ?? throw new InvalidDataException("Falta tblidx en el esquema.");
                var name = Field(schema, "wszNameText", "wszName", "NameText");
                var model = Field(schema, "szModel", "szModelName", "modelName");
                var icon = item ? Field(schema, "szIcon_Name", "szIconName") : null;
                var validity = Field(schema, "bValidity_Able");
                var doc = RdfDocument.Open(path, schema);
                foreach (var row in doc.Rows)
                {
                    if (validity != null && schema.Decode(validity, row.Current) == "0") continue;
                    string rowId = schema.Decode(id, row.Current).Trim();
                    if (rowId.Length == 0 || rowId == "0") continue;
                    string display = name == null ? "" : schema.Decode(name, row.Current).Trim();
                    var nameId = Field(schema, "Name");
                    if (nameId != null && uint.TryParse(schema.Decode(nameId, row.Current), out uint textId) && (item ? _itemNames : _mobNames).TryGetValue(textId, out var translated) && !string.IsNullOrWhiteSpace(translated)) display = translated;
                    string modelValue = model == null ? "" : schema.Decode(model, row.Current).Trim();
                    string iconValue = icon == null ? "" : schema.Decode(icon, row.Current).Trim();
                    string rowKind = item ? "item" : "mob";
                    var record = new WpsReferenceRecord(rowKind, rowId, display, modelValue, iconValue, Path.GetFileName(path), path, null, [], rowKind + ":" + rowId);
                    catalogAdd(record);
                    if (rowKind == "mob") _mobDetails[rowId] = (display, modelValue);
                }
            }
            catch (Exception ex) { Notes.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        LoadNativeLayoutFallback(root);

        void catalogAdd(WpsReferenceRecord row) => Add(row);
    }

    // ResourceCatalog's generated resource-layout.json describes native Taiwan
    // server ABI rows (not a guessed RDF schema). Use it only for rows not already
    // loaded through an explicit RdfSchema, notably Table_Mob_Data where the
    // editor's client schema catalog may not carry a MobData entry.
    private void LoadNativeLayoutFallback(string root)
    {
        string layoutPath = Path.Combine(AppContext.BaseDirectory, "resource-layout.json");
        if (!File.Exists(layoutPath)) return;
        try
        {
            using var layouts = JsonDocument.Parse(File.ReadAllText(layoutPath));
            foreach (var layout in layouts.RootElement.EnumerateArray())
            {
                string table = layout.GetProperty("table").GetString() ?? "";
                string kind = layout.GetProperty("kind").GetString() ?? "";
                if (kind is not ("Mob" or "Item") || _rows.Any(r => r.Kind == kind.ToLowerInvariant())) continue;
                string path = Path.Combine(root, table);
                if (!File.Exists(path)) continue;
                ContentSafety.NoLinks(path);
                if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new IOException("Tabla de mobs mayor a 128 MiB.");
                int stride = layout.GetProperty("rowSize").GetInt32(), idOffset = layout.GetProperty("idOffset").GetInt32();
                int modelOffset = layout.GetProperty("modelOffset").GetInt32(), modelLength = layout.GetProperty("modelLength").GetInt32();
                int nameOffset = layout.GetProperty("nameOffset").GetInt32(), nameLength = layout.GetProperty("nameLength").GetInt32();
                bool wide = layout.GetProperty("nameWide").GetBoolean();
                byte[] bytes = File.ReadAllBytes(path);
                if (stride <= 0 || bytes.Length < 1 || (bytes.Length - 1) % stride != 0 || idOffset < 0 || idOffset + 4 > stride || modelOffset < 0 || modelOffset + modelLength > stride || nameOffset < 0 || nameOffset + nameLength * (wide ? 2 : 1) > stride)
                    throw new InvalidDataException("La tabla no coincide con la ABI declarada por resource-layout.json.");
                for (int offset = 1; offset < bytes.Length; offset += stride)
                {
                    if (layout.TryGetProperty("validityOffset", out var validityElement))
                    {
                        int validityOffset = validityElement.GetInt32();
                        if (validityOffset < 0 || validityOffset >= stride) throw new InvalidDataException("Offset validity inválido.");
                        if (bytes[offset + validityOffset] == 0) continue;
                    }
                    string id = BitConverter.ToUInt32(bytes, offset + idOffset).ToString(CultureInfo.InvariantCulture);
                    if (id == "0") continue;
                    string model = Encoding.Latin1.GetString(bytes, offset + modelOffset, modelLength).Split('\0')[0].Trim();
                    string name = nameLength == 0 ? "" : (wide ? Encoding.Unicode : Encoding.Latin1).GetString(bytes, offset + nameOffset, nameLength * (wide ? 2 : 1)).Split('\0')[0].Trim();
                    if (layout.TryGetProperty("nameIdOffset", out var nameIdElement))
                    {
                        int nameIdOffset = nameIdElement.GetInt32();
                        if (nameIdOffset >= 0 && nameIdOffset + 4 <= stride && _mobNames.TryGetValue(BitConverter.ToUInt32(bytes, offset + nameIdOffset), out var translated) && !string.IsNullOrWhiteSpace(translated)) name = translated;
                    }
                    string rowKind = kind.ToLowerInvariant();
                    Add(new WpsReferenceRecord(rowKind, id, name, model, "", table, path, null, [], rowKind + ":" + id));
                    if (rowKind == "mob") _mobDetails[id] = (name, model);
                }
            }
        }
        catch (Exception ex) { Notes.Add("resource-layout.json: " + ex.Message); }
    }

    private void LoadSpawnGroups(string spawnRoot, IReadOnlyList<RdfSchema> schemas, string clientRoot)
    {
        if (!Directory.Exists(spawnRoot)) { Notes.Add("No se encontró la carpeta server spawn RDF: " + spawnRoot); return; }
        // Server spawn tables have their own Taiwan schema catalog. It sits one
        // directory above table/rdf in the usual server_data/table layout.
        ContentSafety.NoLinks(spawnRoot);
        var schema = WpsSpawnSchema.Select(RdfSchema.LoadCatalog(Path.Combine(AppContext.BaseDirectory, "wps-reference-schemas.txt")));
        if (schema.RowSize != 84) throw new InvalidDataException("MobSpawn Taiwan requiere filas de 84 bytes (SpawnTable.h).");
        var worldBySpawn = LoadWorldSpawnNames(clientRoot, schemas);
        var groupRows = new Dictionary<string, List<WpsGroupMember>>(StringComparer.OrdinalIgnoreCase);
        var groupTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(spawnRoot, "spawn_mob*.rdf", SearchOption.TopDirectoryOnly))
        {
            var mobId = Field(schema, "mob_tblidx", "mob_Tblidx");
            var groupId = Field(schema, "spawnGroup", "spawnGroupId", "spawn_group");
            var quantity = Field(schema, "bySpawnQuantity", "bySpawn_Quantity", "spawnQuantity");
            var rowId = Field(schema, "tblidx");
            if (mobId == null || groupId == null || quantity == null || rowId == null) continue;
            try
            {
                ContentSafety.NoLinks(path);
                var doc = RdfDocument.Open(path, schema);
                string source = Path.GetFileName(path);
                string world = source + (worldBySpawn.TryGetValue(Path.GetFileNameWithoutExtension(path), out var worldId) ? " · " + worldId : "");
                foreach (var row in doc.Rows)
                {
                    string g = schema.Decode(groupId, row.Current).Trim();
                    string m = schema.Decode(mobId, row.Current).Trim();
                    if (g.Length == 0 || g == "4294967295" || m.Length == 0 || m == "0") continue;
                    string key = "group:" + world + ":" + g;
                    if (!groupRows.TryGetValue(key, out var members)) groupRows[key] = members = [];
                    members.Add(new WpsGroupMember(m, schema.Decode(quantity, row.Current).Trim(), schema.Decode(rowId, row.Current).Trim()));
                    groupTables[key] = Path.GetFullPath(path);
                }
            }
            catch (Exception ex) { Notes.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        foreach (var entry in groupRows)
        {
            string id = entry.Key[(entry.Key.LastIndexOf(':') + 1)..];
            var members = entry.Value;
            string name = string.Join(", ", members.GroupBy(m => m.MobId).Select(g =>
            {
                string mobName = _mobDetails.TryGetValue(g.Key, out var info) ? info.Name : "Mob " + g.Key;
                return (string.IsNullOrWhiteSpace(mobName) ? "Mob " + g.Key : mobName) + " ×" + g.Sum(m => int.TryParse(m.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q : 1).ToString(CultureInfo.InvariantCulture);
            }));
            Add(new WpsReferenceRecord("group", id, name, "", "", "MobSpawnTable", groupTables[entry.Key], entry.Key[6..entry.Key.LastIndexOf(':')], members, entry.Key));
        }
    }

    private static Dictionary<string, string> LoadWorldSpawnNames(string root, IReadOnlyList<RdfSchema> schemas)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var worldSchema = schemas.FirstOrDefault(s => s.Name.Equals("WorldTable", StringComparison.OrdinalIgnoreCase));
        if (worldSchema == null) return result;
        string path = Path.Combine(root, worldSchema.FileName);
        if (!File.Exists(path)) return result;
        try
        {
            var id = Field(worldSchema, "tblidx"); var label = Field(worldSchema, "wszName", "szName");
            var spawn = Field(worldSchema, "wszMobSpawn_Table_Name", "mobSpawnTableName");
            if (id == null || spawn == null) return result;
            foreach (var row in RdfDocument.Open(path, worldSchema).Rows)
            {
                string file = Path.GetFileNameWithoutExtension(worldSchema.Decode(spawn, row.Current).Trim());
                if (file.Length > 0) result[file] = "Mundo " + worldSchema.Decode(id, row.Current).Trim() + (label == null ? "" : " " + worldSchema.Decode(label, row.Current).Trim());
            }
        }
        catch { /* A missing/invalid WorldTable means groups retain spawn-filename scope. */ }
        return result;
    }

    private void Add(WpsReferenceRecord row)
    {
        if (_ambiguousKeys.Contains(row.Key)) return;
        if (_byKey.TryGetValue(row.Key, out var previous))
        {
            _byKey.Remove(row.Key); _rows.Remove(previous); _ambiguousKeys.Add(row.Key);
            Notes.Add("ID duplicado excluido del buscador: " + row.Key); return;
        }
        _byKey[row.Key] = row;
        _rows.Add(row);
    }

    private static bool Matches(WpsReferenceRecord row, string query) => string.IsNullOrWhiteSpace(query)
        || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Model.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Icon.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Members.Any(m => m.MobId.Contains(query, StringComparison.OrdinalIgnoreCase))
        || row.Context?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static RdfField? Field(RdfSchema schema, params string[] names) => schema.Fields.FirstOrDefault(f => names.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
}
