using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private WebView2? _browser;
    private readonly Dictionary<string, (byte[] data, string mime)> _media = [];
    private readonly SemaphoreSlim _webCommands = new(1, 1);
    private string[] _looseResources = [];
    private string _modernSource = "";
    private CancellationTokenSource? _modernPreview;
    private string? _selectedResource;
    private string? _selectedResourceKey;
    internal string[] InitialArguments { get; set; } = [];
    private static void StudioLog(string message)
    {
        try
        {
            string folder = AppPaths.Root;
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, "session.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
        }
        catch { /* Diagnostics must not interrupt a GM edit. */ }
    }
    private const string StudioOrigin = "https://studio.dbo-assets.local";

    private void InstallModernShell()
    {
        foreach (Control control in Controls) control.Visible = false;
        _browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(16, 18, 23) };
        Controls.Add(_browser); _browser.BringToFront();
        Shown += async (_, _) =>
        {
            try
            {
                string profile = Path.Combine(AppPaths.Root, "webview");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await _browser.EnsureCoreWebView2Async(environment);
                var core = _browser.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += ServeStudioResource;
                core.NavigationStarting += (_, e) => { if (e.Uri != StudioOrigin + "/index.html") e.Cancel = true; };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.WebMessageReceived += HandleStudioMessage;
                core.Navigate(StudioOrigin + "/index.html");
                StudioLog("WebView initialized " + environment.BrowserVersionString);
            }
            catch (Exception ex)
            {
                ShowError("Content Studio", "No se pudo iniciar la interfaz. Necesita Microsoft Edge WebView2 Runtime.\n\n" + ex.Message);
            }
        };
        FormClosed += (_, _) => { _modernPreview?.Cancel(); _modernPreview?.Dispose(); _media.Clear(); };
    }

    private void ServeStudioResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.GetLeftPart(UriPartial.Authority) != StudioOrigin) { e.Response = _browser!.CoreWebView2.Environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", ""); return; }
            string route = Uri.UnescapeDataString(uri.AbsolutePath);
            byte[] bytes; string mime;
            if (route.StartsWith("/media/"))
            {
                if (!_media.TryGetValue(route, out var media)) throw new FileNotFoundException();
                bytes = media.data; mime = media.mime;
            }
            else
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "web"));
                string path = Path.GetFullPath(Path.Combine(root, route.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException();
                bytes = File.ReadAllBytes(path);
                mime = Path.GetExtension(path).ToLowerInvariant() switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".wasm" => "application/wasm", ".json" => "application/json", _ => "application/octet-stream" };
            }
            string policy = "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self' blob:; worker-src 'self' blob:; object-src 'none'; frame-src 'none'";
            e.Response = _browser!.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nContent-Security-Policy: " + policy);
        }
        catch { e.Response = _browser!.CoreWebView2.Environment.CreateWebResourceResponse(Stream.Null, 404, "Not found", ""); }
    }

    private string MediaUrl(byte[] bytes, string mime)
    {
        string path = "/media/" + Guid.NewGuid().ToString("N"); _media[path] = (bytes, mime); return StudioOrigin + path;
    }

    private async void HandleStudioMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != StudioOrigin + "/index.html") return;
        string id = ""; bool locked = false;
        try
        {
            using var request = JsonDocument.Parse(e.WebMessageAsJson);
            var root = request.RootElement; id = root.GetProperty("id").GetString()!;
            string action = root.GetProperty("action").GetString()!;
            if (action != "preview" && action != "cancelPreview") { await _webCommands.WaitAsync(); locked = true; }
            var result = await StudioCommandAsync(action, root.GetProperty("args"));
            if (!IsDisposed) _browser!.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result }));
        }
        catch (OperationCanceledException) { if (!IsDisposed) _browser!.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, cancelled = true })); }
        catch (Exception ex) { string error = AppPaths.Redact(ex.Message); StudioLog("Command failed: " + error); if (!IsDisposed) _browser!.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error })); }
        finally { if (locked) _webCommands.Release(); }
    }

    private async Task<object?> StudioCommandAsync(string action, JsonElement args)
    {
        if (action.StartsWith("quest-")) return await QuestCommandAsync(action, args);
        if (action.StartsWith("wps-")) return await WpsCommandAsync(action, args);
        if (action.StartsWith("pak-") || action is "rdf-export" or "rdf-patch") return await PackageCommandAsync(action, args);
        string S(string name) => args.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        int N(string name) => args.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
        if (action.StartsWith("workspace-")) return await WorkspaceCommandAsync(action, args);
        switch (action)
        {
            case "diagnostic": StudioLog("UI " + S("message")[..Math.Min(S("message").Length, 2000)]); return null;
            case "ready":
                StudioLog("UI ready");
                if (InitialArguments.Length > 0 && Directory.Exists(InitialArguments[0])) await OpenPackLibraryAsync(InitialArguments[0]);
                if (InitialArguments.Length > 0 && File.Exists(InitialArguments[0]))
                {
                    string path = Path.GetFullPath(InitialArguments[0]);
                    if (Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase)) { _pak = await PakArchive.OpenAsync(path); _modernSource = path; _looseResources = []; }
                    else if (Path.GetExtension(path).Equals(".rdf", StringComparison.OrdinalIgnoreCase)) LoadRdfPath(path);
                    else { _looseResources = [path]; _modernSource = Path.GetDirectoryName(path)!; _pak = null; }
                    if (InitialArguments.Length > 2) _resourceCatalog = Directory.Exists(InitialArguments[2]) ? await Task.Run(() => ResourceCatalog.Load(InitialArguments[2], _schemas)) : await ResourceCatalog.LoadPakAsync(await PakArchive.OpenAsync(InitialArguments[2]), _schemas);
                }
                return new { workspace = _packLibrary == null && _document != null ? "tables" : "resources", source = _modernSource, initialResource = InitialArguments.Length > 1 ? InitialArguments[1] : null, version = "5.4", schemas = _schemas.Count };
            case "open":
                using (var dialog = new OpenFileDialog { Filter = "Contenido DBO y Meshy|*.pak;*.rdf;*.swf;*.tga;*.dds;*.png;*.jpg;*.bmp;*.dff;*.glb;*.wav;*.ogg;*.mp3|Todos los archivos|*.*", Title = "Abrir contenido de DBO" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    _modernPreview?.Cancel();
                    _selectedResourceKey = null; _packLibrary = null;
                    if (Path.GetExtension(dialog.FileName).Equals(".rdf", StringComparison.OrdinalIgnoreCase)) { _workspaceRdfOrigin = null; LoadRdfPath(dialog.FileName); return new { workspace = "tables", source = _modernSource }; }
                    if (Path.GetExtension(dialog.FileName).Equals(".pak", StringComparison.OrdinalIgnoreCase)) { var archive = await PakArchive.OpenAsync(dialog.FileName); _pak = archive; _looseResources = []; _modernSource = archive.IndexPath; _textureArchive = null; }
                    else { _pak = null; _looseResources = [dialog.FileName]; _modernSource = Path.GetDirectoryName(dialog.FileName)!; }
                    return new { workspace = "resources", source = _modernSource };
                }
            case "folder": case "packFolder":
                using (var dialog = new FolderBrowserDialog { Description = action == "packFolder" ? "Elegí la carpeta pack o una copia completa del cliente. Se abrirán juntos los índices y sus recursos." : "Carpeta de recursos. Si contiene índices PAK, se abre como biblioteca unificada." })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    if (action == "packFolder" || PackLibrary.HasIndices(dialog.SelectedPath)) return await OpenPackLibraryAsync(dialog.SelectedPath);
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    string[] paths = await Task.Run(() => Directory.EnumerateFiles(dialog.SelectedPath, "*", options).Take(100000).ToArray());
                    _modernPreview?.Cancel(); _pak = null; _packLibrary = null; _selectedResourceKey = null; _looseResources = paths; _modernSource = dialog.SelectedPath;
                    return new { source = _modernSource, capped = paths.Length == 100000 };
                }
            case "resources": return ModernResourceList(S("query"), S("kind"), S("folder"), N("page"), S("archive"));
            case "preview": return await ModernPreviewAsync(S("key"));
            case "cancelPreview": _modernPreview?.Cancel(); _media.Clear(); return null;
            case "references":
                if (S("mode") == "folder") await LoadResourceCatalogAsync(); else await LoadPakReferencesAsync();
                return new { count = _resourceCatalog?.Count ?? 0, notes = _resourceCatalog?.Notes ?? [], description = _selectedResource == null ? "" : _resourceCatalog?.Describe(_selectedResource) ?? "" };
            case "extract":
            case "editRdf":
                {
                    string key = S("key"); var owner = ResolveModernPakResource(key);
                    var (name, bytes) = await ReadModernResourceAsync(key);
                    string folder = Path.Combine(DefaultStagingFolder(), "pak-extract", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                    string target = Path.Combine(folder, Path.GetFileName(name)); await File.WriteAllBytesAsync(target, bytes);
                    if (action == "editRdf") { LoadRdfPath(target); if (_document?.Path == target) _workspaceRdfOrigin = owner.Archive != null && owner.Entry != null ? (owner.Archive, owner.Entry) : null; }
                    return new { path = target, workspace = action == "editRdf" ? "tables" : "resources" };
                }
            case "table": return ModernTablePage(S("query"), S("preset"), N("page"));
            case "edit":
                if (_document == null) throw new InvalidOperationException("Abrí una tabla RDF.");
                var row = _document.Rows[N("row")]; var field = _document.Schema.Fields.Single(f => f.Name == S("field") && !f.IsPadding);
                byte[] before = row.Current.ToArray(); _document.Schema.Encode(field, S("value"), row.Current);
                if (!before.AsSpan().SequenceEqual(row.Current)) { _undo.Push((row.Index, before, row.Current.ToArray())); _redo.Clear(); }
                return new { value = _document.Schema.Decode(field, row.Current), modified = _document.ModifiedRows };
            case "undo": UndoEdit(); return null;
            case "redo": RedoEdit(); return null;
            case "discard": RevertEdits(); return null;
            case "save": _lastSavedRdf = null; SaveCopy(); var tracked = await TrackRdfRevisionAsync(); return new { tracked, saved = _lastSavedRdf != null && _savedHash != null && _document != null && _savedHash == Convert.ToHexString(SHA256.HashData(_document.BuildBytes())) };
            case "changes":
                RefreshChanges(); return new { count = _changes.Rows.Count, rows = _changes.Rows.Cast<DataGridViewRow>().Take(2000).Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToArray()).ToArray() };
            case "validate":
                ValidateDocument(false); return _validationGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToArray()).ToArray();
            case "revisions":
                string staging = DefaultStagingFolder(); Directory.CreateDirectory(staging); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(staging) { UseShellExecute = true }); return null;
            case "screenshot":
                using (var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = "Assets-Studio.png" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    using var file = File.Create(dialog.FileName); await _browser!.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file); return dialog.FileName;
                }
            default: throw new InvalidOperationException("Acción desconocida.");
        }
    }

    private static string ResourceKind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".swf" => "flash", ".tga" or ".dds" or ".png" or ".jpg" or ".jpeg" or ".bmp" => "image", ".dff" or ".glb" => "model",
        ".wav" or ".ogg" or ".mp3" => "audio", ".rdf" => "table", ".anm" or ".uva" => "animation", _ => "other"
    };
    private object ModernResourceList(string query, string kind, string folder, int page, string archiveFilter = "")
    {
        var entries = _packLibrary != null ? _packLibrary.Entries.Select(e => new { key = e.Key, name = e.Name, size = (long)e.Entry.Size, status = e.Entry.Status, archive = e.Archive.Prefix })
            : _pak != null ? _pak.Entries.Select(e => new { key = e.Number.ToString(), name = e.Name, size = (long)e.Size, status = e.Status, archive = _pak.Prefix })
            : _looseResources.Select((p, i) => new { key = i.ToString(), name = Path.GetRelativePath(_modernSource, p), size = new FileInfo(p).Length, status = "Disponible", archive = "" });
        var all = entries.ToArray();
        var filtered = all.Where(e => (archiveFilter.Length == 0 || e.archive.Equals(archiveFilter, StringComparison.OrdinalIgnoreCase)) && e.name.Contains(query, StringComparison.OrdinalIgnoreCase) && (kind.Length == 0 || ResourceKind(e.name) == kind) && (folder.Length == 0 || e.name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (filtered.Length - 1) / 80));
        return new { source = _modernSource, archive = _pak != null || _packLibrary != null, library = _packLibrary != null, total = all.Length, matched = filtered.Length, page,
            archives = _packLibrary?.Archives.Select(a => a.Prefix).ToArray() ?? [], notes = _packLibrary?.Notes ?? [],
            counts = all.GroupBy(e => ResourceKind(e.name)).ToDictionary(g => g.Key, g => g.Count()),
            folders = all.Select(e => Path.GetDirectoryName(e.name) ?? "").Distinct().Order().Take(500).ToArray(),
            items = filtered.Skip(page * 80).Take(80).Select(e => new { e.key, e.name, e.size, e.status, e.archive, kind = ResourceKind(e.name), extension = Path.GetExtension(e.name).TrimStart('.').ToUpperInvariant() }).ToArray() };
    }
    private async Task<(string name, byte[] bytes)> ReadModernResourceAsync(string key)
    {
        var (archive, entry) = ResolveModernPakResource(key);
        if (archive != null && entry != null) return (entry.Name, _workspace?.CurrentResource(archive, entry) ?? await archive.ReadAsync(entry));
        if (!int.TryParse(key, out int n) || n < 0 || n >= _looseResources.Length) throw new InvalidDataException("Selección de archivo inválida.");
        string path = _looseResources[n]; if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new IOException("Recurso mayor a 128 MiB.");
        return (Path.GetRelativePath(_modernSource, path), await File.ReadAllBytesAsync(path));
    }
    private async Task<object> ModernPreviewAsync(string key)
    {
        _modernPreview?.Cancel(); _modernPreview?.Dispose(); var cancellation = _modernPreview = new CancellationTokenSource();
        var token = cancellation.Token; _media.Clear(); var archive = ResolveModernPakResource(key).Archive;
        var (name, bytes) = await ReadModernResourceAsync(key); token.ThrowIfCancellationRequested(); _selectedResource = name; _selectedResourceKey = key;
        string extension = Path.GetExtension(name).ToLowerInvariant(), kind = ResourceKind(name);
        var result = new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["bytes"] = bytes.Length, ["hash"] = Convert.ToHexString(SHA256.HashData(bytes)), ["references"] = _resourceCatalog?.Describe(name), ["extension"] = extension.TrimStart('.').ToUpperInvariant(), ["archive"] = archive?.IndexPath };
        if (kind == "image")
        {
            using var bitmap = await Task.Run(() => DecodeImage(bytes, extension), token); token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            result["width"] = bitmap.Width; result["height"] = bitmap.Height; result["url"] = MediaUrl(stream.ToArray(), "image/png");
        }
        else if (kind == "flash")
        {
            if (bytes.Length < 8 || !(Encoding.ASCII.GetString(bytes, 0, 3) is "FWS" or "CWS" or "ZWS")) throw new InvalidDataException("El recurso no tiene una cabecera SWF válida.");
            if (BitConverter.ToUInt32(bytes, 4) > 128L * 1024 * 1024) throw new InvalidDataException("SWF descomprimido mayor a 128 MiB.");
            var adapted = await SwfPreviewAdapter.PrepareAsync(bytes, async file =>
            {
                token.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".tga" or ".dds" or ".png" or ".jpg" or ".bmp")) return null;
                if (Path.IsPathRooted(file) || file.Split('\\', '/').Any(p => p == "..")) return null;
                string relative = Path.Combine(Path.GetDirectoryName(name) ?? "", file).Replace('/', '\\');
                byte[] imageBytes;
                if (archive != null)
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(relative, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;
                    imageBytes = await archive.ReadAsync(entry);
                }
                else
                {
                    string path = Path.GetFullPath(Path.Combine(_modernSource, relative));
                    if (!path.StartsWith(Path.GetFullPath(_modernSource) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > 128L * 1024 * 1024) return null;
                    imageBytes = await File.ReadAllBytesAsync(path, token);
                }
                return await Task.Run(() => DecodeImage(imageBytes, extension), token);
            }, token);
            token.ThrowIfCancellationRequested();
            result["embeddedImages"] = adapted.EmbeddedImages; result["missingImages"] = adapted.MissingImages; result["unsupportedTags"] = adapted.UnsupportedTags;
            result["version"] = bytes[3]; result["url"] = MediaUrl(adapted.Bytes, "application/x-shockwave-flash");
        }
        else if (kind == "audio") result["url"] = MediaUrl(await NativeMedia.ConvertAsync("audio", bytes, extension, token), "audio/wav");
        else if (kind == "model")
        {
            if (extension == ".glb")
            {
                var inspection = await Task.Run(() => GlbInspection.ReadBytes(bytes), token); token.ThrowIfCancellationRequested();
                result["format"] = "glb"; result["inspection"] = inspection; result["vertices"] = inspection.Vertices; result["url"] = MediaUrl(bytes, "model/gltf-binary");
                return result;
            }
            byte[] mesh = await NativeMedia.ConvertAsync("model", bytes, extension, token); var model = await Task.Run(() => StudioModel.Parse(mesh), token);
            var textures = archive == null ? new Dictionary<string, byte[]>() : await ReadModelTexturesAsync(model, archive, token);
            token.ThrowIfCancellationRequested(); result["url"] = MediaUrl(mesh, "application/octet-stream");
            result["textures"] = textures.ToDictionary(p => p.Key, p => MediaUrl(p.Value, "image/png"));
            result["vertices"] = model.VertexCount; result["triangles"] = model.TriangleCount;
            result["missing"] = model.TextureNames.Where(n => !textures.ContainsKey(n)).ToArray();
        }
        else result["text"] = extension is ".txt" or ".xml" or ".srf" or ".frm" or ".ini" or ".json" ? Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(32000, bytes.Length))) : Convert.ToHexString(bytes.AsSpan(0, Math.Min(512, bytes.Length)));
        token.ThrowIfCancellationRequested(); StudioLog("Prepared " + kind + " " + name); return result;
    }
    private object ModernTablePage(string query, string preset, int page)
    {
        if (_document == null) return new { loaded = false };
        string[] terms = preset switch { "shop" => ["cost", "cash", "price", "sale", "stack", "item", "category"], "combat" => ["attack", "defence", "offence", "level", "need", "rank"], "models" => ["model", "icon", "name"], _ => [] };
        var fields = _document.Schema.Fields.Where(f => !f.IsPadding && (terms.Length == 0 || f.Name is "tblidx" or "wszNameText" || terms.Any(t => f.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToArray();
        var rows = _document.Rows.Where(r => query.Length == 0 || _document.Schema.Fields.Where(f => !f.IsPadding).Any(f => _document.Schema.Decode(f, r.Current).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (rows.Length - 1) / 60));
        return new { loaded = true, source = _document.Path, name = Path.GetFileName(_document.Path), schema = _document.Schema.Name, rowSize = _document.Schema.RowSize,
            total = _document.Rows.Count, matched = rows.Length, modified = _document.ModifiedRows, page, undo = _undo.Count, redo = _redo.Count,
            fields = fields.Select(f => new { f.Name, f.Type, f.Count }),
            rows = rows.Skip(page * 60).Take(60).Select(r => new { index = r.Index, modified = r.Modified, values = fields.Select(f => _document.Schema.Decode(f, r.Current)).ToArray() }) };
    }
}
