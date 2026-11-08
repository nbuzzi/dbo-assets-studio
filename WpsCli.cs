using System.Text.Json;

namespace DboAssetsStudio;

/// <summary>CLI entry points shared with the Content Studio WPS editor.</summary>
internal static class WpsCli
{
    public static object Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "refs") return References(args[1..]);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return new { commands = new[] { "inspect <file.wps>", "export <file.wps> <new.json>", "validate <file.wps>", "plan <file.wps> <plan.json>", "apply <file.wps> <plan.json> <new-candidate-dir>" }, safety = "WPS Lua is parsed as data; it is never executed." };
        string command = args[0];
        if (args.Length < 2) throw new ArgumentException("Falta el archivo WPS.");
        if (new FileInfo(args[1]).Length > 8L * 1024 * 1024) throw new IOException("Límite de entrada WPS: 8 MiB.");
        var doc = WpsDocument.Load(args[1]);
        switch (command)
        {
            case "inspect":
                Count(args, 2, command);
                return new { sourcePath = doc.SourcePath, doc.OriginalSha256, doc.EncodingName, sourceLength = doc.OriginalText.Length, nodeCount = doc.FlatNodes.Count, doc.CanMutate, diagnostics = doc.Diagnostics, nodes = doc.FlatNodes };
            case "export":
                Count(args, 3, command);
                var exported = ContentSafety.SafeOutput(args[2]);
                if (File.Exists(exported) || Directory.Exists(exported)) throw new IOException("La salida de exportación debe ser nueva.");
                ContentSafety.WriteJson(exported, new { sourcePath = doc.SourcePath, doc.OriginalSha256, doc.EncodingName, text = doc.OriginalText, nodes = doc.FlatNodes, diagnostics = doc.Diagnostics });
                return new { output = exported, sha256 = ContentSafety.Hash(exported) };
            case "validate":
                Count(args, 2, command);
                var diagnostics = doc.Validate();
                return new { sourcePath = doc.SourcePath, sha256 = doc.OriginalSha256, valid = !diagnostics.Any(x => x.Severity == "error"), diagnostics };
            case "plan":
                Count(args, 3, command);
                var planPath = ContentSafety.SafeOutput(args[2]);
                if (File.Exists(planPath) || Directory.Exists(planPath)) throw new IOException("El plan debe ser un archivo nuevo.");
                ContentSafety.WriteJson(planPath, new { format = "wps-plan-v1", sourcePath = doc.SourcePath, sourceSha256 = doc.OriginalSha256, operations = Array.Empty<object>() });
                return new { plan = planPath, sourceSha256 = doc.OriginalSha256, operations = 0 };
            case "apply":
                Count(args, 4, command);
                ApplyPlan(doc, args[2]);
                return doc.SaveCandidate(args[3]);
            default: throw new ArgumentException("Comando WPS desconocido: " + command);
        }
    }

    private static object References(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("wps refs <RDF-folder|tbl.pak> <item|mob|group> [--query texto] [--spawns folder] [--context exact-context] [--page 0]");
        var options = new Dictionary<string, string>();
        for (int i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--query" or "--spawns" or "--context" or "--page") || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Opción refs desconocida o duplicada.");
        }
        var schemas = RdfSchema.LoadCatalog(Path.Combine(AppContext.BaseDirectory, "taiwan.txt"));
        var catalog = Directory.Exists(args[0]) ? WpsReferenceCatalog.LoadFolder(args[0], schemas, options.GetValueOrDefault("--spawns"))
            : WpsReferenceCatalog.LoadPakAsync(PakArchive.OpenAsync(args[0]).GetAwaiter().GetResult(), schemas, options.GetValueOrDefault("--spawns")).GetAwaiter().GetResult();
        if (args[1] is not ("item" or "mob" or "group")) throw new ArgumentException("Kind debe ser item, mob o group.");
        int page = options.TryGetValue("--page", out var p) ? int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new { contexts = catalog.Rows.Where(r => r.Kind == "group").Select(r => r.Context).Distinct(), notes = catalog.Notes,
            rows = catalog.Query(args[1], options.GetValueOrDefault("--query", ""), options.GetValueOrDefault("--context"), page, 60) };
    }

    private static void ApplyPlan(WpsDocument doc, string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("Límite de plan WPS: 8 MiB.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.TryGetProperty("format", out var format) && format.GetString() != "wps-plan-v1") throw new InvalidDataException("Formato de plan WPS desconocido.");
        if (!root.TryGetProperty("sourceSha256", out var sha) || !string.Equals(sha.GetString(), doc.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("El plan no coincide con el SHA-256 del WPS cargado.");
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array) throw new InvalidDataException("El plan requiere operations[].");
        foreach (var operation in operations.EnumerateArray())
        {
            string op = GetString(operation, "op");
            string id = operation.TryGetProperty("nodeId", out var n) ? n.GetString() ?? "" : "";
            if (operation.TryGetProperty("nodePath", out var pathElement))
            {
                if (pathElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("nodePath debe ser un array de índices.");
                id = doc.ResolveNodePath(pathElement.EnumerateArray().Select(x => x.GetInt32()).ToArray());
            }
            else if (operation.TryGetProperty("ccFloor", out var floorElement))
            {
                string? actionName = operation.TryGetProperty("nodeName", out var nameElement) ? nameElement.GetString() : null;
                id = doc.ResolveCcFloor(floorElement.GetInt32(), actionName);
            }
            switch (op)
            {
                case "setParam": doc.SetParam(id, GetString(operation, "key"), GetString(operation, "literal")); break;
                case "clone": doc.CloneNode(id); break;
                case "remove": doc.RemoveNode(id); break;
                case "move": doc.MoveNode(id, GetString(operation, "direction")); break;
                case "cloneCcFloor": doc.CloneCcFloor(operation.GetProperty("sourceFloor").GetInt32(), operation.GetProperty("newFloor").GetInt32()); break;
                case "add":
                    string? parent = operation.TryGetProperty("parentId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
                    if (operation.TryGetProperty("parentPath", out var parentPath)) parent = doc.ResolveNodePath(parentPath.EnumerateArray().Select(x => x.GetInt32()).ToArray());
                    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (operation.TryGetProperty("params", out var parametersJson))
                        foreach (var property in parametersJson.EnumerateObject()) parameters.Add(property.Name, property.Value.GetString() ?? throw new InvalidDataException("Cada params.value debe ser un literal WPS en forma de texto."));
                    doc.AddNode(parent, GetString(operation, "kind"), GetString(operation, "name"), parameters); break;
                default: throw new InvalidDataException("Operación WPS no permitida: " + op);
            }
        }
        var errors = doc.Validate().Where(x => x.Severity == "error").ToArray();
        if (errors.Length != 0) throw new InvalidOperationException("El plan no pasa validación: " + string.Join(" | ", errors.Select(x => $"línea {x.Line}: {x.Message}")));
    }

    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"Falta string '{name}' en el plan WPS.");
    private static void Count(string[] args, int expected, string command) { if (args.Length != expected) throw new ArgumentException($"Uso inválido de wps {command}; ver help."); }
}
