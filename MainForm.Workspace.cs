using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private StudioWorkspace? _workspace;
    private FileSystemWatcher? _workspaceWatch;
    private volatile bool _workspaceDirty = true;
    private object? _workspaceCachedStatus;
    private string? _lastSavedRdf;
    private string? _workspaceRdfHash;
    private (PakArchive Archive, PakEntry Entry)? _workspaceRdfOrigin;
    private void SetWorkspace(StudioWorkspace workspace)
    {
        _workspaceWatch?.Dispose(); _workspace = workspace; _workspaceDirty = true;
        _workspaceRdfHash = null; _wpsProjectHash = null;
        _workspaceWatch = new FileSystemWatcher(workspace.Work) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _workspaceWatch.Changed += (_, _) => _workspaceDirty = true; _workspaceWatch.Created += (_, _) => _workspaceDirty = true;
        _workspaceWatch.Deleted += (_, _) => _workspaceDirty = true; _workspaceWatch.Renamed += (_, _) => _workspaceDirty = true;
        _workspaceWatch.Error += (_, _) => _workspaceDirty = true; _workspaceWatch.EnableRaisingEvents = true;
        FormClosed += (_, _) => _workspaceWatch?.Dispose();
        string preferences = Path.Combine(AppPaths.Root, "workspace-current.json"); Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(preferences, JsonSerializer.Serialize(new { root = workspace.Root }));
    }
    private async Task<object?> WorkspaceCommandAsync(string action, JsonElement args)
    {
        string S(string n) => args.TryGetProperty(n, out var value) ? value.GetString() ?? "" : "";
        bool B(string n) => args.TryGetProperty(n, out var value) && value.ValueKind == JsonValueKind.True;
        if (action == "workspace-state")
        {
            if (_workspace == null)
            {
                string preferences = Path.Combine(AppPaths.Root, "workspace-current.json");
                if (File.Exists(preferences))
                    try { using var doc = JsonDocument.Parse(File.ReadAllText(preferences)); SetWorkspace(StudioWorkspace.Open(doc.RootElement.GetProperty("root").GetString()!)); }
                    catch (Exception ex) { StudioLog("Workspace unavailable: " + ex.Message); }
            }
            if (_workspace == null) return new { loaded = false };
            if (_workspaceDirty || B("scan")) { _workspaceDirty = false; try { _workspaceCachedStatus = await Task.Run(_workspace.Status); } catch { _workspaceDirty = true; throw; } }
            return _workspaceCachedStatus;
        }
        if (action == "workspace-pick-base")
        {
            using var dialog = new FolderBrowserDialog { Description = S("side") == "client" ? "Raíz del cliente (contiene pack). Se registrará como base de sólo lectura." : "Raíz del servidor (contiene resource). Se registrará como base de sólo lectura." };
            return dialog.ShowDialog(this) == DialogResult.OK ? new { path = dialog.SelectedPath } : null;
        }
        if (action == "workspace-create" || action == "workspace-open")
        {
            using var dialog = new FolderBrowserDialog { Description = action == "workspace-create" ? "Carpeta contenedora: se creará un proyecto nuevo dentro." : "Abrir proyecto (contiene work y .dbo-assets)." };
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            string selected = dialog.SelectedPath;
            if (action == "workspace-create")
            {
                string name = S("name"); if (string.IsNullOrWhiteSpace(name)) throw new IOException("Escribí el nombre del proyecto.");
                string folder = Regex.Replace(name, "[^a-zA-Z0-9_-]", "-").Trim('-'); if (folder.Length == 0) folder = "Proyecto";
                selected = Path.Combine(selected, folder + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                SetWorkspace(await Task.Run(() => StudioWorkspace.Create(selected, name, S("client"), S("server"))));
            }
            else SetWorkspace(StudioWorkspace.Open(selected));
            return await Task.Run(_workspace!.Status);
        }
        if (_workspace == null) throw new IOException("Creá o abrí un proyecto en Entregas.");
        _workspaceDirty = true;
        switch (action)
        {
            case "workspace-commit": return await Task.Run(() => _workspace.Commit(S("message")));
            case "workspace-restore": return await Task.Run(() => _workspace.Restore(S("revision")));
            case "workspace-export":
                using (var dialog = new SaveFileDialog { Filter = "Entrega DBO Assets ZIP|*.zip", FileName = "DBO Assets-GM-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return null; return await Task.Run(() => _workspace.Export(dialog.FileName)); }
            case "workspace-folder": Process.Start(new ProcessStartInfo(_workspace.Work) { UseShellExecute = true }); return null;
            case "workspace-import-pak": case "workspace-import-mob":
                using (var dialog = new FolderBrowserDialog { Description = action == "workspace-import-pak" ? "Candidato canónico PAK con audit.json y plan.json." : "Candidato de mob con manifest.json y client-paks." })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return null; return await Task.Run(() => action == "workspace-import-pak" ? _workspace.ImportPakAsync(dialog.SelectedPath, B("replace")) : _workspace.ImportMobAsync(dialog.SelectedPath, B("replace"))); }
            case "workspace-add":
                using (var dialog = new OpenFileDialog { Filter = "Contenido servidor|*.rdf;*.wps", Title = "Agregar revisión al proyecto" })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return null; return await Task.Run(() => _workspace.Adopt(dialog.FileName, S("destination"), B("replace"))); }
            default: throw new ArgumentException("Acción de proyecto desconocida.");
        }
    }
    private async Task<object?> TrackRdfRevisionAsync()
    {
        if (_workspace == null || _lastSavedRdf == null || _document == null) return null;
        if (_workspaceRdfOrigin is { } origin)
        {
            var current = _workspace.CurrentResource(origin.Archive, origin.Entry);
            if (current != null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)) != (_workspaceRdfHash ?? _document.OriginalHash)) throw new IOException("La tabla del proyecto cambió desde que se abrió. Reabrila desde Recursos antes de integrar esta revisión.");
            var op = new PakChange("replace", origin.Entry.Name, _lastSavedRdf, ContentSafety.Hash(_lastSavedRdf));
            string folder = Path.Combine(DefaultStagingFolder(), "workspace-rdf-" + Guid.NewGuid().ToString("N"));
            var plan = new PakPlan { Source = origin.Archive.IndexPath, SourceSha256 = origin.Archive.IndexHash, Prefix = origin.Archive.Prefix, Mode = "overlay", Operations = [op], Reason = "Edición RDF en Studio", Dependencies = [origin.Entry.Name] };
            await Task.Run(() => ContentPakService.ApplyAsync(plan, folder));
            var result = await Task.Run(() => _workspace.ImportPakAsync(folder, true)); _workspaceRdfHash = ContentSafety.Hash(_lastSavedRdf); _workspaceDirty = true; return result;
        }
        string? destination = _workspace.ServerDestination(_document.Path);
        if (destination == null) return new { tracked = false, reason = "Revisión guardada. En Entregas elegí su destino servidor para incluirla." };
        var adopted = await Task.Run(() => _workspace.Adopt(_lastSavedRdf, destination, true, _workspaceRdfHash ?? _document.OriginalHash)); _workspaceRdfHash = ContentSafety.Hash(_lastSavedRdf); _workspaceDirty = true; return adopted;
    }
}
