using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// UI Maker: renders .uilayout.json windows with the client's real GUI library
// (native/ui-render.exe links NtlGui.lib + RenderWare), lints them and keeps
// the review/approval record that gates client changes.
internal static class UiRenderService
{
    // ---------- configuration ----------
    public static string ClientRoot => StudioConfig.ClientRoot() ?? throw new DirectoryNotFoundException("Configurá clientRoot en studio.config.json (o DBO_CLIENT_ROOT): la carpeta de tu cliente, la que contiene pack\\.");
    public static string CacheRoot
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("DBO_STUDIO_DATA_ROOT") is { Length: > 0 } d ? Path.Combine(d, "ui-maker")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBO Assets Studio", "UI Maker");
            Directory.CreateDirectory(root); return root;
        }
    }

    public static string RendererPath()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("DBO_UI_RENDER") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ui-render.exe"));
        if (RepoRoot() is string repo) candidates.Add(Path.Combine(repo, "native", "bin", "ui-render.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Falta ui-render.exe. Compilalo con build-ui-render.ps1 o definí DBO_UI_RENDER.");
    }

    public static string? RepoRoot()
    {
        for (string? p = AppContext.BaseDirectory; p != null; p = Path.GetDirectoryName(p))
            if (File.Exists(Path.Combine(p, "DboAssetsStudio.csproj"))) return p;
        for (string? p = Environment.CurrentDirectory; p != null; p = Path.GetDirectoryName(p))
            if (File.Exists(Path.Combine(p, "DboAssetsStudio.csproj"))) return p;
        return null;
    }

    static string CoreRoot()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("DBO_CLIENT_SOURCE") is { Length: > 0 } env) candidates.Add(env);
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "DBO-Client-2.0", "Client", "Gui", "Assets", "HudEmbeddedPixels.h")))
            ?? throw new DirectoryNotFoundException("No se encontró el core del cliente para leer embedded:. Definí DBO_CLIENT_SOURCE con la carpeta de tus fuentes del cliente.");
    }

    // ---------- render ----------
    public sealed record RenderResult(string Layout, string Variant, int Width, int Height, string Png, string Metrics, IReadOnlyList<LintIssue> Issues, IReadOnlyList<string> Warnings);
    public sealed record LintIssue(string Severity, string Id, string Code, string Message);

    public static async Task<RenderResult> RenderAsync(string layoutPath, string variant, string outPng, int? width = null, int? height = null, string? background = null, CancellationToken token = default)
    {
        var layout = UiLayout.Load(layoutPath);
        var scene = layout.Resolve(variant, width, height);
        if (background != null) scene.Background = Path.GetFullPath(background);
        outPng = Path.GetFullPath(outPng);
        Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        // Renders run inside the extracted client GUI root (game fonts, GuiFont.def, FlashFont.def, flash\).
        string work;
        try { work = await UiFrmService.GuiRootAsync(); }
        catch (FileNotFoundException) { work = FontWorkDirectory(); }
        string run = Path.Combine(CacheRoot, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        string metricsPath = Path.Combine(run, "metrics.json");
        var warnings = new List<string>();
        string tsv = await BuildSceneAsync(scene, outPng, metricsPath, warnings, token);
        string scenePath = Path.Combine(run, "scene.tsv");
        await File.WriteAllTextAsync(scenePath, tsv, new UTF8Encoding(false), token);
        if (File.Exists(outPng)) File.Delete(outPng);

        var psi = new ProcessStartInfo(RendererPath(), $"\"{scenePath}\"") { WorkingDirectory = work, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync(token); var stdout = proc.StandardOutput.ReadToEndAsync(token);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new TimeoutException("ui-render.exe no terminó en 60 s."); }
        }
        if (proc.ExitCode != 0 || !File.Exists(outPng)) throw new InvalidOperationException($"ui-render.exe falló ({proc.ExitCode}): {(await stderr).Trim()} {(await stdout).Trim()}");
        var metrics = JsonNode.Parse(await File.ReadAllTextAsync(metricsPath, token))!.AsObject();
        if (metrics["warnings"] is JsonArray w) foreach (var x in w) warnings.Add((string)x!);
        string metricsOut = Path.ChangeExtension(outPng, ".metrics.json");
        File.Copy(metricsPath, metricsOut, true);
        var issues = Lint(scene, metrics, warnings);
        try { Directory.Delete(run, true); } catch { }
        return new RenderResult(layout.Name, variant, scene.ScreenWidth, scene.ScreenHeight, outPng, metricsOut, issues, warnings);
    }

    // Fonts the same way the client registers them: gui\Font\GuiFont.def + font\*.ttf from the client.
    static readonly SemaphoreSlim FontLock = new(1, 1);
    static string FontWorkDirectory()
    {
        string work = Path.Combine(CacheRoot, "fontwork");
        string def = Path.Combine(work, "gui", "Font", "GuiFont.def");
        FontLock.Wait();
        try
        {
            if (File.Exists(def)) return work;
            Directory.CreateDirectory(Path.GetDirectoryName(def)!); Directory.CreateDirectory(Path.Combine(work, "font"));
            string fonts = Path.Combine(ClientRoot, "font");
            if (Directory.Exists(fonts)) foreach (var f in Directory.GetFiles(fonts)) File.Copy(f, Path.Combine(work, "font", Path.GetFileName(f)), true);
            string? text = null;
            string pak = Path.Combine(ClientRoot, "pack", "gui.pak");
            if (File.Exists(pak))
            {
                try
                {
                    var archive = PakArchive.OpenAsync(pak).GetAwaiter().GetResult();
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(@"gui\font\guifont.def", StringComparison.OrdinalIgnoreCase));
                    if (entry != null) text = Encoding.Latin1.GetString(archive.ReadAsync(entry).GetAwaiter().GetResult());
                }
                catch { text = null; }
            }
            // Client default (gui.pak, 2026-11): Default = MSJHBD.TTF; NtlGui then swaps "Default" for the
            // preferred UI font (Arial unless the client configuration says otherwise).
            text ??= "Default = MSJHBD.TTF, \"Microsoft JhengHei\";\r\ndetail = DFU_HMD.TTF, DFPHeiMedium-UN;\r\nLolita = Lolita.ttf, Lolita;\r\nSimHei = SimHei.ttf,SimHei;\r\n";
            File.WriteAllText(def, text, Encoding.Latin1);
            return work;
        }
        finally { FontLock.Release(); }
    }

    static string Hex(string s) => Convert.ToHexString(Encoding.UTF8.GetBytes(s));
    static string ArgbHex(uint c) => c.ToString("X8");
    static string RgbHex(uint c) => (c & 0xFFFFFF).ToString("X6");
    const int TEXT_LEFT = 0x04, TEXT_CENTER = 0x08, TEXT_RIGHT = 0x10, TEXT_UP = 0x20, TEXT_VCENTER = 0x40, TEXT_DOWN = 0x80;
    static int Style(UiComponent c) => (c.Align switch { "center" => TEXT_CENTER, "right" => TEXT_RIGHT, _ => TEXT_LEFT }) | (c.VAlign switch { "top" => 0, "up" => TEXT_UP, "bottom" => TEXT_DOWN, _ => TEXT_VCENTER });

    static async Task<string> BuildSceneAsync(UiScene scene, string outPng, string metricsPath, List<string> warnings, CancellationToken token)
    {
        var sb = new StringBuilder();
        void L(params object[] parts) => sb.Append(string.Join('\t', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)))).Append('\n');
        L("screen", scene.ScreenWidth, scene.ScreenHeight);
        L("clear", "FF1B2430");
        L("out", outPng); L("metrics", metricsPath);
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<string> Tex(UiImage? img)
        {
            if (img?.File is null) return "-";
            if (textures.TryGetValue(img.File, out var key)) return key;
            string? file = await MaterializeAssetAsync(img.File, warnings, token);
            key = "t" + textures.Count.ToString(CultureInfo.InvariantCulture);
            textures[img.File] = key;
            if (file != null) L("tex", key, file); else warnings.Add("asset no disponible: " + img.File);
            return file == null ? "-" : key;
        }
        if (scene.Background != null)
        {
            string key = await Tex(new UiImage { File = scene.Background });
            if (key != "-") L("panel", "__background", 0, 0, scene.ScreenWidth, scene.ScreenHeight, key, 0, 0, 0, 0, "FFFFFFFF");
        }
        L("dialog", scene.DialogX, scene.DialogY, scene.DialogWidth, scene.DialogHeight);
        if ((scene.DialogColor >> 24) != 0) L("panel", "__dialog", 0, 0, scene.DialogWidth, scene.DialogHeight, "-", 0, 0, 0, 0, ArgbHex(scene.DialogColor));

        async Task Panel(string id, int x, int y, int w, int h, UiImage? img, uint fill)
        {
            if ((fill >> 24) != 0) L("panel", id + (img != null ? "__fill" : ""), x, y, w, h, "-", 0, 0, 0, 0, ArgbHex(fill));
            if (img == null) return;
            string key = await Tex(img);
            if (img.Slice is int[] s && key != "-")
            {
                // 9-slice: corners keep their size, edges/centre stretch. sw/sh must be explicit.
                int sx = img.SrcX, sy = img.SrcY, sw = img.SrcWidth, sh = img.SrcHeight;
                int l = s[0], t = s[1], r = s[2], b = s[3];
                int[] dx = [0, l, w - r], dw = [l, w - l - r, r], ux = [sx, sx + l, sx + sw - r], uw = [l, sw - l - r, r];
                int[] dy = [0, t, h - b], dh = [t, h - t - b, b], uy = [sy, sy + t, sy + sh - b], uh = [t, sh - t - b, b];
                for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++)
                    if (dw[i] > 0 && dh[j] > 0) L("panel", $"{id}__s{j}{i}", x + dx[i], y + dy[j], dw[i], dh[j], key, ux[i], uy[j], uw[i], uh[j], ArgbHex(img.Tint));
                return;
            }
            if (img.Dest is int[] d) { x += d[0]; y += d[1]; w = d[2]; h = d[3]; }
            L("panel", id, x, y, w, h, key, img.SrcX, img.SrcY, img.SrcWidth, img.SrcHeight, ArgbHex(img.Tint));
        }
        // All surfaces of a component: "image" first, then "images" in order (each may carry its own rect).
        async Task Surfaces(string id, int x, int y, int w, int h, UiComponent c, uint fill)
        {
            var all = new List<UiImage>(); if (c.Image != null) all.Add(c.Image); all.AddRange(c.Images);
            if (all.Count == 0) { await Panel(id, x, y, w, h, null, fill); return; }
            for (int i = 0; i < all.Count; i++) await Panel(i == 0 ? id : $"{id}__i{i}", x, y, w, h, all[i], i == 0 ? fill : 0);
        }
        void Label(string id, int x, int y, int w, int h, UiComponent c, string text) =>
            L("label", id, x, y, w, h, c.FontSize, (c.Bold ? 1 : 0) | c.FontAttr, Style(c), RgbHex(c.TextColor), c.Shadow ? 1 : 0, "000000", Hex(text), c.Font);

        // Clipping containers become native child dialogs; their descendants use coordinates relative to them.
        var parentOf = scene.Components.ToDictionary(c => c.Id, c => c.Parent);
        var clipStack = new List<(string Id, int X, int Y)>();
        bool IsAncestor(string anc, string? id) { while (id != null) { if (id == anc) return true; id = parentOf.GetValueOrDefault(id); } return false; }
        foreach (var c in scene.Components)
        {
            while (clipStack.Count > 0 && !IsAncestor(clipStack[^1].Id, c.Parent)) { L("group-end"); clipStack.RemoveAt(clipStack.Count - 1); }
            int ox = clipStack.Count > 0 ? clipStack[^1].X : 0, oy = clipStack.Count > 0 ? clipStack[^1].Y : 0;
            int x = c.X - ox, y = c.Y - oy, w = c.Width, h = c.Height;
            switch (c.Type)
            {
                case "group": break;
                case "panel": case "icon": await Surfaces(c.Id, x, y, w, h, c, c.Fill); if (c.Text != null) Label(c.Id + "__text", x, y, w, h, c, c.Text); break;
                case "label": await Surfaces(c.Id + "__bg", x, y, w, h, c, c.Fill); Label(c.Id, x, y, w, h, c, c.Text ?? ""); break;
                case "input":
                    await Surfaces(c.Id + "__bg", x, y, w, h, c, c.Image == null && c.Images.Count == 0 && c.Fill == 0 ? 0xFF0B0F14 : c.Fill);
                    Label(c.Id, x + 4, y, w - 8, h, c, c.Text ?? ""); break;
                case "progress":
                {
                    await Panel(c.Id + "__track", x, y, w, h, c.Image, c.Image == null && c.Fill == 0 ? 0xFF2A2A2A : 0);
                    int fw = (int)Math.Round(w * Math.Clamp(c.Value, 0, 1));
                    if (fw > 0)
                    {
                        var bar = c.BarImage;
                        if (bar != null && bar.SrcWidth > 0) bar = new UiImage { File = bar.File, SrcX = bar.SrcX, SrcY = bar.SrcY, SrcWidth = (int)Math.Round(bar.SrcWidth * Math.Clamp(c.Value, 0, 1)), SrcHeight = bar.SrcHeight, Tint = bar.Tint };
                        await Panel(c.Id + "__bar", x, y, fw, h, bar, bar == null ? (c.Fill == 0 ? 0xFFE0A020 : c.Fill) : 0);
                    }
                    if (c.Text != null) Label(c.Id, x, y, w, h, c, c.Text);
                    break;
                }
                case "scroll":
                {
                    await Panel(c.Id + "__track", x, y, w, h, c.Image, c.Image == null ? 0xFF14181E : 0);
                    int th = Math.Max(12, h / 4), ty = y + (int)Math.Round((h - th) * Math.Clamp(c.Value, 0, 1));
                    await Panel(c.Id + "__thumb", x, ty, w, th, c.BarImage, c.BarImage == null ? (c.Fill == 0 ? 0xFF6C7686 : c.Fill) : 0);
                    break;
                }
                case "button":
                {
                    bool enabled = c.State != "disabled";
                    L("button", c.Id, x, y, w, h, c.FontSize, (c.Bold ? 1 : 0) | c.FontAttr, Style(c), RgbHex(c.State == "disabled" ? 0xFF969696 : c.TextColor), enabled ? 1 : 0, c.State, Hex(c.Text ?? ""), c.Font);
                    // The forced preview state is painted in every slot, so whichever state NtlGui picks shows it.
                    UiImage? img = c.State != "normal" && c.StateImages.TryGetValue(c.State, out var st) && st != null ? st : c.Image;
                    if (c.State != "normal" && c.StateFills.ContainsKey(c.State) && !c.StateImages.ContainsKey(c.State)) img = null;
                    var list = c.State != "normal" && c.StateImageLists.TryGetValue(c.State, out var sl) ? sl
                        : img == c.Image ? new List<UiImage>(c.Image == null ? c.Images : c.Images.Prepend(c.Image)) : new List<UiImage> { img! };
                    if (img != null || list.Count > 0)
                        foreach (var im in list)
                        {
                            string key = await Tex(im);
                            int[] d = im.Dest ?? [0, 0, w, h];
                            L("bsurf", "all", key, im.SrcX, im.SrcY, im.SrcWidth, im.SrcHeight, ArgbHex(im.Tint), d[2], d[3], d[0], d[1]);
                        }
                    else
                    {
                        uint fill = c.StateFills.TryGetValue(c.State, out var sf) ? sf : c.Fill;
                        if ((fill >> 24) != 0) L("bsurf", "all", "-", 0, 0, 0, 0, ArgbHex(fill), w, h);
                    }
                    break;
                }
                case "flash":
                {
                    string? swf = c.File == null ? null : await UiFrmService.FlashNameAsync(c.File);
                    if (swf == null) { warnings.Add(c.File == null ? $"{c.Id}: flash sin file" : $"{c.Id}: falta el swf {c.File}"); if (c.File != null) await Panel(c.Id, x, y, w, h, null, 0x60FF00FF); break; }
                    L("flash", c.Id, x, y, w, h, swf, c.Frame);
                    foreach (var (k, v) in c.FlashVars) L("flashvar", k, Hex(v));
                    break;
                }
            }
            if (c.Clip) { L("group-begin", c.Id, x, y, w, h); clipStack.Add((c.Id, c.X, c.Y)); }
        }
        for (int i = 0; i < clipStack.Count; i++) L("group-end");
        return sb.ToString();
    }

    // ---------- assets ----------
    // file path | gui:<name> (texture\gui of the client, loose or tex pak) | pak:<pak>|<resource> | embedded:<name> (HudEmbeddedPixels.h)
    public static async Task<string?> MaterializeAssetAsync(string asset, List<string>? warnings, CancellationToken token)
    {
        if (asset.StartsWith("pak:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = asset[4..].Split('|', 2);
            if (parts.Length != 2) throw new InvalidDataException("pak: usa pak:<archivo.pak>|<recurso>");
            string pak = Path.IsPathRooted(parts[0]) ? parts[0] : Path.Combine(ClientRoot, "pack", parts[0]);
            return await FromPakAsync(pak, parts[1], token);
        }
        if (asset.StartsWith("embedded:", StringComparison.OrdinalIgnoreCase)) return Embedded(asset[9..]);
        if (asset.StartsWith("gui:", StringComparison.OrdinalIgnoreCase))
        {
            string name = asset[4..].Replace('/', '\\');
            string loose = Path.Combine(ClientRoot, "texture", "gui", name);
            if (File.Exists(loose)) return loose;
            foreach (string pak in new[] { "tex.pak", "gui.pak" })
            {
                string p = Path.Combine(ClientRoot, "pack", pak);
                if (!File.Exists(p)) continue;
                try
                {
                    string? got = await FromPakAsync(p, @"texture\gui\" + name, token, quiet: true);
                    if (got != null) return got;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings?.Add($"gui: no se pudo leer {pak}: {ex.Message.Split('\n')[0]}"); }
            }
            warnings?.Add("gui: no encontrado en texture\\gui ni en tex.pak: " + name);
            return null;
        }
        return File.Exists(asset) ? asset : null;
    }

    static readonly Dictionary<string, PakArchive> Paks = new(StringComparer.OrdinalIgnoreCase);
    static async Task<string?> FromPakAsync(string pak, string resource, CancellationToken token, bool quiet = false)
    {
        resource = resource.Replace('/', '\\');
        string cache = Path.Combine(CacheRoot, "assets", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(pak).ToLowerInvariant() + "|" + resource.ToLowerInvariant())))[..24] + Path.GetExtension(resource));
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(pak)) return cache;
        PakArchive? archive;
        lock (Paks) Paks.TryGetValue(pak, out archive);
        if (archive == null) { archive = await PakArchive.OpenAsync(pak); lock (Paks) Paks[pak] = archive; }
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(resource, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { if (quiet) return null; throw new FileNotFoundException($"{resource} no está en {pak}"); }
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllBytesAsync(cache, await archive.ReadAsync(entry), token);
        return cache;
    }

    // Images compiled into the client (scripts/embed-hud-art.py): zlib BGRA → 32-bit TGA.
    static string? Embedded(string name)
    {
        string cache = Path.Combine(CacheRoot, "embedded", Regex.Replace(name, @"[^A-Za-z0-9_.-]", "_") + ".tga");
        string assets = Path.Combine(CoreRoot(), "DBO-Client-2.0", "Client", "Gui", "Assets");
        var headers = Directory.GetFiles(assets, "*Pixels.h");
        if (File.Exists(cache) && headers.All(h => File.GetLastWriteTimeUtc(h) <= File.GetLastWriteTimeUtc(cache))) return cache;
        foreach (string header in headers)
        {
            string text = File.ReadAllText(header);
            var entry = Regex.Match(text, "\\{\\s*\"" + Regex.Escape(name) + "\"\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\w+)\\s*\\}");
            if (!entry.Success) continue;
            int w = int.Parse(entry.Groups[1].Value), h = int.Parse(entry.Groups[2].Value);
            var array = Regex.Match(text, Regex.Escape(entry.Groups[4].Value) + @"\s*\[\s*\d*\s*\]\s*=\s*\{([^}]*)\}");
            if (!array.Success) throw new InvalidDataException("No se encontró el arreglo " + entry.Groups[4].Value);
            var bytes = new List<byte>(int.Parse(entry.Groups[3].Value));
            foreach (Match m in Regex.Matches(array.Groups[1].Value, @"0x([0-9A-Fa-f]{1,2})|\b(\d+)\b"))
                bytes.Add(m.Groups[1].Success ? Convert.ToByte(m.Groups[1].Value, 16) : byte.Parse(m.Groups[2].Value));
            using var z = new ZLibStream(new MemoryStream(bytes.ToArray()), CompressionMode.Decompress);
            var pixels = new byte[w * h * 4]; int read = 0, n;
            while (read < pixels.Length && (n = z.Read(pixels, read, pixels.Length - read)) > 0) read += n;
            if (read != pixels.Length) throw new InvalidDataException("Imagen embebida truncada: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            WriteTga(cache, w, h, pixels);
            return cache;
        }
        return null;
    }

    static void WriteTga(string path, int w, int h, byte[] bgra)
    {
        using var f = File.Create(path);
        var hdr = new byte[18]; hdr[2] = 2; hdr[12] = (byte)w; hdr[13] = (byte)(w >> 8); hdr[14] = (byte)h; hdr[15] = (byte)(h >> 8); hdr[16] = 32; hdr[17] = 0x28; // top-left origin, 8 alpha bits
        f.Write(hdr); f.Write(bgra);
    }

    public static IReadOnlyList<string> ListEmbedded()
    {
        string assets = Path.Combine(CoreRoot(), "DBO-Client-2.0", "Client", "Gui", "Assets");
        var names = new List<string>();
        foreach (string header in Directory.GetFiles(assets, "*Pixels.h"))
            foreach (Match m in Regex.Matches(File.ReadAllText(header), "\\{\\s*\"([^\"]+)\"\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,")) names.Add($"{m.Groups[1].Value} ({m.Groups[2].Value}x{m.Groups[3].Value})");
        return names;
    }

    // ---------- lint ----------
    public static IReadOnlyList<LintIssue> Lint(UiScene scene, JsonObject metrics, List<string> warnings)
    {
        var issues = new List<LintIssue>();
        foreach (string w in warnings) issues.Add(new("error", "", "asset", w));
        if (scene.DialogX < 0 || scene.DialogY < 0 || scene.DialogX + scene.DialogWidth > scene.ScreenWidth || scene.DialogY + scene.DialogHeight > scene.ScreenHeight)
            issues.Add(new("error", "dialog", "offscreen", $"La ventana ({scene.DialogWidth}x{scene.DialogHeight}) no entra en {scene.ScreenWidth}x{scene.ScreenHeight}."));
        var byId = scene.Components.ToDictionary(c => c.Id);
        foreach (var c in scene.Components)
        {
            if (c.Width <= 0 || c.Height <= 0) issues.Add(new("error", c.Id, "size", "Ancho/alto deben ser > 0."));
            if (c.X < 0 || c.Y < 0 || c.X + c.Width > scene.DialogWidth || c.Y + c.Height > scene.DialogHeight)
                issues.Add(new("warning", c.Id, "outside", $"Sale de la ventana: [{c.X},{c.Y},{c.Width},{c.Height}] en {scene.DialogWidth}x{scene.DialogHeight}."));
            if (c.Parent != null && byId.TryGetValue(c.Parent, out var p) && (c.X < p.X || c.Y < p.Y || c.X + c.Width > p.X + p.Width || c.Y + c.Height > p.Y + p.Height))
                issues.Add(new("warning", c.Id, "outside-parent", $"Sale de su contenedor {p.Id}."));
            if (c.Type == "button" && (c.Width < 16 || c.Height < 16)) issues.Add(new("warning", c.Id, "hit-target", "Botón menor a 16 px: difícil de clickear."));
            if (c.Type is "button" or "label" or "input" && c.Text != null && Regex.IsMatch(c.Text, @"\{[A-Za-z_]+\}")) issues.Add(new("warning", c.Id, "placeholder", "Texto con variable sin resolver: " + c.Text));
        }
        // Text fit, measured by NtlGui itself.
        if (metrics["components"] is JsonArray comps)
            foreach (var n in comps)
            {
                if ((string?)n!["type"] != "label") continue;
                string id = (string)n["id"]!; var r = (JsonArray)n["rect"]!;
                int w = (int)r[2]!, h = (int)r[3]!, tw = (int)n["textWidth"]!, th = (int)n["textHeight"]!, lines = (int)n["lines"]!;
                string owner = id.Split("__")[0];
                bool wrapAllowed = byId.TryGetValue(owner, out var oc) && oc.Wrap;
                // NtlGui skips the whole text when COMP_TEXT_UP/DOWN is set and the box is shorter than the text.
                if (oc != null && oc.VAlign is "bottom" or "up" && th > h) issues.Add(new("error", owner, "text-hidden", $"Con valign \"{oc.VAlign}\" NtlGui NO dibuja el texto si la caja ({h}px) es más baja que el texto ({th}px). Usá valign top/center o agrandá la caja."));
                else if (th > h + 1) issues.Add(new("warning", owner, "text-overflow", $"El texto ocupa {th}px de alto y la caja tiene {h}px ({lines} líneas): se dibuja fuera de la caja."));
                else if (lines > 1 && !wrapAllowed) issues.Add(new("warning", owner, "text-wrap", $"El texto no entra en una línea ({tw}px en {w}px) y NtlGui lo parte en {lines}. Ampliá la caja, achicá el texto o marcá \"wrap\": true."));
            }
        // Overlapping clickable controls.
        var buttons = scene.Components.Where(c => c.Type is "button" or "input").ToList();
        for (int i = 0; i < buttons.Count; i++)
            for (int j = i + 1; j < buttons.Count; j++)
            {
                var a = buttons[i]; var b = buttons[j];
                if (a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height)
                    issues.Add(new("warning", a.Id, "overlap", $"Se superpone con {b.Id}: el click puede ir al control equivocado."));
            }
        // Collapse repeats (qty0..qty14 with the same finding) into one line.
        return issues
            .GroupBy(i => (i.Severity, i.Code, Base: Regex.Replace(i.Id, @"\d+$", ""), Msg: Regex.Replace(i.Message, @"\b(t\d+|\w+\d+)\b", "#")))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { Id = $"{g.First().Id}…{g.Last().Id}", Message = g.First().Message + $" (×{g.Count()})" })
            .ToList();
    }

    // ---------- review / approval ----------
    public static string LayoutHash(string layoutPath) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(layoutPath)))[..16];
    static string ApprovalPath(string layoutPath) => Regex.Replace(Path.GetFullPath(layoutPath), @"\.uilayout\.json$", "", RegexOptions.IgnoreCase) + ".approval.json";

    public static async Task<object> ReviewAsync(string layoutPath, string outDir, IReadOnlyList<string>? variants, IReadOnlyList<(int W, int H)>? sizes, string? background, CancellationToken token = default)
    {
        var layout = UiLayout.Load(layoutPath);
        variants ??= layout.VariantNames();
        var screen = layout.Root["screen"] as JsonObject;
        sizes ??= [((int?)screen?["width"] ?? 1280, (int?)screen?["height"] ?? 720)];
        outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir);
        var renders = new List<RenderResult>();
        foreach (var size in sizes)
            foreach (string v in variants)
                renders.Add(await RenderAsync(layoutPath, v, Path.Combine(outDir, $"{layout.Name}.{v}.{size.W}x{size.H}.png"), size.W, size.H, background, token));
        string hash = LayoutHash(layoutPath);
        var review = new
        {
            schema = "dbo-ui-review/1", layout = layout.Name, layoutPath = Path.GetFullPath(layoutPath), layoutHash = hash,
            createdAt = DateTimeOffset.Now.ToString("o"), status = "pending",
            renders = renders.Select(r => new { r.Variant, size = $"{r.Width}x{r.Height}", png = Path.GetFileName(r.Png), issues = r.Issues }).ToArray(),
            errors = renders.Sum(r => r.Issues.Count(i => i.Severity == "error")),
            warnings = renders.Sum(r => r.Issues.Count(i => i.Severity == "warning")),
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "review.json"), JsonSerializer.Serialize(review, ContentSafety.Json), token);
        await File.WriteAllTextAsync(Path.Combine(outDir, "review.html"), ReviewHtml(layout.Name, hash, renders), token);
        return new { review = Path.Combine(outDir, "review.json"), html = Path.Combine(outDir, "review.html"), images = renders.Select(r => r.Png).ToArray(), layoutHash = hash, review.errors, review.warnings, issues = renders.SelectMany(r => r.Issues.Select(i => new { r.Variant, size = $"{r.Width}x{r.Height}", i.Severity, i.Id, i.Code, i.Message })).ToArray() };
    }

    static string ReviewHtml(string name, string hash, List<RenderResult> renders)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder($"<!doctype html><meta charset=utf-8><title>UI review · {E(name)}</title><style>body{{background:#111418;color:#e6e1d6;font:14px Segoe UI,Arial;margin:24px}}figure{{margin:0 0 32px}}img{{max-width:100%;border:1px solid #333}}.error{{color:#ff7a6b}}.warning{{color:#f2c14e}}code{{color:#9fc5ff}}</style><h1>{E(name)}</h1><p>Layout <code>{hash}</code>. Aprobar: <code>dbo-assets ui approve &lt;layout&gt; --hash {hash}</code></p>");
        foreach (var r in renders)
        {
            sb.Append($"<figure><figcaption><b>{E(r.Variant)}</b> · {r.Width}x{r.Height}</figcaption><img src=\"{E(Path.GetFileName(r.Png))}\"><ul>");
            foreach (var i in r.Issues) sb.Append($"<li class={i.Severity}>{E(i.Severity)} · <code>{E(i.Id)}</code> {E(i.Code)}: {E(i.Message)}</li>");
            sb.Append("</ul></figure>");
        }
        return sb.ToString();
    }

    public static object Approve(string layoutPath, string hash, string? by, string? note)
    {
        string current = LayoutHash(layoutPath);
        if (!hash.Equals(current, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"El layout cambió desde la revisión ({hash} → {current}). Volvé a generar la revisión y pedí aprobación otra vez.");
        var approval = new { schema = "dbo-ui-approval/1", layoutHash = current, approvedAt = DateTimeOffset.Now.ToString("o"), approvedBy = by ?? Environment.UserName, note };
        File.WriteAllText(ApprovalPath(layoutPath), JsonSerializer.Serialize(approval, ContentSafety.Json));
        return approval;
    }

    public static object Status(string layoutPath)
    {
        string current = LayoutHash(layoutPath), path = ApprovalPath(layoutPath);
        if (!File.Exists(path)) return new { layout = Path.GetFullPath(layoutPath), layoutHash = current, approved = false, reason = "sin aprobación" };
        var a = JsonNode.Parse(File.ReadAllText(path))!;
        bool ok = ((string?)a["layoutHash"])?.Equals(current, StringComparison.OrdinalIgnoreCase) == true;
        return new { layout = Path.GetFullPath(layoutPath), layoutHash = current, approved = ok, approvedHash = (string?)a["layoutHash"], approvedAt = (string?)a["approvedAt"], approvedBy = (string?)a["approvedBy"], reason = ok ? "aprobado" : "el layout cambió después de aprobarse" };
    }
}
