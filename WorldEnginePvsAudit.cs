using System.Text.Json;

namespace DboAssetsStudio;

// World Editor · audit of P\------- (sector PVS). The engine has no PVS writer; world-host rewrites the file on save
// with only the recalculated sector records changed and writes <workspace>\pvs-edits.json with every sector it
// rewrote. This compares the cache and workspace files record by record (per sector: 1 flag byte + 169x6 bytes when
// the flag is set) so the candidate audit can prove nothing else moved.
internal static class WorldEnginePvsAudit
{
    internal const int RecordBytes = 169 * 6;

    internal sealed record PvsResult(bool Changed, bool Ok, object Detail);

    internal static PvsResult Describe(string workspaceRoot, string baseWorld, string wsWorld)
    {
        string before = Path.Combine(baseWorld, "-------"), after = Path.Combine(wsWorld, "-------");
        int[] expected = [];
        string sidecar = Path.Combine(workspaceRoot, "pvs-edits.json");
        if (File.Exists(sidecar))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecar));
            expected = doc.RootElement.GetProperty("sectors").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        }
        if (!File.Exists(after)) return new PvsResult(false, true, new { file = "-------", present = false });
        byte[] a = File.ReadAllBytes(after);
        byte[] b = File.Exists(before) ? File.ReadAllBytes(before) : [];
        if (a.AsSpan().SequenceEqual(b)) return new PvsResult(false, true, new { file = "-------", changed = false, rewrittenSectors = expected });
        var ra = Records(a, out int tailA); var rb = Records(b, out int tailB);
        var changed = new List<int>(); int n = Math.Max(ra.Count, rb.Count);
        for (int i = 0; i < n; i++)
        {
            var x = i < ra.Count ? ra[i] : default; var y = i < rb.Count ? rb[i] : default;
            if (x.Length != y.Length || !a.AsSpan(x.Offset, x.Length).SequenceEqual(b.AsSpan(y.Offset, y.Length))) changed.Add(i);
        }
        bool tailSame = a.AsSpan(a.Length - tailA).SequenceEqual(b.AsSpan(b.Length - tailB));
        var unexpected = changed.Where(s => !expected.Contains(s)).ToArray();
        bool ok = unexpected.Length == 0 && tailSame && ra.Count == rb.Count;
        return new PvsResult(true, ok, new
        {
            file = "-------", changed = true, recordBytes = RecordBytes + 1, sectorRecords = ra.Count, sectorRecordsBefore = rb.Count,
            rewrittenSectors = expected, changedSectors = changed, unexpectedSectors = unexpected,
            identicalSectorRecords = ra.Count - changed.Count, trailingBytesIdentical = tailSame,
            onlyRebuiltSectorsChanged = ok,
            note = "PVS lives outside the fields: P\\------- changes together with the edited fields; every other sector record is byte-identical.",
        });
    }

    private readonly record struct Rec(int Offset, int Length);

    private static List<Rec> Records(byte[] data, out int tail)
    {
        var list = new List<Rec>(); int at = 0;
        while (at < data.Length)
        {
            int len = data[at] != 0 ? 1 + RecordBytes : 1;
            if (at + len > data.Length) break;
            list.Add(new Rec(at, len)); at += len;
        }
        tail = data.Length - at;
        return list;
    }
}
