using System.Text.Json;

namespace DboAssetsStudio;

// World Editor · spawns on the real engine. Mob and NPC tables of the open map can be loaded at the same time
// (one SpawnEditSession per kind, the same engine as the Three.js spawn editor: validation, undo, candidate audit).
// The map's tables come from table_world_data (wszResourceFolder -> wszMobSpawn/NpcSpawn_Table_Name) in the
// server table folder, read-only. The palette lists Table_Mob_Data / Table_NPC_Data (SpawnCatalog); placing is a
// click on the engine's terrain.
internal sealed partial class MainForm
{
    private sealed class WorldEngineSpawnLayer
    {
        public required string Kind { get; init; }
        public required string SessionId { get; init; }
        public required string SourcePath { get; init; }
        public SpawnReadService.SpawnFile? Rows { get; set; }
    }
    private readonly Dictionary<string, WorldEngineSpawnLayer> _worldEngineSpawnLayers = new(StringComparer.Ordinal);
    private int _worldEnginePushes;

    private async Task<object?> WorldEngineSpawnCommandAsync(string action, JsonElement args)
    {
        string S(string name, string fallback = "") => args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()) : fallback;
        double N(string name, double fallback) => args.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : fallback;
        bool B(string name, bool fallback = false) => args.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
        string Kind() { string k = S("kind", "mob"); return k == "npc" ? "npc" : "mob"; }
        WorldEngineSpawnLayer Layer(string kind) => _worldEngineSpawnLayers.TryGetValue(kind, out var l) ? l : throw new InvalidOperationException($"Cargá una tabla de {(kind == "npc" ? "NPC" : "mobs")} primero.");
        int[] Uids() => args.TryGetProperty("uids", out var u) && u.ValueKind == JsonValueKind.Array ? u.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];

        switch (action)
        {
            case "world-engine-spawn-tables":
                return WorldEngineService.SpawnTables(WorldEngineService.ValidWorld(S("world", _worldEngine.World)));
            case "world-engine-spawns-pick":
                using (var dialog = new OpenFileDialog { Filter = "Spawn Taiwan (spawn_mob_*.rdf;spawn_npc_*.rdf)|spawn_mob_*.rdf;spawn_npc_*.rdf|RDF (*.rdf)|*.rdf", Title = "Elegí una tabla de spawns (solo lectura)", InitialDirectory = WorldEngineService.ServerRdfFolder() ?? "" })
                    return dialog.ShowDialog(this) == DialogResult.OK ? new { path = dialog.FileName } : null;
            case "world-engine-spawns-open":
            case "world-engine-spawn-load":
                {
                    string path = S("path");
                    string kind = S("kind") is "mob" or "npc" ? S("kind") : (Path.GetFileName(path).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob");
                    if (_worldEngineSpawnLayers.Remove(kind, out var old)) SpawnEditSession.Close(old.SessionId);
                    var session = SpawnEditSession.Register(SpawnEditSession.Open(path, kind));
                    string? tables = S("tablesFolder").Length > 0 ? S("tablesFolder") : WorldEngineService.ServerRdfFolder();
                    if (tables != null) session.UseTablesFolder(tables);
                    session.TryAttachCatalog();
                    var layer = new WorldEngineSpawnLayer { Kind = kind, SessionId = session.SessionId, SourcePath = Path.GetFullPath(path) };
                    _worldEngineSpawnLayers[kind] = layer;
                    int drawn = await PushWorldEngineSpawnsAsync(kind);
                    return new { session = session.SessionId, kind, source = layer.SourcePath, rows = layer.Rows?.Rows.Length ?? 0, drawn, catalogNote = session.CatalogNote, stats = session.Stats() };
                }
            case "world-engine-spawn-unload":
                {
                    string kind = Kind();
                    if (_worldEngineSpawnLayers.Remove(kind, out var l)) SpawnEditSession.Close(l.SessionId);
                    if (_worldEngine.Running) await _worldEngine.SendAsync("spawns", new { kind, uid = Array.Empty<int>(), x = Array.Empty<float>(), y = Array.Empty<float>(), z = Array.Empty<float>() });
                    return new { kind, unloaded = true };
                }
            case "world-engine-spawn-layers":
                return _worldEngineSpawnLayers.Values.Select(l => new { kind = l.Kind, sourcePath = l.SourcePath, rows = l.Rows?.Rows.Length ?? 0, stats = SpawnEditSession.Get(l.SessionId).Stats() }).ToArray();
            case "world-engine-spawn-catalog":
                {
                    var session = SpawnEditSession.Get(Layer(Kind()).SessionId);
                    if (session.Catalog == null) return new { kind = Kind(), total = 0, notes = new[] { session.CatalogNote ?? "Catálogo no disponible." }, rows = Array.Empty<object>() };
                    return session.Catalog.ToResult(Kind(), session.Catalog.Search(Kind(), S("query"), (int)N("minLevel", 0), (int)N("maxLevel", 255), (int)N("limit", 80), 0));
                }
            case "world-engine-spawn-add":
                {
                    string kind = Kind(); var session = SpawnEditSession.Get(Layer(kind).SessionId);
                    uint refId = (uint)N("ref", 0); if (refId == 0) throw new InvalidDataException("Elegí qué " + (kind == "npc" ? "NPC" : "mob") + " spawnear.");
                    if (session.Catalog != null && !session.Catalog.For(kind).ContainsKey(refId)) throw new InvalidDataException($"{refId} no existe en la tabla de {(kind == "npc" ? "NPC" : "mobs")}.");
                    double yaw = N("yaw", 0) * Math.PI / 180;
                    int? template = args.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
                    var result = session.Add([new SpawnEditSession.NewSpawn(refId, (float)N("x", 0), (float)N("y", 0), (float)N("z", 0), (float)Math.Cos(yaw), (float)Math.Sin(yaw), template)]);
                    await PushWorldEngineSpawnsAsync(kind);
                    return new { result, stats = session.Stats() };
                }
            case "world-engine-spawn-op":
                {
                    string kind = Kind(); var session = SpawnEditSession.Get(Layer(kind).SessionId);
                    object result = S("op") switch
                    {
                        "delete" => session.Delete(Uids()),
                        "clone" => session.Clone(Uids(), (float)N("dx", 3), (float)N("dz", 3)),
                        "rotate" => session.Rotate(Uids(), (float)N("degrees", 45)),
                        "set" => session.SetFields(Uids(), args.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.OrdinalIgnoreCase)),
                        "undo" => session.Undo(),
                        "redo" => session.Redo(),
                        _ => throw new InvalidDataException("Operación de spawn desconocida."),
                    };
                    await PushWorldEngineSpawnsAsync(kind);
                    return new { result, stats = session.Stats() };
                }
            case "world-engine-spawn-select":
                {
                    string kind = Kind(); int[] uids = Uids();
                    await _worldEngine.SendAsync("select", new { kind, uids, focus = B("focus") });
                    return new { kind, selected = uids.Select(u => DescribeSpawn(kind, u)).Where(d => d != null).ToArray() };
                }
            case "world-engine-spawn-move":
                {
                    string kind = Kind(); var session = SpawnEditSession.Get(Layer(kind).SessionId);
                    int uid = (int)N("uid", -1); float x = (float)N("x", 0), y = (float)N("y", 0), z = (float)N("z", 0);
                    var result = session.Move([(uid, x, y, z)]);
                    await _worldEngine.SendAsync("move-spawn", new { kind, uid, x, y, z });
                    await RefreshRowsAsync(kind);
                    return new { result, stats = session.Stats() };
                }
            case "world-engine-spawn-undo":
                {
                    string kind = Kind(); var session = SpawnEditSession.Get(Layer(kind).SessionId);
                    var result = session.Undo(); await PushWorldEngineSpawnsAsync(kind);
                    return new { result, stats = session.Stats() };
                }
            case "world-engine-spawn-save":
                {
                    string kind = Kind(); var session = SpawnEditSession.Get(Layer(kind).SessionId);
                    using var dialog = new FolderBrowserDialog { Description = "Elegí la carpeta donde crear el candidato de spawns (se crea una subcarpeta NUEVA; no se toca el original ni el runtime)", UseDescriptionForTitle = true };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    return session.SaveCandidate(Path.Combine(dialog.SelectedPath, $"spawn-edit-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}"));
                }
            default: return null;
        }
    }

    // Current (possibly edited) rows of one kind -> private JSON -> host markers for that kind.
    private async Task<int> PushWorldEngineSpawnsAsync(string kind)
    {
        if (!_worldEngineSpawnLayers.TryGetValue(kind, out var layer)) return 0;
        await RefreshRowsAsync(kind);
        if (!_worldEngine.Running || layer.Rows == null) return 0;
        string dir = Path.GetFullPath(Path.Combine(WorldEngineService.CacheRoot(), "..", "session"));
        string stem = Path.Combine(dir, $"spawns-{kind}-{Environment.ProcessId}-{Interlocked.Increment(ref _worldEnginePushes)}");
        ContentSafety.WriteJson(stem + ".json", SpawnReadService.ToViewerPayload(layer.Rows));
        var reply = await _worldEngine.SendAsync("spawns", new { file = stem + ".json", kind }, 60);
        foreach (string old in Directory.EnumerateFiles(dir, $"spawns-{kind}-{Environment.ProcessId}-*"))
            if (!old.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (IOException) { }
        return reply.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }
    private async Task PushAllWorldEngineSpawnsAsync() { foreach (string k in _worldEngineSpawnLayers.Keys.ToArray()) await PushWorldEngineSpawnsAsync(k); }

    private async Task RefreshRowsAsync(string kind)
    {
        var layer = _worldEngineSpawnLayers[kind]; var session = SpawnEditSession.Get(layer.SessionId);
        string dir = Path.GetFullPath(Path.Combine(WorldEngineService.CacheRoot(), "..", "session")); Directory.CreateDirectory(dir);
        string rdf = Path.Combine(dir, $"rows-{kind}-{Environment.ProcessId}.rdf");
        await File.WriteAllBytesAsync(rdf, session.CurrentBytes());
        layer.Rows = SpawnReadService.Load(rdf, kind);
    }

    private object? DescribeSpawn(string kind, int uid)
    {
        if (!_worldEngineSpawnLayers.TryGetValue(kind, out var layer)) return null;
        var row = layer.Rows?.Rows.FirstOrDefault(r => r.Index == uid);
        if (row == null) return null;
        var session = SpawnEditSession.Get(layer.SessionId);
        string? name = null; int? level = null;
        if (session.Catalog != null && session.Catalog.For(kind).TryGetValue(row.RefTblidx, out var entry)) { name = entry.Name; level = entry.Level; }
        return new { kind, uid, tblidx = row.Tblidx, refTblidx = row.RefTblidx, name, level, x = row.X, y = row.Y, z = row.Z, range = row.Range, quantity = row.Quantity, group = row.SpawnGroup, moveType = row.MoveType, wander = row.Wander, moveRange = row.MoveRange, coolTime = row.CoolTime };
    }

    private object DescribePick(JsonElement pick)
    {
        int? uid = pick.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : null;
        string kind = pick.TryGetProperty("kind", out var k) && k.GetString() == "npc" ? "npc" : "mob";
        double[]? terrain = pick.TryGetProperty("terrain", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
        bool shift = pick.TryGetProperty("shift", out var s) && s.ValueKind == JsonValueKind.True;
        return new { uid, kind, spawn = uid is int id ? DescribeSpawn(kind, id) : null, terrain, shift };
    }
}
