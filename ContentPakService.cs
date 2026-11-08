using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed record PakChange(string Op, string Path, string? File = null, string? Sha256 = null);
internal sealed record PakAppendUnit(int Number, long Bytes, string Sha256);
internal sealed class PakPlan
{
    public int Version { get; set; } = 1;
    public string Format { get; set; } = "taiwan";
    public string? Family { get; set; }
    public string? Source { get; set; }
    public string? SourceSha256 { get; set; }
    public string Prefix { get; set; } = "";
    public string Mode { get; set; } = "overlay";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PakAppendUnit? AppendUnit { get; set; }
    public int UnitMaxMiB { get; set; } = 1900;
    public List<string> Dependencies { get; set; } = [];
    public string Reason { get; set; } = "";
    public List<PakChange> Operations { get; set; } = [];
}

// Shared by the GM UI and console. Payloads stream directly to staged units;
// no whole-pack extraction, no mutation of original PAKs and no game deployment.
internal static class ContentPakService
{
    public static Task<object> CompareAsync(string baseline, string ours, string theirs) => PakComparison.CompareAsync(baseline, ours, theirs);
    private sealed record Payload(string Name, PakEntry? Original, string? Replacement);
    public static PakPlan ReadPlan(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Plan mayor a 16 MiB.");
        ContentSafety.NoLinks(path);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))?.AsObject()
            ?? throw new InvalidDataException("Plan vacío.");
        // Preserve the sealed add-only append recipe used by earlier Snake Way
        // candidates while keeping the current typed append-overlay engine.
        if (document["mode"]?.GetValue<string>() == "append-unit")
        {
            if ((document["version"]?.GetValue<int>() ?? 1) != 1 ||
                document["appendUnit"] is not System.Text.Json.Nodes.JsonValue number ||
                !number.TryGetValue<int>(out int unit) || unit is < 0 or > 255 ||
                document["appendUnitBytes"]?.GetValue<long>() is not > 0 ||
                document["appendUnitSha256"]?.GetValue<string>() is not string sha ||
                !Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$") ||
                document["operations"] is not System.Text.Json.Nodes.JsonArray operations ||
                operations.Count == 0 || operations.Any(o => o?["op"]?.GetValue<string>() != "add"))
                throw new InvalidDataException("Legacy append requires exact unit pins and add-only operations.");
            long bytes = document["appendUnitBytes"]!.GetValue<long>();
            document["version"] = 2; document["mode"] = "append-overlay";
            document["appendUnit"] = new System.Text.Json.Nodes.JsonObject { ["number"] = unit, ["bytes"] = bytes, ["sha256"] = sha };
            document.Remove("appendUnitBytes"); document.Remove("appendUnitSha256");
        }
        else if (document.ContainsKey("appendUnitBytes") || document.ContainsKey("appendUnitSha256"))
            throw new InvalidDataException("Legacy append pins require append-unit mode.");
        var plan = document.Deserialize<PakPlan>(ContentSafety.Json) ?? throw new InvalidDataException("Plan vacío.");
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (plan.Source != null) plan.Source = Path.GetFullPath(plan.Source, root);
        plan.Operations = plan.Operations.Select(o => o with { File = o.File == null ? null : Path.GetFullPath(o.File, root) }).ToList();
        return plan;
    }
    public static async Task<object> InspectAsync(PakPlan plan)
    {
        var (archive, payloads) = await ResolveAsync(plan);
        return new { version = 1, source = archive?.IndexPath, sourceSha256 = archive?.IndexHash, sourceFormat = plan.Format, targetFormat = "taiwan", plan.Mode, plan.Prefix,
            resourcesBefore = archive?.Entries.Count ?? 0, resourcesAfter = payloads.Count, operations = plan.Operations,
            payloadBytes = payloads.Sum(p => p.Replacement != null ? new FileInfo(p.Replacement).Length : (long)p.Original!.Size),
            output = "Carpeta nueva; overlay requiere las unidades originales indicadas en audit.json. Repack crea un paquete completo.",
            compatibility = "Estructura Taiwan; carga en el juego pendiente." };
    }
    private static async Task<(PakArchive? archive, List<Payload> payloads)> ResolveAsync(PakPlan plan)
    {
        bool append = plan.Version == 2 && plan.Mode == "append-overlay" && plan.AppendUnit != null;
        if ((!append && (plan.Version != 1 || plan.Mode is not ("overlay" or "repack") || plan.AppendUnit != null)) || plan.UnitMaxMiB < 1 || plan.UnitMaxMiB > 1900) throw new InvalidDataException("Versión, modo o límite de unidad inválido.");
        if (!Regex.IsMatch(plan.Prefix, "^[a-z][a-z_]*$")) throw new InvalidDataException("Prefix debe ser una familia Taiwan en minúsculas, sin números (tex, obj, tbl…).");
        if (plan.Source == null && plan.Mode != "repack") throw new InvalidDataException("Un PAK nuevo requiere mode=repack.");
        if (plan.Format != "taiwan") throw new InvalidDataException("Mutación sólo para el formato Taiwan.");
        PakArchive? archive = plan.Source == null ? null : await PakArchive.OpenAsync(plan.Source, plan.Format, plan.Family);
        if (archive != null && (string.IsNullOrEmpty(plan.SourceSha256) || !archive.IndexHash.Equals(plan.SourceSha256, StringComparison.OrdinalIgnoreCase))) throw new IOException("sourceSha256 ausente o distinto: recreá el plan sobre el índice actual.");
        if (archive != null && plan.Mode != "repack" && plan.Prefix != archive.Prefix) throw new InvalidDataException("Overlay debe conservar la familia del índice.");
        if (append)
        {
            if (archive == null || archive.Format != "taiwan" || plan.AppendUnit!.Number is < 0 or > 255 || plan.AppendUnit.Bytes < 0 || !Regex.IsMatch(plan.AppendUnit.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Append requiere unidad Taiwan y preimagen sellada.");
            string file = AppendPath(archive, plan.AppendUnit.Number); ContentSafety.NoLinks(file);
            if (new FileInfo(file).Length != plan.AppendUnit.Bytes || !ContentSafety.Hash(file).Equals(plan.AppendUnit.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Preimagen de unidad append cambió.");
            long added = plan.Operations.Sum(c => c.File == null ? 0L : new FileInfo(c.File).Length);
            if (added <= 0 || added > plan.UnitMaxMiB * 1024L * 1024 - plan.AppendUnit.Bytes) throw new IOException("Append debe caber completo en la unidad sellada; no asigna otra unidad.");
        }
        var entries = new Dictionary<string, Payload>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive?.Entries ?? [])
        {
            string name = ContentSafety.ResourcePath(entry.Name);
            if (!entries.TryAdd(name, new(name, entry, null))) throw new InvalidDataException("Nombre duplicado en índice: " + name);
            if (entry.Status != "Disponible") throw new IOException(entry.Status + ": " + name);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Operations)
        {
            string name = ContentSafety.ResourcePath(change.Path);
            if (!seen.Add(name)) throw new InvalidDataException("Más de una operación para: " + name);
            if (change.Op == "remove") throw new InvalidDataException("Preservación aditiva: no se permite eliminar recursos PAK: " + name);
            if (change.Op is not ("add" or "replace")) throw new InvalidDataException("Operación desconocida: " + change.Op);
            bool exists = entries.TryGetValue(name, out var old);
            if ((change.Op == "add" && exists) || (change.Op != "add" && !exists)) throw new InvalidDataException("Conflicto " + change.Op + ": " + name);
            if (string.IsNullOrWhiteSpace(change.File)) throw new InvalidDataException("Falta file para " + name);
            ContentSafety.NoLinks(change.File);
            if (new FileInfo(change.File).Length > plan.UnitMaxMiB * 1024L * 1024) throw new IOException("Payload supera el límite por unidad: " + name);
            if (string.IsNullOrEmpty(change.Sha256) || !ContentSafety.Hash(change.File).Equals(change.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("sha256 del payload ausente o cambió: " + name);
            entries[name] = new(old?.Name ?? name, old?.Original, Path.GetFullPath(change.File));
        }
        if (entries.Count == 0) throw new InvalidDataException("No se genera un PAK vacío.");
        foreach (var p in entries.Values)
            if (Encoding.Latin1.GetByteCount(p.Name) + 2 >= 128) throw new InvalidDataException("Nombre excede 127 bytes del índice Taiwan: " + p.Name);
        return (archive, entries.Values.ToList());
    }
    private static string AppendPath(PakArchive archive, int number) => Path.Combine(Path.GetDirectoryName(archive.IndexPath)!, archive.Prefix + number + ".pak");
    public static PakAppendUnit SealAppendUnit(PakArchive archive, int number)
    {
        if (archive.Format != "taiwan" || number is < 0 or > 255) throw new InvalidDataException("Append requiere unidad Taiwan 0..255 existente.");
        string file = AppendPath(archive, number); ContentSafety.NoLinks(file);
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return new(number, s.Length, Convert.ToHexString(SHA256.HashData(s)));
    }
    public static async Task<object> ApplyAsync(PakPlan plan, string output)
    {
        output = ContentSafety.SafeOutput(output);
        var (archive, payloads) = await ResolveAsync(plan);
        if (archive != null && output.StartsWith(Path.GetDirectoryName(archive.IndexPath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("La salida debe quedar fuera de la carpeta del PAK original.");
        string pending = ContentSafety.FreshDirectory(output);
        var locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        FileStream Lock(string file)
        {
            if (!locks.TryGetValue(file, out var s)) { ContentSafety.NoLinks(file); locks[file] = s = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, ContentSafety.ReadBufferSize(file), FileOptions.SequentialScan); } return s;
        }
        try
        {
            if (archive != null)
            {
                var idx = Lock(archive.IndexPath);
                if (Convert.ToHexString(SHA256.HashData(idx)) != archive.IndexHash) throw new IOException("Índice cambió durante el plan.");
                foreach (var e in archive.Entries) Lock(e.UnitPath);
                File.Copy(archive.IndexPath, Path.Combine(pending, "source-index.original.bak"));
            }
            foreach (var c in plan.Operations.Where(c => c.File != null))
            {
                var s = Lock(c.File!); s.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(s)).Equals(c.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Payload cambió durante el plan.");
            }
            int unitNumber = 0;
            if (archive != null && plan.Mode == "overlay")
            {
                // Include unused historical files: deployment must not overwrite one.
                unitNumber = archive.Entries.Max(e => (int)e.Unit) + 1;
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(archive.IndexPath)!, plan.Prefix + "*.pak"))
                    if (int.TryParse(Path.GetFileNameWithoutExtension(file)[plan.Prefix.Length..], out int existing)) unitNumber = Math.Max(unitNumber, checked(existing + 1));
            }
            if (plan.AppendUnit != null) unitNumber = plan.AppendUnit.Number;
            var records = new List<byte[]>(); var resources = new List<object>();
            var writtenUnits = new List<object>(); FileStream? target = null; IncrementalHash? unitHash = null;
            string? targetPath = null;
            void CloseUnit()
            {
                if (target == null) return;
                target.Flush(true); target.Dispose(); target = null;
                string expected = Convert.ToHexString(unitHash!.GetHashAndReset()); unitHash.Dispose(); unitHash = null;
                string actual = ContentSafety.Hash(targetPath!);
                if (actual != expected) throw new IOException("La relectura de la unidad difiere de lo escrito.");
                writtenUnits.Add(new { file = Path.GetFileName(targetPath), bytes = new FileInfo(targetPath!).Length, sha256 = actual });
            }
            try
            {
                byte[] buffer = new byte[1024 * 1024]; long limit = plan.UnitMaxMiB * 1024L * 1024;
                foreach (var payload in payloads)
                {
                    byte unit; uint offset, size;
                    if (plan.Mode != "repack" && payload.Replacement == null)
                    { unit = payload.Original!.Unit; offset = payload.Original.Offset; size = payload.Original.Size; }
                    else
                    {
                        var source = Lock(payload.Replacement ?? payload.Original!.UnitPath);
                        long length = payload.Replacement != null ? source.Length : payload.Original!.Size;
                        source.Position = payload.Replacement != null ? 0 : payload.Original!.Offset;
                        if (length > limit || source.Position + length > source.Length) throw new IOException("Payload fuera de límites: " + payload.Name);
                        if (target != null && target.Position + length > limit)
                        {
                            if (plan.AppendUnit != null) throw new IOException("Append excedió la unidad sellada.");
                            CloseUnit(); unitNumber++;
                        }
                        if (target == null)
                        {
                            if (unitNumber > 255) throw new IOException("Sin unidades libres. Usá repack para compactar en una carpeta nueva.");
                            targetPath = Path.Combine(pending, plan.Prefix + unitNumber + ".pak");
                            target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length); unitHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            if (plan.AppendUnit != null)
                            {
                                var original = Lock(AppendPath(archive!, unitNumber)); original.Position = 0;
                                if (original.Length != plan.AppendUnit.Bytes || !Convert.ToHexString(SHA256.HashData(original)).Equals(plan.AppendUnit.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Preimagen append cambió durante apply.");
                                original.Position = 0;
                                using var snapshot = new FileStream(Path.Combine(pending, "source-unit.original.bak"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                while (original.Position < original.Length)
                                {
                                    int n = await original.ReadAsync(buffer); if (n == 0) throw new EndOfStreamException("Append preimage");
                                    await target.WriteAsync(buffer.AsMemory(0, n)); unitHash.AppendData(buffer, 0, n);
                                    await snapshot.WriteAsync(buffer.AsMemory(0, n));
                                }
                                snapshot.Flush(true);
                            }
                        }
                        unit = (byte)unitNumber; offset = checked((uint)target.Position); size = checked((uint)length);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        for (long left = length; left > 0;)
                        {
                            int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                            if (n == 0) throw new EndOfStreamException(payload.Name);
                            await target.WriteAsync(buffer.AsMemory(0, n)); hash.AppendData(buffer, 0, n); unitHash!.AppendData(buffer, 0, n); left -= n;
                        }
                        resources.Add(new { path = payload.Name, size, sha256 = Convert.ToHexString(hash.GetHashAndReset()), unit, offset });
                    }
                    byte[] record = new byte[140]; record[0] = unit;
                    // LoadPack stores raw header names; FilterPathBuffer lowers
                    // ASCII before FindHeader. Match native AddHeader for newly
                    // serialized entries, retaining untouched original records.
                    string nativeName = new(payload.Name.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray());
                    Encoding.Latin1.GetBytes(".\\" + nativeName).CopyTo(record, 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(132, 4), size); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(136, 4), offset); records.Add(record);
                }
                CloseUnit();
            }
            finally { target?.Dispose(); unitHash?.Dispose(); }
            string indexOut = Path.Combine(pending, plan.Prefix + ".pak");
            await WriteIndexAsync(records, indexOut);
            var dependencies = new List<object>();
            if (archive != null && plan.Mode != "repack")
                foreach (var file in payloads.Where(p => p.Replacement == null).Select(p => p.Original!.UnitPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (plan.AppendUnit != null && file.Equals(AppendPath(archive, plan.AppendUnit.Number), StringComparison.OrdinalIgnoreCase)) continue;
                    var s = Lock(file); s.Position = 0;
                    dependencies.Add(new { file = Path.GetFileName(file), source = file, bytes = s.Length, sha256 = Convert.ToHexString(SHA256.HashData(s)) });
                }
            // Native readback checks encrypted index records; all written unit bytes
            // were reread above. This is structural integrity, not a client playtest.
            var appendPreimages = new List<object>();
            if (plan.AppendUnit != null)
            {
                string file = AppendPath(archive!, plan.AppendUnit.Number);
                CheckPrefix(Path.Combine(pending, Path.GetFileName(file)), plan.AppendUnit.Bytes, plan.AppendUnit.Sha256);
                appendPreimages.Add(new { file = Path.GetFileName(file), source = file, bytes = plan.AppendUnit.Bytes, sha256 = plan.AppendUnit.Sha256 });
            }
            var audit = new { version = plan.AppendUnit == null ? 1 : 2, tool = "dbo-assets 5.6", utc = DateTime.UtcNow, status = "candidate_not_installed", format = "taiwan", plan.Mode, plan.Prefix,
                plan, index = new { file = plan.Prefix + ".pak", sha256 = ContentSafety.Hash(indexOut), entries = records.Count },
                writtenUnits, requiredOriginalUnits = dependencies, appendPreimages, resources,
                newEntryPathPolicy = "native ASCII lowercase lookup; untouched original headers preserved",
                integrity = "DES index roundtrip + streamed unit SHA256 readback", gameCompatibility = "not_tested" };
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            ContentSafety.WriteJson(Path.Combine(pending, "plan.json"), plan);
            Directory.Move(pending, output);
            return new { output, index = Path.Combine(output, plan.Prefix + ".pak"), resources = records.Count, mode = plan.Mode, audit = Path.Combine(output, "audit.json"), gameCompatibility = "not_tested" };
        }
        finally
        {
            foreach (var stream in locks.Values) stream.Dispose();
            // A failed staging folder is deliberately retained for diagnosis; its
            // .pending name prevents it being mistaken for a committed candidate.
        }
    }
    private static async Task WriteIndexAsync(List<byte[]> records, string output)
    {
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsStudio", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        try
        {
            using (var raw = File.Create(Path.Combine(cache, "index.bin"))) foreach (var record in records) raw.Write(record);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "pak-tool.exe")) { WorkingDirectory = cache, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            StudioConfig.ApplyPakKey(start);
            foreach (string arg in new[] { "import-index", "index.bin", "index.pak" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("No inició pak-tool.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Timeout cifrando índice."); }
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException("Falló índice Taiwan: " + await stderr);
            File.Move(Path.Combine(cache, "index.pak"), output);
        }
        finally { Directory.Delete(cache, true); }
    }
    public static async Task<object> ExtractAsync(PakArchive archive, string query, string output)
    {
        output = ContentSafety.SafeOutput(output);
        var selected = archive.Entries.Where(e => query == "*" || e.Name.Equals(ContentSafety.ResourcePath(query), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0) throw new FileNotFoundException("No hay coincidencia exacta; '*' extrae todo.");
        using var indexLock = new FileStream(archive.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(indexLock)) != archive.IndexHash) throw new IOException("El índice cambió.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
        var report = new List<object>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in selected)
        {
            string rel = ContentSafety.ResourcePath(e.Name); if (!seen.Add(rel)) throw new InvalidDataException("Recurso duplicado: " + rel);
            string path = Path.Combine(pending, rel); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = new FileStream(e.UnitPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            if ((ulong)e.Offset + e.Size > (ulong)input.Length) throw new InvalidDataException("Payload fuera de unidad: " + rel);
            input.Position = e.Offset; using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[1024 * 1024]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long left = e.Size; left > 0;)
            { int n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length))); if (n == 0) throw new EndOfStreamException(); await target.WriteAsync(buffer.AsMemory(0, n)); hash.AppendData(buffer, 0, n); left -= n; }
            target.Flush(true); report.Add(new { path = rel, e.Size, sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
        }
        ContentSafety.WriteJson(Path.Combine(pending, "assets-extraction.json"), new { source = archive.IndexPath, archive.IndexHash, archive.Format,
            payloadSemantics = "extracted_payload",
            sourceBuildSha256 = archive.SourceBuildHash, sourceModuleSha256 = archive.SourceModuleHash, formatNotes = archive.FormatNotes, resources = report });
        Directory.Move(pending, output); return new { output, count = report.Count };
        }
        catch { Directory.Delete(pending, true); throw; }
    }

    public static async Task<object> ExtractManyAsync(PakArchive archive, string selectionFile, string output)
    {
        // Explicit, hash-pinned selections.
        // once per resource. Wildcards and implicit whole-family extraction stay forbidden.
        ContentSafety.NoLinks(selectionFile);
        if (new FileInfo(selectionFile).Length is < 1 or > 256 * 1024) throw new InvalidDataException("Selección fuera de límites.");
        string selectionHash = ContentSafety.Hash(selectionFile);
        byte[] selectionBytes = await File.ReadAllBytesAsync(selectionFile);
        if (Convert.ToHexString(SHA256.HashData(selectionBytes)) != selectionHash)
            throw new IOException("La selección cambió durante snapshot.");
        using var document = JsonDocument.Parse(selectionBytes);
        var selection = document.RootElement;
        if (!selection.GetProperty("sourceSha256").GetString()!.Equals(archive.IndexHash, StringComparison.OrdinalIgnoreCase) ||
            selection.GetProperty("sourceBuildSha256").GetString() != archive.SourceBuildHash)
            throw new IOException("Selección sellada para otro índice/build.");
        var names = selection.GetProperty("paths").EnumerateArray().Select(n => ContentSafety.ResourcePath(n.GetString()!)).ToArray();
        if (names.Length is < 1 or > 1024 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Selección literal única requerida (1..1024).");
        var lookup = archive.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var entries = names.Select(n => lookup.TryGetValue(n, out var e) ? e : throw new FileNotFoundException("Recurso ausente: " + n)).ToArray();
        if (entries.Sum(e => (long)e.Size) > 256L * 1024 * 1024) throw new InvalidDataException("Límite batch256MiB.");
        output = ContentSafety.SafeOutput(output);
        if (output.StartsWith(Path.GetDirectoryName(archive.IndexPath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Salida fuera de la carpeta del PAK fuente requerida.");
        using var indexLock = new FileStream(archive.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(indexLock)) != archive.IndexHash) throw new IOException("El índice cambió.");
        string pending = ContentSafety.FreshDirectory(output);
        try
        {
            IReadOnlyList<byte[]> payloads;
            if (archive.Format is "taiwan")
                payloads = await ReadStoredBatchAsync(archive, entries, indexLock);
            else { var list = new List<byte[]>(); foreach (var entry in entries) list.Add(await archive.ReadAsync(entry)); payloads = list; }
            var report = new List<object>();
            for (int i = 0; i < entries.Length; ++i)
            {
                string path = Path.Combine(pending, names[i]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, payloads[i]);
                report.Add(new { path = names[i], entries[i].Size, sha256 = ContentSafety.Hash(path) });
            }
            if (ContentSafety.Hash(selectionFile) != selectionHash)
                throw new IOException("La selección cambió durante extracción.");
            ContentSafety.WriteJson(Path.Combine(pending, "assets-extraction.json"), new { source = archive.IndexPath, archive.IndexHash,
                archive.Format, payloadSemantics = "extracted_payload",
                sourceBuildSha256 = archive.SourceBuildHash, sourceModuleSha256 = archive.SourceModuleHash, formatNotes = archive.FormatNotes, selectionSha256 = selectionHash, resources = report });
            Directory.Move(pending, output); return new { output, count = report.Count };
        }
        catch { Directory.Delete(pending, true); throw; }
    }

    private static async Task<IReadOnlyList<byte[]>> ReadStoredBatchAsync(PakArchive archive, PakEntry[] entries, FileStream indexLock)
    {
        // ExtractMany already validated the exact index hash and selection. Keep
        // that read lock for the whole batch; hold every unit against writers too.
        // Taiwan stores raw payloads.
        var units = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Size > 128L * 1024 * 1024 || entry.Status != "Disponible")
                    throw new IOException("Recurso batch ausente, fuera de límites o mayor a128MiB.");
                if (units.ContainsKey(entry.UnitPath)) continue;
                ContentSafety.NoLinks(entry.UnitPath);
                units[entry.UnitPath] = new(entry.UnitPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            }
            var payloads = new List<byte[]>(entries.Length);
            foreach (var entry in entries)
            {
                var unit = units[entry.UnitPath];
                if ((ulong)entry.Offset + entry.Size > (ulong)unit.Length)
                    throw new InvalidDataException("El recurso excede los límites de su unidad PAK.");
                unit.Position = entry.Offset;
                byte[] bytes = new byte[entry.Size];
                await unit.ReadExactlyAsync(bytes); payloads.Add(bytes);
            }
            indexLock.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(indexLock)) != archive.IndexHash)
                throw new IOException("El índice cambió durante extracción batch.");
            return payloads;
        }
        finally { foreach (var unit in units.Values) unit.Dispose(); }
    }

    public static object VerifyCandidate(string directory)
    {
        directory = Path.GetFullPath(directory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "audit.json")));
        var root = doc.RootElement;
        int version = root.GetProperty("version").GetInt32();
        if (version is not (1 or 2) || root.GetProperty("format").GetString() != "taiwan" || (version == 2 && root.GetProperty("mode").GetString() != "append-overlay")) throw new InvalidDataException("Auditoría no compatible.");
        var verified = new List<object>();
        void Check(JsonElement record, bool dependency)
        {
            string rel = ContentSafety.ResourcePath(record.GetProperty("file").GetString()!);
            if (rel.Contains('\\')) throw new InvalidDataException("El manifiesto requiere nombres de unidad simples.");
            string path = dependency ? record.GetProperty("source").GetString()! : Path.Combine(directory, rel);
            string hash = ContentSafety.Hash(path);
            if (!hash.Equals(record.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Hash difiere: " + path);
            if (record.TryGetProperty("bytes", out var length) && new FileInfo(path).Length != length.GetInt64()) throw new IOException("Tamaño difiere: " + path);
            verified.Add(new { path, sha256 = hash, dependency });
        }
        Check(root.GetProperty("index"), false);
        foreach (var unit in root.GetProperty("writtenUnits").EnumerateArray()) Check(unit, false);
        foreach (var unit in root.GetProperty("requiredOriginalUnits").EnumerateArray()) Check(unit, true);
        if (version == 2)
        {
            var pins = root.GetProperty("appendPreimages").EnumerateArray().ToArray();
            if (pins.Length != 1) throw new InvalidDataException("Append requiere exactamente una preimagen.");
            var pin = pins[0]; string file = ContentSafety.ResourcePath(pin.GetProperty("file").GetString()!);
            var sealedUnit = root.GetProperty("plan").GetProperty("appendUnit");
            if (root.GetProperty("plan").GetProperty("version").GetInt32() != 2 || root.GetProperty("plan").GetProperty("mode").GetString() != "append-overlay" ||
                sealedUnit.GetProperty("number").GetInt32() is < 0 or > 255 ||
                file != root.GetProperty("prefix").GetString() + sealedUnit.GetProperty("number").GetInt32() + ".pak" ||
                pin.GetProperty("bytes").GetInt64() != sealedUnit.GetProperty("bytes").GetInt64() ||
                !pin.GetProperty("sha256").GetString()!.Equals(sealedUnit.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Preimagen append no coincide con plan sellado.");
            if (file.Contains('\\') || !root.GetProperty("writtenUnits").EnumerateArray().Any(u => u.GetProperty("file").GetString() == file)) throw new InvalidDataException("Preimagen append sin unidad escrita.");
            Check(pin, true); CheckPrefix(Path.Combine(directory, file), pin.GetProperty("bytes").GetInt64(), pin.GetProperty("sha256").GetString()!);
        }
        return new { directory, verified, integrity = "manifest_hashes_match", gameCompatibility = "not_tested" };
    }
    private static void CheckPrefix(string file, long bytes, string expected)
    {
        ContentSafety.NoLinks(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        if (bytes < 0 || bytes > stream.Length) throw new IOException("Preimagen append fuera de límites.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[1024 * 1024];
        for (long left = bytes; left > 0;)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left)); if (n == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, n); left -= n;
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Append perdió bytes de la unidad original.");
    }
}
