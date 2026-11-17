using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

/// <summary>
/// World Editor phase 2: in-memory edit session over one Taiwan spawn table (84-byte rows).
/// The session never writes to the source. Saving produces a NEW candidate folder with
/// snapshot, candidate RDF and audit.json (same contract as WpsDocument.SaveCandidate).
/// Undo/redo are per-edit deltas, so a 35k-row table costs a few KB per step.
/// </summary>
internal sealed class SpawnEditSession
{
    public const int RowSize = 84;
    private const int MaxUndo = 200;
    private const float CoordLimit = 1_000_000f;

    private sealed class Row
    {
        public required int Uid { get; init; }
        public required byte[] Data { get; set; }
        public byte[]? Original { get; init; }
        public bool Deleted { get; set; }
        public string? Source { get; init; } // how a NEW row was built (audit)
        public bool IsNew => Original == null;
    }

    private sealed record Change(Row Row, byte[] Before, byte[] After, bool BeforeDeleted, bool AfterDeleted, bool Added);

    private sealed class Edit
    {
        public required string Name { get; init; }
        public List<Change> Changes { get; } = [];
        public List<TableChange> TableChanges { get; } = [];
    }

    private sealed record TableChange(TableEdit Table, int RowIndex, byte[] Before, byte[] After);

    /// <summary>The mob / NPC master table (Table_Mob_Data.rdf or Table_NPC_Data.rdf) edited next to the spawns.</summary>
    private sealed class TableEdit
    {
        public required string Path { get; init; }
        public required RdfSchema Schema { get; init; }
        public required byte Margin { get; init; }
        public required string Sha { get; init; }
        public required List<byte[]> Rows { get; init; }
        public required List<byte[]> Originals { get; init; }
        public required Dictionary<uint, int> Index { get; init; }
        public required RdfField IdField { get; init; }
        public int ModifiedCount => Rows.Where((r, i) => !r.AsSpan().SequenceEqual(Originals[i])).Count();
        public bool IsDirty => Rows.Where((r, i) => !r.AsSpan().SequenceEqual(Originals[i])).Any();
        public uint IdOf(byte[] row) => BitConverter.ToUInt32(row, IdField.Offset);
    }

    private static readonly HashSet<string> CuratedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "byLevel", "dwBasic_LP", "wBasic_EP", "wBasic_Physical_Offence", "wBasic_Energy_Offence", "wBasic_Physical_Defence", "wBasic_Energy_Defence",
        "fScale", "fWalk_Speed", "fRun_Speed", "wAttack_Speed_Rate", "wAttack_Rate", "wDodge_Rate", "wBlock_Rate", "wSight_Range", "wScan_Range",
        "szModel", "bValidity_Able", "byGrade", "byMob_Type", "byNpcType"
    };
    private TableEdit? _table;

    // Field offsets resolved from the registered Taiwan schema (never hard-coded guesses).
    private sealed record Layout(int Id, int Ref, int Loc, int Dir, int Range, int Qty, int Cool, int Move, int Wander,
        int MoveRange, int Party, int Leader, int Group, int Action, int Path, int Follow, int Play, int PlayScene, int Ai, int AiScene);

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; }
    public string Kind { get; }
    public string SourceSha256 { get; }
    private readonly byte _margin;
    private readonly Layout _l;
    private readonly List<Row> _rows;
    private readonly Dictionary<int, Row> _byUid = [];
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private int _nextUid;
    private uint _maxId;
    public SpawnCatalog? Catalog { get; private set; }
    public string? CatalogNote { get; private set; }

    /// <summary>Loads Table_Mob_Data/Table_NPC_Data from the spawn file's folder (best effort; editing works without it).</summary>
    private string? _tablesFolder;
    /// <summary>Folder holding Table_Mob_Data / Table_NPC_Data / Table_Portal_Data (defaults to the spawn file's folder).</summary>
    public string TablesFolder => _tablesFolder ?? System.IO.Path.GetDirectoryName(SourcePath)!;
    public void UseTablesFolder(string folder)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("La carpeta de tablas no existe: " + folder);
        _tablesFolder = folder; _table = null;
    }
    /// <summary>Raises the floor for auto-assigned IDs (new maps use their own block, e.g. world 61006 -> 61006000).</summary>
    public void SetFirstId(uint firstId)
    {
        if (firstId == 0 || firstId == uint.MaxValue) throw new ArgumentException("El primer ID debe estar entre 1 y 4294967294.");
        if (firstId - 1 > _maxId) _maxId = firstId - 1;
    }

    /// <summary>Creates an EMPTY spawn table (just the Taiwan margin byte) in a NEW folder, to populate a new map.</summary>
    public static string CreateEmptyFile(string outputDirectory, string kind, string name)
    {
        if (kind is not ("mob" or "npc")) throw new ArgumentException("kind debe ser mob o npc.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[a-z0-9_]{1,40}$")) throw new ArgumentException("El nombre del mapa debe ser a-z, 0-9 o _ (1 a 40).");
        string output = ContentSafety.SafeOutput(outputDirectory);
        string pending = ContentSafety.FreshDirectory(output);
        string file = $"spawn_{kind}_{name}.rdf";
        File.WriteAllBytes(System.IO.Path.Combine(pending, file), [1]);
        Directory.Move(pending, output);
        return System.IO.Path.Combine(output, file);
    }

    public void TryAttachCatalog(string? folder = null)
    {
        try { Catalog = SpawnCatalog.Load(folder ?? TablesFolder); CatalogNote = Catalog.For(Kind).Count == 0 ? string.Join(" ", Catalog.Notes) : null; }
        catch (Exception ex) { Catalog = null; CatalogNote = "Catálogo no disponible: " + ex.Message; }
    }

    private SpawnEditSession(string path, string kind, string sha, byte margin, Layout l, List<Row> rows)
    {
        SourcePath = path; Kind = kind; SourceSha256 = sha; _margin = margin; _l = l; _rows = rows;
        foreach (var r in rows) { _byUid[r.Uid] = r; _maxId = Math.Max(_maxId, ReadU32(r.Data, l.Id)); }
        _nextUid = rows.Count;
    }

    public static SpawnEditSession Open(string path, string kind)
    {
        string schemaName = kind switch { "mob" => "MobSpawn", "npc" => "NpcSpawn", _ => throw new ArgumentException("kind debe ser mob o npc.") };
        var schema = RdfSchema.LoadCatalog(RdfCli.LocateCatalog()).Single(s => s.Name == schemaName);
        if (schema.RowSize != RowSize) throw new InvalidDataException("El esquema de spawn debe medir 84 bytes.");
        var doc = RdfDocument.Open(path, schema);
        int Off(string n) => schema.Fields.Single(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Offset;
        var l = new Layout(Off("tblidx"), Off(kind == "mob" ? "mob_tblidx" : "npc_tblidx"), Off("vSpawn_Loc"), Off("vSpawn_Dir"),
            Off("bySpawnLocRange"), Off("bySpawnQuantity"), Off("wSpawnCoolTime"), Off("bySpawnMoveType"), Off("byWanderRange"),
            Off("byMoveRange"), Off("dwPartyIndex"), Off("bPartyLeader"), Off("spawnGroup"), Off("actionPatternTblidx"), Off("pathTableIndex"),
            Off("vFollowDistance"), Off("playScript"), Off("playScriptScene"), Off("aiScript"), Off("aiScriptScene"));
        var rows = doc.Rows.Select(r => new Row { Uid = r.Index, Data = (byte[])r.Original.Clone(), Original = (byte[])r.Original.Clone() }).ToList();
        return new SpawnEditSession(Path.GetFullPath(path), kind, doc.OriginalHash, doc.Margin, l, rows);
    }

    // ---- registry used by the Studio UI ----
    private static readonly ConcurrentDictionary<string, SpawnEditSession> Sessions = new();
    public static SpawnEditSession Register(SpawnEditSession s)
    {
        if (Sessions.Count >= 4) throw new InvalidOperationException("Hay demasiadas sesiones de edición abiertas; cerrá una.");
        Sessions[s.SessionId] = s; return s;
    }
    public static SpawnEditSession Get(string id) => Sessions.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException("Sesión de spawns inexistente o cerrada.");
    public static void Close(string id) => Sessions.TryRemove(id, out _);

    // ---- byte helpers ----
    private static uint ReadU32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
    private static float ReadF(byte[] d, int o) => BitConverter.ToSingle(d, o);
    private static void WriteU32(byte[] d, int o, uint v) => BitConverter.TryWriteBytes(d.AsSpan(o, 4), v);
    private static void WriteF(byte[] d, int o, float v) => BitConverter.TryWriteBytes(d.AsSpan(o, 4), v);
    private static void WriteU16(byte[] d, int o, ushort v) => BitConverter.TryWriteBytes(d.AsSpan(o, 2), v);

    public object View(int uid) => ToView(_byUid[uid]);
    public IEnumerable<object> Views() => _rows.Select(ToView);

    private object ToView(Row r)
    {
        var d = r.Data;
        return new
        {
            uid = r.Uid, id = ReadU32(d, _l.Id), refId = ReadU32(d, _l.Ref),
            x = ReadF(d, _l.Loc), y = ReadF(d, _l.Loc + 4), z = ReadF(d, _l.Loc + 8),
            dirX = ReadF(d, _l.Dir), dirY = ReadF(d, _l.Dir + 4), dirZ = ReadF(d, _l.Dir + 8),
            range = (int)d[_l.Range], quantity = (int)d[_l.Qty], coolTime = (int)BitConverter.ToUInt16(d, _l.Cool),
            moveType = (int)d[_l.Move], wander = (int)d[_l.Wander], moveRange = (int)d[_l.MoveRange],
            partyIndex = ReadU32(d, _l.Party), partyLeader = d[_l.Leader] != 0, group = ReadU32(d, _l.Group),
            deleted = r.Deleted, isNew = r.IsNew,
            modified = !r.IsNew && !r.Data.AsSpan().SequenceEqual(r.Original)
        };
    }

    public object Stats()
    {
        int modified = 0, added = 0, deleted = 0;
        foreach (var r in _rows)
        {
            if (r.IsNew) { if (!r.Deleted) added++; continue; }
            if (r.Deleted) deleted++; else if (!r.Data.AsSpan().SequenceEqual(r.Original)) modified++;
        }
        return new { total = _rows.Count(r => !r.Deleted), modified, added, deleted, canUndo = _undo.Count > 0, canRedo = _redo.Count > 0,
            tableModified = _table?.ModifiedCount ?? 0, dirty = modified + added + deleted > 0 || (_table?.IsDirty ?? false), nextFreeId = (long)_maxId + 1 };
    }

    // ---- edits ----
    private Row Req(int uid) => _byUid.TryGetValue(uid, out var r) && !r.Deleted ? r : throw new ArgumentException($"Spawn uid {uid} no existe o está borrado.");

    private object Commit(Edit e)
    {
        if (e.Changes.Count == 0 && e.TableChanges.Count == 0) return new { changed = Array.Empty<object>(), stats = Stats() };
        foreach (var c in e.Changes) ApplyForward(c);
        foreach (var t in e.TableChanges) t.Table.Rows[t.RowIndex] = t.After;
        _undo.Push(e); _redo.Clear();
        if (_undo.Count > MaxUndo) { var keep = _undo.Take(MaxUndo).Reverse().ToArray(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        return new { changed = e.Changes.Select(c => ToView(c.Row)).DistinctBy(v => ((dynamic)v).uid).ToArray(), stats = Stats() };
    }

    private void ApplyForward(Change c)
    {
        c.Row.Data = c.After; c.Row.Deleted = c.AfterDeleted;
        if (c.Added && !_byUid.ContainsKey(c.Row.Uid)) { _rows.Add(c.Row); _byUid[c.Row.Uid] = c.Row; }
        _maxId = Math.Max(_maxId, ReadU32(c.After, _l.Id));
    }

    private void ApplyBackward(Change c)
    {
        c.Row.Data = c.Before; c.Row.Deleted = c.BeforeDeleted;
        if (c.Added) { _rows.Remove(c.Row); _byUid.Remove(c.Row.Uid); }
    }

    public object Undo()
    {
        if (_undo.Count == 0) throw new InvalidOperationException("Nada para deshacer.");
        var e = _undo.Pop();
        foreach (var c in Enumerable.Reverse(e.Changes)) ApplyBackward(c);
        foreach (var t in Enumerable.Reverse(e.TableChanges)) t.Table.Rows[t.RowIndex] = t.Before;
        _redo.Push(e);
        return new { changed = e.Changes.Select(c => ToViewOrGone(c)).ToArray(), stats = Stats() };
    }

    public object Redo()
    {
        if (_redo.Count == 0) throw new InvalidOperationException("Nada para rehacer.");
        var e = _redo.Pop();
        foreach (var c in e.Changes) ApplyForward(c);
        foreach (var t in e.TableChanges) t.Table.Rows[t.RowIndex] = t.After;
        _undo.Push(e);
        return new { changed = e.Changes.Select(c => ToView(c.Row)).ToArray(), stats = Stats() };
    }

    // After undoing an add the row leaves the table: report it as deleted so the viewer hides it.
    private object ToViewOrGone(Change c) => c.Added ? new { uid = c.Row.Uid, deleted = true, isNew = true, gone = true } : ToView(c.Row);

    private static void CheckFinite(params float[] v)
    {
        foreach (float f in v) if (!float.IsFinite(f) || Math.Abs(f) > CoordLimit) throw new ArgumentException("Coordenada inválida (NaN/infinito/fuera de ±1.000.000).");
    }

    private Change Modify(Row r, Action<byte[]> mutate)
    {
        var after = (byte[])r.Data.Clone(); mutate(after);
        return new Change(r, (byte[])r.Data.Clone(), after, r.Deleted, r.Deleted, false);
    }

    /// <summary>Moves spawns to absolute game coordinates (Y is supplied by the caller, usually preserving the offset above terrain).</summary>
    public object Move(IEnumerable<(int Uid, float X, float Y, float Z)> items)
    {
        var e = new Edit { Name = "move" };
        foreach (var (uid, x, y, z) in items)
        {
            CheckFinite(x, y, z);
            e.Changes.Add(Modify(Req(uid), d => { WriteF(d, _l.Loc, x); WriteF(d, _l.Loc + 4, y); WriteF(d, _l.Loc + 8, z); }));
        }
        return Commit(e);
    }

    /// <summary>Rotates the facing vector around the vertical axis by degrees (game space). A zero vector starts from +X.</summary>
    public object Rotate(IEnumerable<int> uids, float degrees)
    {
        CheckFinite(degrees);
        double rad = degrees * Math.PI / 180.0, c = Math.Cos(rad), s = Math.Sin(rad);
        var e = new Edit { Name = "rotate" };
        foreach (int uid in uids)
            e.Changes.Add(Modify(Req(uid), d =>
            {
                double dx = ReadF(d, _l.Dir), dz = ReadF(d, _l.Dir + 8), len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-6) { dx = 1; dz = 0; len = 1; }
                dx /= len; dz /= len;
                WriteF(d, _l.Dir, (float)(dx * c - dz * s)); WriteF(d, _l.Dir + 8, (float)(dx * s + dz * c));
            }));
        return Commit(e);
    }

    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> FieldLimits = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
    {
        ["range"] = (0, 255), ["quantity"] = (0, 255), ["coolTime"] = (0, 65535), ["moveType"] = (0, 255), ["wander"] = (0, 255),
        ["moveRange"] = (0, 255), ["refId"] = (1, uint.MaxValue - 1), ["group"] = (0, uint.MaxValue), ["partyIndex"] = (0, uint.MaxValue), ["partyLeader"] = (0, 1)
    };

    public object SetFields(IEnumerable<int> uids, IReadOnlyDictionary<string, double> fields)
    {
        if (fields.Count == 0) throw new ArgumentException("No hay campos para cambiar.");
        foreach (var (name, value) in fields)
        {
            if (!FieldLimits.TryGetValue(name, out var lim)) throw new ArgumentException($"Campo no editable: {name}.");
            if (!double.IsFinite(value) || value != Math.Floor(value) || value < lim.Min || value > lim.Max)
                throw new ArgumentException($"{name}: valor fuera de rango {lim.Min}..{lim.Max}.");
        }
        var e = new Edit { Name = "set-fields" };
        foreach (int uid in uids)
            e.Changes.Add(Modify(Req(uid), d =>
            {
                foreach (var (name, v) in fields)
                    switch (name.ToLowerInvariant())
                    {
                        case "range": d[_l.Range] = (byte)v; break;
                        case "quantity": d[_l.Qty] = (byte)v; break;
                        case "cooltime": WriteU16(d, _l.Cool, (ushort)v); break;
                        case "movetype": d[_l.Move] = (byte)v; break;
                        case "wander": d[_l.Wander] = (byte)v; break;
                        case "moverange": d[_l.MoveRange] = (byte)v; break;
                        case "refid": WriteU32(d, _l.Ref, (uint)v); break;
                        case "group": WriteU32(d, _l.Group, (uint)v); break;
                        case "partyindex": WriteU32(d, _l.Party, (uint)v); break;
                        case "partyleader": d[_l.Leader] = (byte)v; break;
                    }
            }));
        return Commit(e);
    }

    public object Delete(IEnumerable<int> uids)
    {
        var e = new Edit { Name = "delete" };
        foreach (int uid in uids.Distinct())
        {
            var r = Req(uid);
            e.Changes.Add(new Change(r, (byte[])r.Data.Clone(), (byte[])r.Data.Clone(), false, true, false));
        }
        return Commit(e);
    }

    /// <summary>
    /// Clones spawns offset by (dx,dz). New tblidx values are max-existing+1 within THIS file; they are
    /// recorded in the audit as auto-assigned and are NOT checked against other spawn tables (see Validate).
    /// </summary>
    public object Clone(IEnumerable<int> uids, float dx, float dz)
    {
        CheckFinite(dx, dz);
        var e = new Edit { Name = "clone" };
        uint next = _maxId;
        foreach (int uid in uids.Distinct())
        {
            var src = Req(uid);
            if (next >= uint.MaxValue - 1) throw new InvalidOperationException("No quedan IDs libres en este archivo.");
            next++;
            var data = (byte[])src.Data.Clone();
            WriteU32(data, _l.Id, next);
            WriteF(data, _l.Loc, ReadF(data, _l.Loc) + dx); WriteF(data, _l.Loc + 8, ReadF(data, _l.Loc + 8) + dz);
            var row = new Row { Uid = _nextUid++, Data = data, Original = null, Source = "clone:uid" + uid };
            e.Changes.Add(new Change(row, data, data, false, false, true));
        }
        return Commit(e);
    }

    public readonly record struct NewSpawn(uint RefId, float X, float Y, float Z, float DirX, float DirZ, int? TemplateUid);

    private const uint Unset = uint.MaxValue; // -1 sentinel used by Taiwan spawn rows for "no path / script / party / group"

    /// <summary>
    /// Adds spawns of a mob/NPC from the catalog. Behaviour fields come from a template row (explicit uid, else the first live row
    /// with the same reference), otherwise from conservative per-kind defaults. Path/script/party/group are always reset to -1:
    /// copying them would silently attach the new spawn to someone else's route or party.
    /// </summary>
    public object Add(IEnumerable<NewSpawn> items)
    {
        var e = new Edit { Name = "add" };
        uint next = _maxId;
        foreach (var it in items)
        {
            CheckFinite(it.X, it.Y, it.Z, it.DirX, it.DirZ);
            if (it.RefId == 0 || it.RefId == uint.MaxValue) throw new ArgumentException("Referencia de mob/NPC inválida.");
            if (Catalog != null && Catalog.For(Kind).Count > 0 && !Catalog.For(Kind).ContainsKey(it.RefId))
                throw new ArgumentException($"El {(Kind == "npc" ? "NPC" : "mob")} {it.RefId} no existe o está inactivo en el catálogo.");
            if (next >= uint.MaxValue - 1) throw new InvalidOperationException("No quedan IDs libres en este archivo.");
            next++;
            Row? template = it.TemplateUid is int tu ? Req(tu) : _rows.FirstOrDefault(r => !r.Deleted && ReadU32(r.Data, _l.Ref) == it.RefId);
            byte[] data; string source;
            if (template != null) { data = (byte[])template.Data.Clone(); source = (it.TemplateUid != null ? "template:uid" : "same-ref:uid") + template.Uid; }
            else { data = Defaults(); source = "defaults"; }
            WriteU32(data, _l.Id, next); WriteU32(data, _l.Ref, it.RefId);
            WriteF(data, _l.Loc, it.X); WriteF(data, _l.Loc + 4, it.Y); WriteF(data, _l.Loc + 8, it.Z);
            double dx = it.DirX, dz = it.DirZ, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) { dx = 1; dz = 0; len = 1; }
            WriteF(data, _l.Dir, (float)(dx / len)); WriteF(data, _l.Dir + 4, 0f); WriteF(data, _l.Dir + 8, (float)(dz / len));
            foreach (int o in new[] { _l.Path, _l.Play, _l.PlayScene, _l.Ai, _l.AiScene, _l.Party, _l.Group }) WriteU32(data, o, Unset);
            data[_l.Leader] = 0;
            var row = new Row { Uid = _nextUid++, Data = data, Original = null, Source = source };
            e.Changes.Add(new Change(row, data, data, false, false, true));
        }
        if (e.Changes.Count == 0) throw new ArgumentException("No hay spawns para agregar.");
        return Commit(e);
    }

    private byte[] Defaults()
    {
        var d = new byte[RowSize]; bool npc = Kind == "npc";
        d[_l.Range] = 1; d[_l.Qty] = 1; WriteU16(d, _l.Cool, (ushort)(npc ? 15 : 300));
        d[_l.Move] = (byte)(npc ? 0 : 1); d[_l.Wander] = (byte)(npc ? 1 : 5); d[_l.MoveRange] = (byte)(npc ? 1 : 8); d[_l.Action] = 1;
        WriteF(d, _l.Follow, float.MaxValue); WriteF(d, _l.Follow + 4, 0f); WriteF(d, _l.Follow + 8, float.MaxValue);
        return d;
    }

    // ---- master table (mob / NPC row) editing ----
    private TableEdit LoadTable()
    {
        if (_table != null) return _table;
        string file = Kind == "npc" ? "Table_NPC_Data.rdf" : "Table_Mob_Data.rdf";
        string path = System.IO.Path.Combine(TablesFolder, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"No se encontró {file} junto al archivo de spawns; no se puede editar la fila del {(Kind == "npc" ? "NPC" : "mob")}.");
        var schema = RdfSchema.LoadCatalog(RdfCli.LocateCatalog()).FirstOrDefault(x => x.Name == (Kind == "npc" ? "NpcData" : "MobData"))
            ?? throw new InvalidDataException("No hay esquema registrado para la tabla maestra.");
        var doc = RdfDocument.Open(path, schema);
        var idField = schema.Fields.First(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        var index = new Dictionary<uint, int>();
        for (int i = 0; i < doc.Rows.Count; i++) index[BitConverter.ToUInt32(doc.Rows[i].Original, idField.Offset)] = i;
        return _table = new TableEdit
        {
            Path = path, Schema = schema, Margin = doc.Margin, Sha = doc.OriginalHash, IdField = idField, Index = index,
            Rows = doc.Rows.Select(r => (byte[])r.Original.Clone()).ToList(), Originals = doc.Rows.Select(r => (byte[])r.Original.Clone()).ToList()
        };
    }

    /// <summary>Portals next to the spawn file (read-only) with how many live spawns sit within 10 m of each.</summary>
    public object Portals(uint? worldId)
    {
        var portals = PortalReadService.Load(TablesFolder, out var note);
        float r2 = PortalReadService.NearSpawnMetres * PortalReadService.NearSpawnMetres;
        var live = _rows.Where(r => !r.Deleted).Select(r => (X: ReadF(r.Data, _l.Loc), Z: ReadF(r.Data, _l.Loc + 8))).ToArray();
        return PortalReadService.ToResult(portals, note, worldId, (x, z) => live.Count(l => (l.X - x) * (l.X - x) + (l.Z - z) * (l.Z - z) <= r2));
    }

    public static string FieldKey(RdfField f) => $"{f.Name}@{f.Offset}";

    public object TableRow(uint id)
    {
        var t = LoadTable();
        if (!t.Index.TryGetValue(id, out int idx)) throw new ArgumentException($"El {(Kind == "npc" ? "NPC" : "mob")} {id} no existe en {System.IO.Path.GetFileName(t.Path)}.");
        var row = t.Rows[idx]; var orig = t.Originals[idx];
        return new
        {
            id, table = System.IO.Path.GetFileName(t.Path), kind = Kind,
            spawnsUsing = _rows.Count(r => !r.Deleted && ReadU32(r.Data, _l.Ref) == id),
            modified = !row.AsSpan().SequenceEqual(orig),
            warning = "Esta fila es compartida por TODOS los spawns de este mob/NPC en todos los mundos, y la tabla también la lee el cliente (tbl.pak): el cambio exige publicar cliente y servidor en la misma versión.",
            fields = t.Schema.Fields.Where(f => !f.IsPadding).Select(f => new
            {
                key = FieldKey(f), name = f.Name, type = f.Type, count = f.Count,
                value = t.Schema.Decode(f, row), original = t.Schema.Decode(f, orig),
                editable = !ReferenceEquals(f, t.IdField), curated = CuratedFields.Contains(f.Name)
            }).ToArray()
        };
    }

    /// <summary>Edits fields of one mob/NPC master row (keys are name@offset). Values are validated by the Taiwan schema encoder.</summary>
    public object TableSet(uint id, IReadOnlyDictionary<string, string> values)
    {
        var t = LoadTable();
        if (!t.Index.TryGetValue(id, out int idx)) throw new ArgumentException($"El {(Kind == "npc" ? "NPC" : "mob")} {id} no existe en {System.IO.Path.GetFileName(t.Path)}.");
        if (values.Count == 0) throw new ArgumentException("No hay campos para cambiar.");
        var after = (byte[])t.Rows[idx].Clone();
        foreach (var (key, text) in values)
        {
            var f = t.Schema.Fields.FirstOrDefault(x => !x.IsPadding && FieldKey(x) == key) ?? throw new ArgumentException($"Campo desconocido o no editable: {key}.");
            if (ReferenceEquals(f, t.IdField)) throw new ArgumentException("El tblidx no se puede cambiar.");
            try { t.Schema.Encode(f, text, after); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { throw new ArgumentException($"{f.Name}: valor inválido ({ex.Message})"); }
        }
        var e = new Edit { Name = "table-set" };
        if (!after.AsSpan().SequenceEqual(t.Rows[idx])) e.TableChanges.Add(new TableChange(t, idx, (byte[])t.Rows[idx].Clone(), after));
        var result = Commit(e);
        return new { changed = Array.Empty<object>(), stats = Stats(), row = TableRow(id) };
    }

    // ---- validation ----
    public sealed record Issue(string Severity, string Message, int? Uid);

    public IReadOnlyList<Issue> Validate(IEnumerable<string>? alsoCheckAgainst = null)
    {
        var issues = new List<Issue>();
        var ids = new Dictionary<uint, int>();
        foreach (var r in _rows.Where(r => !r.Deleted))
        {
            uint id = ReadU32(r.Data, _l.Id);
            if (id == 0 || id == uint.MaxValue) issues.Add(new Issue("error", $"ID inválido {id}.", r.Uid));
            else if (!ids.TryAdd(id, r.Uid)) issues.Add(new Issue("error", $"ID duplicado {id} (uid {ids[id]} y {r.Uid}).", r.Uid));
            foreach (int o in new[] { _l.Loc, _l.Loc + 4, _l.Loc + 8, _l.Dir, _l.Dir + 4, _l.Dir + 8 })
                if (!float.IsFinite(ReadF(r.Data, o))) { issues.Add(new Issue("error", "Coordenada o dirección no finita.", r.Uid)); break; }
            if (ReadU32(r.Data, _l.Ref) == 0) issues.Add(new Issue("warning", "Referencia de mob/NPC en 0.", r.Uid));
        }
        if (_table?.IsDirty == true)
            issues.Add(new Issue("warning", $"Se modificaron {_table.ModifiedCount} fila(s) de {System.IO.Path.GetFileName(_table.Path)}: la tabla es compartida por todos los mundos y la lee el cliente (tbl.pak); publicar cliente y servidor juntos.", null));
        if (Catalog != null && Catalog.For(Kind).Count > 0)
        {
            var cat = Catalog.For(Kind); int legacyUnresolved = 0;
            foreach (var r in _rows.Where(r => !r.Deleted))
            {
                uint rf = ReadU32(r.Data, _l.Ref);
                bool touched = r.IsNew || (r.Original != null && ReadU32(r.Original, _l.Ref) != rf);
                if (!cat.TryGetValue(rf, out var entry))
                {
                    if (touched) issues.Add(new Issue("error", $"La referencia {rf} no existe o está inactiva en el catálogo.", r.Uid)); else legacyUnresolved++;
                    continue;
                }
                if (touched && Kind == "mob" && entry.Level > SpawnCatalog.MainWorldMobLevelCap)
                    issues.Add(new Issue("warning", $"{entry.Name} ({rf}) es nivel {entry.Level}: en mundos con tblidx <= 15 el servidor descarta mobs sobre nivel {SpawnCatalog.MainWorldMobLevelCap}.", r.Uid));
            }
            if (legacyUnresolved > 0) issues.Add(new Issue("warning", $"{legacyUnresolved} filas preexistentes referencian mobs/NPC inactivos o ausentes (no se tocaron).", null));
        }
        else if (CatalogNote != null) issues.Add(new Issue("warning", CatalogNote, null));
        // New rows get IDs that are only unique inside this file: cross-check other spawn tables when provided.
        foreach (string other in alsoCheckAgainst ?? [])
        {
            string k = Path.GetFileName(other).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob";
            var o = Open(other, k);
            foreach (var r in o._rows)
            {
                uint id = ReadU32(r.Data, o._l.Id);
                if (ids.TryGetValue(id, out int uid) && _byUid[uid].IsNew)
                    issues.Add(new Issue("error", $"El ID nuevo {id} ya existe en {Path.GetFileName(other)}.", uid));
            }
        }
        if (_rows.Any(r => r.IsNew && !r.Deleted) && alsoCheckAgainst == null)
            issues.Add(new Issue("warning", "Hay IDs nuevos asignados automáticamente (máximo+1 del archivo); no se comprobaron contra otras tablas de spawn.", null));
        return issues;
    }

    // ---- hooks for SpawnSelfTest (read-only views of the in-memory state) ----
    internal byte[] CurrentBytes() => BuildBytes();
    internal int NetChanges { get { var c = SpawnCounts(); return c.Modified + c.Added + c.Deleted; } }
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal int[] LiveUids() => _rows.Where(r => !r.Deleted).Select(r => r.Uid).ToArray();
    internal uint[] LiveIds() => _rows.Where(r => !r.Deleted).Select(r => ReadU32(r.Data, _l.Id)).ToArray();

    // ---- save as candidate ----
    private byte[] BuildBytes()
    {
        var live = _rows.Where(r => !r.Deleted).ToList();
        var bytes = new byte[1 + live.Count * RowSize];
        bytes[0] = _margin;
        for (int i = 0; i < live.Count; i++) live[i].Data.CopyTo(bytes, 1 + i * RowSize);
        return bytes;
    }

    private (int Modified, int Added, int Deleted) SpawnCounts()
    {
        int m = 0, a = 0, d = 0;
        foreach (var r in _rows)
        {
            if (r.IsNew) { if (!r.Deleted) a++; continue; }
            if (r.Deleted) d++; else if (!r.Data.AsSpan().SequenceEqual(r.Original)) m++;
        }
        return (m, a, d);
    }

    private object SaveTable(TableEdit t, string pending, string snapDir, string candDir)
    {
        string name = System.IO.Path.GetFileName(t.Path);
        string snapPath = System.IO.Path.Combine(snapDir, name), candPath = System.IO.Path.Combine(candDir, name);
        using (var src = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!Convert.ToHexString(SHA256.HashData(src)).Equals(t.Sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{name} cambió desde que se abrió; abrí la sesión de nuevo antes de guardar.");
            src.Position = 0;
            using var snap = new FileStream(snapPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(snap); snap.Flush(true);
        }
        int rowSize = t.Schema.RowSize;
        var bytes = new byte[1 + t.Rows.Count * rowSize]; bytes[0] = t.Margin;
        for (int i = 0; i < t.Rows.Count; i++) t.Rows[i].CopyTo(bytes, 1 + i * rowSize);
        using (var fs = new FileStream(candPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(bytes); fs.Flush(true); }
        var back = File.ReadAllBytes(candPath);
        if (back.Length != bytes.Length || !back.AsSpan().SequenceEqual(bytes)) throw new IOException($"El candidato de {name} escrito no coincide con la edición.");
        // Original file layout must be preserved exactly outside the edited rows.
        var original = File.ReadAllBytes(snapPath);
        if (original.Length != bytes.Length) throw new IOException($"{name}: el tamaño del candidato difiere del original.");
        int unchanged = 0;
        var diffs = new List<object>();
        for (int i = 0; i < t.Rows.Count; i++)
        {
            if (!t.Rows[i].AsSpan().SequenceEqual(t.Originals[i]))
            {
                if (!original.AsSpan(1 + i * rowSize, rowSize).SequenceEqual(t.Originals[i])) throw new IOException($"{name}: la fila {i} del snapshot no coincide con la leída.");
                diffs.Add(new
                {
                    id = t.IdOf(t.Rows[i]),
                    changedFields = t.Schema.Fields.Where(f => !f.IsPadding && t.Schema.Decode(f, t.Rows[i]) != t.Schema.Decode(f, t.Originals[i]))
                        .Select(f => new { field = f.Name, before = t.Schema.Decode(f, t.Originals[i]), after = t.Schema.Decode(f, t.Rows[i]) }).ToArray()
                });
            }
            else unchanged++;
        }
        return new
        {
            file = name, sourcePath = t.Path, sourceSha256 = t.Sha, snapshot = System.IO.Path.GetRelativePath(pending, snapPath),
            candidate = System.IO.Path.GetRelativePath(pending, candPath), candidateSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            rows = t.Rows.Count, unchangedRowsVerifiedByteIdentical = unchanged, modifiedRows = diffs.Count, diffs,
            note = "Tabla compartida con el cliente (tbl.pak) y con todos los mundos: publicar cliente y servidor en la misma versión."
        };
    }

    public object SaveCandidate(string outputDirectory, IEnumerable<string>? alsoCheckAgainst = null)
    {
        var issues = Validate(alsoCheckAgainst);
        if (issues.Any(i => i.Severity == "error")) throw new InvalidOperationException("La validación encontró errores; no se guardó candidato: " + string.Join(" | ", issues.Where(i => i.Severity == "error").Take(5).Select(i => i.Message)));
        var counts0 = SpawnCounts();
        bool spawnDirty = counts0.Modified + counts0.Added + counts0.Deleted > 0, tableDirty = _table?.IsDirty == true;
        if (!spawnDirty && !tableDirty) throw new InvalidOperationException("No hay cambios para guardar.");
        string output = ContentSafety.SafeOutput(outputDirectory);
        foreach (string src0 in new[] { SourcePath, _table?.Path ?? SourcePath })
            if (Path.GetFullPath(src0).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(src0).Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new IOException("La salida no puede reemplazar ni contener un archivo fuente.");
        if (!File.Exists(SourcePath)) throw new IOException("El archivo fuente ya no existe.");
        string pending = ContentSafety.FreshDirectory(output);
        string name = Path.GetFileName(SourcePath);
        string snapDir = Path.Combine(pending, "snapshot"), candDir = Path.Combine(pending, "candidate");
        Directory.CreateDirectory(snapDir); Directory.CreateDirectory(candDir);
        string snapPath = Path.Combine(snapDir, name), candPath = Path.Combine(candDir, name);
        string? candSha = null; int unchangedVerified = 0, liveCount = 0;
        var moved = new List<object>(); var modified = new List<object>(); var added = new List<object>(); var deleted = new List<object>();
        if (spawnDirty)
        {
            using (var src = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!Convert.ToHexString(SHA256.HashData(src)).Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("El archivo fuente cambió desde que se abrió; abrilo de nuevo antes de guardar.");
                src.Position = 0;
                using var snap = new FileStream(snapPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                src.CopyTo(snap); snap.Flush(true);
            }
            byte[] cand = BuildBytes();
            using (var fs = new FileStream(candPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(cand); fs.Flush(true); }
            candSha = Convert.ToHexString(SHA256.HashData(cand));

            // Read-back proof: re-parse the written file and compare against the expected rows byte by byte.
            var back = File.ReadAllBytes(candPath);
            var live = _rows.Where(r => !r.Deleted).ToList(); liveCount = live.Count;
            if (back.Length != 1 + live.Count * RowSize || back[0] != _margin) throw new IOException("El candidato escrito no tiene el tamaño o margen esperado.");
            for (int i = 0; i < live.Count; i++)
            {
                if (!back.AsSpan(1 + i * RowSize, RowSize).SequenceEqual(live[i].Data)) throw new IOException($"Fila {i} del candidato no coincide con la edición.");
                if (!live[i].IsNew && live[i].Data.AsSpan().SequenceEqual(live[i].Original)) unchangedVerified++;
            }
            foreach (var r in _rows)
            {
                if (r.IsNew) { if (!r.Deleted) added.Add(new { row = ToView(r), source = r.Source }); continue; }
                if (r.Deleted) { deleted.Add(new { uid = r.Uid, id = ReadU32(r.Original!, _l.Id), refId = ReadU32(r.Original!, _l.Ref) }); continue; }
                if (r.Data.AsSpan().SequenceEqual(r.Original)) continue;
                var o = r.Original!; var n = r.Data;
                bool locChanged = !n.AsSpan(_l.Loc, 12).SequenceEqual(o.AsSpan(_l.Loc, 12));
                var changedBytes = Enumerable.Range(0, RowSize).Where(b => n[b] != o[b]).ToArray();
                var rec = new
                {
                    uid = r.Uid, id = ReadU32(n, _l.Id), changedByteOffsets = changedBytes,
                    before = new { x = ReadF(o, _l.Loc), y = ReadF(o, _l.Loc + 4), z = ReadF(o, _l.Loc + 8), refId = ReadU32(o, _l.Ref) },
                    after = new { x = ReadF(n, _l.Loc), y = ReadF(n, _l.Loc + 4), z = ReadF(n, _l.Loc + 8), refId = ReadU32(n, _l.Ref) }
                };
                (locChanged ? moved : modified).Add(rec);
            }
        }
        object? tableAudit = tableDirty ? SaveTable(_table!, pending, snapDir, candDir) : null;
        var audit = new
        {
            format = "spawn-edit-candidate-v2", kind = Kind, sourcePath = SourcePath, sourceSha256 = SourceSha256, spawnChanged = spawnDirty,
            snapshot = spawnDirty ? Path.GetRelativePath(pending, snapPath) : null, candidate = spawnDirty ? Path.GetRelativePath(pending, candPath) : null, candidateSha256 = candSha,
            sourceRows = _rows.Count(r => !r.IsNew), candidateRows = liveCount, unchangedRowsVerifiedByteIdentical = unchangedVerified,
            counts = new { moved = moved.Count, modified = modified.Count, added = added.Count, deleted = deleted.Count },
            autoAssignedNewIds = added, moved, modified, deleted, table = tableAudit, issues, savedUtc = DateTimeOffset.UtcNow,
            installAllowed = false,
            note = "Candidato de edición de spawns (y tabla maestra si aplica). No instalar sin revisar contra el runtime actual (scripts/additive_guard.py) y sin OK explícito."
        };
        ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
        Directory.Move(pending, output);
        return new { directory = output, candidate = spawnDirty ? Path.Combine(output, "candidate", name) : null, audit = Path.Combine(output, "audit.json"), sourceSha256 = SourceSha256, candidateSha256 = candSha,
            tableCandidate = tableDirty ? Path.Combine(output, "candidate", Path.GetFileName(_table!.Path)) : null,
            counts = new { moved = moved.Count, modified = modified.Count, added = added.Count, deleted = deleted.Count, tableRows = _table?.ModifiedCount ?? 0 }, issues };
    }

    // ---- scripted edits (CLI) ----
    public object ApplyOps(JsonElement ops)
    {
        if (ops.ValueKind != JsonValueKind.Array) throw new ArgumentException("ops debe ser un arreglo JSON.");
        int applied = 0;
        foreach (var op in ops.EnumerateArray())
        {
            string kind = op.GetProperty("op").GetString() ?? "";
            int[] Uids() => op.TryGetProperty("uids", out var u) ? u.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [op.GetProperty("uid").GetInt32()];
            float F(string n) => op.GetProperty(n).GetSingle();
            switch (kind)
            {
                case "move": Move([(op.GetProperty("uid").GetInt32(), F("x"), F("y"), F("z"))]); break;
                case "rotate": Rotate(Uids(), F("degrees")); break;
                case "delete": Delete(Uids()); break;
                case "add":
                    Add([new NewSpawn(op.GetProperty("ref").GetUInt32(), F("x"), F("y"), F("z"),
                        op.TryGetProperty("dirX", out var ddx) ? ddx.GetSingle() : 1f, op.TryGetProperty("dirZ", out var ddz) ? ddz.GetSingle() : 0f,
                        op.TryGetProperty("template", out var tpl) ? tpl.GetInt32() : null)]); break;
                case "table-set":
                    TableSet(op.GetProperty("id").GetUInt32(), op.GetProperty("fields").EnumerateObject().ToDictionary(pp => pp.Name, pp => pp.Value.ValueKind == JsonValueKind.String ? pp.Value.GetString() ?? "" : pp.Value.GetRawText())); break;
                case "undo": Undo(); break;
                case "redo": Redo(); break;
                case "clone": Clone(Uids(), F("dx"), F("dz")); break;
                case "set":
                    SetFields(Uids(), op.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.OrdinalIgnoreCase)); break;
                default: throw new ArgumentException("Operación desconocida: " + kind);
            }
            applied++;
        }
        return new { applied, stats = Stats() };
    }
}
