using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DboAssetsStudio;

// Optional native observation of particle-embedded trail resources. Legacy
// dependency and resource-field protocols retain their original contracts.
internal static partial class EffectMergeService
{
    internal static async Task<object> InspectParticleTrailsAsync(string path)
    {
        path = Path.GetFullPath(path); ContentSafety.NoLinks(path);
        string helper = Path.Combine(AppContext.BaseDirectory, "effect-reader.exe"); ContentSafety.NoLinks(helper);
        using var sourceLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var helperLock = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string sourceHash = ContentSafety.Hash(path), helperHash = ContentSafety.Hash(helper);
        byte[] raw = Snapshot(new(path, sourceHash), pins);
        var native = await ReadNativeAsync(raw);
        if (native.Effects.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != native.Effects.Count)
            throw new InvalidDataException("Unique native EFF identities required for trail association.");
        var effects = native.Effects.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--particle-trails"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Native trail observer unavailable.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Native trail observer timeout."); }
        string output = await stdout, diagnostic = await stderr;
        if (process.ExitCode != 0 || output.Length > 64 * 1024 * 1024 || !diagnostic.Contains("PASS native effect reader") ||
            ContentSafety.Hash(path) != sourceHash || ContentSafety.Hash(helper) != helperHash)
            throw new IOException("Native particle trail read failed or input changed.");
        var rows = new List<object>(); var identities = new HashSet<(string, int)>(); int markers = 0;
        var observedEffects = new List<Row>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields[0] == "E")
            {
                if (fields.Length != 4) throw new InvalidDataException("Native trail EFF boundary shape.");
                observedEffects.Add(new(Integer(fields[1]), Integer(fields[2]), fields[3]));
                continue;
            }
            if (fields[0] != "T") continue;
            if (fields.Length == 2 && fields[1] == "1") { ++markers; continue; }
            if (fields.Length != 10 || !effects.TryGetValue(fields[1], out var effect) || fields[3].Length is < 1 or > 255 ||
                fields[4].Length > 518 || fields[4].Length % 2 != 0 || !fields[4].All(Uri.IsHexDigit))
                throw new InvalidDataException("Native particle trail row shape.");
            int index = Integer(fields[2]);
            if (index is < 0 or > 65535 || !identities.Add((fields[1], index)))
                throw new InvalidDataException("Native particle trail system identity.");
            string texture = Encoding.Latin1.GetString(Convert.FromHexString(fields[4]));
            if (texture.Any(c => char.IsControl(c))) throw new InvalidDataException("Native embedded texture contains controls.");
            int maxEdges = int.Parse(fields[5], CultureInfo.InvariantCulture), splinePoints = int.Parse(fields[6], CultureInfo.InvariantCulture);
            float width = float.Parse(fields[7], CultureInfo.InvariantCulture), life = float.Parse(fields[8], CultureInfo.InvariantCulture), gap = float.Parse(fields[9], CultureInfo.InvariantCulture);
            if (!float.IsFinite(width) || !float.IsFinite(life) || !float.IsFinite(gap)) throw new InvalidDataException("Nonfinite native trail dimensions.");
            rows.Add(new { effect = fields[1], systemIndex = index, systemName = fields[3], texture, textureHex = fields[4],
                maxEdges, splinePoints, width, edgeLifeTime = life, edgeGap = gap,
                effectRecordSha256 = Hash(raw.AsSpan(effect.Offset, effect.Length).ToArray()),
                serializedTextureOffsetObserved = false, renderingTested = false });
        }
        if (markers != 1 || !observedEffects.SequenceEqual(native.Effects))
            throw new InvalidDataException("Native particle trail marker/boundaries differ from the canonical reader.");
        return new { profile = "taiwan-eff104-particle-embedded-trails-v1", source = path, sourceSha256 = sourceHash,
            helperSha256 = helperHash, nativeReaderPass = true, effects = native.Effects.Count, particleTrails = rows,
            dependencyCatalogScope = "Particle m_EmitterTrail.strTexture when rpPRTSTDEMITTERDATAFLAGTRAIL is active",
            nativeLoadMayNormalizeLegacyMaxEdges = true, serializedTextureOffsetsObserved = false,
            resourceClosureVerified = false, runtimeModified = false, renderingTested = false, worldTested = false };
    }
}
