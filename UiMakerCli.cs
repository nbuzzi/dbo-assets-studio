using System.Text.Json.Nodes;

namespace DboAssetsStudio;

internal static class UiMakerCli
{
    public const string Help = """
UI Maker (see UI-MAKER.md):
  ui init <name> <output.uilayout.json>          creates a base layout
  ui render <layout> [--variant v] [--size 1280x720] [--bg screenshot.png] [--out output.png]
  ui lint <layout> [--variant v|all] [--size WxH]
  ui review <layout> [--variants a,b] [--sizes 1280x720,1920x1080] [--bg screenshot.png] [--out folder]
  ui approve <layout> --hash <review hash> [--by name] [--note text]   (only with the user's explicit OK)
  ui status <layout>
  ui codegen <layout> <output.h> [--allow-unapproved]
  ui assets                                       lists the client's embedded: images
  ui frm list [--filter text]                     client windows (.frm) and Flash movies
  ui frm render <window.frm> [--dialog id] [--size WxH] [--out png] [--text id=text] [--hide id] [--flash-time s] [--no-center]
                                                  draws the original window as the client builds it from gui.pak
  ui import <window.frm> <output.uilayout.json> [--dialog id] [--size WxH]
                                                  turns an existing window into an editable layout and measures the difference
  ui swf <name.swf|file.swf> [--frame n] [--time s] [--size WxH] [--out png] [--var path=value]
                                                  draws Flash/GFx with the client's NtlFlasher
Assets: file relative to the layout | gui:<file in texture\gui> | pak:<gui.pak>|<resource> | embedded:<name>
Real render: ui-render.exe links NtlGui.lib + RenderWare D3D9 (same fonts and text wrapping as the game).
""";

    public static async Task<object> RunAsync(string[] args)
    {
        var positional = new List<string>(); var options = new Dictionary<string, string>(); var multi = new Dictionary<string, List<string>>();
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
            {
                if (args[i] is "--allow-unapproved" or "--no-center") { options[args[i]] = "1"; continue; }
                if (args[i] is "--text" or "--hide" or "--var")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[i]);
                    if (!multi.TryGetValue(args[i], out var l)) multi[args[i]] = l = [];
                    l.Add(args[++i]); continue;
                }
                if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[i]);
                options[args[i]] = args[++i];
            }
            else positional.Add(args[i]);
        if (positional.Count == 0) throw new ArgumentException(Help);
        string? Opt(string k) => options.TryGetValue(k, out var v) ? v : null;
        (int, int)? Size(string? s)
        {
            if (s == null) return null;
            var p = s.ToLowerInvariant().Split('x');
            if (p.Length != 2 || !int.TryParse(p[0], out int w) || !int.TryParse(p[1], out int h)) throw new ArgumentException("Invalid size (use 1280x720): " + s);
            return (w, h);
        }
        void Need(int n) { if (positional.Count != n) throw new ArgumentException(Help); }
        Dictionary<string, string> Pairs(string key) =>
            (multi.TryGetValue(key, out var l) ? l : []).Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        switch (positional[0])
        {
            case "init":
                Need(3);
                return Init(positional[1], positional[2]);
            case "render":
            {
                Need(2);
                var layout = UiLayout.Load(positional[1]);
                string variant = Opt("--variant") ?? "default";
                var size = Size(Opt("--size"));
                string outPng = Opt("--out") ?? Path.Combine(layout.Directory, "renders", $"{layout.Name}.{variant}.png");
                return await UiRenderService.RenderAsync(positional[1], variant, outPng, size?.Item1, size?.Item2, Opt("--bg"));
            }
            case "lint":
            {
                Need(2);
                var layout = UiLayout.Load(positional[1]);
                var variants = Opt("--variant") is null or "all" ? layout.VariantNames() : [Opt("--variant")!];
                var size = Size(Opt("--size"));
                var results = new List<object>(); int errors = 0;
                string dir = Path.Combine(UiRenderService.CacheRoot, "lint", Guid.NewGuid().ToString("N"));
                foreach (var v in variants)
                {
                    var r = await UiRenderService.RenderAsync(positional[1], v, Path.Combine(dir, v + ".png"), size?.Item1, size?.Item2);
                    errors += r.Issues.Count(i => i.Severity == "error");
                    results.Add(new { variant = v, issues = r.Issues });
                }
                try { Directory.Delete(dir, true); } catch { }
                return new { layout = layout.Name, errors, variants = results };
            }
            case "review":
            {
                Need(2);
                var layout = UiLayout.Load(positional[1]);
                var variants = Opt("--variants")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sizes = Opt("--sizes")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Size(s.Trim())!.Value).ToList();
                string outDir = Opt("--out") ?? Path.Combine(layout.Directory, "review", $"{layout.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
                return await UiRenderService.ReviewAsync(positional[1], outDir, variants, sizes, Opt("--bg"));
            }
            case "approve":
                Need(2);
                return UiRenderService.Approve(positional[1], Opt("--hash") ?? throw new ArgumentException("approve requires --hash (the one from the review the user saw)."), Opt("--by"), Opt("--note"));
            case "status":
                Need(2);
                return UiRenderService.Status(positional[1]);
            case "codegen":
                Need(3);
                return UiCodegen.Generate(positional[1], positional[2], options.ContainsKey("--allow-unapproved"));
            case "assets":
                return new { embedded = UiRenderService.ListEmbedded() };
            case "frm" when positional.Count >= 2 && positional[1] == "list":
                return await UiFrmService.ListAsync(Opt("--filter"));
            case "frm" when positional.Count == 3 && positional[1] == "render":
            {
                var size = Size(Opt("--size")) ?? (1280, 720);
                string outPng = Opt("--out") ?? Path.Combine(UiRenderService.CacheRoot, "frm", Path.GetFileNameWithoutExtension(positional[2]) + ".png");
                var r = await UiFrmService.RenderFrmAsync(positional[2], outPng, size.Item1, size.Item2, Opt("--dialog"), !options.ContainsKey("--no-center"),
                    Pairs("--text"), multi.GetValueOrDefault("--hide"), double.Parse(Opt("--flash-time") ?? "0", System.Globalization.CultureInfo.InvariantCulture));
                return new { png = r.Png, tree = r.Dump, frm = r.Files.Frm, srf = r.Files.Srf, rsr = r.Files.Rsr, textures = r.Files.Textures.Count, flash = r.Files.Flash, warnings = r.Warnings };
            }
            case "import":
            {
                Need(3);
                var size = Size(Opt("--size")) ?? (1280, 720);
                return await UiFrmService.ImportAsync(positional[1], positional[2], Opt("--dialog"), size.Item1, size.Item2);
            }
            case "swf":
            {
                Need(2);
                var size = Size(Opt("--size")) ?? (1024, 768);
                string outPng = Opt("--out") ?? Path.Combine(UiRenderService.CacheRoot, "swf", Path.GetFileNameWithoutExtension(positional[1]) + ".png");
                return await UiFrmService.RenderSwfAsync(positional[1], outPng, size.Item1, size.Item2, int.Parse(Opt("--frame") ?? "-1"),
                    double.Parse(Opt("--time") ?? "0", System.Globalization.CultureInfo.InvariantCulture), Pairs("--var"));
            }
            default:
                throw new ArgumentException(Help);
        }
    }

    static object Init(string name, string path)
    {
        path = ContentSafety.SafeOutput(path);
        if (File.Exists(path)) throw new IOException("Already exists: " + path);
        var doc = new JsonObject
        {
            ["schema"] = UiLayout.Schema,
            ["name"] = name,
            ["screen"] = new JsonObject { ["width"] = 1280, ["height"] = 720 },
            ["dialog"] = new JsonObject { ["width"] = 420, ["height"] = 300, ["anchor"] = "center", ["movable"] = true, ["color"] = "#101418E8" },
            ["assets"] = new JsonObject(),
            ["strings"] = new JsonObject { ["title"] = name, ["body"] = "Texto de ejemplo", ["ok"] = "ACEPTAR" },
            ["components"] = new JsonArray(
                new JsonObject { ["id"] = "titleBar", ["type"] = "panel", ["rect"] = new JsonArray(0, 0, 420, 36), ["fill"] = "#1E2733FF" },
                new JsonObject { ["id"] = "title", ["type"] = "label", ["rect"] = new JsonArray(12, 0, 360, 36), ["text"] = "@title", ["fontSize"] = 110, ["bold"] = true, ["color"] = "#FFCD28" },
                new JsonObject { ["id"] = "btnClose", ["type"] = "button", ["rect"] = new JsonArray(388, 6, 24, 24), ["text"] = "X", ["fill"] = "#3A2020FF" },
                new JsonObject { ["id"] = "body", ["type"] = "label", ["rect"] = new JsonArray(16, 52, 388, 160), ["text"] = "@body", ["valign"] = "top", ["wrap"] = true, ["color"] = "#EBE1C8" },
                new JsonObject { ["id"] = "btnOk", ["type"] = "button", ["rect"] = new JsonArray(130, 244, 160, 36), ["text"] = "@ok", ["bold"] = true, ["fill"] = "#2E7D32FF", ["states"] = new JsonObject { ["focus"] = "#3E9B44FF", ["down"] = "#1F5E23FF", ["disabled"] = "#3A3F45FF" } }),
            ["variants"] = new JsonObject
            {
                ["en"] = new JsonObject { ["strings.body"] = "Sample text", ["strings.ok"] = "OK" },
                ["hover-ok"] = new JsonObject { ["btnOk.state"] = "focus" },
                ["disabled-ok"] = new JsonObject { ["btnOk.state"] = "disabled" },
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, UiLayout.Serialize(doc));
        return new { layout = path };
    }
}
