using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private WpsDocument? _wps;
    private readonly Stack<string> _wpsUndo = new();
    private readonly Stack<string> _wpsRedo = new();
    private string? _wpsSavedText;
    private string? _wpsProjectHash;
    private bool ConfirmLeaveWps() => _wps == null || _wps.Text == _wpsSavedText || MessageBox.Show(this, "Hay cambios WPS sin exportar. ¿Descartarlos al salir?", "Editor WPS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    private object WpsState() => _wps == null ? new { loaded = false } : (object)new
    {
        loaded = true, dialect = _wps.Dialect, name = Path.GetFileName(_wps.SourcePath), source = _wps.SourcePath,
        hash = _wps.OriginalSha256, encoding = _wps.EncodingName, modified = _wps.Modified,
        canMutate = _wps.CanMutate, nodes = _wps.Nodes.Select(n => new { n.Id, n.Kind, n.Name, n.Stage, n.Line, n.Params, children = n.Children.Select(c => c.Id).ToArray() }), diagnostics = _wps.Diagnostics,
        undo = _wpsUndo.Count > 0, redo = _wpsRedo.Count > 0,
    };
    private async Task<object?> WpsCommandAsync(string action, JsonElement args)
    {
        if (action.StartsWith("wps-ref-")) return await WpsReferenceCommandAsync(action, args);
        string S(string n) => args.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
        int N(string n) => args.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
        switch (action)
        {
            case "wps-state": return WpsState();
            case "wps-catalog": return JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, (S("dialect") == "tqs" ? "tqs" : "wps") + "-catalog.json")));
            case "wps-open":
                if (!ConfirmLeaveWps()) return null;
                using (var dialog = new OpenFileDialog { Filter = "Scripts de dungeon y TMQ|*.wps;*.tqs|World Play Script|*.wps|Time Quest Script|*.tqs", Title = "Abrir WPS / TQS para preparar una revisión" })
                {
                    string sample = Path.Combine(AppContext.BaseDirectory, "examples", S("sample") == "tmq" ? "00002.tqs" : "83000.wps");
                    if (S("sample") is "cc" or "tmq" && File.Exists(sample)) dialog.FileName = sample;
                    else if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "examples"))) dialog.InitialDirectory = Path.Combine(AppContext.BaseDirectory, "examples");
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    _wps = await Task.Run(() => Path.GetExtension(dialog.FileName).Equals(".tqs", StringComparison.OrdinalIgnoreCase) ? WpsDocument.LoadTqs(dialog.FileName) : WpsDocument.Load(dialog.FileName)); _wpsProjectHash = null; _wpsSavedText = _wps.Text; _wpsUndo.Clear(); _wpsRedo.Clear(); return WpsState();
                }
            case "wps-new":
                if (!ConfirmLeaveWps()) return null;
                int scriptId = N("scriptId"); if (scriptId < 1) throw new ArgumentException("Elegí un ID de script positivo y libre en el servidor.");
                string project = ContentSafety.SafeOutput(Path.Combine(AppPaths.Root, "wps-projects", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(project);
                if (S("dialect") == "tqs") _wps = TqsTemplates.Create(scriptId, Path.Combine(project, scriptId.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".tqs"));
                else
                {
                    if (S("dialect") is not ("" or "wps")) throw new ArgumentException("Formato esperado: WPS o TQS.");
                    string newFile = Path.Combine(project, scriptId + ".wps");
                    await File.WriteAllTextAsync(newFile, "-- DBO Assets WPS project. Confirm script ID and WorldPlay references before deployment.\r\nGameStage( 0 )\r\nEnd()\r\n\r\nGameFailed()\r\nEnd()\r\n");
                    _wps = WpsDocument.Load(newFile);
                }
                _wpsProjectHash = null; _wpsSavedText = _wps.Text; _wpsUndo.Clear(); _wpsRedo.Clear(); return WpsState();
            case "wps-source":
                if (_wps == null) throw new IOException("Abrí un WPS.");
                return new { original = _wps.OriginalText, current = _wps.Text };
            case "wps-save":
                if (_wps == null) throw new IOException("Abrí un WPS.");
                using (var dialog = new FolderBrowserDialog { Description = "Carpeta donde crear una revisión WPS nueva. El original se conserva." })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    string destination = Path.Combine(dialog.SelectedPath, "wps-revision-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..5]);
                    var result = await Task.Run(() => _wps.SaveCandidate(destination)); _wpsSavedText = _wps.Text;
                    if (_workspace != null) {
                        var data = JsonSerializer.SerializeToElement(result); string file = data.GetProperty("candidate").GetString()!;
                        string target = _workspace.ServerDestination(_wps.SourcePath) ?? (_wps.Dialect == "tqs" ? "server/resource/server_data/tqs/tqs/" : "server/resource/server_data/wps/wps/") + Path.GetFileName(_wps.SourcePath);
                        await Task.Run(() => _workspace.Adopt(file, target, true, _wpsProjectHash ?? _wps.OriginalSha256)); _wpsProjectHash = ContentSafety.Hash(file); _workspaceDirty = true;
                    }
                    return result;
                }
            case "wps-edit": case "wps-edit-many": case "wps-clone-floor": case "wps-clone": case "wps-remove": case "wps-move": case "wps-add": case "wps-template": case "wps-add-stage":
                if (_wps == null) throw new IOException("Abrí un WPS.");
                string before = _wps.Text;
                try { switch (action)
                {
                    case "wps-add-stage":
                        string rootKind = S("kind") is "" ? "GameStage" : S("kind");
                        if (rootKind is not ("GameStage" or "GameBegin" or "GameEnd" or "GameFailed")) throw new ArgumentException("Sección raíz no permitida.");
                        _wps.AddNode(null, rootKind, N("stage").ToString(System.Globalization.CultureInfo.InvariantCulture), new()); break;
                    case "wps-edit": _wps.SetParam(S("id"), S("key"), S("literal")); break;
                    case "wps-edit-many": foreach (var update in args.GetProperty("updates").EnumerateArray()) _wps.SetParam(S("id"), update.GetProperty("key").GetString()!, update.GetProperty("literal").GetString()!); break;
                    case "wps-clone-floor": _wps.CloneCcFloor(N("source"), N("target")); break;
                    case "wps-clone": _wps.CloneNode(S("id")); break;
                    case "wps-remove": _wps.RemoveNode(S("id")); break;
                    case "wps-move": _wps.MoveNode(S("id"), N("direction")); break;
                    case "wps-add":
                        var parameters = new Dictionary<string, string>();
                        foreach (var p in args.GetProperty("parameters").EnumerateObject()) parameters.Add(p.Name, p.Value.GetString()!);
                        _wps.AddNode(string.IsNullOrEmpty(S("parent")) ? null : S("parent"), S("kind"), S("name"), parameters); break;
                    case "wps-template":
                        int amount = N("value"); if (amount < 0) throw new ArgumentException("Usá un valor no negativo.");
                        if (S("template") == "spawn-group") _wps.AddNode(S("parent"), "Action", "add mobgroup", new() { ["group"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                        else if (S("template") is "wait-time" or "wait-group")
                        {
                            string waitId = _wps.AddNode(S("parent"), "Action", "wait", new());
                            _wps.AddNode(waitId, "Condition", S("template") == "wait-time" ? "check time" : "check mobgroup", S("template") == "wait-time" ? new() { ["time"] = amount.ToString() } : new() { ["group"] = amount.ToString(), ["count"] = "0" });
                        }
                        else throw new ArgumentException("Plantilla desconocida.");
                        break;
                } } catch { _wps.ReplaceText(before); throw; }
                if (before != _wps.Text)
                {
                    _wpsUndo.Push(before); _wpsRedo.Clear();
                    if (_wpsUndo.Count > 40) { var latest = _wpsUndo.Take(40).Reverse().ToArray(); _wpsUndo.Clear(); foreach (var entry in latest) _wpsUndo.Push(entry); }
                }
                return WpsState();
            case "wps-undo": case "wps-redo":
                if (_wps == null) throw new IOException("Abrí un WPS.");
                var from = action == "wps-undo" ? _wpsUndo : _wpsRedo; var to = action == "wps-undo" ? _wpsRedo : _wpsUndo;
                if (from.Count > 0) { to.Push(_wps.Text); _wps.ReplaceText(from.Pop()); } return WpsState();
            default: throw new ArgumentException("Acción WPS desconocida.");
        }
    }
}
