using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// Read-only exact resource identities, sharing the real index/format readers.
// Avoids extracting entire dungeon projects just to compare their bytes.
internal static class PakFingerprintService
{
    public static async Task<object> InspectAsync(PakArchive archive, string selectionFile)
    {
        if (archive.Format is not "taiwan")
            throw new InvalidDataException("Fingerprint supports audited stored Taiwan payloads only.");
        ContentSafety.NoLinks(selectionFile);
        if (new FileInfo(selectionFile).Length is < 1 or > 256 * 1024)
            throw new InvalidDataException("Selection limit256KiB.");
        using var selectionLock = new FileStream(selectionFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        string selectionHash = Convert.ToHexString(SHA256.HashData(selectionLock));
        selectionLock.Position = 0;
        using var document = await JsonDocument.ParseAsync(selectionLock);
        var selection = document.RootElement; TerrainGeometryService.NoDuplicateKeys(selection);
        if (selection.ValueKind != JsonValueKind.Object || selection.EnumerateObject().Count() != 3 ||
            selection.EnumerateObject().Any(p => p.Name is not ("sourceSha256" or "sourceBuildSha256" or "paths")) ||
            selection.GetProperty("sourceSha256").GetString() != archive.IndexHash ||
            selection.GetProperty("sourceBuildSha256").GetString() != archive.SourceBuildHash)
            throw new InvalidDataException("Exact index/build and known literal selection fields required.");
        var names = selection.GetProperty("paths").EnumerateArray().Select(x => ContentSafety.ResourcePath(x.GetString()!)).ToArray();
        if (names.Length is < 1 or > 512 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Unique literal selection1..512 required.");
        var catalog = archive.Entries.ToDictionary(e => ContentSafety.ResourcePath(e.Name), StringComparer.OrdinalIgnoreCase);
        var entries = names.Select(n => catalog.TryGetValue(n, out var e) ? e : throw new FileNotFoundException("Missing resource: " + n)).ToArray();
        if (entries.Any(e => e.Size > 128L * 1024 * 1024) || entries.Sum(e => (long)e.Size) > 512L * 1024 * 1024)
            throw new InvalidDataException("Resource128MiB/batch512MiB bounds.");
        var locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        FileStream Lock(string path)
        {
            if (!locks.TryGetValue(path, out var stream))
            {
                ContentSafety.NoLinks(path);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                locks.Add(path, stream);
            }
            return stream;
        }
        try
        {
            var indexLock = Lock(archive.IndexPath);
            if (Convert.ToHexString(SHA256.HashData(indexLock)) != archive.IndexHash)
                throw new IOException("Index changed after opening.");
            // Lock all selected units before reading any payload: a batch is one
            // stable snapshot, not a sequence of independently mutable reads.
            foreach (var entry in entries)
            {
                var unit = Lock(entry.UnitPath);
                if ((ulong)entry.Offset + entry.Size > (ulong)unit.Length)
                    throw new InvalidDataException("Resource exceeds unit bounds: " + entry.Name);
            }
            var resources = new List<object>(); byte[] buffer = new byte[1024 * 1024];
            foreach (var entry in entries)
            {
                var unit = Lock(entry.UnitPath); unit.Position = entry.Offset;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long left = entry.Size; left > 0;)
                {
                    int n = await unit.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)));
                    if (n == 0) throw new EndOfStreamException(entry.Name);
                    hash.AppendData(buffer, 0, n); left -= n;
                }
                resources.Add(new { path = ContentSafety.ResourcePath(entry.Name), size = entry.Size,
                    sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
            }
            return new { schema = "dbo-pak-resource-fingerprints-v1", source = archive.IndexPath, archive.IndexHash,
                archive.Format, sourceBuildSha256 = archive.SourceBuildHash, selectionSha256 = selectionHash,
                payloadSemantics = "stored_bytes; no adaptation or decoding of RDF/EDF/media", resources,
                readOnly = true, runtimeModified = false, sourceExecuted = false, gameCompatibility = "not_tested" };
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
    }
}
