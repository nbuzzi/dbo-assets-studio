using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Additive exact records from a pinned Taiwan-compatible TextAll source.
// Uses the existing variable-width codec; never rewrites existing text/IDs.
internal static class TextAllRdfService
{
    private const int MaxBytes = 128 * 1024 * 1024;
    // CTextAllTable::TABLETYPE (Taiwan): MOB_DATA=5, SKILL_DATA=7, MAP_NAME=10.
    internal static uint Category(string name)
    {
        // All28 native CTextAll chunks use the same reviewed text record codec.
        // Explicit numeric names permit complete additive transport, not a
        // guessed external schema or an arbitrary new chunk type.
        if (name.StartsWith("native:", StringComparison.Ordinal) &&
            uint.TryParse(name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out uint native) && native < 28)
            return native;
        return name switch
        {
            "item" => 3, "mob" => 5, "npc" => 6, "skill" => 7, "useitem" => 9, "map" => 10, "object" => 11, "title" => 26, "partydungeon" => 27,
            _ => throw new InvalidDataException("Registered TextAll category: item, mob, npc, skill, useitem, map, object, title partydungeon or native:0..27.")
        };
    }
    private static byte[] Snapshot(string path, bool requireFileName = true)
    {
        if (requireFileName && !Path.GetFileName(path).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TextAll requires table_text_all_data.rdf.");
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > MaxBytes) throw new InvalidDataException("TextAll size limit128MiB.");
        return File.ReadAllBytes(path);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Dictionary<uint, byte[]> Records(byte[] bytes, uint category) =>
        // CTextTable::AddTable accepts a unique ID zero in existing source data;
        // FindData cannot look it up. Preserve it, but never select/add ID zero.
        MapNameRdfService.ReadRecords(MapNameRdfService.ReadChunks(bytes).Single(c => c.Kind == category).Payload, allowZero: true);
    internal static void ValidateFile(string path)
    {
        // Workspace selects this codec by the validated logical destination;
        // its immutable CAS stores files under SHA, without an RDF extension.
        byte[] bytes = Snapshot(path, requireFileName: false);
        var encoding = new UnicodeEncoding(false, false, true);
        // Workspace validation must inspect every category, not only the
        // selected item/mob/skill/title picker. Unique pre-existing ID0 is valid.
        foreach (var chunk in MapNameRdfService.ReadChunks(bytes))
            foreach (var record in MapNameRdfService.ReadRecords(chunk.Payload, allowZero: true).Values)
                _ = encoding.GetString(record.AsSpan(6));
    }
    public static object Inspect(string path, string categoryName)
    {
        uint category = Category(categoryName); byte[] bytes = Snapshot(path);
        var records = Records(bytes, category); var encoding = new UnicodeEncoding(false, false, true);
        return new { source = Path.GetFullPath(path), sourceSha256 = Hash(bytes), category, categoryName, count = records.Count,
            records = records.Select(p => new { id = p.Key.ToString(CultureInfo.InvariantCulture),
                text = encoding.GetString(p.Value.AsSpan(6)), recordSha256 = Hash(p.Value) }).ToArray(), runtimeTested = false };
    }
    public static object InspectAll(string path)
    {
        byte[] bytes = Snapshot(path); var encoding = new UnicodeEncoding(false, false, true);
        return new { source = Path.GetFullPath(path), sourceSha256 = Hash(bytes),
            categories = MapNameRdfService.ReadChunks(bytes).Select(chunk => new {
                category = chunk.Kind, rawChunkSha256 = Hash(chunk.Raw),
                records = MapNameRdfService.ReadRecords(chunk.Payload, allowZero: true).Select(row => new {
                    id = row.Key.ToString(CultureInfo.InvariantCulture), recordSha256 = Hash(row.Value),
                    text = encoding.GetString(row.Value.AsSpan(6)) }).ToArray()
            }).ToArray(), runtimeWritten = false };
    }
    // Read every registered native chunk; a picker category is insufficient
    // evidence when transporting the complete TextAll file.
    public static object Preservation(string beforePath, string afterPath)
    {
        byte[] before = Snapshot(beforePath), after = Snapshot(afterPath);
        var oldChunks = MapNameRdfService.ReadChunks(before);
        var newChunks = MapNameRdfService.ReadChunks(after);
        var missing = new List<object>(); var changed = new List<object>();
        int retained = 0, added = 0;
        foreach (var chunk in oldChunks)
        {
            var incoming = newChunks.SingleOrDefault(c => c.Kind == chunk.Kind);
            var oldRows = MapNameRdfService.ReadRecords(chunk.Payload, allowZero: true);
            var newRows = incoming is null ? new Dictionary<uint, byte[]>() :
                MapNameRdfService.ReadRecords(incoming.Payload, allowZero: true);
            foreach (var row in oldRows)
                if (!newRows.TryGetValue(row.Key, out var value))
                    missing.Add(new { category = chunk.Kind, id = row.Key });
                else if (!row.Value.AsSpan().SequenceEqual(value))
                    changed.Add(new { category = chunk.Kind, id = row.Key,
                        beforeSha256 = Hash(row.Value), afterSha256 = Hash(value) });
                else retained++;
            added += newRows.Keys.Count(id => !oldRows.ContainsKey(id));
        }
        foreach (var chunk in newChunks.Where(c => !oldChunks.Any(o => o.Kind == c.Kind)))
            added += MapNameRdfService.ReadRecords(chunk.Payload, allowZero: true).Count;
        bool categoriesPreserved = oldChunks.Select(c => c.Kind).SequenceEqual(
            newChunks.Where(c => oldChunks.Any(o => o.Kind == c.Kind)).Select(c => c.Kind));
        return new { beforeSha256 = Hash(before), afterSha256 = Hash(after),
            categoriesBefore = oldChunks.Count, categoriesAfter = newChunks.Count,
            categoriesPreserved, retainedRecordsByteExact = retained, addedRecords = added,
            missing, changed, safeToReplaceOurs = categoriesPreserved && missing.Count == 0 && changed.Count == 0,
            runtimeWritten = false };
    }
    public static object MergeNativeRecords(string targetPath, string sourcePath, string planPath, string destination) =>
        Merge(targetPath, sourcePath, planPath, destination, preserveNativeRecords: true);
    public static object Merge(string targetPath, string sourcePath, string planPath, string destination, string? reviewedPlanHash = null, bool preserveNativeRecords = false)
    {
        ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("TextAll plan size limit1MiB.");
        byte[] target = Snapshot(targetPath), source = Snapshot(sourcePath), planBytes = File.ReadAllBytes(planPath);
        using var plan = JsonDocument.Parse(planBytes);
        var root = plan.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("targetSha256" or "sourceSha256" or "category" or "ids")))
            throw new InvalidDataException("TextAll plan only accepts targetSha256/sourceSha256/category/ids.");
        string targetHash = Hash(target), sourceHash = Hash(source);
        if (!root.GetProperty("targetSha256").GetString()!.Equals(targetHash, StringComparison.OrdinalIgnoreCase) ||
            !root.GetProperty("sourceSha256").GetString()!.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TextAll pinned target/source changed.");
        uint category = Category(root.GetProperty("category").GetString()!);
        var ids = root.GetProperty("ids");
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 100000) throw new InvalidDataException("Select1..100000 missing TextAll IDs.");
        var chunks = MapNameRdfService.ReadChunks(target); var before = Records(target, category); var incoming = Records(source, category);
        var occupied = before.Keys.ToHashSet(); var changes = new List<object>();
        using var added = new MemoryStream(); var encoding = new UnicodeEncoding(false, false, true);
        foreach (var idNode in ids.EnumerateArray())
        {
            if (idNode.ValueKind != JsonValueKind.String || !uint.TryParse(idNode.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == uint.MaxValue || (!preserveNativeRecords && id == 0))
                throw new InvalidDataException("TextAll selection requires a valid registered native ID; literal additions cannot use zero.");
            if (!occupied.Add(id)) throw new InvalidDataException("TextAll ID exists or is duplicated; no replacement allowed.");
            if (!incoming.TryGetValue(id, out var record)) throw new InvalidDataException("Selected TextAll source ID absent.");
            string text = encoding.GetString(record.AsSpan(6));
            // Native CTextTable accepts an existing empty string. Complete
            // role transport retains its ID and exact serialized record;
            // literal text-add keeps the established nonempty text guard.
            if (!preserveNativeRecords && string.IsNullOrWhiteSpace(text.TrimEnd('\0'))) throw new InvalidDataException("Selected TextAll text is empty.");
            added.Write(record); changes.Add(new { id = id.ToString(CultureInfo.InvariantCulture), recordSha256 = Hash(record), textBytes = record.Length - 6 });
        }
        if (target.Length + added.Length > MaxBytes) throw new InvalidDataException("Merged TextAll exceeds128MiB.");
        using var output = new MemoryStream(); Span<byte> header = stackalloc byte[8];
        foreach (var chunk in chunks)
        {
            if (chunk.Kind != category) { output.Write(chunk.Raw); continue; }
            BinaryPrimitives.WriteUInt32LittleEndian(header, category);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(chunk.Payload.Length + added.Length)));
            output.Write(header); output.Write(chunk.Payload); added.Position = 0; added.CopyTo(output);
        }
        byte[] bytes = output.ToArray(); var final = MapNameRdfService.ReadChunks(bytes); var after = Records(bytes, category);
        if (after.Count != occupied.Count || before.Any(p => !after[p.Key].AsSpan().SequenceEqual(p.Value)) ||
            chunks.Where(c => c.Kind != category).Any(c => !final.Single(f => f.Kind == c.Kind).Raw.AsSpan().SequenceEqual(c.Raw)))
            throw new IOException("TextAll existing records/categories were not preserved.");
        string full = ContentSafety.SafeOutput(destination);
        foreach (string path in new[] { targetPath, sourcePath, planPath })
        {
            string absolute = Path.GetFullPath(path), relative = Path.GetRelativePath(full, absolute);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative))
                throw new IOException("Destination cannot contain/replace a source.");
        }
        using var targetLock = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(targetLock)) != targetHash || Convert.ToHexString(SHA256.HashData(sourceLock)) != sourceHash)
            throw new IOException("TextAll changed during preparation.");
        string pending = ContentSafety.FreshDirectory(full);
        try
        {
            string file = "table_text_all_data.rdf";
            File.WriteAllBytes(Path.Combine(pending, file), bytes); File.WriteAllBytes(Path.Combine(pending, file + ".original.bak"), target);
            if (!File.ReadAllBytes(Path.Combine(pending, file)).AsSpan().SequenceEqual(bytes)) throw new IOException("TextAll disk readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, schema = "Taiwan TextAll", utc = DateTime.UtcNow,
                target = Path.GetFullPath(targetPath), targetSha256 = targetHash, source = Path.GetFullPath(sourcePath), sourceSha256 = sourceHash,
                planSha256 = reviewedPlanHash ?? Hash(planBytes), mergePlanSha256 = Hash(planBytes), reviewedPlanSha256 = reviewedPlanHash,
                outputSha256 = Hash(bytes), category, recordsBefore = before.Count, recordsAfter = after.Count,
                operations = changes, existingRecords = "byte-exact", otherCategories = "byte-exact", preserveNativeRecords, runtimeTested = false });
            Directory.Move(pending, full);
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
        return new { output = Path.Combine(full, "table_text_all_data.rdf"), outputSha256 = Hash(bytes), addedRecords = changes.Count,
            category, audit = Path.Combine(full, "audit.json"), runtimeTested = false };
    }
}
