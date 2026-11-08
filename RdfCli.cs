using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DboAssetsStudio;

/// <summary>
/// Small, schema-driven command surface for automation and the Content Studio UI.
/// Args are the tokens after the top-level "rdf" command. All RDF bytes are read
/// and written through Taiwan's existing schema/row codecs; schema is never inferred
/// from row contents.
/// </summary>
internal static class RdfCli
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Returns a serializable result; errors propagate to the UI/CLI boundary.</summary>
    public static object Run(string[] args)
    {
            if (args.Length == 0) throw new ArgumentException("Usage: rdf compare <base.rdf> <ours.rdf> <theirs.rdf> | reconcile-plan <base.rdf> <ours.rdf> <theirs.rdf> <decisions.json> <new-plan.json> | reconcile-preview <plan.json> | reconcile-apply <plan.json> <new-output-dir> | schemas | identities <rdf> | native-merge <target.rdf> <source.rdf> <plan.json> <new-output-dir> | inspect <rdf> | export <rdf> <new.json|xml> | apply <rdf> <patch.json> <new-output-dir> | map-clone <table_text_all_data.rdf> <patch.json> <new-output-dir> | text-inspect <table_text_all_data.rdf> item|title | text-merge <target.rdf> <source.rdf> <plan.json> <new-output-dir>");
            return args[0].ToLowerInvariant() switch
            {
                "schemas" when args.Length == 1 => Schemas(),
                "text-reconcile-plan" when args.Length == 6 => TextAllReconcileService.Plan(args[1], args[2], args[3], args[4], args[5]),
                "text-reconcile-preview" when args.Length == 2 => TextAllReconcileService.Preview(args[1]),
                "text-reconcile-apply" when args.Length == 3 => TextAllReconcileService.Apply(args[1], args[2]),
                "quest-text-plan" when args.Length == 6 => QuestTextReconcileService.Plan(args[1], args[2], args[3], args[4], args[5]),
                "quest-text-compare" when args.Length == 4 => QuestTextReconcileService.Compare(args[1], args[2], args[3]),
                "quest-text-preview" when args.Length == 2 => QuestTextReconcileService.Preview(args[1]),
                "quest-text-apply" when args.Length == 3 => QuestTextReconcileService.Apply(args[1], args[2]),
                "create-plan" when args.Length == 5 => RdfCreateService.Plan(args[1], args[2], args[3], args[4]),
                "create-preview" when args.Length == 2 => RdfCreateService.Preview(args[1]),
                "create-apply" when args.Length == 3 => RdfCreateService.Apply(args[1], args[2]),
                "compare" when args.Length == 4 => RdfReconcileService.Compare(args[1], args[2], args[3]),
                "reconcile-plan" when args.Length == 6 => RdfVariableTextReconcileService.Handles(args[1])
                    ? RdfVariableTextReconcileService.Plan(args[1], args[2], args[3], args[4], args[5])
                    : RdfReconcileService.Plan(args[1], args[2], args[3], args[4], args[5]),
                "reconcile-preview" when args.Length == 2 => RdfVariableTextReconcileService.IsPlan(args[1])
                    ? RdfVariableTextReconcileService.Preview(args[1]) : RdfReconcileService.Preview(args[1]),
                "reconcile-apply" when args.Length == 3 => RdfVariableTextReconcileService.IsPlan(args[1])
                    ? RdfVariableTextReconcileService.Apply(args[1], args[2]) : RdfReconcileService.Apply(args[1], args[2]),
                "identities" when args.Length == 2 => Identities(args[1]),
                "protected-check" when args.Length == 4 => ProtectedCheck(args[1], args[2], args[3]),
                "native-merge" when args.Length == 5 => NativeTableMergeService.Merge(args[1], args[2], args[3], args[4]),
                "item-additive-plan" when args.Length == 5 => RdfAdditiveImportService.Plan(args[1], args[2], args[3], args[4]),
                "item-additive-preview" when args.Length == 2 => RdfAdditiveImportService.Preview(args[1]),
                "item-additive-apply" when args.Length == 3 => RdfAdditiveImportService.Apply(args[1], args[2]),
                "row-additive-plan" when args.Length == 5 => RdfAdditiveImportService.Plan(args[1], args[2], args[3], args[4]),
                "row-additive-preview" when args.Length == 2 => RdfAdditiveImportService.Preview(args[1]),
                "row-additive-apply" when args.Length == 3 => RdfAdditiveImportService.Apply(args[1], args[2]),
                "inspect" when args.Length == 2 => Inspect(args[1]),
                "export" when args.Length == 3 => Export(args[1], args[2]),
                "apply" when args.Length == 4 => Apply(args[1], args[2], args[3]),
                "map-clone" when args.Length == 4 => MapNameRdfService.Clone(args[1], args[2], args[3]),
                "text-inspect" when args.Length == 3 => TextAllRdfService.Inspect(args[1], args[2]),
                "text-preservation" when args.Length == 3 => TextAllRdfService.Preservation(args[1], args[2]),
                "text-inspect-all" when args.Length == 2 => TextAllRdfService.InspectAll(args[1]),
                "text-merge" when args.Length == 5 => TextAllRdfService.Merge(args[1], args[2], args[3], args[4]),
                "text-merge-records" when args.Length == 5 => TextAllRdfService.MergeNativeRecords(args[1], args[2], args[3], args[4]),
                "text-add" when args.Length == 4 => TextAllAddService.Add(args[1], args[2], args[3]),
                _ => throw new ArgumentException("Invalid RDF command or argument count; invoke rdf without arguments for usage.")
            };
    }

    private static object ProtectedCheck(string before, string candidate, string selection)
    {
        foreach (string path in new[] { before, candidate, selection }) ContentSafety.NoLinks(path);
        using var beforeLock = new FileStream(before, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var candidateLock = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var selectionLock = new FileStream(selection, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (selectionLock.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Bounded protected-row selection required.");
        string selectionHash = Convert.ToHexString(SHA256.HashData(selectionLock)); selectionLock.Position = 0;
        using var json = JsonDocument.Parse(selectionLock);
        TerrainGeometryService.NoDuplicateKeys(json.RootElement);
        var input = json.RootElement;
        if (input.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Protected-row selection version1 required.");
        var (baseDoc, key) = Open(before);
        // Use the same explicitly registered schema for both snapshots.
        var proposed = RdfDocument.Open(candidate, baseDoc.Schema);
        if (!RequiredString(input, "sourceSha256").Equals(baseDoc.OriginalHash, StringComparison.OrdinalIgnoreCase) ||
            RequiredString(input, "schema") != baseDoc.Schema.Name || proposed.Margin != baseDoc.Margin)
            throw new InvalidDataException("Protected-row selection/base/schema mismatch.");
        var baseRows = baseDoc.Rows.ToDictionary(r => baseDoc.Schema.Decode(key, r.Current), StringComparer.Ordinal);
        var nextRows = proposed.Rows.ToDictionary(r => baseDoc.Schema.Decode(key, r.Current), StringComparer.Ordinal);
        string[] missing = baseRows.Keys.Except(nextRows.Keys).Order().ToArray();
        if (missing.Length != 0) throw new InvalidDataException("Candidate loses base IDs: " + string.Join(",", missing.Take(20)));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var protectedRows = input.GetProperty("rows");
        if (protectedRows.ValueKind != JsonValueKind.Array || protectedRows.GetArrayLength() is < 1 or > 10000)
            throw new InvalidDataException("Explicit nonempty bounded protection cohort required.");
        foreach (var row in protectedRows.EnumerateArray())
        {
            string id = RequiredString(row, "id"), expected = RequiredString(row, "rowSha256");
            if (!ids.Add(id) || !baseRows.TryGetValue(id, out var original) ||
                !expected.Equals(Convert.ToHexString(SHA256.HashData(original.Current)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Duplicate, absent or stale protected ID: " + id);
            if (!original.Current.AsSpan().SequenceEqual(nextRows[id].Current))
                throw new InvalidDataException("Protected row changed (including padding): " + id);
        }
        return new { schema = baseDoc.Schema.Name, sourceSha256 = baseDoc.OriginalHash, candidateSha256 = proposed.OriginalHash,
            selectionSha256 = selectionHash, baseCount = baseRows.Count, candidateCount = nextRows.Count,
            protectedCount = ids.Count, protectedRowsByteExact = true, allBaseIdsPreserved = true,
            runtimeModified = false, gameplayTested = false };
    }

    private static object Schemas()
    {
        var schemas = RdfSchema.LoadCatalog(LocateCatalog());
        return new { ok = true, catalog = LocateCatalog(), version = 2, schemas = schemas.Select(s => new
        {
            s.Name, s.FileName, s.Sheet, s.RowSize, keyField = s.KeyField,
            fields = s.Fields.Select(f => new { f.Name, f.Type, f.Count, f.Offset, f.Size, f.IsPadding })
        }).ToArray() };
    }

    private static object Inspect(string path)
    {
        var (doc, key) = Open(path);
        return new { ok = true, version = 2, product = "DBO Assets Studio Taiwan 2.0", source = Path.GetFullPath(path),
            sourceSha256 = doc.OriginalHash, schema = doc.Schema.Name, fileName = doc.Schema.FileName,
            rowSize = doc.Schema.RowSize, rowCount = doc.Rows.Count, margin = doc.Margin,
            fields = doc.Schema.Fields.Select(f => new { f.Name, f.Type, f.Count, f.Offset, f.Size, f.IsPadding }).ToArray(),
            rows = doc.Rows.Select(r => new { index = r.Index, id = doc.Schema.Decode(key, r.Current) }).ToArray() };
    }

    private static object Export(string path, string outputPath)
    {
        var (doc, key) = Open(path);
        string fullOutput = ContentSafety.SafeOutput(outputPath);
        string ext = Path.GetExtension(fullOutput);
        var fields = doc.Schema.Fields.Select(f => new { name = f.Name, type = f.Type, count = f.Count, offset = f.Offset, padding = f.IsPadding }).ToArray();
        byte[] bytes;
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var export = new
            {
                format = "dbo-taiwan-rdf-export", version = 2, product = "DBO Assets Studio Taiwan 2.0",
                source = Path.GetFullPath(path), sourceSha256 = doc.OriginalHash, schema = doc.Schema.Name,
                fileName = doc.Schema.FileName, sheet = doc.Schema.Sheet, rowSize = doc.Schema.RowSize,
                rowCount = doc.Rows.Count, margin = doc.Margin, fields,
                rows = doc.Rows.Select(r => new
                {
                    index = r.Index, id = doc.Schema.Decode(key, r.Current), rawBase64 = Convert.ToBase64String(r.Current),
                    values = doc.Schema.Fields.ToDictionary(f => f.Name, f => doc.Schema.Decode(f, r.Current), StringComparer.Ordinal)
                }).ToArray()
            };
            bytes = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
        }
        else if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var root = new XElement("rdfExport", new XAttribute("format", "dbo-taiwan-rdf-export"), new XAttribute("version", 2),
                new XAttribute("product", "DBO Assets Studio Taiwan 2.0"), new XAttribute("source", Path.GetFullPath(path)),
                new XAttribute("sourceSha256", doc.OriginalHash), new XAttribute("schema", doc.Schema.Name),
                new XAttribute("fileName", doc.Schema.FileName), new XAttribute("sheet", doc.Schema.Sheet),
                new XAttribute("rowSize", doc.Schema.RowSize), new XAttribute("rowCount", doc.Rows.Count), new XAttribute("margin", doc.Margin),
                new XElement("fields", doc.Schema.Fields.Select(f => new XElement("field", new XAttribute("name", f.Name),
                    new XAttribute("type", f.Type), new XAttribute("count", f.Count), new XAttribute("offset", f.Offset),
                    new XAttribute("padding", f.IsPadding)))),
                new XElement("rows", doc.Rows.Select(r => new XElement("row", new XAttribute("index", r.Index),
                    new XAttribute("id", doc.Schema.Decode(key, r.Current)), new XAttribute("rawBase64", Convert.ToBase64String(r.Current)),
                    new XElement("values", doc.Schema.Fields.Select(f => new XElement("value", new XAttribute("field", f.Name),
                        new XAttribute("type", f.Type), SafeXmlText(doc.Schema.Decode(f, r.Current)))))))));
            bytes = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString());
        }
        else throw new ArgumentException("Export extension must be .json or .xml.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        using (var output = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(true); }
        return new { ok = true, output = fullOutput, format = ext[1..].ToLowerInvariant(), schema = doc.Schema.Name,
            rows = doc.Rows.Count, sourceSha256 = doc.OriginalHash, outputSha256 = Convert.ToHexString(SHA256.HashData(bytes)), lossless = true,
            note = "rawBase64 preserves every row byte; values are informational decoded views. This export is not an import format." };
    }

    private static object Apply(string path, string patchPath, string outputDirectory)
    {
        var (doc, key) = Open(path);
        int sourceRowCount = doc.Rows.Count, cloneCount = 0;
        var sourceIds = doc.Rows.Select(r => doc.Schema.Decode(key, r.Current)).ToHashSet(StringComparer.Ordinal);
        if (new FileInfo(patchPath).Length > 16 * 1024 * 1024) throw new IOException("Patch exceeds 16 MiB.");
        var patchBytes = File.ReadAllBytes(patchPath);
        using var json = JsonDocument.Parse(patchBytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Patch root must be a JSON object.");
        if (root.TryGetProperty("sourceSha256", out var expectedHash) &&
            !string.Equals(expectedHash.GetString(), doc.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Patch sourceSha256 does not match the current RDF; inspect/export again before applying.");
        if (!root.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Patch requires an operations array.");

        var operationAudit = new List<object>();
        foreach (var op in ops.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object || !op.TryGetProperty("op", out var nameNode)) throw new InvalidDataException("Each operation needs an op property.");
            string action = nameNode.GetString() ?? "";
            switch (action)
            {
                case "update":
                {
                    string id = RequiredString(op, "id");
                    var row = FindRow(doc, key, id);
                    ApplyFields(doc.Schema, row.Current, RequiredObject(op, "fields"));
                    if (doc.Schema.Decode(key, row.Current) != id)
                        throw new InvalidDataException("Additive preservation: an update cannot rename an existing RDF ID. Clone to a new ID and retain the original.");
                    operationAudit.Add(new { op = action, id });
                    break;
                }
                case "clone":
                {
                    if ((long)(doc.Rows.Count + 1) * doc.Schema.RowSize + 1 > 512L * 1024 * 1024) throw new InvalidDataException("Output RDF exceeds 512 MiB.");
                    string templateId = RequiredString(op, "templateId"), newId = RequiredString(op, "newId");
                    var template = FindRow(doc, key, templateId);
                    var data = (byte[])template.Current.Clone();
                    doc.Schema.Encode(key, newId, data);
                    if (op.TryGetProperty("fields", out var fields)) ApplyFields(doc.Schema, data, fields);
                    if (doc.Schema.Decode(key, data) != newId) throw new InvalidDataException("A clone fields object may not replace its required newId.");
                    doc.Rows.Add(new RdfRow { Index = sourceRowCount + cloneCount++,
                        Original = (byte[])template.Current.Clone(), Current = data });
                    operationAudit.Add(new { op = action, templateId, newId });
                    break;
                }
                case "delete":
                {
                    throw new InvalidDataException("Additive preservation: deleting RDF IDs is forbidden.");
                }
                default: throw new InvalidDataException($"Unknown operation '{action}'. Allowed: update, clone.");
            }
        }

        ValidateFinalRows(doc, key);
        var finalIds = doc.Rows.Select(r => doc.Schema.Decode(key, r.Current)).ToHashSet(StringComparer.Ordinal);
        if (!sourceIds.IsSubsetOf(finalIds))
            throw new InvalidDataException("Additive preservation: existing RDF IDs cannot disappear or be renamed.");
        string fullDestination = ContentSafety.SafeOutput(outputDirectory);
        string sourceFull = Path.GetFullPath(path);
        string relativeSource = Path.GetRelativePath(fullDestination, sourceFull);
        if (relativeSource != ".." && !relativeSource.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relativeSource))
            throw new IOException("Output directory cannot contain or replace the source RDF.");
        using var source = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(source)) != doc.OriginalHash)
                throw new IOException("The source changed since it was read. Inspect again and reapply the patch.");
        byte[] resultBytes = BuildBytes(doc);
        string pending = ContentSafety.FreshDirectory(fullDestination);
        Directory.CreateDirectory(pending);
        try
        {
            string fileName = Path.GetFileName(path);
            WriteFlushed(Path.Combine(pending, fileName), resultBytes);
            WriteFlushed(Path.Combine(pending, fileName + ".original.bak"), doc.OriginalBytes);
            byte[] readback = File.ReadAllBytes(Path.Combine(pending, fileName));
            if (!readback.AsSpan().SequenceEqual(resultBytes)) throw new IOException("RDF disk readback differs from staged output.");
            var reopened = RdfDocument.Open(Path.Combine(pending, fileName), doc.Schema);
            if (reopened.Rows.Count != doc.Rows.Count || reopened.Margin != doc.Margin) throw new IOException("RDF schema readback differs from staged rows.");
            var changes = BuildAuditChanges(doc, key);
            var audit = new
            {
                version = 2, product = "DBO Assets Studio Taiwan 2.0", utc = DateTime.UtcNow,
                source = sourceFull, sourceSha256 = doc.OriginalHash,
                patch = Path.GetFullPath(patchPath), patchSha256 = Convert.ToHexString(SHA256.HashData(patchBytes)),
                outputSha256 = Convert.ToHexString(SHA256.HashData(resultBytes)), schema = doc.Schema.Name,
                schemaRowSize = doc.Schema.RowSize, margin = doc.Margin, sourceRowCount,
                rowCount = doc.Rows.Count, diskReadback = "byte-exact", operations = operationAudit, changes
            };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            Directory.Move(pending, fullDestination);
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
        return new { ok = true, output = Path.Combine(fullDestination, Path.GetFileName(path)), outputDirectory = fullDestination,
            audit = Path.Combine(fullDestination, "audit.json"), schema = doc.Schema.Name, rows = doc.Rows.Count,
            sourceSha256 = doc.OriginalHash, outputSha256 = Convert.ToHexString(SHA256.HashData(resultBytes)), operations = operationAudit };
    }

    // Read-only IDs for server profile references, using compiled Taiwan ABI layouts.
    private static object Identities(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new IOException("RDF exceeds 128 MiB.");
        using var layouts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resource-layout.json")));
        var candidates = layouts.RootElement.EnumerateArray().Where(x => string.Equals(x.GetProperty("table").GetString(), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
        {
            var (doc, key) = Open(path); var schemaIds = new HashSet<string>(); var schemaDuplicates = new HashSet<string>();
            foreach (var row in doc.Rows)
            {
                string id = doc.Schema.Decode(key, row.Current).Trim();
                if (id == "0" || id == "4294967295") continue;
                if (!schemaIds.Add(id)) schemaDuplicates.Add(id);
            }
            return new { source = Path.GetFullPath(path), sourceSha256 = doc.OriginalHash, kind = doc.Schema.Name,
                rowSize = doc.Schema.RowSize, rowCount = doc.Rows.Count, ids = schemaIds.Order().ToArray(),
                duplicateIds = schemaDuplicates.Order().ToArray(), readOnly = true, worldTested = false };
        }
        if (candidates.Length != 1) throw new InvalidDataException("No unique registered native identity layout for this RDF.");
        var layout = candidates[0]; int stride = layout.GetProperty("rowSize").GetInt32(), idOffset = layout.GetProperty("idOffset").GetInt32();
        int validity = layout.TryGetProperty("validityOffset", out var v) ? v.GetInt32() : -1;
        byte[] bytes = File.ReadAllBytes(path);
        if (stride < 4 || stride > 8192 || idOffset < 0 || idOffset + 4 > stride || validity < -1 || validity >= stride || bytes.Length < 1 || bytes[0] != 1 || (bytes.Length - 1) % stride != 0)
            throw new InvalidDataException("RDF does not match registered Taiwan margin/layout.");
        var ids = new HashSet<uint>(); var duplicates = new HashSet<uint>(); int inactive = 0;
        for (int offset = 1; offset < bytes.Length; offset += stride)
        {
            if (validity >= 0 && bytes[offset + validity] == 0) { ++inactive; continue; }
            uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + idOffset, 4));
            if (id == 0 || id == uint.MaxValue) continue;
            if (!ids.Add(id)) duplicates.Add(id);
        }
        return new { source = Path.GetFullPath(path), sourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            kind = layout.GetProperty("kind").GetString(), rowSize = stride, rowCount = (bytes.Length - 1) / stride,
            inactiveRows = inactive, ids = ids.Order().Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            duplicateIds = duplicates.Order().Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            readOnly = true, worldTested = false };
    }

    internal static (RdfDocument Document, RdfField Key) Open(string path)
    {
        string catalog = LocateCatalog();
        var schemas = RdfSchema.LoadCatalog(catalog);
        var schema = RdfSchema.Detect(path, schemas) ?? throw new InvalidDataException($"No Taiwan schema in {catalog} matches {Path.GetFileName(path)}.");
        var doc = RdfDocument.Open(path, schema);
        if (schema.Name is "CharTitle" or "UseItem" or "SystemEffect" or "QuestProbability" or "ObjectSpawn" && doc.Margin != 1)
            throw new InvalidDataException("Registered native RDF requires Taiwan margin 1.");
        var key = schema.Fields.FirstOrDefault(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Schema {schema.Name} has no tblidx key; row edits require an explicit known key.");
        return (doc, key);
    }

    private static string LocateCatalog()
    {
        var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "taiwan.txt"),
            Path.Combine(AppContext.BaseDirectory, "tools", "taiwan-table-studio", "taiwan.txt"),
            Path.Combine(Environment.CurrentDirectory, "tools", "taiwan-table-studio", "taiwan.txt"),
            Path.Combine(Environment.CurrentDirectory, "config", "rdf-schemas", "taiwan.txt") };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Taiwan RDF catalog taiwan.txt not found.", candidates[0]);
    }

    private static RdfRow FindRow(RdfDocument doc, RdfField key, string id)
    {
        var matches = doc.Rows.Where(r => doc.Schema.Decode(key, r.Current) == id).Take(2).ToArray();
        if (matches.Length == 0) throw new InvalidDataException($"No row with id '{id}'.");
        if (matches.Length > 1) throw new InvalidDataException($"Source contains ambiguous duplicate id '{id}'.");
        return matches[0];
    }

    private static void ApplyFields(RdfSchema schema, byte[] row, JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) throw new InvalidDataException("fields must be a JSON object of field names to typed text values.");
        foreach (var property in fields.EnumerateObject())
        {
            var field = schema.Fields.FirstOrDefault(f => string.Equals(f.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Unknown field '{property.Name}' in schema {schema.Name}.");
            if (field.IsPadding) throw new InvalidDataException($"Reserved padding field '{field.Name}' cannot be edited.");
            string value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => throw new InvalidDataException($"Field '{field.Name}' must be a string or number (booleans may be true/false).")
            };
            schema.Encode(field, value, row);
        }
    }

    internal static void ValidateFinalRows(RdfDocument doc, RdfField key)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.Rows)
        {
            string id = doc.Schema.Decode(key, row.Current);
            if (id is "0" or "4294967295" || !ids.Add(id)) throw new InvalidDataException("ID inválido o duplicado: " + id);
            if (doc.Schema.Name == "CharTitle" && (!uint.TryParse(id, out uint titleId) || titleId > 512))
                throw new InvalidDataException("CharTitle ID must fit Taiwan ownership bits: 1..512.");
            foreach (var field in doc.Schema.Fields.Where(f => f.IsPadding))
                if (!row.Current.AsSpan(field.Offset, field.Size).SequenceEqual(row.Original.AsSpan(field.Offset, field.Size)))
                    throw new InvalidDataException("Se modificó padding reservado.");
        }
        if (doc.Rows.Count > 0 && doc.Rows.Count > (512 * 1024 * 1024 - 1) / doc.Schema.RowSize)
            throw new InvalidDataException("Output RDF exceeds the 512 MiB safety limit.");
    }

    private static byte[] BuildBytes(RdfDocument doc)
    {
        byte[] result = new byte[checked(1 + doc.Rows.Count * doc.Schema.RowSize)];
        result[0] = doc.Margin;
        int offset = 1;
        foreach (var row in doc.Rows) { row.Current.CopyTo(result, offset); offset += doc.Schema.RowSize; }
        return result;
    }

    private static object[] BuildAuditChanges(RdfDocument doc, RdfField key)
    {
        var changes = new List<object>();
        foreach (var row in doc.Rows)
        {
            bool isNew = row.Index >= (doc.OriginalBytes.Length - 1) / doc.Schema.RowSize;
            foreach (var field in doc.Schema.Fields.Where(f => !row.Original.AsSpan(f.Offset, f.Size).SequenceEqual(row.Current.AsSpan(f.Offset, f.Size))))
                changes.Add(new { row = doc.Rows.IndexOf(row) + 1, originalRow = row.Index + 1, id = doc.Schema.Decode(key, row.Current), field = field.Name,
                    before = doc.Schema.Decode(field, row.Original), after = doc.Schema.Decode(field, row.Current), addedRow = isNew });
        }
        return changes.ToArray();
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString()))
            throw new InvalidDataException($"Operation needs non-empty string '{name}'.");
        return property.GetString()!;
    }

    private static JsonElement RequiredObject(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object
            ? property : throw new InvalidDataException($"Operation needs object '{name}'.");

    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }

    private static string SafeXmlText(string value)
    {
        var safe = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (rune.Value is 0x9 or 0xA or 0xD || rune.Value >= 0x20 && rune.Value <= 0xD7FF ||
                rune.Value >= 0xE000 && rune.Value <= 0xFFFD || rune.Value >= 0x10000 && rune.Value <= 0x10FFFF)
                safe.Append(rune.ToString());
            else safe.Append('\uFFFD');
        return safe.ToString();
    }
}
