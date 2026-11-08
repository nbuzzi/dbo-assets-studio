using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// A revision is a single directory transaction: RDF, original and audit become
// visible together. A failed write never replaces the input or an older revision.
internal static class RdfSaveService
{
    public static string SaveRevision(RdfDocument document, string root, IReadOnlyList<string>? validationNotes = null)
    {
        root = ContentSafety.SafeOutput(root);
        Directory.CreateDirectory(root);
        using var original = new FileStream(document.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(original)) != document.OriginalHash)
            throw new IOException("El origen cambió desde que se abrió. Recargalo antes de guardar.");
        byte[] output = document.BuildBytes();
        if (output.Length != document.OriginalBytes.Length || output[0] != document.Margin)
            throw new InvalidDataException("Cambió la estructura de la tabla.");
        var key = document.Schema.Fields.First(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        var ids = new HashSet<string>();
        foreach (var row in document.Rows)
        {
            string id = document.Schema.Decode(key, row.Current);
            if (id is "0" or "4294967295" || !ids.Add(id)) throw new InvalidDataException("ID inválido o duplicado: " + id);
            if (id != document.Schema.Decode(key, row.Original))
                throw new InvalidDataException("IDs existentes inmutables: usar clone del motor RDF conservando la fila original.");
            foreach (var field in document.Schema.Fields.Where(f => f.IsPadding))
                if (!row.Current.AsSpan(field.Offset, field.Size).SequenceEqual(row.Original.AsSpan(field.Offset, field.Size)))
                    throw new InvalidDataException("Se modificó padding reservado.");
        }
        string revision = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string pending = Path.Combine(root, ".pending-" + revision), destination = Path.Combine(root, revision);
        Directory.CreateDirectory(pending);
        try
        {
            string name = Path.GetFileName(document.Path), rdf = Path.Combine(pending, name);
            WriteFlushed(rdf, output);
            var reread = RdfDocument.Open(rdf, document.Schema);
            if (!reread.OriginalBytes.AsSpan().SequenceEqual(output) || reread.Rows.Count != document.Rows.Count)
                throw new IOException("La relectura del RDF desde disco difiere de lo editado.");
            WriteFlushed(Path.Combine(pending, name + ".original.bak"), document.OriginalBytes);
            var changes = document.Rows.Where(r => r.Modified).SelectMany(row => document.Schema.Fields
                .Where(f => !row.Original.AsSpan(f.Offset, f.Size).SequenceEqual(row.Current.AsSpan(f.Offset, f.Size)))
                .Select(f => new { row = row.Index + 1, id = document.Schema.Decode(key, row.Current), field = f.Name,
                    before = document.Schema.Decode(f, row.Original), after = document.Schema.Decode(f, row.Current) })).ToArray();
            var operations = document.Rows.Where(r => r.Modified).Select(row => new {
                op = "update", id = document.Schema.Decode(key, row.Original),
                fields = document.Schema.Fields.Where(f => !row.Original.AsSpan(f.Offset, f.Size).SequenceEqual(row.Current.AsSpan(f.Offset, f.Size)))
                    .ToDictionary(f => f.Name, f => document.Schema.Decode(f, row.Current))
            }).ToArray();
            byte[] planBytes = JsonSerializer.SerializeToUtf8Bytes(new {
                sourceSha256 = document.OriginalHash, operations
            }, ContentSafety.Json);
            WriteFlushed(Path.Combine(pending, "plan.json"), planBytes);
            var audit = new { version = 2, product = "DBO Assets Studio Taiwan 2.0", utc = DateTime.UtcNow,
                source = document.Path, sourceSha256 = document.OriginalHash,
                outputSha256 = Convert.ToHexString(SHA256.HashData(output)), document.Schema.RowSize,
                planSha256 = Convert.ToHexString(SHA256.HashData(planBytes)),
                rowCount = document.Rows.Count, document.Margin, schema = document.Schema.Name,
                schemaFields = document.Schema.Fields, diskReadback = "byte-exact",
                validationNotes = validationNotes ?? ["Guardado estructural; referencias externas no comprobadas por este servicio."], changes };
            WriteFlushed(Path.Combine(pending, "audit.json"), JsonSerializer.SerializeToUtf8Bytes(audit, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(pending, destination);
            return Path.Combine(destination, name);
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }

    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
}
