using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DboAssetsStudio;

// Add exact rows from another Taiwan table using the compiled native layout.
// No decoding/re-encoding, replacement, balance conversion or deployment.
internal static class NativeTableMergeService
{
    public static object Merge(string target, string source, string plan, string output)
    {
        foreach (var path in new[] { target, source, plan }) ContentSafety.NoLinks(path);
        if (!string.Equals(Path.GetFileName(target), Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Matching native table names required.");
        using var layouts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resource-layout.json")));
        var matches = layouts.RootElement.EnumerateArray().Where(x => string.Equals(x.GetProperty("table").GetString(), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("kind").GetString() is not ("Mob" or "Npc"))
            throw new InvalidDataException("Only registered native Mob/Npc tables supported.");
        var layout = matches[0];
        int stride = layout.GetProperty("rowSize").GetInt32(), idOffset = layout.GetProperty("idOffset").GetInt32();
        int validity = layout.TryGetProperty("validityOffset", out var v) ? v.GetInt32() : -1;
        if (stride < 4 || stride > 8192 || idOffset < 0 || idOffset + 4 > stride || validity < -1 || validity >= stride)
            throw new InvalidDataException("Invalid native layout.");
        byte[] Read(string path)
        {
            if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new IOException("RDF exceeds 128 MiB.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 1 || bytes[0] != 1 || (bytes.Length - 1) % stride != 0)
                throw new InvalidDataException("RDF does not match native Taiwan margin/stride.");
            return bytes;
        }
        Dictionary<uint,int> Rows(byte[] bytes)
        {
            var rows = new Dictionary<uint,int>();
            for (int offset = 1; offset < bytes.Length; offset += stride)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + idOffset, 4));
                if (id == 0 || id == uint.MaxValue || !rows.TryAdd(id, offset))
                    throw new InvalidDataException("Invalid/duplicate native row ID.");
            }
            return rows;
        }
        byte[] before = Read(target), from = Read(source);
        var existing = Rows(before); var available = Rows(from);
        if (new FileInfo(plan).Length > 1024 * 1024) throw new IOException("Plan exceeds 1 MiB.");
        byte[] planBytes = File.ReadAllBytes(plan);
        using var document = JsonDocument.Parse(planBytes); var p = document.RootElement;
        var keys = p.EnumerateObject().Select(x => x.Name).ToArray();
        if (keys.Length != 3 || !keys.ToHashSet().SetEquals(new[] { "targetSha256", "sourceSha256", "ids" }))
            throw new InvalidDataException("Plan requires only targetSha256/sourceSha256/ids.");
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        string targetHash = Hash(before), sourceHash = Hash(from);
        if (!string.Equals(targetHash, p.GetProperty("targetSha256").GetString(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceHash, p.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source/target hash changed.");
        var ids = p.GetProperty("ids").EnumerateArray().Select(x => uint.Parse(x.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (ids.Length == 0 || ids.Length > 4096 || ids.Distinct().Count() != ids.Length
            || ids.Any(id => id == 0 || id == uint.MaxValue || existing.ContainsKey(id) || !available.ContainsKey(id)
                || (validity >= 0 && from[available[id] + validity] == 0)))
            throw new InvalidDataException("Selected rows must be new, unique and active.");
        int size = checked(before.Length + stride * ids.Length);
        if (size > 128 * 1024 * 1024) throw new IOException("Output exceeds 128 MiB.");
        byte[] candidate = new byte[size]; before.CopyTo(candidate, 0);
        for (int i = 0; i < ids.Length; ++i) Array.Copy(from, available[ids[i]], candidate, before.Length + i * stride, stride);
        Rows(candidate);
        string destination = ContentSafety.SafeOutput(output);
        foreach (var input in new[] { target, source, plan })
            if (Path.GetFullPath(input).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output cannot contain inputs.");
        string pending = ContentSafety.FreshDirectory(destination);
        try
        {
            if (ContentSafety.Hash(target) != targetHash || ContentSafety.Hash(source) != sourceHash) throw new IOException("Input changed before write.");
            string name = Path.GetFileName(target), file = Path.Combine(pending, name);
            File.WriteAllBytes(file, candidate); File.WriteAllBytes(file + ".original.bak", before);
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(candidate)) throw new IOException("Readback differs.");
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { format = "native-table-merge-v1", table = name,
                targetSha256 = targetHash, sourceSha256 = sourceHash, outputSha256 = Hash(candidate), rowSize = stride,
                planSha256 = Hash(planBytes),
                preservedRows = existing.Count, addedIds = ids, byteExactReadback = true, worldTested = false });
            Directory.Move(pending, destination);
            return new { output = Path.Combine(destination, name), preservedRows = existing.Count, addedRows = ids.Length, sha256 = Hash(candidate), worldTested = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
