using System.Text.Json;

namespace DboAssetsStudio;

// World Editor · edit mode (workspace) actions shared by every tool module (terrain, objects, water, ...).
// Tool UIs talk to the host through "world-engine-host" with a whitelisted command family, so a new tool usually
// needs only its native header + its web module. Saving writes into the private workspace; exporting creates a
// NEW candidate folder with only the files that differ from the base cache (audit.json). Nothing is installed.
internal sealed partial class MainForm
{
    private WorldEngineWorkspace.Workspace? _worldEngineWorkspace;
    private readonly HashSet<int> _worldEngineEditedFields = [];

    // Host command families tool modules may call directly (anything else needs its own C# action).
    private static readonly string[] WorldEngineHostCommandPrefixes =
        ["tool", "terrain-", "height-at", "alpha-stats", "undo", "edit-state", "objects-", "object-", "water-", "light-", "effect-", "sound-", "prop-", "pvs-", "nav-"];

    private async Task<object?> WorldEngineEditCommandAsync(string action, JsonElement args)
    {
        string S(string name, string fallback = "") => args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()) : fallback;
        switch (action)
        {
            case "world-engine-host":
                {
                    string cmd = S("cmd");
                    if (!WorldEngineHostCommandPrefixes.Any(p => cmd.Equals(p, StringComparison.Ordinal) || (p.EndsWith('-') && cmd.StartsWith(p, StringComparison.Ordinal))))
                        throw new InvalidDataException("Engine command not allowed from the UI: " + cmd);
                    var values = args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new Dictionary<string, JsonElement>();
                    var reply = await _worldEngine.SendAsync(cmd, values, 60);
                    return reply;
                }
            case "world-engine-edit-state":
                return new { workspace = _worldEngineWorkspace?.Id, workspaceRoot = _worldEngineWorkspace?.Root, editedFields = _worldEngineEditedFields.OrderBy(x => x).ToArray(), prepare = _worldEnginePrepareNote };
            case "world-engine-save":
                {
                    if (_worldEngineWorkspace == null) throw new InvalidOperationException("Open the world in edit mode (workspace) to save.");
                    var saved = await _worldEngine.SendAsync("save", null, 120);
                    if (saved.TryGetProperty("editedFields", out var ef)) foreach (var f in ef.EnumerateArray()) _worldEngineEditedFields.Add(f.GetInt32());
                    return saved;
                }
            case "world-engine-export":
                {
                    if (_worldEngineWorkspace == null) throw new InvalidOperationException("No edit workspace is open.");
                    var saved = await _worldEngine.SendAsync("save", null, 120);
                    if (saved.TryGetProperty("editedFields", out var ef)) foreach (var f in ef.EnumerateArray()) _worldEngineEditedFields.Add(f.GetInt32());
                    using var dialog = new FolderBrowserDialog { Description = Loc.T("Choose where to create the world candidate (a NEW subfolder is created; never the client or runtime)", "Elegí dónde crear el candidato del mundo (se crea una subcarpeta NUEVA; nunca el cliente ni runtime)"), UseDescriptionForTitle = true };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    string target = Path.Combine(dialog.SelectedPath, $"world-edit-{_worldEngineWorkspace.World}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    string evidence = Path.Combine(_worldEngineWorkspace.Root, "captures"); Directory.CreateDirectory(evidence);
                    string after = Path.Combine(evidence, $"after-{DateTime.Now:HHmmssfff}.png");
                    await _worldEngine.SendAsync("capture", new { path = after });
                    var extra = new Dictionary<string, string> { ["evidence/after.png"] = after };
                    string before = Path.Combine(evidence, "before.png"); if (File.Exists(before)) extra["evidence/before.png"] = before;
                    return WorldEngineWorkspace.ExportCandidate(_worldEngineWorkspace, target, _worldEngineEditedFields, extra, new { source = "studio" });
                }
            case "world-engine-before":
                {   // evidence: capture the untouched state right after opening a workspace
                    if (_worldEngineWorkspace == null) return null;
                    string evidence = Path.Combine(_worldEngineWorkspace.Root, "captures"); Directory.CreateDirectory(evidence);
                    string before = Path.Combine(evidence, "before.png");
                    if (!File.Exists(before)) await _worldEngine.SendAsync("capture", new { path = before });
                    return new { path = before };
                }
            case "world-engine-textures":
                {
                    string dir = Path.Combine(WorldEngineService.CacheRoot(), "texture", "ntlwe");
                    var names = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.dds").Select(Path.GetFileNameWithoutExtension).Where(n => n != null)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray() : [];
                    return new { count = names.Length, textures = names };
                }
            default: throw new InvalidDataException("Unknown engine edit action.");
        }
    }
}
