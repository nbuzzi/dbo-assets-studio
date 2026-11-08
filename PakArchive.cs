using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed record PakEntry(int Number, string Name, byte Unit, uint Offset, uint Size, string UnitPath, string Status);

internal sealed class PakArchive
{
    public required string IndexPath { get; init; }
    public required string IndexHash { get; init; }
    public required IReadOnlyList<PakEntry> Entries { get; init; }
    public string Format { get; init; } = "taiwan";
    public string Prefix { get; init; } = "";
    public string? SourceModuleHash { get; init; }
    public string? FormatNotes { get; init; }
    public string? SourceBuildHash { get; init; }

    public static async Task<PakArchive> OpenAsync(string selectedPath, string format = "taiwan", string? family = null)
    {
        if (format != "taiwan") throw new InvalidDataException("Formato soportado: taiwan.");
        string index = Path.GetFullPath(selectedPath);
        string stem = Path.GetFileNameWithoutExtension(index);
        var unitMatch = Regex.Match(stem, @"^(.*?)(\d+)$");
        if (unitMatch.Success)
        {
            string sibling = Path.Combine(Path.GetDirectoryName(index)!, unitMatch.Groups[1].Value + ".pak");
            if (!File.Exists(sibling)) throw new IOException("Esta es una unidad de datos. Falta su índice: " + sibling);
            index = sibling;
        }
        using var source = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > 128L * 1024 * 1024) throw new IOException("El archivo supera el límite del índice (128 MiB). Abrí el índice, por ejemplo tex.pak.");
        string hash = Convert.ToHexString(SHA256.HashData(source));
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsStudio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            // The native serializer uses ANSI filenames: pass an ASCII cache path
            // and keep all original Unicode paths in managed code.
            source.Position = 0;
            string snapshot = Path.Combine(cache, "index.pak"), raw = Path.Combine(cache, "index.bin");
            using (var copy = File.Create(snapshot)) await source.CopyToAsync(copy);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "pak-tool.exe"))
            { WorkingDirectory = cache, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            StudioConfig.ApplyPakKey(start);
            start.ArgumentList.Add("export-index"); start.ArgumentList.Add("index.pak"); start.ArgumentList.Add("index.bin");
            using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el lector Taiwan.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("El lector PAK excedió 45 segundos."); }
            // The deadline covers the redirected stream readers too: waiting
            // for process exit alone does not bound inherited pipe handles.
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidDataException("No es un índice Taiwan válido o está dañado. Otros formatos de PAK no están soportados.\n" + stderr.Result);
            // This bounded, already completed native snapshot is local. Read it
            // sequentially without an outstanding overlapped read during parse.
            byte[] bytes = File.ReadAllBytes(raw);
            if (bytes.Length % 140 != 0) throw new InvalidDataException("Índice con ABI inválida.");
            var entries = new List<PakEntry>();
            var lengths = new Dictionary<byte, long>();
            stem = Path.GetFileNameWithoutExtension(index);
            // Directory enumeration already returns file lengths. Resolve each
            // exact filename from that metadata snapshot instead of issuing
            // File.Exists + FileInfo.Length for every referenced unit. Apply
            // still locks/rechecks actual index and unit bytes before writing.
            var unitFiles = new DirectoryInfo(Path.GetDirectoryName(index)!)
                .EnumerateFiles(stem + "*.pak")
                .ToDictionary(file => file.Name, file => file.Length, StringComparer.OrdinalIgnoreCase);
            for (int offset = 0; offset < bytes.Length; offset += 140)
            {
                byte unit = bytes[offset];
                int nul = Array.IndexOf(bytes, (byte)0, offset + 1, 128);
                if (nul < 0) throw new InvalidDataException("Nombre de recurso sin terminador.");
                string name = Encoding.Latin1.GetString(bytes, offset + 1, nul - offset - 1).Replace('/', '\\');
                if (name.StartsWith(".\\")) name = name[2..];
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 132, 4));
                uint position = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 136, 4));
                string unitPath = Path.Combine(Path.GetDirectoryName(index)!, stem + unit + ".pak");
                if (!lengths.TryGetValue(unit, out long length))
                    lengths[unit] = length = unitFiles.TryGetValue(stem + unit + ".pak", out long observedLength) ? observedLength : -1;
                string status = length < 0 ? "Falta unidad" : (ulong)position + size > (ulong)length ? "Fuera de límites" : "Disponible";
                entries.Add(new PakEntry(entries.Count, name, unit, position, size, unitPath, status));
            }
            return new PakArchive { IndexPath = index, IndexHash = hash, Entries = entries, Prefix = stem };
        }
        finally { Directory.Delete(cache, true); }
    }

    public async Task<byte[]> ReadAsync(PakEntry entry)
    {
        if (!Entries.Contains(entry)) throw new InvalidDataException("Entrada ajena al archivo abierto.");
        if (entry.Size > 128L * 1024 * 1024) throw new IOException("Vista previa/extracción limitada a 128 MiB por archivo.");
        using (var index = File.OpenRead(IndexPath))
            if (Convert.ToHexString(SHA256.HashData(index)) != IndexHash) throw new IOException("El índice cambió. Volvé a abrir el PAK.");
        using var unit = new FileStream(entry.UnitPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if ((ulong)entry.Offset + entry.Size > (ulong)unit.Length) throw new InvalidDataException("El recurso excede los límites de su unidad PAK.");
        unit.Position = entry.Offset;
        byte[] bytes = new byte[entry.Size];
        await unit.ReadExactlyAsync(bytes);
        return bytes;
    }

    // Canonical streaming observer for large semantic inventories. Keeps the
    // index/build/units immutable, with one bounded payload in memory.
    internal async Task VisitPayloadsAsync(IReadOnlyList<PakEntry> entries, Func<PakEntry, byte[], Task> visit)
    {
        if (entries.Count is < 1 or > 50000 || entries.Any(e => e.Number < 0 || e.Number >= Entries.Count || Entries[e.Number] != e || e.Status != "Disponible"))
            throw new InvalidDataException("Bounded exact available entries required.");
        using var index = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(index)) != IndexHash) throw new IOException("Source index changed.");
        if (Format != "taiwan") throw new InvalidDataException("Streaming payload profile not implemented.");
        var units = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in entries)
            {
                if (!units.TryGetValue(entry.UnitPath, out var unit))
                { ContentSafety.NoLinks(entry.UnitPath); units[entry.UnitPath] = unit = new FileStream(entry.UnitPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                if (entry.Size > 128L * 1024 * 1024 || (ulong)entry.Offset + entry.Size > (ulong)unit.Length) throw new IOException("Bounded payload required.");
                unit.Position = entry.Offset; byte[] bytes = new byte[entry.Size]; await unit.ReadExactlyAsync(bytes); await visit(entry, bytes);
            }
        }
        finally { foreach (var unit in units.Values) unit.Dispose(); }
    }
}
