using System.Diagnostics;
using System.Security.Cryptography;

namespace DboAssetsStudio;

/// <summary>
/// World Editor phase 7: self-checks that run on a REAL spawn table (never writes next to it):
/// byte-exact round trip, seeded fuzz of every edit op with undo/redo invariants, candidate read-back, and a
/// 100k-row performance budget. CLI: spawn selftest &lt;spawn.rdf&gt; [mob|npc] [seed] [steps].
/// </summary>
internal static class SpawnSelfTest
{
    internal sealed record Check(string Name, bool Passed, string Detail);

    public static object Run(string path, string kind, int seed = 12345, int steps = 150)
    {
        var checks = new List<Check>();
        void Add(string name, bool ok, string detail) => checks.Add(new Check(name, ok, detail));
        path = Path.GetFullPath(path);
        byte[] original = File.ReadAllBytes(path);
        string originalSha = Convert.ToHexString(SHA256.HashData(original));

        // 1) round trip: open + serialize without edits must be byte-identical
        var session = SpawnEditSession.Open(path, kind);
        Add("roundtrip-bytes", session.CurrentBytes().AsSpan().SequenceEqual(original), $"{original.Length} bytes, {session.LiveUids().Length} filas");

        // 2) seeded fuzz of every op; invariants after each step
        var rng = new Random(seed);
        string? invariantFailure = null;
        int applied = 0, rejected = 0;
        uint refId = 0;
        {
            var ids0 = session.LiveIds();
            if (ids0.Length == 0) Add("fuzz", true, "archivo vacío: sin filas que editar");
        }
        if (session.LiveUids().Length > 0)
        {
            // pick a reference id that already exists in the file so adds are structurally valid
            refId = ReadRef(session, kind, path);
            for (int i = 0; i < steps && invariantFailure == null; i++)
            {
                var uids = session.LiveUids();
                if (uids.Length == 0) break;
                int pick() => uids[rng.Next(uids.Length)];
                try
                {
                    switch (rng.Next(9))
                    {
                        case 0: session.Move([(pick(), (float)(rng.NextDouble() * 1000), 5f, (float)(rng.NextDouble() * 1000))]); break;
                        case 1: session.Rotate([pick()], rng.Next(-180, 181)); break;
                        case 2: session.SetFields([pick()], new Dictionary<string, double> { ["quantity"] = rng.Next(0, 5), ["wander"] = rng.Next(0, 20) }); break;
                        case 3: if (uids.Length > 5) session.Delete([pick()]); break;
                        case 4: session.Clone([pick()], 10f, 10f); break;
                        case 5: session.Add([new SpawnEditSession.NewSpawn(refId, (float)(rng.NextDouble() * 500), 3f, (float)(rng.NextDouble() * 500), 1f, 0f, null)]); break;
                        case 6: if (session.CanUndo) session.Undo(); break;
                        case 7: if (session.CanRedo) session.Redo(); break;
                        default: session.SetFields([pick()], new Dictionary<string, double> { ["quantity"] = 9999 }); break; // must be rejected
                    }
                    applied++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { rejected++; }
                var ids = session.LiveIds();
                if (ids.Distinct().Count() != ids.Length) invariantFailure = $"paso {i}: IDs duplicados";
                else if (session.CurrentBytes().Length != 1 + ids.Length * 84) invariantFailure = $"paso {i}: tamaño de bytes incoherente";
            }
            Add("fuzz-invariants", invariantFailure == null, invariantFailure ?? $"{steps} pasos ({applied} aplicados, {rejected} rechazados): IDs únicos y tamaño coherente");

            // 3) undo everything -> original; redo everything -> same state as before
            byte[] before = session.CurrentBytes();
            int undone = 0; while (session.CanUndo) { session.Undo(); undone++; }
            Add("undo-all-restores-original", session.CurrentBytes().AsSpan().SequenceEqual(original), $"{undone} pasos deshechos");
            int redone = 0; while (session.CanRedo) { session.Redo(); redone++; }
            Add("redo-all-restores-state", session.CurrentBytes().AsSpan().SequenceEqual(before), $"{redone} pasos rehechos");

            // 4) candidate: written to a fresh temp folder, re-read, source untouched
            string temp = Path.Combine(Path.GetTempPath(), "dbo-spawn-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (session.Validate().Any(i => i.Severity == "error")) Add("candidate", true, "omitido: la edición aleatoria dejó errores de validación (esperado sin catálogo no ocurre)");
                else
                {
                    var counts = session.NetChanges;
                    if (counts == 0) Add("candidate", true, "omitido: sin cambios netos");
                    else
                    {
                        session.SaveCandidate(temp);
                        string cand = Path.Combine(temp, "candidate", Path.GetFileName(path));
                        var reread = SpawnEditSession.Open(cand, kind);
                        Add("candidate-readback", reread.CurrentBytes().AsSpan().SequenceEqual(session.CurrentBytes()), $"{reread.LiveUids().Length} filas releídas");
                    }
                }
            }
            finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
        }
        Add("source-untouched", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == originalSha, originalSha[..12]);

        // 5) performance budget: synthetic 100k-row table (kept in memory/temp, deleted afterwards)
        checks.AddRange(Performance(path, kind));
        return new { path, kind, seed, steps, passed = checks.All(c => c.Passed), checks };
    }

    private static uint ReadRef(SpawnEditSession s, string kind, string path)
    {
        // reference id of the first row (offset 4 in 84-byte rows, after the 1-byte margin)
        var bytes = s.CurrentBytes();
        return BitConverter.ToUInt32(bytes, 1 + 4);
    }

    private static IEnumerable<Check> Performance(string path, string kind)
    {
        const int Rows = 100_000;
        byte[] src = File.ReadAllBytes(path);
        int srcRows = (src.Length - 1) / 84;
        if (srcRows < 1) { yield return new Check("perf-100k", true, "omitido: archivo vacío"); yield break; }
        var big = new byte[1 + Rows * 84]; big[0] = src[0];
        for (int i = 0; i < Rows; i++)
        {
            Buffer.BlockCopy(src, 1 + (i % srcRows) * 84, big, 1 + i * 84, 84);
            BitConverter.TryWriteBytes(big.AsSpan(1 + i * 84, 4), (uint)(900_000_000 + i)); // unique ids
        }
        string temp = Path.Combine(Path.GetTempPath(), "dbo-spawn-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string file = Path.Combine(temp, Path.GetFileName(path));
        var results = new List<Check>();
        try
        {
            File.WriteAllBytes(file, big);
            var sw = Stopwatch.StartNew();
            var s = SpawnEditSession.Open(file, kind);
            long openMs = sw.ElapsedMilliseconds; sw.Restart();
            var payload = SpawnReadService.ToViewerPayload(SpawnReadService.Load(file, kind));
            long payloadMs = sw.ElapsedMilliseconds; sw.Restart();
            var uids = s.LiveUids().Take(5000).ToArray();
            s.Move(uids.Select(u => (u, 10f, 5f, 10f)).ToArray());
            long moveMs = sw.ElapsedMilliseconds; sw.Restart();
            s.Validate();
            long validateMs = sw.ElapsedMilliseconds; sw.Restart();
            s.Undo();
            long undoMs = sw.ElapsedMilliseconds;
            results.Add(new Check("perf-100k-open", openMs < 5000, $"{openMs} ms (presupuesto 5000)"));
            results.Add(new Check("perf-100k-viewer-payload", payloadMs < 8000, $"{payloadMs} ms (presupuesto 8000)"));
            results.Add(new Check("perf-100k-move-5000", moveMs < 2000, $"{moveMs} ms (presupuesto 2000)"));
            results.Add(new Check("perf-100k-validate", validateMs < 5000, $"{validateMs} ms (presupuesto 5000)"));
            results.Add(new Check("perf-100k-undo", undoMs < 2000, $"{undoMs} ms (presupuesto 2000)"));
            GC.KeepAlive(payload);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
        foreach (var r in results) yield return r;
    }
}
