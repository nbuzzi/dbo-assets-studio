using System.Text;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Reader for the client's GUI scripts (.frm components, .srf surfaces, .rsr resources).
// Syntax: <type> <name> { key = value; ... nested blocks }  (srf/rsr: surface "name" { ... }).
internal sealed class GuiScriptBlock
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public GuiScriptBlock? Parent { get; init; }
    public List<(string Key, string Value)> Values { get; } = [];
    public List<GuiScriptBlock> Children { get; } = [];

    public string? Get(string key) => Values.LastOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> All(string key) => Values.Where(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value);
    public int Int(string key, int fallback = 0) => double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d) : fallback;
    public bool Bool(string key, bool fallback) => Get(key)?.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => fallback };
    public IEnumerable<GuiScriptBlock> Descendants() { foreach (var c in Children) { yield return c; foreach (var d in c.Descendants()) yield return d; } }
}

internal static class GuiScript
{
    // GUI scripts are Korean-era ANSI (cp949) files; ASCII keys parse the same either way.
    static readonly Encoding Ansi = CodePagesEncoding();
    static Encoding CodePagesEncoding()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949); }
        catch { return Encoding.Latin1; }
    }

    public static string ReadText(string path) => Ansi.GetString(File.ReadAllBytes(path));

    public static List<GuiScriptBlock> Parse(string text)
    {
        text = Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        var tokens = Regex.Matches(text, "\"(?:[^\"]*)\"|[{};=]|[^\\s{};=\"]+").Select(m => m.Value).ToList();
        int i = 0;
        var roots = new List<GuiScriptBlock>();
        List<GuiScriptBlock> ParseBlocks(GuiScriptBlock? parent, List<GuiScriptBlock> into)
        {
            while (i < tokens.Count)
            {
                if (tokens[i] == "}") { i++; return into; }
                // key = value ;
                if (i + 1 < tokens.Count && tokens[i + 1] == "=")
                {
                    string key = tokens[i]; i += 2;
                    var sb = new StringBuilder();
                    while (i < tokens.Count && tokens[i] != ";" && tokens[i] != "}") { if (sb.Length > 0) sb.Append(' '); sb.Append(Unquote(tokens[i])); i++; }
                    if (i < tokens.Count && tokens[i] == ";") i++;
                    parent?.Values.Add((key, sb.ToString()));
                    continue;
                }
                // type name { ... }
                if (i + 2 < tokens.Count && tokens[i + 2] == "{")
                {
                    var block = new GuiScriptBlock { Type = tokens[i].ToLowerInvariant(), Name = Unquote(tokens[i + 1]), Parent = parent };
                    i += 3; into.Add(block);
                    ParseBlocks(block, block.Children);
                    continue;
                }
                i++; // stray token
            }
            return into;
        }
        ParseBlocks(null, roots);
        return roots;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    // Files a window needs: its frm, every srf it names (surface_file) and every rsr those name (resource_file).
    public sealed record WindowFiles(string Frm, List<string> Srf, List<string> Rsr, List<string> Textures, List<string> Flash, List<string> Missing);

    public static WindowFiles Collect(string guiRoot, string frmName)
    {
        string Find(string name)
        {
            string direct = Path.Combine(guiRoot, "gui", name);
            if (File.Exists(direct)) return direct;
            string? hit = Directory.EnumerateFiles(Path.Combine(guiRoot, "gui"), Path.GetFileName(name), SearchOption.AllDirectories).FirstOrDefault();
            return hit ?? "";
        }
        string frm = File.Exists(frmName) ? Path.GetFullPath(frmName) : Find(frmName.EndsWith(".frm", StringComparison.OrdinalIgnoreCase) ? frmName : frmName + ".frm");
        if (frm.Length == 0) throw new FileNotFoundException("No existe el .frm en gui.pak: " + frmName);
        var missing = new List<string>();
        var blocks = Parse(ReadText(frm));
        // Always include the global pages the client loads at startup.
        var srf = new List<string> { "GlobalGui.srf" };
        foreach (var b in blocks.Concat(blocks.SelectMany(x => x.Descendants())))
            foreach (var s in b.All("surface_file")) if (!srf.Contains(s, StringComparer.Ordinal)) srf.Add(s);
        var rsr = new List<string> { "GlobalGui.rsr" };
        var textures = new List<string>();
        foreach (var s in srf)
        {
            string p = Find(s);
            if (p.Length == 0) { missing.Add(s); continue; }
            foreach (var sb in Parse(ReadText(p)))
                foreach (var r in sb.All("resource_file")) if (!rsr.Contains(r, StringComparer.Ordinal)) rsr.Add(r);
        }
        foreach (var r in rsr)
        {
            string p = Find(r);
            if (p.Length == 0) { missing.Add(r); continue; }
            foreach (var rb in Parse(ReadText(p)))
                if (rb.Get("file") is string f && f.Length > 0 && !textures.Contains(f, StringComparer.OrdinalIgnoreCase)) textures.Add(f);
        }
        var flash = blocks.Concat(blocks.SelectMany(x => x.Descendants())).Where(b => b.Type == "flash").Select(b => b.Get("filename")).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new WindowFiles(frm, srf, rsr, textures, flash, missing);
    }

    // Text styling from the frm (NtlGui does not expose fonts on live components).
    public sealed record TextInfo(string? Text, string Font, int Height, int Attr, uint Color, string Align, string VAlign, string? Effect, bool HasStyle);

    public static TextInfo Text(GuiScriptBlock b)
    {
        string align = "left", valign = "top"; bool has = false;
        foreach (var s in b.All("text_style"))
        {
            has = true;
            switch (s.Trim().ToLowerInvariant())
            {
                case "left": align = "left"; break;
                case "center": align = "center"; break;
                case "right": align = "right"; break;
                case "up" or "upper": valign = "up"; break;
                case "down": valign = "bottom"; break;
                case "verticalcenter": valign = "center"; break;
            }
        }
        int r = b.Int("text_color_red", 255), g = b.Int("text_color_green", 255), bl = b.Int("text_color_blue", 255);
        return new TextInfo(b.Get("text"), (b.Get("font_name") ?? "Default"), b.Int("font_height", 90), b.Int("font_attribute", 0) & 0xF,
            0xFF000000u | (uint)(r & 255) << 16 | (uint)(g & 255) << 8 | (uint)(bl & 255),
            align, valign, b.Get("text_effect_mode")?.ToLowerInvariant(), has);
    }
}
