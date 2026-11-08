using System.Security.Cryptography;

namespace DboAssetsStudio;

// Read-only semantic-path comparison, using the canonical Taiwan reader.
// Unit numbers/offsets are packaging details, NOT resource identity.
internal static class PakComparison
{
    internal sealed record Resource(string Sha256, uint Size);
    private sealed record Snapshot(string Index, string IndexSha256, Dictionary<string, Resource> Resources);

    public static async Task<object> CompareAsync(string baseline, string ours, string theirs)
    {
        var archives = new List<PakArchive>();
        foreach (string path in new[] { baseline, ours, theirs })
        {
            ContentSafety.NoLinks(path);
            archives.Add(await PakArchive.OpenAsync(path));
        }
        if (archives.Select(a => a.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidDataException("La comparación requiere la MISMA familia Taiwan y todas sus unidades en tres carpetas separadas.");
        // Hold all three index + unit sets simultaneously: no mixed snapshots
        // if another editor writes while we stream potentially large families.
        var locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var archive in archives)
                foreach (string path in archive.Entries.Select(e => e.UnitPath).Prepend(archive.IndexPath))
                {
                    // Read each locked unit in offset order below. Keep bounded
                    // read-ahead for tiny terrain/swap records and do not seek
                    // when the next resource is physically contiguous.
                    if (!locks.ContainsKey(path))
                    {
                        ContentSafety.NoLinks(path);
                        locks[path] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                    }
                }
            var snapshots = new List<Snapshot>();
            var identicalSources = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
            byte[] buffer = new byte[1024 * 1024];
            foreach (var archive in archives)
            {
                FileStream index = locks[archive.IndexPath]; index.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(index)).Equals(archive.IndexHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Índice cambió al abrir: " + archive.IndexPath);
                // The index/unit read locks are held for the WHOLE comparison.
                // An identical pinned path is the same snapshot, not a third
                // independent source. Do not reread gigabytes when base==ours.
                if (identicalSources.TryGetValue(archive.IndexPath, out var identical))
                {
                    snapshots.Add(identical); continue;
                }
                var resources = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries.OrderBy(e => e.UnitPath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Offset).ThenBy(e => e.Size))
                {
                    string name = ContentSafety.ResourcePath(entry.Name);
                    if (resources.ContainsKey(name)) throw new InvalidDataException("Ruta interna duplicada/ambigua: " + name);
                    FileStream unit = locks[entry.UnitPath];
                    if ((ulong)entry.Offset + entry.Size > (ulong)unit.Length) throw new InvalidDataException("Recurso fuera de límites: " + name);
                    if (unit.Position != entry.Offset) unit.Position = entry.Offset;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long remaining = entry.Size;
                    while (remaining > 0)
                    {
                        int count = await unit.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                        if (count == 0) throw new EndOfStreamException(name);
                        hash.AppendData(buffer, 0, count); remaining -= count;
                    }
                    resources[name] = new Resource(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), entry.Size);
                }
                var snapshot = new Snapshot(archive.IndexPath, archive.IndexHash.ToLowerInvariant(), resources);
                identicalSources.Add(archive.IndexPath, snapshot); snapshots.Add(snapshot);
            }
            var b = snapshots[0].Resources; var o = snapshots[1].Resources; var t = snapshots[2].Resources;
            var changes = b.Keys.Union(o.Keys, StringComparer.OrdinalIgnoreCase).Union(t.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(name =>
                {
                    b.TryGetValue(name, out var before); o.TryGetValue(name, out var left); t.TryGetValue(name, out var right);
                    string status = left == right ? (left == before ? "unchanged" : "same_change") : left == before ? "theirs_only" : right == before ? "ours_only" : "conflict";
                    return new { path = name, status, baseline = before, ours = left, theirs = right };
                }).Where(c => c.status != "unchanged").ToArray();
            return new { schema = "dbo-pak-comparison-v1", format = "taiwan", family = archives[0].Prefix,
                sources = snapshots.Select(s => new { s.Index, s.IndexSha256, resources = s.Resources.Count }),
                changes, conflicts = changes.Where(c => c.status == "conflict").Select(c => c.path).ToArray(),
                wouldLoseOurs = changes.Where(c => c.status == "ours_only").Select(c => c.path).ToArray(),
                removedPaths = changes.Where(c => c.theirs == null && (c.baseline != null || c.ours != null)).Select(c => c.path).ToArray(),
                safeToReplaceOurs = changes.All(c => c.theirs != null && (c.status is "same_change" or "theirs_only")),
                note = "Solo informe. No fusiona ni instala. RDF modificado por ambos es conflicto de recurso; resolver por esquema/filas con revisión. La carga en cliente sigue pendiente." };
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
    }
}
