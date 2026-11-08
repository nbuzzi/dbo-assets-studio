using System.Text.Json;

namespace DboAssetsStudio;

/// <summary>Explicit TQS dialect; scripts are parsed as data and never evaluated.</summary>
internal static class TqsCli
{
    public static object Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return new { commands = new[] { "inspect <file.tqs>", "export <file.tqs> <new.json>", "validate <file.tqs>", "plan <file.tqs> <new-plan.json>", "apply <file.tqs> <plan.json> <new-candidate-dir>", "new <id> <new-file.tqs>" }, safety = "Non-executing TQS authoring drafts; UD/TMQ maximum stays 5. Gameplay requires separate validation." };
        if (args[0] == "new")
        {
            Count(args, 3);
            var created = TqsTemplates.Create(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2]);
            return new { created.SourcePath, created.OriginalSha256, created.Dialect, draft = true, partyMaximum = 5, created.CanMutate, diagnostics = created.Diagnostics };
        }
        if (args.Length < 2) throw new ArgumentException("Falta el archivo TQS.");
        var doc = WpsDocument.LoadTqs(args[1]);
        switch (args[0])
        {
            case "inspect":
                Count(args, 2);
                return new { doc.SourcePath, doc.Dialect, doc.OriginalSha256, doc.EncodingName, doc.CanMutate, nodeCount = doc.FlatNodes.Count, diagnostics = doc.Diagnostics, nodes = doc.FlatNodes };
            case "validate":
                Count(args, 2);
                var diagnostics = doc.Validate();
                return new { doc.SourcePath, doc.Dialect, valid = doc.CanMutate, diagnostics, validationScope = "DSL syntax, native node names and root structure; not gameplay, parameter contracts, quest tables or assets." };
            case "export":
                Count(args, 3);
                ContentSafety.WriteJson(args[2], new { doc.SourcePath, doc.Dialect, doc.OriginalSha256, doc.EncodingName, text = doc.Text, nodes = doc.FlatNodes, diagnostics = doc.Diagnostics });
                return new { output = Path.GetFullPath(args[2]), sha256 = ContentSafety.Hash(args[2]) };
            case "plan":
                Count(args, 3);
                ContentSafety.WriteJson(args[2], new { format = "tqs-plan-v1", sourcePath = doc.SourcePath, sourceSha256 = doc.OriginalSha256, operations = Array.Empty<object>() });
                return new { plan = Path.GetFullPath(args[2]), sourceSha256 = doc.OriginalSha256, operations = 0 };
            case "apply":
                Count(args, 4);
                ApplyPlan(doc, args[2]);
                return doc.SaveCandidate(args[3]);
            default: throw new ArgumentException("Comando TQS desconocido: " + args[0]);
        }
    }

    public static void ApplyPlan(WpsDocument doc, string path)
    {
        if (doc.Dialect != "tqs") throw new ArgumentException("El plan TQS requiere LoadTqs explícito.");
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("Límite de plan: 8 MiB.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (String(root, "format") != "tqs-plan-v1") throw new InvalidDataException("Formato de plan TQS desconocido.");
        if (!String(root, "sourceSha256").Equals(doc.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("El plan no coincide con el SHA-256 del TQS cargado.");
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array) throw new InvalidDataException("El plan requiere operations[].");
        foreach (var operation in operations.EnumerateArray())
        {
            string op = String(operation, "op");
            string id = operation.TryGetProperty("nodePath", out var nodePath) ? doc.ResolveNodePath(nodePath.EnumerateArray().Select(n => n.GetInt32()).ToArray())
                : operation.TryGetProperty("nodeId", out var nodeId) ? nodeId.GetString() ?? "" : "";
            switch (op)
            {
                case "setParam": doc.SetParam(id, String(operation, "key"), String(operation, "literal")); break;
                case "clone": doc.CloneNode(id); break;
                case "add":
                    string? parent = operation.TryGetProperty("parentPath", out var parentPath) ? doc.ResolveNodePath(parentPath.EnumerateArray().Select(n => n.GetInt32()).ToArray())
                        : operation.TryGetProperty("parentId", out var parentId) && parentId.ValueKind != JsonValueKind.Null ? parentId.GetString() : null;
                    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (operation.TryGetProperty("params", out var values))
                        foreach (var value in values.EnumerateObject()) parameters.Add(value.Name, value.Value.GetString() ?? throw new InvalidDataException("params requiere literales como strings."));
                    doc.AddNode(parent, String(operation, "kind"), operation.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", parameters);
                    break;
                default: throw new InvalidDataException("Operación TQS no permitida: " + op);
            }
        }
        if (doc.Validate().Any(d => d.Severity == "error")) throw new InvalidDataException("El plan TQS no pasa validación.");
    }

    private static string String(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Falta string: " + key);
    private static void Count(string[] args, int expected) { if (args.Length != expected) throw new ArgumentException("Uso inválido; consultar tqs help."); }
}
