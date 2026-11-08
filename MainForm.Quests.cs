using System.Globalization;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private QuestDocument? _quest;
    private string? _questSaved;
    private bool _questFormDirty;
    private readonly Stack<string> _questUndo = new(), _questRedo = new();
    private bool ConfirmLeaveQuest() => !_questFormDirty && (_quest == null || _quest.Text == _questSaved) ||
        MessageBox.Show(this, "Hay cambios de quest sin exportar. ¿Descartarlos al continuar?", "Quests", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    private object QuestState() => _quest == null ? new { loaded = false } : (object)new
    {
        loaded = true, name = Path.GetFileName(_quest.SourcePath), source = _quest.SourcePath,
        hash = _quest.OriginalSha256, encoding = _quest.EncodingName, modified = _questFormDirty || _quest.Text != _questSaved,
        canMutate = _quest.CanMutate, nodes = _quest.Nodes, diagnostics = _quest.Diagnostics,
        undo = _questUndo.Count > 0, redo = _questRedo.Count > 0,
    };
    private void SetQuest(QuestDocument document, bool saved)
    {
        _quest = document; _questSaved = saved ? document.Text : null; _questFormDirty = false;
        _questUndo.Clear(); _questRedo.Clear();
    }
    private async Task<object?> QuestCommandAsync(string action, JsonElement args)
    {
        var result = await ExecuteQuestCommandAsync(action, args);
        return result is null ? null : JsonSerializer.SerializeToElement(result, ContentSafety.Json);
    }
    private async Task<object?> ExecuteQuestCommandAsync(string action, JsonElement args)
    {
        string S(string key) => args.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        uint NewId()
        {
            uint id = args.GetProperty("id").GetUInt32();
            if (id is 0 or > 65534) throw new ArgumentException("El ID nativo debe estar entre 1 y 65534.");
            return id;
        }
        switch (action)
        {
            case "quest-state": return QuestState();
            case "quest-dirty": _questFormDirty = !args.TryGetProperty("dirty", out var dirty) || dirty.GetBoolean(); return null;
            case "quest-open":
                if (!ConfirmLeaveQuest()) return null;
                using (var dialog = new OpenFileDialog { Filter = "Quest / trigger nativo|*.t", Title = "Abrir una quest para preparar una revisión" })
                {
                    string examples = Path.Combine(AppContext.BaseDirectory, "examples", "quests");
                    if (Directory.Exists(examples)) dialog.InitialDirectory = examples;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    SetQuest(await Task.Run(() => QuestDocument.Load(dialog.FileName)), true); return QuestState();
                }
            case "quest-new":
                if (!ConfirmLeaveQuest()) return null;
                uint id = NewId();
                string project = ContentSafety.SafeOutput(Path.Combine(AppPaths.Root, "quest-projects", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(project);
                SetQuest(QuestTemplates.NewSkeleton(id, Path.Combine(project, id.ToString(CultureInfo.InvariantCulture) + ".t")), false);
                return QuestState();
            case "quest-clone-template":
                if (!ConfirmLeaveQuest()) return null;
                uint cloneId = NewId();
                using (var source = new OpenFileDialog { Filter = "Quest / trigger nativo|*.t", Title = "Elegir la quest plantilla que se conservará" })
                using (var output = new FolderBrowserDialog { Description = "Carpeta donde crear el clon auditado" })
                {
                    if (source.ShowDialog(this) != DialogResult.OK || output.ShowDialog(this) != DialogResult.OK) return null;
                    string destination = Path.Combine(output.SelectedPath, "quest-clone-" + Guid.NewGuid().ToString("N"));
                    var created = await Task.Run(() => QuestTemplates.CloneAsNew(source.FileName, cloneId, destination));
                    var data = JsonSerializer.SerializeToElement(created, ContentSafety.Json);
                    SetQuest(QuestDocument.Load(data.GetProperty("candidate").GetString()!), true); return QuestState();
                }
            case "quest-source":
                if (_quest == null) throw new IOException("Abrí una quest.");
                return new { original = _quest.OriginalText, current = _quest.Text };
            case "quest-edit-many": case "quest-add":
                if (_quest == null) throw new IOException("Abrí una quest.");
                string before = _quest.Text;
                try
                {
                    if (action == "quest-edit-many")
                        foreach (var update in args.GetProperty("updates").EnumerateArray())
                            _quest.SetField(S("id"), update.GetProperty("key").GetString()!, update.GetProperty("literal").GetString()!);
                    else
                    {
                        var fields = args.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                        _quest.AddNode(S("parent"), S("kind"), fields);
                    }
                    _quest.Validate();
                    if (!_quest.CanMutate) throw new InvalidDataException("La edición produjo una estructura que no puede analizarse con seguridad.");
                }
                catch { _quest.ReplaceText(before); throw; }
                if (_quest.Text != before)
                {
                    _questUndo.Push(before); _questRedo.Clear();
                    if (_questUndo.Count > 40)
                    {
                        var recent = _questUndo.Take(40).Reverse().ToArray(); _questUndo.Clear();
                        foreach (string entry in recent) _questUndo.Push(entry);
                    }
                }
                _questFormDirty = false; return QuestState();
            case "quest-undo": case "quest-redo":
                if (_quest == null) throw new IOException("Abrí una quest.");
                var from = action == "quest-undo" ? _questUndo : _questRedo;
                var to = action == "quest-undo" ? _questRedo : _questUndo;
                if (from.Count > 0) { to.Push(_quest.Text); _quest.ReplaceText(from.Pop()); }
                _questFormDirty = false; return QuestState();
            case "quest-save":
                if (_quest == null) throw new IOException("Abrí una quest.");
                using (var output = new FolderBrowserDialog { Description = "Crear una revisión con original y auditoría; no se instala" })
                {
                    if (output.ShowDialog(this) != DialogResult.OK) return null;
                    string destination = Path.Combine(output.SelectedPath, "quest-revision-" + Guid.NewGuid().ToString("N"));
                    var saved = await Task.Run(() => _quest.SaveCandidate(destination)); _questSaved = _quest.Text;
                    return saved;
                }
            default: throw new ArgumentException("Comando de quests desconocido.");
        }
    }
}
