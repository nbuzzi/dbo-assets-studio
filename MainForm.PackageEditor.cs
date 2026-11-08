using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private readonly List<PakChange> _pakEdits = [];
    private PakArchive? _editArchive;
    private async Task<object?> PackageCommandAsync(string action, JsonElement args)
    {
        string S(string key) => args.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        if (action == "pak-clear") { _pakEdits.Clear(); _editArchive = null; return null; }
        if (action == "pak-pending")
        {
            var selected = _selectedResourceKey == null ? _pak : ResolveModernPakResource(_selectedResourceKey).Archive;
            return new { source = _editArchive?.IndexPath ?? selected?.IndexPath, target = _editArchive?.Prefix ?? selected?.Prefix ?? "",
                targets = _packLibrary?.Archives.Select(a => a.Prefix).ToArray() ?? (_pak == null ? [] : new[] { _pak.Prefix }), operations = _pakEdits, count = _pakEdits.Count };
        }
        if (action == "rdf-export")
        {
            if (_document == null) throw new IOException("Abrí una tabla RDF.");
            if (_document.ModifiedRows > 0) throw new IOException("Guardá y abrí la revisión antes de exportar sus cambios.");
            using var dialog = new SaveFileDialog { Filter = "JSON|*.json|XML de intercambio|*.xml", FileName = Path.GetFileNameWithoutExtension(_document.Path) + ".json" };
            return dialog.ShowDialog(this) == DialogResult.OK ? await Task.Run(() => RdfCli.Run(["export", _document.Path, dialog.FileName])) : null;
        }
        if (action == "rdf-patch")
        {
            if (_document == null) throw new IOException("Abrí una tabla RDF.");
            if (_document.ModifiedRows > 0) throw new IOException("Guardá y abrí la revisión antes de aplicar operaciones por ID.");
            using var dialog = new OpenFileDialog { Filter = "Operaciones RDF JSON|*.json", Title = "Update, clone y delete por ID · Ver rdf-cli.md" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            string destination = Path.Combine(DefaultStagingFolder(), "rdf-operations-" + Guid.NewGuid().ToString("N")[..8]);
            object result = await Task.Run(() => RdfCli.Run(["apply", _document.Path, dialog.FileName, destination]));
            return new { output = destination, result };
        }
        PakArchive? archive = _pak;
        PakEntry? selectedEntry = null;
        if (_packLibrary != null)
        {
            if (action == "pak-queue" && S("op") != "add") (archive, selectedEntry) = ResolveModernPakResource(S("key"));
            else if (S("archive").Length > 0) archive = _packLibrary.Archives.SingleOrDefault(a => a.Prefix.Equals(S("archive"), StringComparison.OrdinalIgnoreCase));
            else archive = _editArchive ?? (_selectedResourceKey == null ? null : ResolveModernPakResource(_selectedResourceKey).Archive);
        }
        else if (action == "pak-queue" && S("op") != "add") selectedEntry = ResolveModernPakResource(S("key")).Entry;
        if (archive == null) throw new IOException("Elegí el PAK de destino en el selector del editor o seleccioná un recurso de la biblioteca.");
        if (_editArchive != null && _editArchive != archive) throw new IOException("Hay operaciones de otro PAK. Exportá su plan o descartalas antes de continuar.");
        if (S("archive").Length > 0 && !archive.Prefix.Equals(S("archive"), StringComparison.OrdinalIgnoreCase)) throw new IOException("El recurso seleccionado pertenece a otro PAK. Elegí ese PAK en el editor antes de reemplazar/quitar.");
        switch (action)
        {
            case "pak-queue":
                string op = S("op");
                string name = ContentSafety.ResourcePath(op == "add" ? S("path") : selectedEntry?.Name ?? throw new IOException("Seleccioná un recurso del PAK."));
                if (_pakEdits.Any(c => c.Path.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Ya hay una operación para esta ruta; quitála de la lista para cambiarla.");
                if (op == "add" && archive.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Esa ruta ya existe. Usá Reemplazar.");
                if (op is not ("add" or "replace" or "remove")) throw new IOException("Operación inválida.");
                string? file = null;
                if (op != "remove")
                {
                    using var dialog = new OpenFileDialog { Title = op == "add" ? "Elegir recurso para agregar" : "Elegir reemplazo", Filter = "Recursos|*.*" };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    file = dialog.FileName;
                }
                var change = await Task.Run(() => ContentCli.SealChanges([new(op, name, file)]).Single());
                _editArchive = archive; _pakEdits.Add(change); return new { count = _pakEdits.Count };
            case "pak-unqueue": _pakEdits.RemoveAll(c => c.Path == S("path")); if (_pakEdits.Count == 0) _editArchive = null; return null;
            case "pak-export-plan": case "pak-build":
                var plan = new PakPlan { Source = archive.IndexPath, SourceSha256 = archive.IndexHash, Format = archive.Format, Family = archive.Prefix, Prefix = archive.Prefix,
                    Mode = S("mode"), Operations = _pakEdits.ToList(), Reason = S("reason"), Dependencies = S("dependencies").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
                await Task.Run(() => ContentPakService.InspectAsync(plan));
                if (action == "pak-export-plan")
                {
                    using var dialog = new SaveFileDialog { Filter = "Plan JSON|*.json", FileName = archive.Prefix + "-plan.json" };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    ContentSafety.WriteJson(dialog.FileName, plan); return new { output = dialog.FileName };
                }
                using (var dialog = new FolderBrowserDialog { Description = "Carpeta contenedora: se creará un candidato nuevo dentro" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    string output = Path.Combine(dialog.SelectedPath, "assets-" + plan.Prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                    var result = await Task.Run(() => ContentPakService.ApplyAsync(plan, output));
                    if (_workspace != null) { await Task.Run(() => _workspace.ImportPakAsync(output)); _workspaceDirty = true; }
                    return result;
                }
            default: throw new IOException("Acción de paquete desconocida.");
        }
    }
}
