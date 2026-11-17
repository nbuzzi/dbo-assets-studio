using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Client GUI scripts (.frm/.srf/.rsr) and Flash (.swf) for UI Maker:
// render the original window with NtlGui, render Flash with GFx, import a window into a layout.
internal static class UiFrmService
{
    // ---------- client GUI root: gui.pak + flash.pak extracted, fonts, gui textures on demand ----------
    static readonly SemaphoreSlim RootLock = new(1, 1);

    public static string PakTool()
    {
        foreach (string p in new[] { Path.Combine(AppContext.BaseDirectory, "pak-tool.exe"), UiRenderService.RepoRoot() is string r ? Path.Combine(r, "native", "bin", "pak-tool.exe") : "" })
            if (p.Length > 0 && File.Exists(p)) return p;
        throw new FileNotFoundException("pak-tool.exe is missing next to the Studio/CLI.");
    }

    static async Task<string> RunAsync(string exe, IEnumerable<string> args, string? cwd = null, int timeoutSeconds = 300, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (Path.GetFileName(exe).Equals("pak-tool.exe", StringComparison.OrdinalIgnoreCase)) StudioConfig.ApplyPakKey(psi);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException(Path.GetFileName(exe) + " did not finish."); }
        if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {(await e).Trim()} {(await o).Trim()}");
        return await o;
    }

    public static async Task<string> GuiRootAsync()
    {
        string root = Path.Combine(UiRenderService.CacheRoot, "guipak");
        string pack = Path.Combine(UiRenderService.ClientRoot, "pack");
        await RootLock.WaitAsync();
        try
        {
            string stamp = Path.Combine(root, ".source.json");
            string current = JsonSerializer.Serialize(new[] { "gui.pak", "flash.pak" }.Select(n => Path.Combine(pack, n)).Where(File.Exists).Select(f => new { f, t = File.GetLastWriteTimeUtc(f).Ticks }));
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != current)
            {
                if (!File.Exists(Path.Combine(pack, "gui.pak"))) throw new FileNotFoundException("The client's gui.pak is not in " + pack + " (set DBO_CLIENT_ROOT).");
                foreach (string pak in new[] { "gui.pak", "flash.pak" })
                    if (File.Exists(Path.Combine(pack, pak))) await RunAsync(PakTool(), ["extract", Path.Combine(pack, pak), pack, root], allowFailure: true);
                File.WriteAllText(stamp, current);
            }
            string fonts = Path.Combine(UiRenderService.ClientRoot, "font"), fontOut = Path.Combine(root, "font");
            Directory.CreateDirectory(fontOut);
            if (Directory.Exists(fonts))
                foreach (var f in Directory.GetFiles(fonts))
                {
                    string dst = Path.Combine(fontOut, Path.GetFileName(f));
                    if (!File.Exists(dst)) File.Copy(f, dst);
                }
            return root;
        }
        finally { RootLock.Release(); }
    }

    static Dictionary<string, string>? _texIndex;
    // Copies gui textures (as named in .rsr) into <root>\texture\gui: loose client files first, then tex.pak.
    public static async Task<List<string>> EnsureTexturesAsync(string root, IEnumerable<string> names)
    {
        var missing = new List<string>();
        string loose = Path.Combine(UiRenderService.ClientRoot, "texture", "gui");
        string texPak = Path.Combine(UiRenderService.ClientRoot, "pack", "tex.pak");
        foreach (string raw in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = raw.Replace('/', '\\');
            string dst = Path.Combine(root, "texture", "gui", name);
            string[] candidates = Path.HasExtension(name) ? [name] : [name + ".png", name + ".dds", name];
            if (candidates.Any(c => File.Exists(Path.Combine(root, "texture", "gui", c)))) continue;
            string? src = candidates.Select(c => Path.Combine(loose, c)).FirstOrDefault(File.Exists);
            if (src != null) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(src, Path.Combine(root, "texture", "gui", Path.GetFileName(src) == Path.GetFileName(name) ? name : name + Path.GetExtension(src)), true); continue; }
            if (!File.Exists(texPak)) { missing.Add(raw); continue; }
            if (_texIndex == null)
            {
                _texIndex = new(StringComparer.OrdinalIgnoreCase);
                foreach (var line in (await RunAsync(PakTool(), ["list", texPak, @"texture\gui\"], allowFailure: true)).Split('\n'))
                {
                    var parts = line.TrimEnd('\r').Split('\t');
                    if (parts.Length >= 4) { string p = parts[3].StartsWith(".\\") ? parts[3][2..] : parts[3]; _texIndex.TryAdd(p, p); }
                }
            }
            string? hit = candidates.Select(c => @"texture\gui\" + c).FirstOrDefault(c => _texIndex.ContainsKey(c));
            if (hit == null) { missing.Add(raw); continue; }
            // tex.pak carries a few corrupt zero-size records; pak-tool counts them as errors even when the match was extracted.
            await RunAsync(PakTool(), ["extract-match", texPak, Path.GetDirectoryName(texPak)!, root, _texIndex[hit]], allowFailure: true);
            if (!File.Exists(Path.Combine(root, _texIndex[hit]))) missing.Add(raw);
        }
        return missing;
    }

    // ---------- list / render original windows ----------
    public static async Task<object> ListAsync(string? filter)
    {
        string root = await GuiRootAsync();
        var frms = Directory.EnumerateFiles(Path.Combine(root, "gui"), "*.frm", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(root, "gui"), f)).Where(n => filter == null || n.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
        var swfs = Directory.Exists(Path.Combine(root, "flash")) ? Directory.EnumerateDirectories(Path.Combine(root, "flash")).Select(Path.GetFileName).Where(n => filter == null || n!.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList() : [];
        return new { frm = frms, flash = swfs.Select(s => s + ".swf") };
    }

    public sealed record FrmRender(string Png, string Dump, IReadOnlyList<string> Warnings, GuiScript.WindowFiles Files, string Root);

    public static async Task<FrmRender> RenderFrmAsync(string frmName, string outPng, int width, int height, string? dialog, bool center,
        IReadOnlyDictionary<string, string>? texts = null, IEnumerable<string>? hide = null, double flashTime = 0)
    {
        string root = await GuiRootAsync();
        var files = GuiScript.Collect(root, frmName);
        var missing = await EnsureTexturesAsync(root, files.Textures);
        string run = Path.Combine(UiRenderService.CacheRoot, "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        outPng = Path.GetFullPath(outPng); Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        string dump = Path.ChangeExtension(outPng, ".tree.json");
        var sb = new StringBuilder();
        void L(params object[] p) => sb.Append(string.Join('\t', p.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))).Append('\n');
        L("screen", width, height); L("clear", "FF1B2430"); L("out", outPng); L("metrics", Path.Combine(run, "m.json")); L("dump", dump);
        // Pages are keyed by file name exactly as the scripts reference them (case-sensitive in NtlGui).
        foreach (var r in files.Rsr) if (FindGui(root, r) is string p) L("rsr", @"gui\" + Path.GetRelativePath(Path.Combine(root, "gui"), Path.Combine(Path.GetDirectoryName(p)!, r)));
        foreach (var s in files.Srf) if (FindGui(root, s) is string p) L("srf", @"gui\" + Path.GetRelativePath(Path.Combine(root, "gui"), Path.Combine(Path.GetDirectoryName(p)!, s)));
        L("frm", @"gui\" + Path.GetRelativePath(Path.Combine(root, "gui"), files.Frm), dialog ?? "-", center ? 1 : 0);
        if (texts != null) foreach (var (k, v) in texts) L("frm-text", k, Convert.ToHexString(Encoding.UTF8.GetBytes(v)));
        if (hide != null) foreach (var h in hide) L("frm-show", h, 0);
        if (flashTime > 0) L("flashtime", flashTime);
        string scene = Path.Combine(run, "scene.tsv"); await File.WriteAllTextAsync(scene, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(outPng)) File.Delete(outPng);
        await RunAsync(UiRenderService.RendererPath(), [scene], root, 90);
        var warnings = new List<string>(missing.Select(m => "texture not found: " + m));
        warnings.AddRange(files.Missing.Select(m => "script not found: " + m));
        if (File.Exists(Path.Combine(run, "m.json")) && JsonNode.Parse(File.ReadAllText(Path.Combine(run, "m.json")))?["warnings"] is JsonArray w) warnings.AddRange(w.Select(x => (string)x!));
        try { Directory.Delete(run, true); } catch { }
        return new FrmRender(outPng, dump, warnings.Distinct().ToList(), files, root);
    }

    static string? FindGui(string root, string name)
    {
        string direct = Path.Combine(root, "gui", name);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(Path.Combine(root, "gui"), Path.GetFileName(name), SearchOption.AllDirectories).FirstOrDefault();
    }

    // ---------- Flash ----------
    // Resolves a flash reference to the name CFlash loads (flash\<stem>\<stem>.swf under the GUI root).
    // "flash:<name>.swf" = movie from flash.pak; a file path = custom swf copied next to them.
    public static async Task<string?> FlashNameAsync(string reference)
    {
        string root = await GuiRootAsync();
        if (reference.StartsWith("flash:", StringComparison.OrdinalIgnoreCase))
        {
            string name = reference[6..]; string stem = Path.GetFileNameWithoutExtension(name);
            return File.Exists(Path.Combine(root, "flash", stem, stem + ".swf")) ? stem + ".swf" : null;
        }
        if (!File.Exists(reference)) return null;
        string custom = "uimaker_" + Regex.Replace(Path.GetFileNameWithoutExtension(reference), "[^A-Za-z0-9_]", "_");
        string dir = Path.Combine(root, "flash", custom); Directory.CreateDirectory(dir);
        File.Copy(reference, Path.Combine(dir, custom + ".swf"), true);
        return custom + ".swf";
    }

    public static async Task<object> RenderSwfAsync(string reference, string outPng, int width, int height, int frame, double time, IReadOnlyDictionary<string, string>? vars)
    {
        string root = await GuiRootAsync();
        string name = await FlashNameAsync(reference.Contains(':') || File.Exists(reference) ? reference : "flash:" + reference) ?? throw new FileNotFoundException("swf not found: " + reference);
        string run = Path.Combine(UiRenderService.CacheRoot, "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        outPng = Path.GetFullPath(outPng); Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        var sb = new StringBuilder();
        void L(params object[] p) => sb.Append(string.Join('\t', p.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))).Append('\n');
        L("screen", width, height); L("clear", "FF1B2430"); L("out", outPng); L("metrics", Path.Combine(run, "m.json"));
        L("dialog", 0, 0, width, height);
        L("flash", "movie", 0, 0, width, height, name, frame);
        if (vars != null) foreach (var (k, v) in vars) L("flashvar", k, Convert.ToHexString(Encoding.UTF8.GetBytes(v)));
        if (time > 0) L("flashtime", time);
        string scene = Path.Combine(run, "scene.tsv"); await File.WriteAllTextAsync(scene, sb.ToString(), new UTF8Encoding(false));
        await RunAsync(UiRenderService.RendererPath(), [scene], root, 90);
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "m.json")))!;
        var comp = (m["components"] as JsonArray)?.FirstOrDefault(c => (string?)c!["id"] == "movie");
        try { Directory.Delete(run, true); } catch { }
        return new { png = outPng, flash = name, frameCount = (int?)comp?["textWidth"] ?? 0, currentFrame = (int?)comp?["textHeight"] ?? 0, warnings = m["warnings"] };
    }

    // ---------- import ----------
    public static async Task<object> ImportAsync(string frmName, string outLayout, string? dialog, int width, int height)
    {
        outLayout = ContentSafety.SafeOutput(outLayout);
        if (File.Exists(outLayout)) throw new IOException("Already exists: " + outLayout);
        string work = Path.Combine(Path.GetDirectoryName(outLayout)!, "review", Path.GetFileName(outLayout).Replace(".uilayout.json", "") + "-import");
        Directory.CreateDirectory(work);
        var original = await RenderFrmAsync(frmName, Path.Combine(work, "original.png"), width, height, dialog, center: true);
        var tree = JsonNode.Parse(File.ReadAllText(original.Dump))!.AsObject();
        var roots = (tree["components"] as JsonArray)!.OfType<JsonObject>().ToList();
        var root = roots.FirstOrDefault() ?? throw new InvalidDataException("The .frm did not create any visible component.");
        var blocks = GuiScript.Parse(GuiScript.ReadText(original.Files.Frm));
        var byName = new Dictionary<string, GuiScriptBlock>(StringComparer.Ordinal);
        foreach (var b in blocks.Concat(blocks.SelectMany(x => x.Descendants()))) byName.TryAdd(b.Name, b);

        // Surfaces declared in the scripts, for component types whose live surfaces NtlGui does not expose (htmlbox...).
        var srfBlocks = new Dictionary<string, GuiScriptBlock>(StringComparer.Ordinal);
        var rsrFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in original.Files.Srf) if (FindGui(original.Root, s) is string p) foreach (var b in GuiScript.Parse(GuiScript.ReadText(p))) srfBlocks.TryAdd(b.Name, b);
        foreach (var r in original.Files.Rsr) if (FindGui(original.Root, r) is string p) foreach (var b in GuiScript.Parse(GuiScript.ReadText(p))) if (b.Get("file") is string file) rsrFiles.TryAdd(b.Name, file);
        var assets = new JsonObject(); var assetByTex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        string Asset(string tex)
        {
            if (assetByTex.TryGetValue(tex, out var a)) return a;
            a = "t" + assetByTex.Count.ToString(CultureInfo.InvariantCulture); assetByTex[tex] = a; assets[a] = "gui:" + tex; return a;
        }
        string Id(string raw)
        {
            string id = Regex.Replace(raw, "[^A-Za-z0-9_]", "_"); if (id.Length == 0 || char.IsDigit(id[0])) id = "c_" + id;
            string u = id; for (int n = 2; !ids.Add(u); n++) u = id + "_" + n; return u;
        }
        JsonArray Images(JsonArray? surfaces, string slot, int cw, int ch)
        {
            var list = new JsonArray();
            foreach (var s in surfaces?.OfType<JsonObject>() ?? [])
            {
                if ((string?)s["slot"] != slot) continue;
                string tex = (string?)s["tex"] ?? ""; var uv = (JsonArray)s["uv"]!; var snap = (JsonArray)s["snap"]!;
                int tw = (int)s["tw"]!, th = (int)s["th"]!; uint argb = (uint)(long)s["argb"]!;
                var img = new JsonObject();
                if (tex.Length > 0 && tw > 0)
                {
                    double l = (double)uv[0]!, t = (double)uv[1]!, r = (double)uv[2]!, b = (double)uv[3]!;
                    img["asset"] = Asset(tex);
                    img["src"] = new JsonArray((int)Math.Round(l * tw), (int)Math.Round(t * th), (int)Math.Round((r - l) * tw), (int)Math.Round((b - t) * th));
                }
                else img["asset"] = null;
                int sx = (int)snap[0]!, sy = (int)snap[1]!, sw = (int)snap[2]!, sh = (int)snap[3]!;
                if (sx != 0 || sy != 0 || sw != cw || sh != ch) img["rect"] = new JsonArray(sx, sy, sw, sh);
                if (argb != 0xFFFFFFFF) img["tint"] = "#" + (argb & 0xFFFFFF).ToString("X6") + (argb >> 24).ToString("X2");
                if (img["asset"] is null) { img.Remove("asset"); img["fill"] = (string?)img["tint"] ?? "#FFFFFFFF"; }
                list.Add(img);
            }
            return list;
        }
        JsonArray ScriptSurfaces(GuiScriptBlock b)
        {
            var list = new JsonArray();
            foreach (string sname in b.All("surface"))
            {
                if (!srfBlocks.TryGetValue(sname, out var s)) continue;
                var img = new JsonObject();
                if (s.Get("resource_name") is string rn && rsrFiles.TryGetValue(rn, out var file))
                {
                    img["asset"] = Asset(file);
                    int l = s.Int("uv_left"), t = s.Int("uv_top");
                    img["src"] = new JsonArray(l, t, s.Int("uv_right") - l, s.Int("uv_bottom") - t);
                }
                img["rect"] = new JsonArray(s.Int("x"), s.Int("y"), s.Int("width"), s.Int("height"));
                uint argb = (uint)(s.Int("color_alpha", 255) & 255) << 24 | (uint)(s.Int("color_red", 255) & 255) << 16 | (uint)(s.Int("color_green", 255) & 255) << 8 | (uint)(s.Int("color_blue", 255) & 255);
                if (argb != 0xFFFFFFFF) img["tint"] = "#" + (argb & 0xFFFFFF).ToString("X6") + (argb >> 24).ToString("X2");
                if (img["asset"] is null) img["fill"] = (string?)img["tint"] ?? "#FFFFFFFF";
                list.Add(img);
            }
            if (list.Count > 0) notes.Add($"{b.Name}: surfaces taken from the .srf (NtlGui does not expose them for {b.Type}).");
            return list;
        }
        // Dynamic dialogs (dynamic = true): NtlGui stretches a 9-piece frame (corners fixed, lines and centre stretched).
        JsonArray DynamicSurfaces(GuiScriptBlock b, int w, int h)
        {
            var list = new JsonArray();
            GuiScriptBlock? S(string key) => b.Get(key) is string n && srfBlocks.TryGetValue(n, out var s) ? s : null;
            (int W, int H) Size(GuiScriptBlock? s) => s == null ? (0, 0) : (s.Int("width"), s.Int("height"));
            var lt = Size(S("surface_corner_lefttop")); var rt = Size(S("surface_corner_righttop"));
            var lb = Size(S("surface_corner_leftbottom")); var rb = Size(S("surface_corner_rightbottom"));
            var tl = Size(S("surface_line_top")); var bl = Size(S("surface_line_bottom"));
            var ll = Size(S("surface_line_left")); var rl = Size(S("surface_line_right"));
            void Add(string key, int x, int y, int ww, int hh)
            {
                var s = S(key); if (s == null || ww <= 0 || hh <= 0) return;
                var img = new JsonObject();
                if (s.Get("resource_name") is string rn && rsrFiles.TryGetValue(rn, out var file))
                {
                    int l = s.Int("uv_left"), t = s.Int("uv_top");
                    img["asset"] = Asset(file); img["src"] = new JsonArray(l, t, s.Int("uv_right") - l, s.Int("uv_bottom") - t);
                }
                img["rect"] = new JsonArray(x, y, ww, hh);
                uint argb = (uint)(s.Int("color_alpha", 255) & 255) << 24 | (uint)(s.Int("color_red", 255) & 255) << 16 | (uint)(s.Int("color_green", 255) & 255) << 8 | (uint)(s.Int("color_blue", 255) & 255);
                if (argb != 0xFFFFFFFF) img["tint"] = "#" + (argb & 0xFFFFFF).ToString("X6") + (argb >> 24).ToString("X2");
                if (img["asset"] is null) img["fill"] = (string?)img["tint"] ?? "#FFFFFFFF";
                list.Add(img);
            }
            Add("surface_center", ll.W, tl.H, w - ll.W - rl.W, h - tl.H - bl.H);
            Add("surface_line_top", lt.W, 0, w - lt.W - rt.W, tl.H);
            Add("surface_line_bottom", lb.W, h - bl.H, w - lb.W - rb.W, bl.H);
            Add("surface_line_left", 0, lt.H, ll.W, h - lt.H - lb.H);
            Add("surface_line_right", w - rl.W, rt.H, rl.W, h - rt.H - rb.H);
            Add("surface_corner_lefttop", 0, 0, lt.W, lt.H);
            Add("surface_corner_righttop", w - rt.W, 0, rt.W, rt.H);
            Add("surface_corner_leftbottom", 0, h - lb.H, lb.W, lb.H);
            Add("surface_corner_rightbottom", w - rb.W, h - rb.H, rb.W, rb.H);
            if (list.Count > 0) notes.Add($"{b.Name}: dynamic dialog rebuilt as 9 pieces from the .srf.");
            return list;
        }
        void ApplyText(JsonObject o, GuiScriptBlock? b, string? liveText, string defaultAlign)
        {
            var ti = b != null ? GuiScript.Text(b) : null;
            string? text = !string.IsNullOrEmpty(liveText) ? liveText : ti?.Text;
            if (!string.IsNullOrEmpty(text)) o["text"] = text;
            if (ti == null) return;
            if (ti.Height != 90) o["fontSize"] = ti.Height;
            if (!ti.Font.Equals("Default", StringComparison.OrdinalIgnoreCase)) o["font"] = ti.Font;
            if ((ti.Attr & 1) != 0) o["bold"] = true;
            if ((ti.Attr & ~1) != 0) o["fontAttr"] = ti.Attr & ~1;
            if (ti.Color != 0xFFFFFFFF) o["color"] = "#" + (ti.Color & 0xFFFFFF).ToString("X6");
            o["align"] = ti.HasStyle ? ti.Align : defaultAlign;
            o["valign"] = ti.VAlign;
            if (ti.Effect is "shadow" or "outline") o["shadow"] = true;
            if (ti.Effect == "outline") notes.Add($"{b!.Name}: outline approximated as a shadow.");
        }
        JsonObject Convert(JsonObject n, int px, int py)
        {
            var scr = (JsonArray)n["screen"]!;
            int x = (int)scr[0]! - px, y = (int)scr[1]! - py, w = (int)scr[2]!, h = (int)scr[3]!;
            string name = (string)n["id"]!, type = (string)n["type"]!;
            byName.TryGetValue(name, out var block);
            var o = new JsonObject { ["id"] = Id(name), ["type"] = "panel", ["rect"] = new JsonArray(x, y, w, h) };
            var surfaces = n["surfaces"] as JsonArray;
            void SetImages(JsonArray imgs)
            {
                // Only for types whose live surfaces NtlGui does not expose; for the rest an empty list is authoritative.
                if (imgs.Count == 0 && block?.Get("surface_corner_lefttop") != null) imgs = DynamicSurfaces(block, w, h);
                else if (imgs.Count == 0 && block != null && type is "htmlbox" or "component") imgs = ScriptSurfaces(block);
                if (imgs.Count == 1 && imgs[0]!["rect"] is null) o["image"] = imgs[0]!.DeepClone(); else if (imgs.Count > 0) o["images"] = imgs;
            }
            switch (type)
            {
                case "button":
                {
                    o["type"] = "button";
                    SetImages(Images(surfaces, "up", w, h));
                    var states = new JsonObject();
                    foreach (var (slot, key) in new[] { ("focus", "focus"), ("down", "down"), ("disable", "disabled") })
                    { var l = Images(surfaces, slot, w, h); if (l.Count > 0) states[key] = l; }
                    if (states.Count > 0) o["states"] = states;
                    ApplyText(o, block, (string?)n["text"], "center");
                    break;
                }
                case "staticbox": SetImages(Images(surfaces, "base", w, h)); o["type"] = "label"; ApplyText(o, block, (string?)n["text"], "left"); o["wrap"] = true; break;
                case "inputbox": o["type"] = "input"; break;
                case "flash":
                {
                    string? file = block?.Get("filename");
                    if (file != null) { o["type"] = "flash"; o["file"] = "flash:" + file; }
                    else { o["type"] = "group"; notes.Add($"{name}: flash without a movie in the .frm (the code loads it); kept as an empty group."); }
                    break;
                }
                default:
                    SetImages(Images(surfaces, "base", w, h));
                    if (o["image"] is null && o["images"] is null) o["type"] = "group";
                    if (type is "scrollbar" or "sliderbar" or "listbox" or "htmlbox" or "outputbox" or "progressbar" or "component")
                        notes.Add($"{name}: {type} imported as {(string)o["type"]!} (static; the code fills in the content).");
                    break;
            }
            if (block?.Bool("clipping", false) == true) o["clip"] = true;
            if (!(bool)n["visible"]!) o["hidden"] = true;
            if (block != null) o["note"] = $"frm: {block.Type} {name}";
            var kids = new JsonArray();
            // NtlGui paints children from the end of its list to the front: the first child is on top.
            foreach (var c in ((n["children"] as JsonArray)?.OfType<JsonObject>() ?? []).Reverse()) kids.Add(Convert(c, (int)scr[0]!, (int)scr[1]!));
            if (kids.Count > 0) o["children"] = kids;
            return o;
        }
        var rootScreen = (JsonArray)root["screen"]!;
        if ((int)rootScreen[2]! <= 0 || (int)rootScreen[3]! <= 0)
        {
            notes.Add($"{(string)root["id"]!}: the root dialog has no size in the .frm (the code sets it); using the screen size.");
            rootScreen = new JsonArray((int)rootScreen[0]!, (int)rootScreen[1]!, width, height);
        }
        int rx = (int)rootScreen[0]!, ry = (int)rootScreen[1]!;
        var components = new JsonArray();
        var rootImages = Images(root["surfaces"] as JsonArray, "base", (int)rootScreen[2]!, (int)rootScreen[3]!);
        if (rootImages.Count > 0) components.Add(new JsonObject { ["id"] = Id((string)root["id"]! + "_bg"), ["type"] = "panel", ["rect"] = new JsonArray(0, 0, (int)rootScreen[2]!, (int)rootScreen[3]!), ["images"] = rootImages });
        foreach (var c in ((root["children"] as JsonArray)?.OfType<JsonObject>() ?? []).Reverse()) components.Add(Convert(c, rx, ry));

        string layoutName = Path.GetFileName(outLayout).Replace(".uilayout.json", "", StringComparison.OrdinalIgnoreCase);
        var doc = new JsonObject
        {
            ["schema"] = UiLayout.Schema, ["name"] = layoutName,
            ["description"] = $"Imported from {Path.GetFileName(original.Files.Frm)} ({string.Join(", ", original.Files.Srf.Skip(1))}). Original source: the client's gui.pak.",
            ["source"] = new JsonObject { ["frm"] = Path.GetFileName(original.Files.Frm), ["dialog"] = (string)root["id"]!, ["srf"] = new JsonArray(original.Files.Srf.Select(s => (JsonNode)s!).ToArray()), ["rsr"] = new JsonArray(original.Files.Rsr.Select(s => (JsonNode)s!).ToArray()) },
            ["screen"] = new JsonObject { ["width"] = width, ["height"] = height },
            ["dialog"] = new JsonObject { ["width"] = (int)rootScreen[2]!, ["height"] = (int)rootScreen[3]!, ["anchor"] = "center" },
            ["assets"] = assets, ["components"] = components, ["variants"] = new JsonObject(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outLayout)!);
        await File.WriteAllTextAsync(outLayout, UiLayout.Serialize(doc));

        // Fidelity check: imported layout vs the window built from its own scripts.
        var imported = await UiRenderService.RenderAsync(outLayout, "default", Path.Combine(work, "imported.png"), width, height);
        object? diff = null;
        try { diff = JsonNode.Parse(await RunAsync(UiRenderService.RendererPath(), ["--diff", Path.Combine(work, "original.png"), Path.Combine(work, "imported.png"), Path.Combine(work, "diff.png")], original.Root, 60)); }
        catch (Exception ex) { notes.Add("comparison not available: " + ex.Message); }
        return new
        {
            layout = outLayout, original = original.Png, imported = imported.Png, diffImage = Path.Combine(work, "diff.png"), diff,
            components = ids.Count, assets = assetByTex.Count, warnings = original.Warnings, notes = notes.Distinct().ToArray(), issues = imported.Issues,
        };
    }
}
