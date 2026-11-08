using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

/// <summary>Non-executing, span-based editor for native CNtlTSTrigger .t documents.</summary>
internal sealed class QuestDocument
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex FieldIdentifier = new(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant);
    private readonly Encoding encoding;
    private readonly bool bom;
    private readonly byte[] originalBytes;
    private readonly bool isNew;
    private readonly string newline;
    private readonly List<QuestNode> nodes = [];
    private readonly List<QuestDiagnostic> diagnostics = [];
    private bool parsed;
    private int cursor;
    private readonly List<int> lineStarts = [];
    public string SourcePath { get; }
    public string OriginalText { get; }
    public string Text { get; private set; }
    public string OriginalSha256 { get; }
    public string EncodingName { get; }
    public IReadOnlyList<QuestNode> Nodes => nodes;
    public IReadOnlyList<QuestNode> FlatNodes => nodes;
    public IReadOnlyList<QuestDiagnostic> Diagnostics => diagnostics;
    public bool CanMutate => parsed && !nodes.Any(n => n.Fields.GroupBy(f => f.Key).Any(g => g.Count() > 1));
    public bool Modified => Text != OriginalText;

    private QuestDocument(string path, byte[] bytes, bool newDocument = false)
    {
        SourcePath = Path.GetFullPath(path); originalBytes = bytes; isNew = newDocument;
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        try { OriginalText = Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); encoding = Utf8; EncodingName = bom ? "utf-8-bom" : "utf-8"; }
        catch (DecoderFallbackException) { OriginalText = Encoding.Latin1.GetString(bytes); encoding = Encoding.Latin1; EncodingName = "latin1-rawbytes"; }
        Text = OriginalText; newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"; Parse();
    }
    public static QuestDocument Load(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Límite de entrada quest: 8 MiB.");
        return new(path, File.ReadAllBytes(path));
    }
    internal static QuestDocument Create(string path, string text)
    {
        ContentSafety.SafeOutput(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("El archivo de quest nueva ya existe.");
        return new(path, Utf8.GetBytes(text), true);
    }
    public void ReplaceText(string text)
    {
        if (text.Length > 8 * 1024 * 1024 || encoding.GetByteCount(text) + (bom ? 3 : 0) > 8 * 1024 * 1024) throw new IOException("Límite de quest codificada: 8 MiB.");
        Text = text; Parse();
    }
    public void SetField(string nodeId, string key, string literal)
    {
        EnsureMutable(); CheckFieldIdentifier(key); CheckLiteral(literal);
        var node = Find(nodeId); var field = node.Fields.SingleOrDefault(x => x.Key == key);
        if (field != null) Patch(field.Start, field.Length, literal);
        else Patch(node.Close, 0, newline + Indent(node) + "\t" + key + " = " + literal + ";" + newline + Indent(node));
    }
    public string AddNode(string? parentId, string kind, IReadOnlyDictionary<string, string> fields)
    {
        EnsureMutable(); CheckIdentifier(kind);
        if (nodes.Count >= 50000) throw new InvalidDataException("Máximo 50000 nodos.");
        foreach (var field in fields) { CheckFieldIdentifier(field.Key); CheckLiteral(field.Value); }
        var parent = parentId == null ? null : Find(parentId);
        int offset = parent?.Close ?? Text.Length;
        string indent = parent == null ? "" : Indent(parent) + "\t";
        var chunk = new StringBuilder(newline + indent + kind + newline + indent + "{" + newline);
        foreach (var field in fields) chunk.Append(indent + "\t" + field.Key + " = " + field.Value + ";" + newline);
        chunk.Append(indent + "}" + newline + (parent == null ? "" : Indent(parent)));
        int start = offset + newline.Length + indent.Length;
        Patch(offset, 0, chunk.ToString());
        return nodes.First(x => x.Start == start).Id;
    }
    public string ResolveNodePath(IReadOnlyList<int> path)
    {
        IReadOnlyList<QuestNode> level = nodes.Where(n => n.ParentId == null).ToArray(); QuestNode? node = null;
        foreach (int index in path)
        {
            if (index < 0 || index >= level.Count) throw new ArgumentException("nodePath inválido.");
            node = level[index]; level = node.ChildrenIds.Select(Find).ToArray();
        }
        return node?.Id ?? throw new ArgumentException("nodePath vacío.");
    }
    public string ResolveContainer(int groupId, int containerId)
    {
        var group = nodes.SingleOrDefault(x => x.Kind == "CNtlTSGroup" && Integer(x, "gid") == groupId) ?? throw new ArgumentException("Grupo inexistente o ambiguo.");
        return nodes.SingleOrDefault(x => x.ParentId == group.Id && Integer(x, "cid") == containerId)?.Id ?? throw new ArgumentException("Contenedor inexistente o ambiguo.");
    }
    public IReadOnlyList<QuestDiagnostic> Validate() { Parse(); return diagnostics.ToArray(); }
    public object SaveCandidate(string outputDirectory, string? candidateName = null, string? note = null)
    {
        EnsureMutable(); var validation = Validate();
        if (validation.Any(x => x.Severity == "error")) throw new InvalidOperationException("La quest contiene errores de validación; no se guardó candidato.");
        string output = ContentSafety.SafeOutput(outputDirectory);
        if (SourcePath.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SourcePath.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("La salida no puede contener la fuente.");
        ContentSafety.NoLinks(SourcePath);
        using var source = isNew ? null : new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source != null && Convert.ToHexString(SHA256.HashData(source)) != OriginalSha256) throw new IOException("SHA-256 obsoleto: la quest fuente cambió.");
        if (isNew && (File.Exists(SourcePath) || Directory.Exists(SourcePath))) throw new IOException("La quest nueva ya existe.");
        byte[] candidateBytes = Encode(); string sha = Convert.ToHexString(SHA256.HashData(candidateBytes));
        string pending = ContentSafety.FreshDirectory(output);
        string name = Path.GetFileName(SourcePath);
        if (candidateName != null && (candidateName != Path.GetFileName(candidateName) || !Regex.IsMatch(candidateName, @"^[0-9]+\.t$"))) throw new ArgumentException("El candidato requiere nombre <tid>.t."); candidateName ??= name;
        Directory.CreateDirectory(Path.Combine(pending, "snapshot")); Directory.CreateDirectory(Path.Combine(pending, "candidate"));
        File.WriteAllBytes(Path.Combine(pending, "snapshot", name), originalBytes);

        File.WriteAllBytes(Path.Combine(pending, "candidate", candidateName), candidateBytes);
        ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { format = "quest-candidate-v1", sourcePath = SourcePath, sourceState = isNew ? "absent" : "existing", sourceSha256 = isNew ? null : OriginalSha256, snapshotOrigin = isNew ? "generated-skeleton" : "existing-source", installAllowed = false, runtimeTested = false, candidateSha256 = sha, encoding = EncodingName, created = isNew, modified = Modified, snapshot = "snapshot/" + name, candidate = "candidate/" + candidateName, note, diagnostics = validation, savedUtc = DateTimeOffset.UtcNow });
        Directory.Move(pending, output);
        return new { directory = output, candidate = Path.Combine(output, "candidate", candidateName), audit = Path.Combine(output, "audit.json"), sourceSha256 = OriginalSha256, candidateSha256 = sha };
    }
    private byte[] Encode()
    {
        var strict = (Encoding)encoding.Clone(); strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        byte[] data = strict.GetBytes(Text); if (data.Length + (bom ? 3 : 0) > 8 * 1024 * 1024) throw new IOException("Límite de quest codificada: 8 MiB."); return bom ? Encoding.UTF8.Preamble.ToArray().Concat(data).ToArray() : data;
    }
    private QuestNode Find(string id) => nodes.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Nodo quest inexistente.");
    private void EnsureMutable() { if (!CanMutate) throw new InvalidOperationException("La quest contiene errores; corrija el documento antes de editar o exportar."); }
    private void Patch(int start, int length, string replacement)
    {
        if ((long)Text.Length - length + replacement.Length > 8 * 1024 * 1024) throw new IOException("Límite de quest: 8 MiB.");
        ReplaceText(Text.Remove(start, length).Insert(start, replacement));
    }
    private string Indent(QuestNode node) { int start = Text.LastIndexOf('\n', Math.Max(0, node.Start - 1)); string s = Text[(start + 1)..node.Start]; return s.All(c => c is ' ' or '\t') ? s : ""; }
    private int Line(int at) { int index = lineStarts.BinarySearch(Math.Min(at, Text.Length)); return index >= 0 ? index + 1 : ~index; }
    private void Error(string message, int at) => diagnostics.Add(new("error", Line(at), message));
    private void Warn(string message, QuestNode node) => diagnostics.Add(new("warning", node.Line, message));
    private static void CheckFieldIdentifier(string value) { if (!FieldIdentifier.IsMatch(value)) throw new ArgumentException("Campo quest inválido."); }
    private static void CheckIdentifier(string value) { if (!Identifier.IsMatch(value)) throw new ArgumentException("Identificador quest inválido."); }
    private static bool IsLiteral(string value)
    {
        if (Number.IsMatch(value)) return true;
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return false;
        for (int i = 1; i < value.Length - 1; i++) { if (value[i] is '\r' or '\n' or '"') return false; if (value[i] == '\\' && ++i >= value.Length - 1) return false; }
        return true;
    }
    private static void CheckLiteral(string value)
    {
        if (value.Length > 8 * 1024 * 1024) throw new IOException("Límite de literal quest: 8 MiB.");
        if (!IsLiteral(value)) throw new ArgumentException("Se requiere entero o string entre comillas; no se ejecuta código.");
    }
    private void Skip()
    {
        while (cursor < Text.Length)
        {
            if (char.IsWhiteSpace(Text[cursor])) { cursor++; continue; }
            if (Text.AsSpan(cursor).StartsWith("//")) { while (cursor < Text.Length && Text[cursor] != '\n') cursor++; continue; }
            if (Text.AsSpan(cursor).StartsWith("/*")) { int end = Text.IndexOf("*/", cursor + 2, StringComparison.Ordinal); if (end < 0) throw new FormatException("Comentario sin cierre."); cursor = end + 2; continue; }
            break;
        }
    }
    private string ReadIdentifier(bool field = false)
    {
        Skip(); int start = cursor;
        while (cursor < Text.Length && (char.IsAsciiLetterOrDigit(Text[cursor]) || Text[cursor] == '_')) cursor++;
        string id = Text[start..cursor]; if (!(field ? FieldIdentifier : Identifier).IsMatch(id)) throw new FormatException("Se esperaba un identificador."); return id;
    }
    private void Expect(char token) { Skip(); if (cursor >= Text.Length || Text[cursor] != token) throw new FormatException("Se esperaba '" + token + "'."); cursor++; }
    private QuestNode ReadNode(string kind, int start, string? parent, int depth)
    {
        if (depth > 64 || nodes.Count >= 50000) throw new FormatException("Límite de profundidad (64) o nodos (50000).");
        Expect('{'); var node = new QuestNode { Id = "q" + nodes.Count, Kind = kind, Start = start, ParentId = parent, Line = Line(start) }; nodes.Add(node);
        while (true)
        {
            Skip(); if (cursor >= Text.Length) throw new FormatException("Nodo sin cierre.");
            if (Text[cursor] == '}') { node.Close = cursor++; node.End = cursor; break; }
            int fieldStart = cursor; string name = ReadIdentifier(true); Skip();
            if (cursor < Text.Length && Text[cursor] == '{') { if (!Identifier.IsMatch(name)) throw new FormatException("Tipo de nodo inválido."); var child = ReadNode(name, fieldStart, node.Id, depth + 1); node.ChildrenIds.Add(child.Id); continue; }
            Expect('='); Skip(); int literalStart = cursor;
            if (cursor < Text.Length && Text[cursor] == '"')
            {
                cursor++; bool closed = false;
                while (cursor < Text.Length) { char c = Text[cursor++]; if (c is '\r' or '\n') throw new FormatException("String con salto de línea."); if (c == '\\') { cursor++; continue; } if (c == '"') { closed = true; break; } }
                if (!closed || cursor > Text.Length) throw new FormatException("String sin cierre.");
            }
            else { if (cursor < Text.Length && Text[cursor] is '+' or '-') cursor++; while (cursor < Text.Length && char.IsAsciiDigit(Text[cursor])) cursor++; }
            string literal = Text[literalStart..cursor]; if (!IsLiteral(literal)) throw new FormatException("Literal quest inválido.");
            node.Fields.Add(new(name, literal, literal.StartsWith('"') ? literal[1..^1] : literal, literal.StartsWith('"') ? "string" : "number", literalStart, literal.Length)); Expect(';');
        }
        return node;
    }
    private void Parse()
    {
        nodes.Clear(); diagnostics.Clear(); cursor = 0; parsed = false; lineStarts.Clear(); lineStarts.Add(0);
        for (int i = 0; i < Text.Length; i++) if (Text[i] == '\n') lineStarts.Add(i + 1);
        try { Skip(); while (cursor < Text.Length) { int start = cursor; string kind = ReadIdentifier(); ReadNode(kind, start, null, 0); Skip(); } }
        catch (FormatException ex) { Error(ex.Message, cursor); return; }
        parsed = true; ValidateStructure();
    }
    private static long? Integer(QuestNode node, string key) { var f = node.Fields.FirstOrDefault(x => x.Key == key); return f?.Kind == "number" && long.TryParse(f.Literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null; }
    private static bool IsContainer(QuestNode node) => node.Kind == "CNtlTSCont" || node.Kind.StartsWith("CDboTSCont", StringComparison.Ordinal);
    private void ValidateStructure()
    {
        var roots = nodes.Where(x => x.ParentId == null).ToArray();
        if (roots.Length != 1 || roots[0].Kind != "CNtlTSTrigger") Error("Se requiere exactamente una raíz CNtlTSTrigger.", 0);
        var byId = nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            // AttachChildForScript: Trigger accepts Group, Group accepts Cont,
            // Cont accepts Entity. Plain Entity's default attachment is a no-op.
            // Keep unknown entity classes under containers with a warning, while
            // refusing misplaced structural nodes whose native traversal is wrong.
            QuestNode? parent = node.ParentId == null ? null : byId[node.ParentId];
            bool hierarchyValid = node.Kind == "CNtlTSTrigger" ? parent == null
                : node.Kind == "CNtlTSGroup" ? parent?.Kind == "CNtlTSTrigger"
                : IsContainer(node) ? parent?.Kind == "CNtlTSGroup"
                : parent != null && IsContainer(parent);
            if (!hierarchyValid) Error("Jerarquía nativa inválida: se requiere Trigger → Group → Contenedor → Entidad; " + node.Kind + " tiene padre " + (parent?.Kind ?? "raíz") + ".", node.Start);
            foreach (var duplicate in node.Fields.GroupBy(x => x.Key).Where(x => x.Count() > 1)) Error("Campo duplicado: " + duplicate.Key, node.Start);
            string? identity = node.Kind == "CNtlTSTrigger" ? "tid" : node.Kind == "CNtlTSGroup" ? "gid" : IsContainer(node) ? "cid" : null;
            if (identity != null && (Integer(node, identity) is not long id || id < 0 || id > (identity == "tid" ? 65534L : 254L))) Error("ID requerido o fuera de rango: " + identity, node.Start);
            foreach (var field in node.Fields.Where(x => x.Key is "tid" or "gid" or "cid" or "yeslnk" or "nolnk" or "nextlnk" or "elnk" or "oklnk" or "cancellnk" or "canclnk" or "taid" or "stdiag" or "minlvl" or "maxlvl"))
                if (field.Kind != "number" || !long.TryParse(field.Literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) Error("Campo nativo entero requerido: " + field.Key, field.Start);
            if (node.Kind != "CNtlTSTrigger" && node.Kind != "CNtlTSGroup" && !KnownKinds.Contains(node.Kind)) Warn("Tipo conservado sin esquema de autoría: " + node.Kind + ". Revisar en el motor nativo.", node);
        }
        foreach (var dup in nodes.Where(x => x.Kind == "CNtlTSGroup").GroupBy(x => Integer(x, "gid")).Where(g => g.Count() > 1)) Error("gid duplicado.", dup.First().Start);
        foreach (var group in nodes.Where(x => x.Kind == "CNtlTSGroup"))
        {
            var containers = nodes.Where(x => x.ParentId == group.Id && IsContainer(x)).ToArray();
            foreach (var dup in containers.GroupBy(x => Integer(x, "cid")).Where(g => g.Count() > 1)) Error("cid duplicado dentro del grupo.", dup.First().Start);
            var ids = containers.Select(x => Integer(x, "cid")).ToHashSet();
            var byCid = containers.Where(x => Integer(x, "cid").HasValue).GroupBy(x => Integer(x, "cid")!.Value).ToDictionary(g => g.Key, g => g.First());
            foreach (var node in containers)
            {
                foreach (var field in node.Fields.Where(x => x.Key != "prelnk" && x.Key.EndsWith("lnk", StringComparison.Ordinal)))
                {
                    if (Integer(node, field.Key) is not long target || target < 0 || target > 255) Error("Enlace requiere entero 0..255: " + field.Key, field.Start);
                    else if (target != 255 && !ids.Contains(target)) Error("Enlace " + field.Key + " apunta a cid inexistente " + target + ". 255 es el único sentinel nativo.", field.Start);
                    else if (target != 255 && byCid.TryGetValue(target, out var targetNode) && Integer(node, "cid") is long sourceId)
                    {
                        var targetPrevious = targetNode.Fields.FirstOrDefault(x => x.Key == "prelnk");
                        if (targetPrevious == null || !PreviousIds(targetPrevious).Contains(sourceId)) Warn("Revisar reciprocidad: " + field.Key + " hacia cid " + target + " no aparece en prelnk del destino.", node);
                    }
                }
                var previous = node.Fields.FirstOrDefault(x => x.Key == "prelnk");
                if (previous != null)
                {
                    if (previous.Kind != "string" || !Regex.IsMatch(previous.Value, @"^(?:[0-9; ]*)$")) Error("prelnk debe contener IDs separados por punto y coma.", previous.Start);
                    else foreach (string part in previous.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!long.TryParse(part, out long id) || !ids.Contains(id)) Warn("prelnk referencia cid ausente: " + part, node);
                        else if (byCid.TryGetValue(id, out var predecessor) && Integer(node, "cid") is long targetId && !predecessor.Fields.Any(f => f.Key != "prelnk" && f.Key.EndsWith("lnk", StringComparison.Ordinal) && Integer(predecessor, f.Key) == targetId)) Warn("Revisar reciprocidad: prelnk declara cid " + id + " sin enlace escalar de vuelta; puede requerir lógica dinámica del motor.", node);
                    }
                }
            }
        }
        if (isNew && roots.Length > 0) Warn("Esqueleto nuevo: faltan textos, evento/condiciones de aceptación, objetivos, recompensas y registro/tabla de quest. No desplegar sin completar y probar.", roots[0]);
    }
    private static IEnumerable<long> PreviousIds(QuestField field) => field.Kind == "string" ? field.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out long value) ? (long?)value : null).Where(x => x.HasValue).Select(x => x!.Value) : [];
    // Built-in authoring subset; all other native entities are retained losslessly.
    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal) { "CDboTSContStart", "CDboTSContEnd", "CDboTSContGAct", "CDboTSContGCond", "CDboTSContProposal", "CDboTSContReward", "CDboTSContUsrSel", "CDboTSCheckLvl", "CDboTSClickNPC", "CDboTSCheckClrQst", "CDboTSCheckPCCls", "CDboTSActNPCConv", "CDboTSActRegQInfo", "CDboTSActOutMsg", "CDboTSActQItem", "CDboTSActQItemDrop", "CDboTSCheckQItem", "CDboTSActWaitTS", "CDboTSActSendSvrEvt" };
}
internal sealed class QuestNode
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? ParentId { get; init; }
    public int Line { get; init; }
    public List<QuestField> Fields { get; } = [];
    public List<string> ChildrenIds { get; } = [];
    public int Start { get; init; }
    public int End { get; set; }
    internal int Close { get; set; }
    public override string ToString() => Kind + (Fields.FirstOrDefault(x => x.Key is "tid" or "gid" or "cid") is { } f ? " · " + f.Key + " " + f.Value : "");
}
internal sealed record QuestField(string Key, string Literal, string Value, string Kind, int Start, int Length) { public string ValueKind => Kind; }
internal sealed record QuestDiagnostic(string Severity, int Line, string Message);
