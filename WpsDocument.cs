using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

/// <summary>Lossless, non-executing editor for the small Lua-like WPS builder language.</summary>
internal sealed class WpsDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex NumberLiteralRegex = new(@"\G[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly bool _hadUtf8Bom;
    private readonly Encoding _encoding;
    private readonly string _newline;
    private List<WpsNode> _nodes = [];
    private List<WpsDiagnostic> _diagnostics = [];
    private List<int> _lineStarts = [];

    public string Dialect { get; }
    public string SourcePath { get; }
    public string OriginalText { get; }
    public string Text { get; private set; }
    public string OriginalSha256 { get; }
    public string EncodingName { get; }
    public IReadOnlyList<WpsNode> Nodes => Flatten(_nodes).ToList();
    public IReadOnlyList<WpsNode> Roots => _nodes;
    public IReadOnlyList<WpsNode> FlatNodes => Nodes;
    public IReadOnlyList<WpsDiagnostic> Diagnostics => _diagnostics;
    public bool CanMutate => !_diagnostics.Any(x => x.Severity == "error");
    public bool Modified => !string.Equals(Text, OriginalText, StringComparison.Ordinal);
    public string Newline => _newline;

    private WpsDocument(string path, byte[] bytes, string dialect = "wps")
    {
        Dialect = dialect;
        SourcePath = Path.GetFullPath(path);
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        _hadUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        if (_hadUtf8Bom)
        {
            _encoding = StrictUtf8;
            EncodingName = "utf-8-bom";
            OriginalText = StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try { OriginalText = StrictUtf8.GetString(bytes); _encoding = StrictUtf8; EncodingName = "utf-8"; }
            catch (DecoderFallbackException) { OriginalText = Encoding.Latin1.GetString(bytes); _encoding = Encoding.Latin1; EncodingName = "latin1-rawbytes"; }
        }
        Text = OriginalText;
        _newline = OriginalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Parse();
    }

    public static WpsDocument Load(string path) => LoadDialect(path, "wps");
    public static WpsDocument LoadTqs(string path) => LoadDialect(path, "tqs");
    private static WpsDocument LoadDialect(string path, string dialect)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Falta el archivo de script.", nameof(path));
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("Límite de entrada: 8 MiB.");
        return new WpsDocument(path, File.ReadAllBytes(path), dialect);
    }

    public void SetParam(string nodeId, string key, string newLiteral)
    {
        EnsureMutable();
        var node = Find(nodeId);
        var parsed = ParseLiteral(newLiteral, 0);
        if (parsed is null) throw new ArgumentException("El nuevo valor debe ser un literal número, string o booleano.");
        if (parsed.Value.Kind == "boolean") throw new ArgumentException("WPSScript.cpp no admite booleanos como Param; use un número o string literal.");
        EnsureRuntimeParamsSupported(node);
        var param = node.Params.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (param is not null) Replace(param.Start, param.Length, newLiteral);
        else
        {
            int at = node.End - ClosingEndLength(node);
            string indent = IndentAt(node.Start) + "\t";
            Replace(at, 0, indent + "Param( " + LuaQuote(key) + ", " + newLiteral + " )" + _newline);
        }
    }

    public string CloneNode(string nodeId)
    {
        EnsureMutable();
        var node = Find(nodeId);
        EnsureRuntimeParamsSupported(node);
        string chunk = Text[node.Start..node.End];
        string insert = _newline + chunk;
        Replace(node.End, 0, insert);
        return Flatten(_nodes).FirstOrDefault(n => n.Start == node.End + _newline.Length)?.Id ?? string.Empty;
    }

    public string ResolveNodePath(IReadOnlyList<int> path)
    {
        if (path.Count == 0 || path[0] < 0 || path[0] >= _nodes.Count) throw new ArgumentException("nodePath debe comenzar con un índice de raíz válido.");
        WpsNode node = _nodes[path[0]];
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] < 0 || path[i] >= node.Children.Count) throw new ArgumentException("nodePath contiene un índice de hijo inválido.");
            node = node.Children[path[i]];
        }
        return node.Id;
    }

    public string ResolveCcFloor(int floor, string? actionName = null)
    {
        var stages = Flatten(_nodes).Where(n => n.Kind == "Action" && n.Name.Equals("CCBD stage", StringComparison.OrdinalIgnoreCase) &&
            n.Params.Any(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase) && p.Value is double d && d == floor)).ToList();
        if (stages.Count != 1) throw new ArgumentException($"Se esperaba exactamente un CCBD stage {floor}; hay {stages.Count}.");
        if (string.IsNullOrWhiteSpace(actionName)) return stages[0].Id;
        var matching = Flatten(stages[0].Children).Where(n => n.Kind == "Action" && n.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count != 1) throw new ArgumentException($"En CCBD stage {floor} se esperaba un único Action('{actionName}'); hay {matching.Count}.");
        return matching[0].Id;
    }

    private static void EnsureRuntimeParamsSupported(WpsNode node)
    {
        var unsupported = Flatten(node.Children).Append(node).SelectMany(n => n.Params).FirstOrDefault(p => p.ValueKind == "boolean");
        if (unsupported is not null) throw new InvalidOperationException($"No se puede clonar/editar este nodo: Param '{unsupported.Key}' es booleano y el wrapper WPS solo acepta número/string.");
    }

    public void RemoveNode(string nodeId)
    {
        EnsureMutable();
        var node = Find(nodeId);
        Replace(node.Start, node.End - node.Start, string.Empty);
    }

    public void MoveNode(string nodeId, string direction)
    {
        EnsureMutable();
        var node = Find(nodeId);
        var siblings = node.Parent?.Children ?? _nodes.ToList();
        int index = siblings.IndexOf(node);
        int otherIndex = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? index - 1 : direction.Equals("down", StringComparison.OrdinalIgnoreCase) ? index + 1 : -1;
        if (otherIndex < 0 || otherIndex >= siblings.Count) throw new ArgumentException("Dirección inválida o nodo en el límite; use up/down.");
        var other = siblings[otherIndex];
        if (otherIndex < index) (node, other) = (other, node);
        string a = Text[node.Start..node.End], gap = Text[node.End..other.Start], b = Text[other.Start..other.End];
        Replace(node.Start, other.End - node.Start, b + gap + a);
    }

    public void MoveNode(string nodeId, int direction) => MoveNode(nodeId, direction < 0 ? "up" : direction > 0 ? "down" : throw new ArgumentOutOfRangeException(nameof(direction)));

    /// <summary>Undo/redo hook for the UI. Re-parses text while preserving source snapshot and encoding.</summary>
    public void ReplaceText(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Parse();
    }

    public string AddNode(string? parentId, string kind, string name, Dictionary<string, string> parameters)
    {
        EnsureMutable();
        kind = kind.Trim();
        string call = kind switch { "Action" => "Action", "Condition" => "Condition", "GameStage" => "GameStage", "GameFailed" when Dialect == "wps" => "GameFailed", "GameBegin" or "GameEnd" when Dialect == "tqs" => kind, _ => throw new ArgumentException("kind debe ser Action, Condition, GameStage o GameFailed.") };
        if (call == "GameStage")
        {
            if (parentId is not null) throw new ArgumentException("GameStage/GameFailed solo se agregan en la raíz.");
            if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stage) || stage is < 0 or > 254)
                throw new ArgumentException("GameStage requiere un entero decimal de 0 a 254.");
            name = stage.ToString(CultureInfo.InvariantCulture);
        }
        else if (call is "GameFailed" or "GameBegin" or "GameEnd")
        {
            if (parentId is not null) throw new ArgumentException("GameStage/GameFailed solo se agregan en la raíz.");
        }
        else if (parentId is null) throw new ArgumentException("Action/Condition requiere nodo padre.");
        var parent = parentId is null ? null : Find(parentId);
        string indent = parent is null ? "" : IndentAt(parent.Start) + "\t";
        string arg = call == "GameStage" ? name : call is "GameFailed" or "GameBegin" or "GameEnd" ? "" : LuaQuote(name);
        var sb = new StringBuilder();
        if (parent is null && Text.Length > 0 && Text[^1] is not ('\r' or '\n')) sb.Append(_newline);
        sb.Append(indent).Append(call).Append("( ").Append(arg).Append(" )").Append(_newline);
        foreach (var pair in parameters)
        {
            var parsed = ParseLiteral(pair.Value, 0);
            if (parsed is null || parsed.Value.Kind == "boolean") throw new ArgumentException($"El valor de Param '{pair.Key}' debe ser número/string; el wrapper de WPSScript.cpp no admite booleanos.");
            sb.Append(indent).Append("\tParam( ").Append(LuaQuote(pair.Key)).Append(", ").Append(pair.Value).Append(" )").Append(_newline);
        }
        sb.Append(indent).Append("End()").Append(_newline);
        int at = parent is null ? Text.Length : parent.End - ClosingEndLength(parent);
        Replace(at, 0, sb.ToString());
        return Flatten(_nodes).FirstOrDefault(n => n.Start == at + indent.Length)?.Id ?? string.Empty;
    }

    /// <summary>Duplicate a CCBD stage action and replace only its numeric stage Param.</summary>
    public string CloneCcFloor(int sourceFloor, int newFloor)
    {
        EnsureMutable();
        if (newFloor is < 0 or > 254) throw new ArgumentOutOfRangeException(nameof(newFloor), "CCBD stage debe estar entre 0 y 254; 255 es INVALID_BYTE.");
        var stages = Flatten(_nodes).Where(n => n.Kind == "Action" && n.Name.Equals("CCBD stage", StringComparison.OrdinalIgnoreCase) &&
            n.Params.Any(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase) && p.Value is double d && d == sourceFloor)).ToList();
        if (stages.Count != 1) throw new InvalidOperationException($"Se esperaba exactamente una Action(\"CCBD stage\") con Param stage={sourceFloor}; se encontraron {stages.Count}.");
        if (Flatten(_nodes).Any(n => n.Kind == "Action" && n.Name.Equals("CCBD stage", StringComparison.OrdinalIgnoreCase) &&
            n.Params.Any(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase) && p.Value is double d && d == newFloor)))
            throw new InvalidOperationException($"Ya existe CCBD stage {newFloor}.");
        var source = stages[0];
        EnsureRuntimeParamsSupported(source);
        string copy = Text[source.Start..source.End];
        var stageParam = source.Params.Last(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase));
        int offsetInCopy = stageParam.Start - source.Start;
        copy = copy.Remove(offsetInCopy, stageParam.Length).Insert(offsetInCopy, newFloor.ToString(CultureInfo.InvariantCulture));
        Replace(source.End, 0, _newline + copy);
        return Flatten(_nodes).FirstOrDefault(n => n.Start == source.End + _newline.Length)?.Id ?? string.Empty;
    }

    public IReadOnlyList<WpsDiagnostic> Validate()
    {
        Parse();
        return _diagnostics;
    }

    public object SaveCandidate(string outputDirectory)
    {
        EnsureMutable();
        var validation = Validate();
        if (validation.Any(x => x.Severity == "error")) throw new InvalidOperationException("La validación encontró errores; no se guardó candidato.");
        string output = ContentSafety.SafeOutput(outputDirectory);
        if (Path.GetFullPath(SourcePath).Equals(output, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(SourcePath).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("La salida no puede reemplazar ni contener el archivo fuente.");
        if (!File.Exists(SourcePath)) throw new IOException("El archivo fuente ya no existe; vuelva a cargarlo antes de guardar.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
            string originalFolder = Path.Combine(pending, "snapshot");
            string candidateFolder = Path.Combine(pending, "candidate");
            Directory.CreateDirectory(originalFolder);
            Directory.CreateDirectory(candidateFolder);
            string sourceName = Path.GetFileName(SourcePath);
            string originalPath = Path.Combine(originalFolder, sourceName);
            string candidatePath = Path.Combine(candidateFolder, sourceName);
            byte[] originalBytes;
            using (var source = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                originalBytes = SHA256.HashData(source);
                if (!Convert.ToHexString(originalBytes).Equals(OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("El archivo fuente cambió desde que se abrió; vuelva a cargarlo antes de guardar.");
                source.Position = 0;
                using var snapshot = new FileStream(originalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(snapshot);
                snapshot.Flush(true);
            }
            byte[] candidateBytes = Encode(Text);
            using (var fs = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(candidateBytes); fs.Flush(true); }
            string candidateSha = Convert.ToHexString(SHA256.HashData(candidateBytes));
            if (ContentSafety.Hash(candidatePath) != candidateSha || ContentSafety.Hash(originalPath) != OriginalSha256) throw new IOException("La revisión escrita no coincide con sus hashes; no se confirma la carpeta.");
            var audit = new { format = Dialect + "-candidate-v1", dialect = Dialect, sourcePath = SourcePath, sourceSha256 = OriginalSha256, snapshot = Path.GetRelativePath(pending, originalPath), candidate = Path.GetRelativePath(pending, candidatePath), candidateSha256 = candidateSha, encoding = EncodingName, savedUtc = DateTimeOffset.UtcNow, modified = Modified, diff = ExportDiff(), diagnostics = validation };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            Directory.Move(pending, output);
            return new { directory = output, original = Path.Combine(output, Path.GetRelativePath(pending, originalPath)), candidate = Path.Combine(output, Path.GetRelativePath(pending, candidatePath)), audit = Path.Combine(output, "audit.json"), sourceSha256 = OriginalSha256, candidateSha256 = candidateSha };
        }
        catch { throw; }
    }

    public string ExportDiff()
    {
        int prefix = 0; while (prefix < OriginalText.Length && prefix < Text.Length && OriginalText[prefix] == Text[prefix]) prefix++;
        int suffix = 0; while (suffix < OriginalText.Length - prefix && suffix < Text.Length - prefix && OriginalText[^(suffix + 1)] == Text[^(suffix + 1)]) suffix++;
        return $"firstChangedOffset={prefix}{_newline}originalLength={OriginalText.Length}{_newline}editedLength={Text.Length}{_newline}--- original excerpt ---{_newline}{OriginalText.Substring(Math.Max(0, prefix - 160), Math.Min(320, OriginalText.Length - Math.Max(0, prefix - 160)))}{_newline}--- edited excerpt ---{_newline}{Text.Substring(Math.Max(0, prefix - 160), Math.Min(320, Text.Length - Math.Max(0, prefix - 160)))}";
    }

    private byte[] Encode(string text)
    {
        Encoding strict = (Encoding)_encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        byte[] data = strict.GetBytes(text);
        if (_hadUtf8Bom) return Encoding.UTF8.GetPreamble().Concat(data).ToArray();
        return data;
    }
    private void Replace(int start, int length, string replacement)
    {
        if ((long)Text.Length - length + replacement.Length > 8 * 1024 * 1024) throw new IOException("La revisión excedería el límite de tamaño WPS.");
        string before = Text;
        Text = Text.Remove(start, length).Insert(start, replacement);
        Parse();
        if (!CanMutate)
        {
            Text = before;
            Parse();
            throw new InvalidOperationException("La edición dejaría una estructura WPS inválida; se revirtió automáticamente.");
        }
    }
    private void EnsureMutable() { if (!CanMutate) throw new InvalidOperationException("Edición bloqueada: WPS inseguro/no compatible o estructura inválida. Revisá Diagnostics."); }
    private WpsNode Find(string id) => Flatten(_nodes).FirstOrDefault(n => n.Id == id) ?? throw new ArgumentException("No existe nodo con Id=" + id);
    private string IndentAt(int pos) { int line = Text.LastIndexOf('\n', Math.Max(0, pos - 1)); int i = line + 1; while (i < Text.Length && Text[i] is ' ' or '\t') i++; return Text[(line + 1)..i]; }
    private int ClosingEndLength(WpsNode node) { var m = Regex.Match(Text[node.Start..node.End], @"End\s*\(\s*\)\s*$", RegexOptions.CultureInvariant); return m.Success ? m.Length : 0; }
    private static string LuaQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private void Parse()
    {
        _nodes = []; _diagnostics = [];
        _lineStarts = [0];
        for (int lineScan = 0; lineScan < Text.Length; lineScan++) if (Text[lineScan] == '\n') _lineStarts.Add(lineScan + 1);
        var stack = new Stack<WpsNode>();
        int i = 0;
        int nodeCount = 0;
        int ignoredNestedDepth = 0;
        while (i < Text.Length)
        {
            SkipTrivia(ref i, true);
            if (i >= Text.Length) break;
            int start = i;
            if (!IsIdentifierStart(Text[i])) { _diagnostics.Add(Diagnostic("error", "Código Lua/expresión no compatible; solo se admite el DSL WPS declarado.", i)); SkipLine(ref i); continue; }
            string id = ReadIdentifier(ref i);
            if ((Dialect == "tqs" && id == "GameFailed") || (!new[] { "GameStage", "GameFailed", "Action", "Condition", "Param", "End" }.Contains(id, StringComparer.Ordinal) && !(Dialect == "tqs" && id is "GameBegin" or "GameEnd")))
            { _diagnostics.Add(Diagnostic("error", $"Identificador ejecutable no permitido: {id}.", start)); SkipLine(ref i); continue; }
            SkipWhitespace(ref i);
            if (i >= Text.Length || Text[i++] != '(') { _diagnostics.Add(Diagnostic("error", $"Se esperaba '(' después de {id}.", start)); SkipLine(ref i); continue; }
            var args = new List<ParsedLiteral>();
            SkipTrivia(ref i);
            if (i < Text.Length && Text[i] != ')')
            {
                while (i < Text.Length)
                {
                    var literal = ReadLiteral(ref i);
                    if (literal is null) { _diagnostics.Add(Diagnostic("error", $"Argumento de {id} debe ser literal número/string/booleano.", i)); SkipLine(ref i); break; }
                    args.Add(literal.Value);
                    SkipTrivia(ref i);
                    if (i < Text.Length && Text[i] == ',') { i++; SkipTrivia(ref i); continue; }
                    break;
                }
            }
            SkipTrivia(ref i);
            if (i >= Text.Length || Text[i++] != ')') { _diagnostics.Add(Diagnostic("error", $"Llamada {id} sin ')' válido.", start)); continue; }
            int endCall = i;
            int expected = id switch { "GameFailed" or "GameBegin" or "GameEnd" or "End" => 0, _ => 1 };
            if (id == "Param") expected = 2;
            if (args.Count != expected) { _diagnostics.Add(Diagnostic("error", $"{id} requiere {expected} argumento(s) literal(es); encontró {args.Count}.", start)); continue; }
            bool opensNode = id is "GameStage" or "GameFailed" or "GameBegin" or "GameEnd" or "Action" or "Condition";
            if (ignoredNestedDepth > 0)
            {
                if (opensNode) ignoredNestedDepth++;
                else if (id == "End") ignoredNestedDepth--;
                continue;
            }
            if (opensNode && nodeCount >= 100_000) { _diagnostics.Add(Diagnostic("error", "Límite WPS de 100.000 nodos excedido.", start)); break; }
            if (opensNode && stack.Count >= 128) { _diagnostics.Add(Diagnostic("error", "Anidamiento WPS excede el límite seguro de 128.", start)); ignoredNestedDepth = 1; continue; }
            if (opensNode) nodeCount++;
            switch (id)
            {
                case "GameStage": case "GameFailed": case "GameBegin": case "GameEnd": case "Action": case "Condition":
                    bool isRootKind = id is "GameStage" or "GameFailed" or "GameBegin" or "GameEnd";
                    bool parentIsRoot = stack.Count > 0 && (stack.Peek().Kind is "GameStage" or "GameFailed" or "GameBegin" or "GameEnd");
                    if (parentIsRoot && isRootKind)
                        _diagnostics.Add(Diagnostic("error", "Stage/GameFailed no puede anidarse dentro de otra raíz.", start));
                    var val = id == "GameStage" ? args[0].Value : args.Count > 0 ? args[0].Value : null;
                    if (id == "GameStage" && val is not double) { _diagnostics.Add(Diagnostic("error", "GameStage requiere número literal.", start)); break; }
                    if (id == "GameStage" && val is double stageArg && (stageArg != Math.Truncate(stageArg) || stageArg < 0 || stageArg > 254))
                        _diagnostics.Add(Diagnostic("error", "GameStage debe ser entero entre 0 y 254; 255 se reserva como INVALID_BYTE.", start));
                    if ((id == "Action" || id == "Condition") && val is not string) { _diagnostics.Add(Diagnostic("error", id + " requiere string literal.", start)); break; }
                    if ((id == "Action" || id == "Condition") && stack.Count == 0) { _diagnostics.Add(Diagnostic("error", id + " requiere un nodo padre abierto.", start)); break; }
                    int? stageValue = null;
                    if (id == "GameStage" && val is double stageNumber && stageNumber >= int.MinValue && stageNumber <= int.MaxValue && stageNumber == Math.Truncate(stageNumber)) stageValue = (int)stageNumber;
                    var node = new WpsNode { Id = start.ToString(CultureInfo.InvariantCulture), Kind = id, Name = val?.ToString() ?? "", Stage = stageValue, Start = start, End = endCall, Line = LineAt(start), Parent = stack.Count == 0 ? null : stack.Peek() };
                    if (node.Parent is null) _nodes.Add(node); else node.Parent.Children.Add(node);
                    if (isRootKind && node.Parent is not null) _diagnostics.Add(Diagnostic("error", "Raíz WPS anidada.", start));
                    stack.Push(node);
                    break;
                case "Param":
                    if (stack.Count == 0) { _diagnostics.Add(Diagnostic("error", "Param fuera de un nodo.", start)); break; }
                    if (args[0].Value is not string key) { _diagnostics.Add(Diagnostic("error", "La clave Param debe ser string.", start)); break; }
                    stack.Peek().Params.Add(new WpsParam { Key = key, Literal = args[1].Raw, Value = args[1].Value, ValueKind = args[1].Kind, Start = args[1].Start, Length = args[1].Length });
                    break;
                case "End":
                    if (stack.Count == 0) { _diagnostics.Add(Diagnostic("error", "End sin nodo abierto.", start)); break; }
                    stack.Pop().End = endCall;
                    break;
            }
        }
        foreach (var open in stack) _diagnostics.Add(Diagnostic("error", $"{open.Kind} no tiene End() correspondiente.", open.Start));
        foreach (var n in Flatten(_nodes)) if (n.End == n.Start || n.End > Text.Length) _diagnostics.Add(Diagnostic("error", $"Nodo {n.Kind} sin End() correspondiente.", n.Start));
        foreach (var node in Flatten(_nodes))
        foreach (var p in node.Params.Where(p => p.ValueKind == "boolean"))
            _diagnostics.Add(Diagnostic("warning", $"Param '{p.Key}' usa booleano; WPSScript.cpp solo enlaza Param número/string.", p.Start, p.Length));
        foreach (var n in Flatten(_nodes).Where(n => n.Kind is "Action" or "Condition"))
        {
            bool known = n.Kind == "Action" ? (Dialect == "tqs" ? TqsActions : KnownActions).Contains(n.Name) : (Dialect == "tqs" ? TqsConditions : KnownConditions).Contains(n.Name);
            if (!known) _diagnostics.Add(Diagnostic("warning", $"{n.Kind} no está en {Dialect.ToUpperInvariant()}NodeFactory.cpp: '{n.Name}'. Se conserva para lectura.", n.Start));
        }
        AddSemanticDiagnostics();
    }

    private void AddSemanticDiagnostics()
    {
        foreach (string kind in new[] { "GameBegin", "GameEnd" })
            if (_nodes.Count(n => n.Kind == kind) > 1)
                foreach (var node in _nodes.Where(n => n.Kind == kind)) _diagnostics.Add(Diagnostic("error", kind + " duplicado.", node.Start));
        if (_nodes.Count(n => n.Kind == "GameFailed") > 1)
            foreach (var node in _nodes.Where(n => n.Kind == "GameFailed")) _diagnostics.Add(Diagnostic("error", "GameFailed duplicado: la revisión debe definir un único bloque de fallo.", node.Start));
        foreach (var group in _nodes.Where(n => n.Kind == "GameStage" && n.Stage.HasValue).GroupBy(n => n.Stage!.Value).Where(g => g.Count() > 1))
            foreach (var node in group) _diagnostics.Add(Diagnostic("error", $"GameStage duplicado: {group.Key}.", node.Start));
        foreach (var node in Flatten(_nodes).Where(n => n.Kind == "GameStage" && (n.Stage is < 0 or > 254)))
            _diagnostics.Add(Diagnostic("error", "GameStage debe estar entre 0 y 254; 255 se reserva como INVALID_BYTE.", node.Start));

        if (Dialect == "tqs") return;
        var floors = Flatten(_nodes).Where(n => n.Kind == "Action" && n.Name.Equals("CCBD stage", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var floor in floors)
        {
            var values = floor.Params.Where(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase)).ToList();
            if (values.Count != 1 || values[0].Value is not double value || value != Math.Truncate(value) || value < 0 || value > 254)
                _diagnostics.Add(Diagnostic("error", "Action CCBD stage requiere exactamente un Param(\"stage\", número entero 0..254).", floor.Start));
        }
        var numericFloors = floors.Select(f => (Node: f, Param: f.Params.FirstOrDefault(p => p.Key.Equals("stage", StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Param?.Value is double d && d == Math.Truncate(d) && d is >= 0 and <= 254).ToList();
        foreach (var group in numericFloors.GroupBy(x => (double)x.Param!.Value!).Where(g => g.Count() > 1))
            foreach (var item in group) _diagnostics.Add(Diagnostic("error", $"CCBD stage duplicado: {group.Key}.", item.Node.Start));
        int maxFloor = numericFloors.Count == 0 ? -1 : (int)numericFloors.Max(x => (double)x.Param!.Value!);
        foreach (var item in numericFloors.Where(x => (double)x.Param!.Value! > 150))
            _diagnostics.Add(Diagnostic("warning", "CCBD floor supera CCBD_MAX_STAGE=150 en NtlCCBD.h; requiere revisar/compilar cliente y servidor antes de aceptación.", item.Node.Start));
        foreach (var item in numericFloors.Where(x =>
        {
            int floor = (int)(double)x.Param!.Value!;
            bool lastStage = Flatten(x.Node.Children).Where(n => n.Kind == "Action" && n.Name.Equals("CCBD reward", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.Params).Any(p => p.Key.Equals("last stage", StringComparison.OrdinalIgnoreCase) && p.Value is string s && s.Equals("true", StringComparison.OrdinalIgnoreCase));
            return lastStage && floor < maxFloor;
        }))
            _diagnostics.Add(Diagnostic("warning", $"CCBD stage {(double)item.Param!.Value!} marca last stage=true aunque el mayor piso CCBD declarado es {maxFloor}; revisar Action(\"CCBD reward\") descendiente y flujo de finalización.", item.Node.Start));
    }

    // Exact native registration names from TQSNodeFactory.cpp; no Lua evaluation.
    private static readonly HashSet<string> TqsActions = new(StringComparer.Ordinal) { "add npc", "remove npc", "add mob", "add mobgroup", "remove mobgroup", "direct play", "wait", "change stage", "change object state", "time countdown", "send ts event", "send tqs event", "reward", "information", "broad message", "progress message", "direction indicate", "register condition", "teleport", "play bgm", "play jingle", "function" };
    private static readonly HashSet<string> TqsConditions = new(StringComparer.Ordinal) { "recv ts event", "recv sps event", "check time", "check mobgroup", "npc faint", "all player faint", "area enter", "child" };
    private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase) { "add mob", "add mobgroup", "add npc", "broad message", "calculate", "case", "change char attribute", "change npc attribute", "change object state", "set object state", "change sps scene", "change stage", "clear event", "default", "direction indicate", "direct play", "else", "event handler", "event status clear", "event status register", "event status select", "exec at once", "exec wps", "flash play", "function", "get faint location", "get location", "if", "mob list", "play bgm", "play jingle", "register buff", "remove mobgroup", "remove npc", "send ais event", "send event to ais", "send event to sps", "send event to tqs", "send event to ts", "send event to wps", "send sps event", "switch", "system message", "bot message", "telecast message", "then", "time countdown", "unregister buff", "wait", "while", "wps fail", "loop", "CCBD", "CCBD register direct play", "CCBD wait user", "CCBD stage", "CCBD exec pattern", "CCBD stage clear", "CCBD reward", "CCBD leave" };
    private static readonly HashSet<string> KnownConditions = new(StringComparer.OrdinalIgnoreCase) { "all player faint", "area enter", "check object state", "check battle", "check countdown", "check lp", "check mobgroup", "check time", "child", "compare", "loop cond", "npc faint", "recv event from ais", "recv event from item", "recv event from sps", "recv event from tqs", "recv event from ts", "recv event from wps", "recv sps event", "recv ts event", "spawned mob", "spawned npc", "wait condition", "CCBD check run state", "CCBD check limit time", "CCBD pattern" };

    private ParsedLiteral? ReadLiteral(ref int i)
    {
        int start = i;
        if (i >= Text.Length) return null;
        if (Text[i] is '\'' or '"')
        {
            char quote = Text[i++]; var value = new StringBuilder();
            while (i < Text.Length)
            {
                char c = Text[i++];
                if (c == quote) return new ParsedLiteral(value.ToString(), "string", Text[start..i], start, i - start);
                if (c is '\r' or '\n') return null;
                if (c == '\\' && i < Text.Length)
                {
                    char e = Text[i++];
                    if (e is '\r' or '\n') { if (e == '\r' && i < Text.Length && Text[i] == '\n') i++; continue; }
                    if (char.IsDigit(e))
                    {
                        int digits = 1, code = e - '0';
                        while (digits < 3 && i < Text.Length && char.IsDigit(Text[i])) { code = code * 10 + Text[i++] - '0'; digits++; }
                        if (code > 255) return null;
                        value.Append((char)code); continue;
                    }
                    switch (e)
                    {
                        case 'a': value.Append('\a'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'v': value.Append('\v'); break;
                        case '\\': case '\'': case '"': value.Append(e); break;
                        default: return null;
                    }
                }
                else value.Append(c);
            }
            return null;
        }
        if (IsIdentifierStart(Text[i]))
        {
            string word = ReadIdentifier(ref i);
            return word switch { "true" => new(true, "boolean", word, start, i - start), "false" => new(false, "boolean", word, start, i - start), _ => null };
        }
        var match = NumberLiteralRegex.Match(Text, i);
        if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return null;
        i += match.Length; return new ParsedLiteral(number, "number", Text[start..i], start, i - start);
    }
    private ParsedLiteral? ParseLiteral(string raw, int start)
    {
        string temp = Text; Text = raw;
        try { int i = 0; var value = ReadLiteral(ref i); SkipWhitespace(ref i); return value is not null && i == raw.Length ? value : null; }
        finally { Text = temp; }
    }
    private void SkipTrivia(ref int i, bool allowSemicolon = false)
    {
        while (i < Text.Length)
        {
            if (char.IsWhiteSpace(Text[i])) { i++; continue; }
            if (allowSemicolon && Text[i] == ';') { i++; continue; }
            if (Text[i] == '-' && i + 1 < Text.Length && Text[i + 1] == '-')
            {
                int commentStart = i;
                i += 2;
                if (TryLongComment(ref i))
                {
                    if (i < 0) { _diagnostics.Add(Diagnostic("error", "Comentario Lua largo sin cierre.", commentStart)); i = Text.Length; }
                }
                else SkipLine(ref i);
                continue;
            }
            break;
        }
    }
    private bool TryLongComment(ref int i)
    {
        int open = i;
        if (open >= Text.Length || Text[open] != '[') return false;
        int cursor = open + 1;
        while (cursor < Text.Length && Text[cursor] == '=') cursor++;
        if (cursor >= Text.Length || Text[cursor] != '[') return false;
        int level = cursor - open - 1;
        string close = "]" + new string('=', level) + "]";
        int end = Text.IndexOf(close, cursor + 1, StringComparison.Ordinal);
        if (end < 0) { i = -1; return true; }
        i = end + close.Length;
        return true;
    }
    private void SkipWhitespace(ref int i) { while (i < Text.Length && char.IsWhiteSpace(Text[i])) i++; }
    private void SkipLine(ref int i) { while (i < Text.Length && Text[i] is not ('\r' or '\n')) i++; }
    private string ReadIdentifier(ref int i) { int s = i++; while (i < Text.Length && (char.IsLetterOrDigit(Text[i]) || Text[i] == '_')) i++; return Text[s..i]; }
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private int LineAt(int pos)
    {
        int found = _lineStarts.BinarySearch(Math.Clamp(pos, 0, Text.Length));
        return found >= 0 ? found + 1 : ~found;
    }
    private WpsDiagnostic Diagnostic(string severity, string message, int start, int length = 1) => new() { Severity = severity, Message = message, Line = LineAt(Math.Clamp(start, 0, Text.Length)), Start = start, Length = length };
    private static IEnumerable<WpsNode> Flatten(IEnumerable<WpsNode> nodes) { foreach (var n in nodes) { yield return n; foreach (var child in Flatten(n.Children)) yield return child; } }

    private readonly record struct ParsedLiteral(object Value, string Kind, string Raw, int Start, int Length);
}

internal sealed class WpsNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Stage { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public int Line { get; set; }
    public List<WpsParam> Params { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public List<WpsNode> Children { get; set; } = [];
    public List<string> ChildrenIds => Children.Select(x => x.Id).ToList();
    [System.Text.Json.Serialization.JsonIgnore] public WpsNode? Parent { get; set; }
}
internal sealed class WpsParam { public string Key { get; set; } = ""; public string Literal { get; set; } = ""; public object? Value { get; set; } public string ValueKind { get; set; } = ""; public int Start { get; set; } public int Length { get; set; } }
internal sealed class WpsDiagnostic { public string Severity { get; set; } = ""; public string Message { get; set; } = ""; public int Line { get; set; } public int Start { get; set; } public int Length { get; set; } }
