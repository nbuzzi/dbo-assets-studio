using System.Globalization;

namespace DboAssetsStudio;

/// <summary>
/// World Editor phase 3: read-only catalog of mobs and NPCs (name, level, model) used by the palette and by
/// spawn validation. Reads Table_Mob_Data.rdf / Table_NPC_Data.rdf through the registered Taiwan schemas.
/// </summary>
internal sealed class SpawnCatalog
{
    internal sealed record Entry(string Kind, uint Id, string Name, string Model, int Level, int Type);

    public string Folder { get; init; } = "";
    public Dictionary<uint, Entry> Mobs { get; } = [];
    public Dictionary<uint, Entry> Npcs { get; } = [];
    public List<string> Notes { get; } = [];

    public IReadOnlyDictionary<uint, Entry> For(string kind) => kind == "npc" ? Npcs : Mobs;

    // The server drops mobs above this level in worlds with tblidx <= MAIN_WORLD_2 (see missing-overworld-mobs-20261009.md).
    public const int MainWorldMobLevelCap = 30;

    private static readonly object Gate = new();
    private static (string Key, SpawnCatalog Value)? _cache;

    public static SpawnCatalog Load(string folder)
    {
        folder = Path.GetFullPath(folder);
        string mobPath = Path.Combine(folder, "Table_Mob_Data.rdf"), npcPath = Path.Combine(folder, "Table_NPC_Data.rdf");
        string key = folder + "|" + Stamp(mobPath) + "|" + Stamp(npcPath) + "|" + Stamp(Path.Combine(folder, "Table_Text_All_Data.rdf"));
        lock (Gate) { if (_cache is { } c && c.Key == key) return c.Value; }
        var catalog = new SpawnCatalog { Folder = folder };
        var schemas = RdfSchema.LoadCatalog(RdfCli.LocateCatalog());
        IReadOnlyDictionary<uint, string> mobNames = new Dictionary<uint, string>();
        string textAll = Path.Combine(folder, "Table_Text_All_Data.rdf");
        if (File.Exists(textAll))
        {
            try { mobNames = MapNameRdfService.ReadTextCategory(textAll, 5); }
            catch (Exception ex) { catalog.Notes.Add("Nombres de mobs (TextAll): " + ex.Message + "; se usan los embebidos."); }
        }
        catalog.ReadTable(mobPath, schemas.FirstOrDefault(s => s.Name == "MobData"), "mob", "byGrade", mobNames);
        catalog.ReadTable(npcPath, schemas.FirstOrDefault(s => s.Name == "NpcData"), "npc", "byNpcType", new Dictionary<uint, string>());
        lock (Gate) { _cache = (key, catalog); }
        return catalog;
    }

    private static string Stamp(string path) => File.Exists(path) ? new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : "missing";

    private void ReadTable(string path, RdfSchema? schema, string kind, string typeField, IReadOnlyDictionary<uint, string> textNames)
    {
        if (schema == null) { Notes.Add($"No hay esquema registrado para {kind}."); return; }
        if (!File.Exists(path)) { Notes.Add($"Falta {Path.GetFileName(path)} junto al archivo de spawns; la paleta de {kind} no está disponible."); return; }
        RdfField? F(string n) => schema.Fields.FirstOrDefault(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        var id = F("tblidx") ?? throw new InvalidDataException("Falta tblidx.");
        var name = F("wszNameText"); var textId = F("Name"); var model = F("szModel"); var level = F("byLevel"); var type = F(typeField); var valid = F("bValidity_Able");
        var dict = kind == "npc" ? Npcs : Mobs;
        var doc = RdfDocument.Open(path, schema);
        foreach (var row in doc.Rows)
        {
            ReadOnlySpan<byte> r = row.Original;
            if (valid != null && schema.Decode(valid, r) == "0") continue;
            if (!uint.TryParse(schema.Decode(id, r), NumberStyles.None, CultureInfo.InvariantCulture, out uint tbl) || tbl == 0) continue;
            string display = name == null ? "" : schema.Decode(name, r).Trim();
            if (textId != null && uint.TryParse(schema.Decode(textId, r), NumberStyles.None, CultureInfo.InvariantCulture, out uint t) && textNames.TryGetValue(t, out var translated) && !string.IsNullOrWhiteSpace(translated)) display = translated.Trim();
            if (display.Length == 0 || display == "0") display = model == null ? "" : schema.Decode(model, r).Trim();
            int lvl = level == null ? 0 : int.Parse(schema.Decode(level, r), CultureInfo.InvariantCulture);
            int ty = type == null ? 0 : int.Parse(schema.Decode(type, r), CultureInfo.InvariantCulture);
            dict[tbl] = new Entry(kind, tbl, display, model == null ? "" : schema.Decode(model, r).Trim(), lvl, ty);
        }
    }

    public IReadOnlyList<Entry> Search(string kind, string? query, int minLevel, int maxLevel, int limit, int offset)
    {
        string q = (query ?? "").Trim();
        IEnumerable<Entry> rows = For(kind).Values.Where(e => e.Level >= minLevel && e.Level <= maxLevel);
        if (q.Length > 0)
            rows = rows.Where(e => e.Id.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal)
                || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Model.Contains(q, StringComparison.OrdinalIgnoreCase));
        return rows.OrderBy(e => e.Level).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public object ToResult(string kind, IReadOnlyList<Entry> rows) => new
    {
        kind, folder = Folder, total = For(kind).Count, notes = Notes, levelCapWarning = MainWorldMobLevelCap,
        rows = rows.Select(e => new { id = e.Id, name = e.Name, model = e.Model, level = e.Level, type = e.Type }).ToArray()
    };
}
