using System.Text.Json;

namespace DboAssetsStudio;

internal static class QuestCli
{
    public static object Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return new { commands = new[] { "inspect <quest.t>", "validate <quest.t>", "plan <quest.t> <new-plan.json>", "apply <quest.t> <plan.json> <new-candidate-dir>", "new <tid> <new-candidate-dir>", "clone <source.t> <new-tid> <new-candidate-dir>" }, safety = "Analiza datos nativos sin ejecutar scripts. Genera candidatos auditados; no instala. 255 es enlace nativo inválido; 252/253 son contenedores reales." };
        if (args[0] == "new") { Count(args, 3); uint id = uint.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture); return QuestTemplates.NewSkeleton(id, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, id + ".t")).SaveCandidate(args[2]); }
        if (args[0] == "clone") { Count(args, 4); return QuestTemplates.CloneAsNew(args[1], uint.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]); }
        if (args.Length < 2) throw new ArgumentException("Falta el archivo .t.");
        var doc = QuestDocument.Load(args[1]);
        switch (args[0])
        {
            case "inspect": Count(args, 2); return new { doc.SourcePath, doc.OriginalSha256, doc.EncodingName, doc.CanMutate, nodes = doc.Nodes, diagnostics = doc.Diagnostics };
            case "validate": Count(args, 2); var validation = doc.Validate(); return new { valid = !validation.Any(x => x.Severity == "error"), diagnostics = validation };
            case "plan":
                Count(args, 3); ContentSafety.WriteJson(ContentSafety.SafeOutput(args[2]), new { format = "quest-plan-v1", sourcePath = doc.SourcePath, sourceSha256 = doc.OriginalSha256, operations = Array.Empty<object>() });
                return new { plan = Path.GetFullPath(args[2]), sourceSha256 = doc.OriginalSha256 };
            case "apply": Count(args, 4); ApplyPlan(doc, args[2]); return doc.SaveCandidate(args[3]);
            default: throw new ArgumentException("Comando quest desconocido.");
        }
    }
    public static void ApplyPlan(QuestDocument doc, string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Límite de plan: 8 MiB.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 }); var root = json.RootElement;
        if (String(root, "format") != "quest-plan-v1") throw new InvalidDataException("Formato de plan quest desconocido.");
        if (!String(root, "sourceSha256").Equals(doc.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("El SHA-256 del plan no coincide con la fuente.");
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() > 10000) throw new InvalidDataException("Se requiere operations[] (máximo 10000).");
        string before = doc.Text;
        try
        {
            foreach (var op in operations.EnumerateArray())
            {
                string type = String(op, "op");
                switch (type)
                {
                    case "setField": doc.SetField(Select(doc, op), String(op, "key"), String(op, "literal")); break;
                    case "add":
                        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var field in op.GetProperty("fields").EnumerateObject()) fields.Add(field.Name, field.Value.GetString() ?? throw new InvalidDataException("fields requiere literales string."));
                        doc.AddNode(Select(doc, op), String(op, "kind"), fields); break;
                    default: throw new InvalidDataException("Operación quest no permitida: " + type);
                }
            }
            if (doc.Validate().Any(x => x.Severity == "error")) throw new InvalidDataException("El plan genera una quest inválida.");
        }
        catch { doc.ReplaceText(before); throw; }
    }
    private static string Select(QuestDocument doc, JsonElement op)
    {
        if (op.TryGetProperty("nodePath", out var path)) return doc.ResolveNodePath(path.EnumerateArray().Select(x => x.GetInt32()).ToArray());
        if (op.TryGetProperty("groupId", out var group))
        {
            if (op.TryGetProperty("containerId", out var container)) return doc.ResolveContainer(group.GetInt32(), container.GetInt32());
            return doc.Nodes.Single(x => x.Kind == "CNtlTSGroup" && x.Fields.Any(f => f.Key == "gid" && f.Value == group.GetRawText())).Id;
        }
        if (op.TryGetProperty("nodeId", out var id) && id.ValueKind == JsonValueKind.String) return id.GetString()!;
        throw new InvalidDataException("Se requiere selector nodePath, groupId/containerId o nodeId.");
    }
    private static string String(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Falta string " + key + ".");
    private static void Count(string[] args, int count) { if (args.Length != count) throw new ArgumentException("Uso quest inválido; ver quest help."); }
}
