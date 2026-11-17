using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DboAssetsStudio;

// UI Maker tab: edit .uilayout.json windows and preview them with the client's real GUI renderer.
internal sealed partial class MainForm
{
    private string? _uiLayoutPath;

    private static string UiExamplesDirectory()
    {
        foreach (string dir in new[] { Path.Combine(AppContext.BaseDirectory, "examples", "ui"), UiRenderService.RepoRoot() is string r ? Path.Combine(r, "examples", "ui") : "" })
            if (dir.Length > 0 && Directory.Exists(dir)) return dir;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private object UiMakerState(string path)
    {
        var layout = UiLayout.Load(path);
        _uiLayoutPath = layout.Path;
        var screen = layout.Root["screen"] as JsonObject;
        return new
        {
            path = layout.Path, name = layout.Name, text = File.ReadAllText(layout.Path), variants = layout.VariantNames(),
            width = (int?)screen?["width"] ?? 1280, height = (int?)screen?["height"] ?? 720,
            source = (string?)layout.Root["source"]?["frm"],
            status = UiRenderService.Status(layout.Path), hash = UiRenderService.LayoutHash(layout.Path),
        };
    }

    private async Task<object?> UiMakerCommandAsync(string action, JsonElement args)
    {
        string S(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int N(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string Path_() { string p = S("path"); if (p.Length == 0) p = _uiLayoutPath ?? throw new InvalidOperationException("Open a layout first."); return p; }
        switch (action)
        {
            case "ui-maker-open":
                using (var dialog = new OpenFileDialog { Filter = Loc.T("UI Maker layout|*.uilayout.json|JSON|*.json", "Layout UI Maker|*.uilayout.json|JSON|*.json"), Title = Loc.T("Open UI layout", "Abrir layout de UI"), InitialDirectory = UiExamplesDirectory() })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    return UiMakerState(dialog.FileName);
                }
            case "ui-maker-new":
                using (var dialog = new SaveFileDialog { Filter = Loc.T("UI Maker layout|*.uilayout.json", "Layout UI Maker|*.uilayout.json"), Title = Loc.T("Create UI layout", "Crear layout de UI"), FileName = "new-window.uilayout.json", InitialDirectory = UiExamplesDirectory(), OverwritePrompt = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                    string name = Path.GetFileName(dialog.FileName).Replace(".uilayout.json", "", StringComparison.OrdinalIgnoreCase);
                    await Task.Run(() => UiMakerCliInit(name, dialog.FileName));
                    return UiMakerState(dialog.FileName);
                }
            case "ui-maker-load": return UiMakerState(Path_());
            case "ui-maker-save":
            {
                string path = Path_(), text = S("text");
                // Validate before writing: parse, schema and a full resolve of every variant.
                string probe = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".check");
                await File.WriteAllTextAsync(probe, text);
                try { var l = UiLayout.Load(probe); foreach (var v in l.VariantNames()) l.Resolve(v); }
                finally { File.Delete(probe); }
                await File.WriteAllTextAsync(path, text);
                return UiMakerState(path);
            }
            case "ui-maker-render":
            {
                string path = Path_(), variant = S("variant") is { Length: > 0 } v ? v : "default";
                int w = N("width"), h = N("height");
                string png = Path.Combine(UiRenderService.CacheRoot, "studio", Guid.NewGuid().ToString("N") + ".png");
                var r = await UiRenderService.RenderAsync(path, variant, png, w > 0 ? w : null, h > 0 ? h : null, S("background") is { Length: > 0 } bg ? bg : null);
                var layout = UiLayout.Load(path);
                var scene = layout.Resolve(variant, w > 0 ? w : null, h > 0 ? h : null);
                var editable = new HashSet<string>();
                void Walk(JsonArray? a) { if (a == null) return; foreach (var n in a) if (n is JsonObject o) { if ((string?)o["id"] is string id && !id.Contains('{')) editable.Add(id); if ((string?)o["type"] != "repeat") Walk(o["children"] as JsonArray); } }
                Walk(layout.Root["components"] as JsonArray);
                byte[] bytes = await File.ReadAllBytesAsync(r.Png);
                try { File.Delete(r.Png); File.Delete(r.Metrics); } catch { }
                return new
                {
                    image = MediaUrl(bytes, "image/png"), width = r.Width, height = r.Height,
                    dialog = new { x = scene.DialogX, y = scene.DialogY, w = scene.DialogWidth, h = scene.DialogHeight },
                    components = scene.Components.Select(c => new { id = c.Id, type = c.Type, x = c.X, y = c.Y, w = c.Width, h = c.Height, parent = c.Parent, editable = editable.Contains(c.Id), text = c.Text, note = c.Note }),
                    issues = r.Issues.Select(i => new { severity = i.Severity, id = i.Id, code = i.Code, message = i.Message }),
                    hash = UiRenderService.LayoutHash(path),
                };
            }
            case "ui-maker-move":
            {
                // Nudge/resize a component that is written directly in the layout (not one generated by repeat).
                string path = Path_(), id = S("id");
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
                var comp = UiLayout.FindComponent(root["components"] as JsonArray, id) ?? throw new InvalidOperationException($"\"{id}\" is generated by repeat: edit the repeat or its children in the JSON.");
                if (comp["rect"] is not JsonArray rect || rect.Count != 4 || rect.Any(n => n is not JsonValue jv || !jv.TryGetValue<int>(out _))) throw new InvalidOperationException($"The rect of \"{id}\" uses expressions; edit it in the JSON.");
                int[] r = rect.Select(n => (int)n!).ToArray();
                r[0] += N("dx"); r[1] += N("dy"); r[2] = Math.Max(1, r[2] + N("dw")); r[3] = Math.Max(1, r[3] + N("dh"));
                comp["rect"] = new JsonArray(r[0], r[1], r[2], r[3]);
                await File.WriteAllTextAsync(path, UiLayout.Serialize(root));
                return UiMakerState(path);
            }
            case "ui-maker-review":
            {
                string path = Path_(); var layout = UiLayout.Load(path);
                string outDir = Path.Combine(layout.Directory, "review", $"{layout.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
                var result = await UiRenderService.ReviewAsync(path, outDir, null, null, null);
                Process.Start(new ProcessStartInfo(Path.Combine(outDir, "review.html")) { UseShellExecute = true });
                return result;
            }
            case "ui-maker-approve":
            {
                string path = Path_(), hash = S("hash");
                if (MessageBox.Show(this, Loc.T($"Approve layout {Path.GetFileName(path)} (hash {hash})?\n\nApproval enables generating the C++ code for the client. If the layout changes, the approval no longer applies.", $"¿Aprobar el layout {Path.GetFileName(path)} (hash {hash})?\n\nLa aprobación habilita generar el código C++ para el cliente. Si el layout cambia, la aprobación deja de valer."), Loc.T("UI Maker · Approve", "UI Maker · Aprobar"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return null;
                UiRenderService.Approve(path, hash, Environment.UserName, "Approved from Content Studio");
                return UiMakerState(path);
            }
            case "ui-maker-codegen":
            {
                string path = Path_(); var layout = UiLayout.Load(path);
                using var dialog = new SaveFileDialog { Filter = Loc.T("C++ header|*.h", "Header C++|*.h"), FileName = layout.Name.Replace("-", "") + "Layout.generated.h", Title = Loc.T("Generate code for the client", "Generar código para el cliente"), InitialDirectory = layout.Directory };
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                return UiCodegen.Generate(path, dialog.FileName, false);
            }
            case "ui-maker-catalog":
                return await UiFrmService.ListAsync(S("filter") is { Length: > 0 } f ? f : null);
            case "ui-maker-import":
            {
                string frm = S("frm"); if (frm.Length == 0) throw new ArgumentException("Choose a .frm window.");
                using var dialog = new SaveFileDialog { Filter = Loc.T("UI Maker layout|*.uilayout.json", "Layout UI Maker|*.uilayout.json"), FileName = Path.GetFileNameWithoutExtension(frm).ToLowerInvariant() + ".uilayout.json", Title = Loc.T("Save the imported window as a layout", "Guardar la ventana importada como layout"), InitialDirectory = UiExamplesDirectory(), OverwritePrompt = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                int w = N("width"), h = N("height");
                var result = await UiFrmService.ImportAsync(frm, dialog.FileName, S("dialog") is { Length: > 0 } d ? d : null, w > 0 ? w : 1280, h > 0 ? h : 720);
                return new { state = UiMakerState(dialog.FileName), import = result };
            }
            case "ui-maker-original":
            {
                // The window as the client builds it from its .frm/.srf (layouts imported with "source").
                string path = Path_(); var layout = UiLayout.Load(path);
                string? frm = (string?)layout.Root["source"]?["frm"] ?? throw new InvalidOperationException("This layout does not come from an imported .frm.");
                int w = N("width"), h = N("height");
                string png = Path.Combine(UiRenderService.CacheRoot, "studio", Guid.NewGuid().ToString("N") + ".png");
                var r = await UiFrmService.RenderFrmAsync(frm, png, w > 0 ? w : 1280, h > 0 ? h : 720, (string?)layout.Root["source"]?["dialog"], true);
                byte[] bytes = await File.ReadAllBytesAsync(r.Png);
                try { File.Delete(r.Png); File.Delete(r.Dump); } catch { }
                return new { image = MediaUrl(bytes, "image/png"), warnings = r.Warnings };
            }
            case "ui-maker-swf":
            {
                string png = Path.Combine(UiRenderService.CacheRoot, "studio", Guid.NewGuid().ToString("N") + ".png");
                int w = N("width"), h = N("height");
                double time = args.TryGetProperty("time", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetDouble() : 0;
                var r = await UiFrmService.RenderSwfAsync(S("name"), png, w > 0 ? w : 1024, h > 0 ? h : 768, args.TryGetProperty("frame", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : -1, time, null);
                byte[] bytes = await File.ReadAllBytesAsync(png);
                try { File.Delete(png); } catch { }
                return new { image = MediaUrl(bytes, "image/png"), info = r };
            }
            case "ui-maker-folder":
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(Path_())}\"") { UseShellExecute = true });
                return null;
            default: throw new ArgumentException("Unknown UI Maker action: " + action);
        }
    }

    private static void UiMakerCliInit(string name, string path) =>
        UiMakerCli.RunAsync(["init", name, path]).GetAwaiter().GetResult();
}
