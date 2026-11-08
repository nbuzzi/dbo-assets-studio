using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private WpsReferenceCatalog? _wpsReferences;
    private WpsAssetLibrary? _wpsAssets;
    private string _wpsRdfPath = "", _wpsSpawnPath = "", _wpsAssetPath = "";
    private bool _wpsReferencesInitialized;
    private string _wpsReferencesError = "";
    private static string WpsReferenceSettingsPath => Path.Combine(AppPaths.Root, "wps-references.json");
    private async Task LoadWpsReferencesAsync(string rdf, string spawn)
    {
        var next = File.Exists(rdf)
            ? await WpsReferenceCatalog.LoadPakAsync(await PakArchive.OpenAsync(rdf), _schemas, string.IsNullOrEmpty(spawn) ? null : spawn)
            : await Task.Run(() => WpsReferenceCatalog.LoadFolder(rdf, _schemas, string.IsNullOrEmpty(spawn) ? null : spawn));
        _wpsReferences = next; _wpsRdfPath = rdf; _wpsSpawnPath = spawn; _wpsReferencesError = "";
    }
    private void SaveWpsReferenceSettings()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(WpsReferenceSettingsPath, JsonSerializer.Serialize(new { rdf = _wpsRdfPath, spawn = _wpsSpawnPath, assets = _wpsAssetPath }));
    }
    private async Task InitializeWpsReferencesAsync()
    {
        if (_wpsReferencesInitialized) return;
        _wpsReferencesInitialized = true;
        try
        {
            if (File.Exists(WpsReferenceSettingsPath))
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(WpsReferenceSettingsPath));
                _wpsRdfPath = json.RootElement.GetProperty("rdf").GetString() ?? "";
                _wpsSpawnPath = json.RootElement.GetProperty("spawn").GetString() ?? "";
                _wpsAssetPath = json.RootElement.GetProperty("assets").GetString() ?? "";
            }
            else if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "reference-data")))
            {
                _wpsRdfPath = Path.Combine(AppContext.BaseDirectory, "reference-data");
                if (Directory.Exists(Path.Combine(_wpsRdfPath, "spawns"))) _wpsSpawnPath = Path.Combine(_wpsRdfPath, "spawns");
            }
            if (_wpsRdfPath.Length > 0) await LoadWpsReferencesAsync(_wpsRdfPath, _wpsSpawnPath);
            if (_wpsAssetPath.Length > 0) _wpsAssets = await WpsAssetLibrary.OpenAsync(_wpsAssetPath);
        }
        catch (Exception ex) { _wpsReferencesError = "No se pudo restaurar una fuente: " + ex.Message; }
    }
    private object WpsReferencesState() => new
    {
        loaded = _wpsReferences != null, rdf = _wpsRdfPath, spawn = _wpsSpawnPath, assets = _wpsAssets?.Root ?? "",
        counts = _wpsReferences?.Rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()),
        worlds = _wpsReferences?.Rows.Where(r => r.Kind == "group" && r.Context != null).Select(r => r.Context!).Distinct().Order().ToArray() ?? [],
        notes = (_wpsReferences?.Notes ?? []).Concat(_wpsAssets?.Notes ?? []).Concat(_wpsReferencesError.Length > 0 ? [_wpsReferencesError] : []).ToArray(),
    };
    private async Task<object?> WpsReferenceCommandAsync(string action, JsonElement args)
    {
        string S(string n) => args.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
        int N(string n) => args.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
        await InitializeWpsReferencesAsync();
        switch (action)
        {
            case "wps-ref-state": return WpsReferencesState();
            case "wps-ref-configure":
                string mode = S("mode");
                if (mode == "pak")
                {
                    using var dialog = new OpenFileDialog { Filter = "Índice Taiwan de tablas|tbl.pak|Índice PAK Taiwan|*.pak", Title = "Vincular tablas del cliente" };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    await LoadWpsReferencesAsync(dialog.FileName, _wpsSpawnPath);
                }
                else
                {
                    using var dialog = new FolderBrowserDialog { Description = mode == "assets" ? "Copia del cliente Taiwan (o carpeta pack) con índices obj.pak y tex.pak y todas sus unidades" : mode == "spawn" ? "Carpeta table/rdf del servidor que contiene spawn_mob*.rdf; los grupos se separan por mundo" : "Carpeta de tablas RDF Taiwan: Item, Mob y World" };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    if (mode == "assets") { _wpsAssets = await WpsAssetLibrary.OpenAsync(dialog.SelectedPath); _wpsAssetPath = dialog.SelectedPath; }
                    else if (mode == "spawn")
                    {
                        if (_wpsRdfPath.Length == 0) throw new IOException("Vinculá primero las tablas RDF o tbl.pak.");
                        await LoadWpsReferencesAsync(_wpsRdfPath, dialog.SelectedPath);
                    }
                    else if (mode == "folder") await LoadWpsReferencesAsync(dialog.SelectedPath, _wpsSpawnPath);
                    else throw new ArgumentException("Fuente desconocida.");
                }
                SaveWpsReferenceSettings(); return WpsReferencesState();
            case "wps-ref-query":
                if (_wpsReferences == null) return new { rows = Array.Empty<object>(), more = false };
                string kind = S("kind"), world = S("world");
                if (kind == "group" && world.Length == 0) return new { rows = Array.Empty<object>(), more = false };
                var rows = _wpsReferences.Query(kind, S("query"), world.Length == 0 ? null : world, Math.Max(0, N("page")), 60);
                return new { rows = rows.Select(r => new { key = r.Key, kind = r.Kind, id = r.Id, name = r.Name, model = r.Model, icon = r.Icon, table = r.Table, source = r.Source, context = r.Context }), more = rows.Count == 60 };
            case "wps-ref-details":
                if (_wpsReferences == null) throw new IOException("Vinculá las tablas primero.");
                var row = _wpsReferences.Get(S("key"));
                return new { key = row.Key, kind = row.Kind, id = row.Id, name = row.Name, model = row.Model, icon = row.Icon, table = row.Table, source = row.Source, context = row.Context,
                    members = row.Members.Select(m =>
                    {
                        var mob = _wpsReferences.Rows.FirstOrDefault(r => r.Kind == "mob" && r.Id == m.MobId);
                        return new { mobId = m.MobId, quantity = m.Quantity, spawnRowId = m.SpawnRowId, name = mob?.Name, key = mob?.Key };
                    }), assets = _wpsAssets?.Find(row.Model, row.Icon) ?? [] };
            case "wps-ref-preview":
                if (_wpsAssets == null) throw new IOException("Vinculá la carpeta del cliente para visualizar sus recursos.");
                var (archive, entry) = _wpsAssets.Get(S("key"));
                byte[] data = await archive.ReadAsync(entry); _media.Clear();
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (ext != ".dff")
                {
                    using var bitmap = await Task.Run(() => DecodeImage(data, ext)); using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    return new { kind = "image", url = MediaUrl(stream.ToArray(), "image/png"), name = entry.Name };
                }
                byte[] mesh = await NativeMedia.ConvertAsync("model", data, ext); var model = StudioModel.Parse(mesh);
                var rawTextures = await _wpsAssets.ReadTexturesAsync(model); var textures = new Dictionary<string, string>();
                long pixelsLeft = 16_777_216;
                foreach (var texture in rawTextures)
                {
                    try
                    {
                        using var bitmap = await Task.Run(() => DecodeImage(texture.Value, _wpsAssets.TextureExtension(texture.Key)));
                        long pixels = (long)bitmap.Width * bitmap.Height; if (pixels > pixelsLeft) continue; pixelsLeft -= pixels;
                        using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); textures[texture.Key] = MediaUrl(stream.ToArray(), "image/png");
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { /* Explicitly reported as missing below. */ }
                }
                return new { kind = "model", url = MediaUrl(mesh, "application/octet-stream"), textures, name = entry.Name, missing = model.TextureNames.Where(n => !textures.ContainsKey(n)).ToArray() };
            default: throw new ArgumentException("Comando de referencias WPS desconocido.");
        }
    }
}
