using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// UI Maker layout document (dbo-ui-layout/1).
// A layout describes one game window with the same building blocks the client
// uses in code (gui::CDialog, CPanel, CStaticBox, CButton, CProgressBar...).
// It is the single source of truth: renders, review captures and the C++
// generated for the client all come from it.
internal sealed class UiLayout
{
    public const string Schema = "dbo-ui-layout/1";
    public static readonly string[] Types = ["panel", "label", "button", "progress", "icon", "input", "scroll", "group", "repeat", "flash"];

    public required string Path { get; init; }
    public required JsonObject Root { get; init; }
    private bool _includeHidden;
    public string Name => (string?)Root["name"] ?? System.IO.Path.GetFileNameWithoutExtension(Path).Replace(".uilayout", "");
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public static UiLayout Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Layout not found: " + path);
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("The layout must be a JSON object.");
        if ((string?)node["schema"] != Schema) throw new InvalidDataException($"schema must be \"{Schema}\".");
        return new UiLayout { Path = path, Root = node };
    }

    // Indented JSON with short numeric arrays (rect, src, step...) kept on one line.
    public static string Serialize(JsonObject root) =>
        Regex.Replace(root.ToJsonString(ContentSafety.Json), @"\[\s*(-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?){0,5})\s*\]",
            m => "[" + Regex.Replace(m.Groups[1].Value, @"\s*,\s*", ", ") + "]");

    public IReadOnlyList<string> VariantNames()
    {
        var names = new List<string> { "default" };
        if (Root["variants"] is JsonObject v) foreach (var kv in v) if (kv.Key != "default") names.Add(kv.Key);
        return names;
    }

    // Resolve a variant into a flat, absolute render scene.
    public UiScene Resolve(string variant = "default", int? screenW = null, int? screenH = null, bool includeHidden = false)
    {
        var root = (JsonObject)Root.DeepClone();
        if (variant != "default")
        {
            if (root["variants"] is not JsonObject variants || variants[variant] is not JsonObject overrides) throw new ArgumentException("Unknown variant: " + variant + ". Available: " + string.Join(", ", VariantNames()));
            ApplyOverrides(root, overrides);
        }
        else if (root["variants"] is JsonObject variants && variants["default"] is JsonObject defaults) ApplyOverrides(root, defaults);

        var screen = root["screen"] as JsonObject;
        int sw = screenW ?? Int(screen?["width"], 1280), sh = screenH ?? Int(screen?["height"], 720);
        var dialog = root["dialog"] as JsonObject ?? throw new InvalidDataException("dialog is missing.");
        int dw = Int(dialog["width"], 0), dh = Int(dialog["height"], 0);
        if (dw <= 0 || dh <= 0) throw new InvalidDataException("dialog.width/height must be > 0.");
        int dx = Int(dialog["x"], 0), dy = Int(dialog["y"], 0);
        string anchor = (string?)dialog["anchor"] ?? "center";
        (int ax, int ay) = anchor switch
        {
            "center" => ((sw - dw) / 2, (sh - dh) / 2),
            "top-left" => (0, 0),
            "top" => ((sw - dw) / 2, 0),
            "top-right" => (sw - dw, 0),
            "left" => (0, (sh - dh) / 2),
            "right" => (sw - dw, (sh - dh) / 2),
            "bottom-left" => (0, sh - dh),
            "bottom" => ((sw - dw) / 2, sh - dh),
            "bottom-right" => (sw - dw, sh - dh),
            _ => throw new InvalidDataException("Invalid dialog.anchor: " + anchor)
        };
        var scene = new UiScene
        {
            Layout = Name, Variant = variant, ScreenWidth = sw, ScreenHeight = sh,
            DialogX = ax + dx, DialogY = ay + dy, DialogWidth = dw, DialogHeight = dh,
            Background = ResolveAsset(root, (string?)root["background"] ?? (string?)dialog["background"]),
            DialogColor = Color((string?)dialog["color"], 0),
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        _includeHidden = includeHidden;
        if (root["components"] is JsonArray comps) Flatten(root, comps, scene, 0, 0, dw, dh, null, ids, new Dictionary<string, string>());
        return scene;
    }

    private void Flatten(JsonObject root, JsonArray comps, UiScene scene, int ox, int oy, int pw, int ph, string? parent, HashSet<string> ids, Dictionary<string, string> vars)
    {
        foreach (var item in comps)
        {
            if (item is not JsonObject c) throw new InvalidDataException("Each component must be an object.");
            bool hiddenFlag = (bool?)c["hidden"] == true;
            if (hiddenFlag && !_includeHidden) continue;
            string type = (string?)c["type"] ?? throw new InvalidDataException("Component without type.");
            if (!Types.Contains(type)) throw new InvalidDataException($"Unknown type \"{type}\". Valid: {string.Join(", ", Types)}");
            string id = Expand((string?)c["id"] ?? "", vars);
            if (type == "repeat")
            {
                int count = Int(c["count"], 0), cols = Math.Max(1, Int(c["columns"], count));
                var step = c["step"] as JsonArray; int sx = Int(step?[0], 0), sy = Int(step?[1], 0);
                var origin = c["origin"] as JsonArray; int rx = Int(origin?[0], 0), ry = Int(origin?[1], 0);
                if (count < 0 || count > 512) throw new InvalidDataException($"repeat {id}: count out of range.");
                var data = c["data"] as JsonArray;
                for (int i = 0; i < count; i++)
                {
                    var v = new Dictionary<string, string>(vars) { ["i"] = i.ToString(CultureInfo.InvariantCulture), ["n"] = (i + 1).ToString(CultureInfo.InvariantCulture) };
                    if (data != null && i < data.Count && data[i] is JsonObject row) foreach (var kv in row) v[kv.Key] = kv.Value is JsonValue jv ? jv.ToString() : kv.Value?.ToJsonString() ?? "";
                    if (c["children"] is JsonArray ch) Flatten(root, ch, scene, ox + rx + (i % cols) * sx, oy + ry + (i / cols) * sy, pw, ph, parent, ids, v);
                }
                continue;
            }
            if (id.Length == 0) id = type + scene.Components.Count.ToString(CultureInfo.InvariantCulture);
            if (!Regex.IsMatch(id, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException($"Invalid id \"{id}\" (letters, digits and _).");
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate id: " + id);
            var r = c["rect"] as JsonArray ?? throw new InvalidDataException($"{id}: rect [x,y,w,h] is missing.");
            if (r.Count != 4) throw new InvalidDataException($"{id}: rect must be [x,y,w,h].");
            int x = ox + Int(r[0], 0, vars), y = oy + Int(r[1], 0, vars), w = Int(r[2], 0, vars), h = Int(r[3], 0, vars);
            var comp = new UiComponent
            {
                Id = id, Type = type, Parent = parent, X = x, Y = y, Width = w, Height = h,
                Text = c["text"] is null ? null : Expand(Str(root, c["text"]), vars),
                FontSize = Int(c["fontSize"], 90), Bold = (bool?)c["bold"] ?? false,
                TextColor = Color(ExpandOrNull((string?)c["color"], vars), 0xFFFFFFFF),
                Align = (string?)c["align"] ?? (type == "button" ? "center" : "left"),
                VAlign = (string?)c["valign"] ?? "center",
                Image = ResolveImage(root, c["image"], vars),
                Font = (string?)c["font"] ?? "Default", FontAttr = Int(c["fontAttr"], 0),
                File = c["file"] is JsonValue fv ? ResolveAsset(root, Expand((string)fv!, vars)) : null, Frame = Int(c["frame"], -1),
                Fill = Color(ExpandOrNull((string?)c["fill"], vars), 0),
                Value = (double?)c["value"] ?? 0,
                State = (string?)c["state"] ?? "normal",
                Shadow = (bool?)c["shadow"] ?? false,
                Wrap = (bool?)c["wrap"] ?? false,
                Movable = (bool?)c["movable"] ?? false,
                Clip = (bool?)c["clip"] ?? false,
                Hidden = hiddenFlag,
                Note = (string?)c["note"],
            };
            if (c["images"] is JsonArray imgs) foreach (var n in imgs) if (ResolveImage(root, n, vars) is UiImage im) comp.Images.Add(im);
            if (c["vars"] is JsonObject fvars) foreach (var kv in fvars) comp.FlashVars[kv.Key] = kv.Value?.ToString() ?? "";
            if (c["states"] is JsonObject states)
                foreach (var kv in states)
                    if (kv.Value is JsonValue sv && ((string?)sv)?.StartsWith('#') == true) comp.StateFills[kv.Key] = Color((string?)sv, 0);
                    else if (kv.Value is JsonArray list) { var l = list.Select(n => ResolveImage(root, n, vars)).OfType<UiImage>().ToList(); comp.StateImages[kv.Key] = l.FirstOrDefault(); comp.StateImageLists[kv.Key] = l; }
                    else comp.StateImages[kv.Key] = ResolveImage(root, kv.Value, vars);
            if (c["bar"] is JsonNode bar) comp.BarImage = ResolveImage(root, bar, vars);
            scene.Components.Add(comp);
            if (c["children"] is JsonArray children) Flatten(root, children, scene, x, y, w, h, id, ids, vars);
        }
    }

    private UiImage? ResolveImage(JsonObject root, JsonNode? node, Dictionary<string, string> vars)
    {
        if (node is null) return null;
        if (node is JsonValue) return new UiImage { File = ResolveAsset(root, Expand((string)node!, vars)) };
        var o = (JsonObject)node;
        var src = o["src"] as JsonArray;
        if (o["asset"] is null && o["fill"] is JsonValue fillOnly)
        {
            var dd = o["rect"] as JsonArray;
            return new UiImage { File = null, Tint = Color(ExpandOrNull((string?)fillOnly, vars), 0), Dest = dd is { Count: 4 } ? [Int(dd[0], 0, vars), Int(dd[1], 0, vars), Int(dd[2], 0, vars), Int(dd[3], 0, vars)] : null };
        }
        return new UiImage
        {
            File = ResolveAsset(root, Expand((string?)o["asset"] ?? throw new InvalidDataException("image without asset."), vars)),
            SrcX = Int(src?[0], 0, vars), SrcY = Int(src?[1], 0, vars), SrcWidth = Int(src?[2], -1, vars), SrcHeight = Int(src?[3], -1, vars),
            Tint = Color((string?)o["tint"], 0xFFFFFFFF),
            Slice = o["slice"] is JsonArray s && s.Count == 4 ? [Int(s[0], 0), Int(s[1], 0), Int(s[2], 0), Int(s[3], 0)] : null,
            Dest = o["rect"] is JsonArray d && d.Count == 4 ? [Int(d[0], 0, vars), Int(d[1], 0, vars), Int(d[2], 0, vars), Int(d[3], 0, vars)] : null,
        };
    }

    // Assets: "name" from the assets map, a path relative to the layout, or pak:<pak>|<resource>.
    public string? ResolveAsset(JsonObject root, string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        if (root["assets"] is JsonObject assets && assets[reference] is JsonValue alias) reference = (string)alias!;
        if (Regex.IsMatch(reference!, "^(pak|gui|embedded|flash):", RegexOptions.IgnoreCase)) return reference;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, reference));
    }

    private static string Str(JsonObject root, JsonNode? node)
    {
        string s = (string?)node ?? "";
        // "@key" pulls a string from the strings table, so ES/EN variants only override strings.
        if (s.StartsWith('@') && root["strings"] is JsonObject strings && strings[s[1..]] is JsonValue v) return (string)v!;
        return s;
    }

    private static void ApplyOverrides(JsonObject root, JsonObject overrides)
    {
        foreach (var (key, value) in overrides)
        {
            // "strings.title", "dialog.width", "<componentId>.<prop>"
            int dot = key.IndexOf('.');
            if (dot <= 0) throw new InvalidDataException("Invalid override (use id.prop): " + key);
            string head = key[..dot], prop = key[(dot + 1)..];
            JsonObject? target = head switch
            {
                "strings" => root["strings"] as JsonObject ?? (JsonObject)(root["strings"] = new JsonObject()),
                "dialog" => root["dialog"] as JsonObject,
                "screen" => root["screen"] as JsonObject ?? (JsonObject)(root["screen"] = new JsonObject()),
                _ => FindComponent(root["components"] as JsonArray, head)
            };
            if (target is null) throw new InvalidDataException("Override points to a missing id: " + head);
            target[prop] = value?.DeepClone();
        }
    }

    internal static JsonObject? FindComponent(JsonArray? comps, string id)
    {
        if (comps is null) return null;
        foreach (var n in comps)
            if (n is JsonObject o)
            {
                if ((string?)o["id"] == id) return o;
                if (FindComponent(o["children"] as JsonArray, id) is JsonObject found) return found;
            }
        return null;
    }

    private static string? ExpandOrNull(string? s, Dictionary<string, string> vars) => s is null ? null : Expand(s, vars);

    private static string Expand(string s, Dictionary<string, string> vars)
    {
        if (vars.Count == 0 || !s.Contains('{')) return s;
        return Regex.Replace(s, @"\{([A-Za-z_][A-Za-z0-9_]*)([+\-*]\d+)?\}", m =>
        {
            if (!vars.TryGetValue(m.Groups[1].Value, out var val)) return m.Value;
            if (!m.Groups[2].Success || !long.TryParse(val, out long n)) return val;
            long k = long.Parse(m.Groups[2].Value[1..], CultureInfo.InvariantCulture);
            return (m.Groups[2].Value[0] switch { '+' => n + k, '-' => n - k, _ => n * k }).ToString(CultureInfo.InvariantCulture);
        });
    }

    private static int Int(JsonNode? node, int fallback, Dictionary<string, string>? vars = null)
    {
        if (node is null) return fallback;
        if (node is JsonValue v && v.TryGetValue<int>(out int i)) return i;
        if (node is JsonValue d && d.TryGetValue<double>(out double dbl)) return (int)Math.Round(dbl);
        string s = vars is null ? node.ToString() : Expand(node.ToString(), vars);
        // Small arithmetic: "12+{i*40}" after expansion → "12+80".
        var m = Regex.Match(s, @"^\s*(-?\d+)\s*(([+\-])\s*(\d+))?\s*$");
        if (!m.Success) throw new InvalidDataException("Invalid number: " + s);
        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Success ? (m.Groups[3].Value == "+" ? a + int.Parse(m.Groups[4].Value) : a - int.Parse(m.Groups[4].Value)) : a;
    }

    // "#RRGGBB" or "#RRGGBBAA" → ARGB
    public static uint Color(string? s, uint fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        if (!Regex.IsMatch(s, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) throw new InvalidDataException("Invalid color (use #RRGGBB or #RRGGBBAA): " + s);
        uint rgb = uint.Parse(s.AsSpan(1, 6), NumberStyles.HexNumber);
        uint a = s.Length == 9 ? uint.Parse(s.AsSpan(7, 2), NumberStyles.HexNumber) : 255;
        return a << 24 | rgb;
    }
}

internal sealed class UiScene
{
    public required string Layout { get; init; }
    public required string Variant { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public int DialogX { get; init; }
    public int DialogY { get; init; }
    public int DialogWidth { get; init; }
    public int DialogHeight { get; init; }
    public string? Background { get; set; }
    public uint DialogColor { get; init; }
    public List<UiComponent> Components { get; } = [];
}

internal sealed class UiComponent
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Parent { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Text { get; init; }
    public int FontSize { get; init; }
    public bool Bold { get; init; }
    public uint TextColor { get; init; }
    public string Align { get; init; } = "left";
    public string VAlign { get; init; } = "center";
    public UiImage? Image { get; set; }
    public UiImage? BarImage { get; set; }
    public Dictionary<string, UiImage?> StateImages { get; } = [];
    public Dictionary<string, uint> StateFills { get; } = [];
    public Dictionary<string, List<UiImage>> StateImageLists { get; } = [];
    public List<UiImage> Images { get; } = [];
    public string Font { get; init; } = "Default";
    public int FontAttr { get; init; }
    public string? File { get; init; }
    public int Frame { get; init; } = -1;
    public Dictionary<string, string> FlashVars { get; } = [];
    public uint Fill { get; init; }
    public double Value { get; init; }
    public string State { get; init; } = "normal";
    public bool Shadow { get; init; }
    public bool Wrap { get; init; }
    public bool Movable { get; init; }
    public bool Clip { get; init; }
    public bool Hidden { get; init; }   // children are clipped to this component (NtlGui clipping)
    public string? Note { get; init; }
}

internal sealed class UiImage
{
    public string? File { get; set; }
    public int SrcX { get; init; }
    public int SrcY { get; init; }
    public int SrcWidth { get; init; } = -1;   // -1 = whole texture (no src given)
    public int SrcHeight { get; init; } = -1;
    public uint Tint { get; init; } = 0xFFFFFFFF;
    public int[]? Slice { get; init; }
    public int[]? Dest { get; init; }   // [x,y,w,h] inside the component; null = whole component
}
