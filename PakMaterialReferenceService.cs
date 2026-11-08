using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DboAssetsStudio;

// Read-only semantic search over authenticated model payloads. This exposes
// source identities/hashes and material names, never bulk model extraction.
internal static class PakMaterialReferenceService
{
    internal static async Task<object> ScanAsync(PakArchive archive, string requests, string output)
    {
        if (archive.Format is not "taiwan" || archive.Prefix != "obj") throw new InvalidDataException("Explicit supported obj/asset model inventory required.");
        requests = Path.GetFullPath(requests); output = ContentSafety.SafeOutput(output); ContentSafety.NoLinks(requests);
        string packRoot = Path.GetDirectoryName(archive.IndexPath)!;
        if (output.Equals(packRoot, StringComparison.OrdinalIgnoreCase) || output.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Report outside source pack required.");
        if (new FileInfo(requests).Length is < 1 or > 16384 || File.Exists(output) || Directory.Exists(output) || output.Equals(requests, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bounded requests and fresh disjoint report required.");
        using var requestLock = new FileStream(requests, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var indexLock = new FileStream(archive.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(indexLock)) != archive.IndexHash) throw new IOException("Model index changed.");
        byte[] input = File.ReadAllBytes(requests); using var doc = JsonDocument.Parse(input); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
        if (doc.RootElement.EnumerateObject().Any(x => x.Name != "names")) throw new InvalidDataException("Requests only accept names.");
        string[] names = doc.RootElement.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (names.Length is < 1 or > 128 || names.Any(x => x == null || x.Length is < 1 or > 31 || x.Any(c => c < 32 || c > 126 || c == '/' || c == '\\')) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Unique ASCII material basenames required.");
        var models = archive.Entries.Where(x => x.Name.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Unit).ThenBy(x => x.Offset).ToArray();
        long total = models.Sum(x => (long)x.Size); if (models.Length is < 1 or > 50000 || total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Model scan limits50k/8GiB.");
        var references = new List<object>(); var errors = new List<object>(); int checkedModels = 0, strictModels = 0;
        var tokens = names.Select(x => Encoding.ASCII.GetBytes(x.ToLowerInvariant())).ToArray();
        await archive.VisitPayloadsAsync(models, (entry, bytes) =>
        {
            checkedModels++; var typed = new List<DffRepairService.TextureName>(); string? error = null;
            try { DffRepairService.Inspect(bytes, false, true, typed); strictModels++; }
            catch (InvalidDataException e) { error = e.Message; errors.Add(new { entry.Name, bytes = entry.Size, error }); }
            string[] materials = typed.Select(x => Encoding.Latin1.GetString(Convert.FromHexString(x.NameHex))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] hits = names.Where(x => materials.Any(m => m.Equals(x, StringComparison.OrdinalIgnoreCase) || m.StartsWith(x + ".", StringComparison.OrdinalIgnoreCase))).ToArray();
            string originalHash = Convert.ToHexString(SHA256.HashData(bytes));
            // Raw ASCII fallbacks are diagnostic candidates only when the
            // strict material parser could not consume the entire model.
            var rawHits = new List<string>();
            if (error != null)
            {
                for (int i = 0; i < bytes.Length; i++) if (bytes[i] is >= (byte)'A' and <= (byte)'Z') bytes[i] += 32;
                for (int i = 0; i < names.Length; i++) if (bytes.AsSpan().IndexOf(tokens[i]) >= 0) rawHits.Add(names[i]);
            }
            if (hits.Length > 0 || rawHits.Count > 0)
                references.Add(new { entry.Name, entry.Unit, entry.Offset, entry.Size, payloadSha256 = originalHash, authenticated = false, strictMaterialParserPass = error == null, materials, requestedMaterials = hits, rawDiagnosticCandidates = rawHits, parserError = error });
            return Task.CompletedTask;
        });
        var report = new { profile = "pak-model-material-references-v1", utc = DateTimeOffset.UtcNow, sourceFormat = archive.Format, source = archive.IndexPath, sourceIndexSha256 = archive.IndexHash, sourceBuildSha256 = archive.SourceBuildHash,
            requestsSha256 = Convert.ToHexString(SHA256.HashData(input)), names, checkedModels, strictModels, inspectedPayloadBytes = total, allPayloadsAuthenticated = false, references, parserErrors = errors,
            sourceLockedDuringScan = true, sourcePreserved = true, extractedModels = false, nativeModelReaderTested = false, completeness = strictModels == checkedModels ? "all model materials parsed" : "all DFF payloads inspected; raw diagnostic search also covers parser failures" };
        ContentSafety.WriteJson(output, report); return new { report = output, checkedModels, strictModels, matchedModels = references.Count, parserErrors = errors.Count, sourcePreserved = true };
    }
}
