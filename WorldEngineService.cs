using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// World Editor over the real NTL engine (world-host.exe, NtlPresentation built with dNTL_WORLD_TOOL_MODE).
// Read-only on the client: worlds and the private cache come from the client's pack folder, which is only read.
// See WORLD-EDITOR-ENGINE.md.
internal static partial class WorldEngineService
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")] private static partial Regex WorldNameRegex();

    // Folders every world needs besides its own world\<name>\ tree (terrain textures, object meshes/textures, effects,
    // world meshes, properties and the world scripts: water/plant/weather/sound).
    private static readonly (string Pack, string Folder)[] SharedFolders =
    [
        ("scr", @"script\"), ("ter", @"world\mesh\"), ("obj", @"world\"), ("obj", @"object\"), ("obj", @"effect\"),
        ("tex", @"texture\ntlwe\"), ("tex", @"texture\object\"), ("tex", @"texture\effect\"), ("prop", @"property\"),
    ];

    internal static string CacheRoot()
    {
        string root = Environment.GetEnvironmentVariable("DBO_WORLD_CACHE") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBO Assets Studio", "World Editor", "cache", "base");
        root = Path.GetFullPath(root);
        GuardNotClient(root, "La caché del World Editor");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string PackDirectory(string? requested = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        if (Environment.GetEnvironmentVariable("DBO_WORLD_PACK") is { Length: > 0 } env) candidates.Add(env);
        if (StudioConfig.ClientRoot() is string client) candidates.Add(Path.Combine(client, "pack"));
        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(Path.Combine(full, "ter.pak"))) return full;
        }
        throw new DirectoryNotFoundException("No encontré la carpeta pack del cliente (ter.pak). Pasá --pack o definí DBO_WORLD_PACK.");
    }

    internal static string HostPath()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("DBO_WORLD_HOST") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "world-host", "world-host.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "world-host.exe"));
        if (UiRenderService.RepoRoot() is string repo) candidates.Add(Path.Combine(repo, "native", "bin", "world-host", "world-host.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "Falta world-host.exe. Compilalo con build-world-host.ps1 o definí DBO_WORLD_HOST.");
    }

    internal static void GuardNotClient(string path, string label)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        foreach (string forbidden in new[] { StudioConfig.ClientRoot() ?? "", PackDirectorySafe() })
        {
            if (string.IsNullOrEmpty(forbidden)) continue;
            string f = Path.GetFullPath(forbidden).TrimEnd('\\') + "\\";
            if (full.StartsWith(f, StringComparison.OrdinalIgnoreCase)) throw new IOException(label + " no puede estar dentro del cliente (" + f + ").");
        }
        if (UiRenderService.RepoRoot() is string repo)
        {
            string runtime = Path.Combine(repo, "runtime") + "\\";
            if (full.StartsWith(runtime, StringComparison.OrdinalIgnoreCase)) throw new IOException(label + " no puede estar dentro de runtime/.");
        }
    }
    private static string PackDirectorySafe() { try { return PackDirectory(); } catch { return ""; } }

    internal static string ValidWorld(string world)
    {
        if (!WorldNameRegex().IsMatch(world ?? "")) throw new InvalidDataException("Nombre de mundo inválido: " + world);
        return world!;
    }

    internal static bool IsCached(string world) => File.Exists(Path.Combine(CacheRoot(), "world", world, "#######.gwp")) && SharedReady();
    private static bool SharedReady() => File.Exists(Path.Combine(CacheRoot(), "shared.json"));

    /// <summary>All worlds in ter.pak with a world descriptor. "outdoor" = heightfield fields (wfif/wfpf) present.</summary>
    internal static async Task<object> ListWorldsAsync(string? pack = null)
    {
        string packDir = PackDirectory(pack);
        var archive = await PakArchive.OpenAsync(Path.Combine(packDir, "ter.pak"), "taiwan");
        var worlds = new Dictionary<string, (bool Gwp, int Fields, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            if (e.Status != "Disponible") continue;
            string[] parts = e.Name.Replace('/', '\\').TrimStart('.', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[0].Equals("world", StringComparison.OrdinalIgnoreCase) || !WorldNameRegex().IsMatch(parts[1])) continue;
            if (parts[1].Equals("mesh", StringComparison.OrdinalIgnoreCase)) continue;
            worlds.TryGetValue(parts[1], out var w);
            if (parts.Length == 3 && parts[2].Equals("#######.gwp", StringComparison.OrdinalIgnoreCase)) w.Gwp = true;
            if (parts.Length == 4 && parts[2].Equals("fields", StringComparison.OrdinalIgnoreCase) && parts[3].StartsWith("wfif", StringComparison.OrdinalIgnoreCase)) w.Fields++;
            if (parts.Length == 5 && parts[2].Equals("fields", StringComparison.OrdinalIgnoreCase) && parts[4].StartsWith("wfif", StringComparison.OrdinalIgnoreCase)) w.Fields++;
            w.Bytes += e.Size;
            worlds[parts[1]] = w;
        }
        var list = worlds.Where(kv => kv.Value.Gwp).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { world = kv.Key, outdoor = kv.Value.Fields > 0, fields = kv.Value.Fields, megabytes = Math.Round(kv.Value.Bytes / 1048576.0, 1), cached = IsCached(kv.Key) })
            .ToArray();
        return new { pack = packDir, cacheRoot = CacheRoot(), indexSha256 = archive.IndexHash, count = list.Length, worlds = list };
    }

    /// <summary>Extracts world\&lt;world&gt;\ (and the shared folders once) from the pack folder into the private cache. Incremental.</summary>
    internal static async Task<object> PrepareAsync(string world, string? pack = null, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        ValidWorld(world);
        string packDir = PackDirectory(pack), cache = CacheRoot();
        var report = new List<object>();
        var specs = new List<(string Pack, string Folder)> { ("ter", @"world\" + world + @"\") };
        if (!SharedReady()) specs.AddRange(SharedFolders);
        var archives = new Dictionary<string, PakArchive>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packName, folder) in specs)
        {
            cancel.ThrowIfCancellationRequested();
            if (!archives.TryGetValue(packName, out var archive))
                archives[packName] = archive = await PakArchive.OpenAsync(Path.Combine(packDir, packName + ".pak"), "taiwan");
            int extracted = 0, skipped = 0, failed = 0; long bytes = 0;
            string prefix = folder.ToLowerInvariant();
            // The index can list a name more than once (patch units); like the client, the last entry wins.
            var latest = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.Status != "Disponible") continue;
                string n = entry.Name.Replace('/', '\\'); if (n.StartsWith(@".\", StringComparison.Ordinal)) n = n[2..];
                if (n.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal)) latest[n] = entry;
            }
            var pending = new List<(PakEntry Entry, string Dst)>();
            foreach (var (rel, e) in latest)
            {
                if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { failed++; continue; }
                if (e.Size == 0) { failed++; continue; }   // known corrupt zero-size records (tex.pak): never shadow a real file with an empty one
                string dst = Path.GetFullPath(Path.Combine(cache, rel));
                if (!dst.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase)) { failed++; continue; }
                var fi = new FileInfo(dst);
                if (fi.Exists && fi.Length == e.Size) { skipped++; continue; }
                pending.Add((e, dst));
            }
            // Streaming read: ReadAsync re-hashes the whole index (ter.pak ≈ 66 MB) for every file, which made a
            // 2 000-file world take minutes; VisitPayloadsAsync verifies the index once per batch.
            var target = pending.ToDictionary(p => p.Entry, p => p.Dst);
            foreach (var chunk in pending.Select(p => p.Entry).Chunk(20000))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    await archive.VisitPayloadsAsync(chunk, async (entry, data) =>
                    {
                        string dst = target[entry];
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        await File.WriteAllBytesAsync(dst, data, cancel);
                        extracted++; bytes += data.Length;
                        if (extracted % 250 == 0) progress?.Report($"{folder}: {extracted} de {pending.Count} archivos");
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // Fall back to per-file reads so one bad record does not lose the whole batch.
                    foreach (var entry in chunk)
                    {
                        if (File.Exists(target[entry]) && new FileInfo(target[entry]).Length == entry.Size) continue;
                        try { byte[] data = await archive.ReadAsync(entry); Directory.CreateDirectory(Path.GetDirectoryName(target[entry])!); await File.WriteAllBytesAsync(target[entry], data, cancel); extracted++; bytes += data.Length; }
                        catch (Exception inner) when (inner is IOException or InvalidDataException) { failed++; progress?.Report($"{folder}: ilegible {entry.Name} ({inner.Message})"); }
                    }
                    progress?.Report($"{folder}: lectura en bloque falló ({ex.Message}); se reintentó archivo por archivo");
                }
            }
            report.Add(new { pack = packName, folder, extracted, skipped, failed, megabytes = Math.Round(bytes / 1048576.0, 1) });
            progress?.Report($"{folder}: listo ({extracted} nuevos, {skipped} ya estaban, {failed} ilegibles)");
        }
        if (!SharedReady())
            ContentSafety.WriteJson(Path.Combine(cache, "shared.json"), new { version = 1, createdUtc = DateTime.UtcNow, pack = packDir, folders = SharedFolders.Select(s => s.Pack + ":" + s.Folder).ToArray(), clientWritten = false });
        if (!File.Exists(Path.Combine(cache, "world", world, "#######.gwp"))) throw new InvalidDataException("El mundo " + world + " no tiene #######.gwp en ter.pak.");
        return new { world, cacheRoot = cache, pack = packDir, cached = true, steps = report, clientWritten = false };
    }

    /// <summary>Viewer payload of a spawn table (SpawnReadService) written as a private JSON file the host can load.</summary>
    internal static string SpawnPayloadFile(string spawnPath, string kind)
    {
        var file = SpawnReadService.Load(spawnPath, kind);
        string dir = Path.Combine(CacheRoot(), "..", "session");
        Directory.CreateDirectory(dir);
        string path = Path.GetFullPath(Path.Combine(dir, $"spawns-{kind}-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"));
        ContentSafety.WriteJson(path, SpawnReadService.ToViewerPayload(file));
        return path;
    }

    // ---- spawn tables of a map (server table folder, read-only) ----
    internal static string? ServerRdfFolder()
    {
        if (Environment.GetEnvironmentVariable("DBO_SERVER_TABLES") is { Length: > 0 } env && Directory.Exists(env)) return env;
        string? repo = UiRenderService.RepoRoot();
        var candidates = new List<string>();
        if (StudioConfig.ServerTables() is string configured) candidates.Add(configured);
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "Table_World_Data.rdf")) || File.Exists(Path.Combine(c, "table_world_data.rdf")));
    }

    internal static string? FindRdf(string folder, string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return null;
        string leaf = Path.GetFileNameWithoutExtension(tableName.Trim().Replace('/', '\\').Split('\\').Last());
        return Directory.EnumerateFiles(folder, "*.rdf").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(leaf, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>World-table rows whose resource folder is this world, with their mob/NPC spawn tables (read-only).</summary>
    internal static object SpawnTables(string world)
    {
        string folder = ServerRdfFolder() ?? throw new DirectoryNotFoundException("No encontré la carpeta de tablas del servidor (Table_World_Data.rdf). Configurá serverTables en studio.config.json (o DBO_SERVER_TABLES).");
        string worldTable = Directory.EnumerateFiles(folder, "*.rdf").First(f => Path.GetFileName(f).Equals("table_world_data.rdf", StringComparison.OrdinalIgnoreCase));
        var (doc, key) = RdfCli.Open(worldTable);
        string V(RdfRow row, string name) { var f = doc.Schema.Fields.FirstOrDefault(x => x.Name == name); return f == null ? "" : doc.Schema.Decode(f, row.Current).Trim(); }
        var rows = new List<object>();
        foreach (var row in doc.Rows)
        {
            // szName/wszName carry the world folder (DBO_Main_World); wszResourceFolder is the client map folder (Map_MainWorld\).
            string res = V(row, "wszResourceFolder").TrimEnd('\\', '/');
            if (!(V(row, "szName").Equals(world, StringComparison.OrdinalIgnoreCase) || V(row, "wszName").Equals(world, StringComparison.OrdinalIgnoreCase)
                  || res.Equals(world, StringComparison.OrdinalIgnoreCase))) continue;
            string mob = V(row, "wszMobSpawn_Table_Name"), npc = V(row, "wszNpcSpawn_Table_Name");
            rows.Add(new
            {
                worldId = doc.Schema.Decode(key, row.Current), name = V(row, "wszName"), internalName = V(row, "szName"),
                mobTable = mob, mobPath = FindRdf(folder, mob), npcTable = npc, npcPath = FindRdf(folder, npc),
            });
        }
        // Fallback by naming convention (spawn_mob_<world minus dbo_>.rdf) when the world table has no row.
        string stem = world.StartsWith("dbo_", StringComparison.OrdinalIgnoreCase) ? world[4..] : world;
        return new
        {
            world, tablesFolder = folder, rows,
            guess = new { mobPath = FindRdf(folder, "spawn_mob_" + stem), npcPath = FindRdf(folder, "spawn_npc_" + stem) },
        };
    }

}
