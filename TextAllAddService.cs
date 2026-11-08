using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

// Literal reviewed text -> registered Taiwan variable-width codec. Reuses the
// additive merge/preservation/audit engine; never rewrites an existing record.
internal static class TextAllAddService
{
    public static object Add(string target, string planPath, string destination)
    {
        ContentSafety.NoLinks(target); ContentSafety.NoLinks(planPath);
        if (new FileInfo(target).Length is < 1 or > 128 * 1024 * 1024 || new FileInfo(planPath).Length is < 1 or > 1024 * 1024)
            throw new InvalidDataException("TextAll/add plan size limit.");
        byte[] baseline = File.ReadAllBytes(target), planBytes = File.ReadAllBytes(planPath);
        string targetHash = Convert.ToHexString(SHA256.HashData(baseline));
        string planHash = Convert.ToHexString(SHA256.HashData(planBytes));
        using var doc = JsonDocument.Parse(planBytes);
        var root = doc.RootElement;
        if (root.EnumerateObject().Any(p => p.Name is not ("targetSha256" or "category" or "records" or "reason")) ||
            root.GetProperty("targetSha256").GetString() != targetHash || string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()))
            throw new InvalidDataException("TextAll reviewed reason and exact target SHA required.");
        string categoryName = root.GetProperty("category").GetString()!;
        uint category = TextAllRdfService.Category(categoryName);
        var records = root.GetProperty("records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() is < 1 or > 4096)
            throw new InvalidDataException("Select 1..4096 literal text records.");
        var chunks = MapNameRdfService.ReadChunks(baseline);
        var occupied = MapNameRdfService.ReadRecords(chunks.Single(c => c.Kind == category).Payload, allowZero: true).Keys.ToHashSet();
        var ids = new List<string>();
        using var payload = new MemoryStream(); payload.WriteByte(1);
        var encoding = new UnicodeEncoding(false, false, true);
        Span<byte> header = stackalloc byte[8];
        foreach (var record in records.EnumerateArray())
        {
            if (record.EnumerateObject().Any(p => p.Name is not ("id" or "text")) || record.GetProperty("id").ValueKind != JsonValueKind.String ||
                !uint.TryParse(record.GetProperty("id").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ||
                id is 0 or uint.MaxValue || !occupied.Add(id)) throw new InvalidDataException("Duplicate/existing/invalid TextAll ID.");
            string text = record.GetProperty("text").GetString()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Contains('\0')) throw new InvalidDataException("Nonempty UTF16 text, no embedded NUL, maximum4096 code units.");
            byte[] bytes = encoding.GetBytes(text);
            BinaryPrimitives.WriteUInt32LittleEndian(header, id);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], checked((ushort)(bytes.Length / 2)));
            payload.Write(header[..6]); payload.Write(bytes); ids.Add(id.ToString(CultureInfo.InvariantCulture));
        }
        using var source = new MemoryStream();
        foreach (var chunk in chunks)
        {
            if (chunk.Kind != category) { source.Write(chunk.Raw); continue; }
            BinaryPrimitives.WriteUInt32LittleEndian(header, category);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
            source.Write(header); payload.Position = 0; payload.CopyTo(source);
        }
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsTextAdd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            string incoming = Path.Combine(cache, "table_text_all_data.rdf"), mergePlan = Path.Combine(cache, "merge.json");
            byte[] incomingBytes = source.ToArray(); File.WriteAllBytes(incoming, incomingBytes);
            ContentSafety.WriteJson(mergePlan, new { targetSha256 = targetHash, sourceSha256 = Convert.ToHexString(SHA256.HashData(incomingBytes)), category = categoryName, ids });
            // Hold both reviewed files across the canonical merge, including its
            // own source snapshot: concurrent edits fail closed, not overwrite.
            using var targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var planLock = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(targetLock)) != targetHash || Convert.ToHexString(SHA256.HashData(planLock)) != planHash)
                throw new IOException("TextAll/add source changed during preparation.");
            var result = TextAllRdfService.Merge(target, incoming, mergePlan, destination, reviewedPlanHash: planHash);
            File.Copy(incoming, Path.Combine(Path.GetFullPath(destination), "literal-text-source.rdf.bak"));
            File.Copy(planPath, Path.Combine(Path.GetFullPath(destination), "reviewed-text-add-plan.json"));
            // The external delivery chain is pinned to the HUMAN-reviewed add
            // plan, not the transient internal merge recipe. Preserve both.
            File.Copy(mergePlan, Path.Combine(Path.GetFullPath(destination), "canonical-merge-plan.json"));
            return new { result, reviewedPlanSha256 = planHash, category, addedIds = ids, installed = false, nativeReaderTested = false };
        }
        finally { Directory.Delete(cache, true); }
    }
}
