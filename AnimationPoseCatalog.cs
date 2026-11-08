using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal static class AnimationPoseCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Sample(float Time, string Bone);
    private sealed record Plan(int Version, Source Model, Source Animation, List<Sample> Samples);
    public static async Task<object> InspectAsync(string path, string? sealedHelperPath = null)
    {
        string planPath = Path.GetFullPath(path); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Pose plan maximum1MiB.");
        string planHash = ContentSafety.Hash(planPath);
        Plan plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllBytesAsync(planPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Pose plan required.");
        if (plan.Version != 1 || plan.Model is null || plan.Animation is null || plan.Samples is null || plan.Samples.Count is < 1 or > 128)
            throw new InvalidDataException("Pose plan version1, 1..128 samples required.");
        string helper = sealedHelperPath is null ? Path.Combine(AppContext.BaseDirectory, "media-helper.exe") : Path.GetFullPath(sealedHelperPath); ContentSafety.NoLinks(helper);
        string helperHash = ContentSafety.Hash(helper);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<byte[]> Snapshot(Source source, string extension)
        {
            string file = Path.GetFullPath(Path.IsPathRooted(source.Path) ? source.Path : Path.Combine(Path.GetDirectoryName(planPath)!, source.Path));
            ContentSafety.NoLinks(file);
            if (Path.GetExtension(file).ToLowerInvariant() != extension || new FileInfo(file).Length is < 1 or > 128L * 1024 * 1024 ||
                source.Sha256 is null || source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Pose source/hash/extension invalid.");
            byte[] data = await File.ReadAllBytesAsync(file);
            string hash = Convert.ToHexString(SHA256.HashData(data));
            if (!hash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase) || ContentSafety.Hash(file) != hash) throw new IOException("Pose source changed or pin mismatch.");
            sources[file] = hash; return data;
        }
        byte[] model = await Snapshot(plan.Model, ".dff"), animation = await Snapshot(plan.Animation, ".anm");
        using var bundle = new MemoryStream(); using var writer = new BinaryWriter(bundle, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("OMP1")); writer.Write((uint)model.Length); writer.Write(model);
        writer.Write((uint)animation.Length); writer.Write(animation); writer.Write((uint)plan.Samples.Count);
        foreach (var sample in plan.Samples)
        {
            if (!float.IsFinite(sample.Time) || sample.Time < 0 || sample.Bone is null || sample.Bone.Length > 31 || sample.Bone.Any(c => c < 32 || c > 126))
                throw new InvalidDataException("Finite non-negative time and ASCII bone<=31 required.");
            byte[] bone = Encoding.ASCII.GetBytes(sample.Bone); writer.Write(sample.Time); writer.Write((uint)bone.Length); writer.Write(bone);
        }
        if (bundle.Length > 128L * 1024 * 1024) throw new InvalidDataException("Pose bundle maximum128MiB.");
        using var result = JsonDocument.Parse(await NativeMedia.ConvertAsync("model-poses", bundle.ToArray(), ".omp", helperPath: helper));
        if (ContentSafety.Hash(planPath) != planHash || ContentSafety.Hash(helper) != helperHash || sources.Any(x => ContentSafety.Hash(x.Key) != x.Value))
            throw new IOException("Pose plan/helper/source changed during inspection.");
        var native = result.RootElement;
        if (!native.GetProperty("texturesSubstituted").GetBoolean() || native.GetProperty("renderingTested").GetBoolean() ||
            !native.GetProperty("animationBinding").GetProperty("nativeHierarchyPlayback").GetBoolean() ||
            native.GetProperty("poseSamples").GetArrayLength() != plan.Samples.Count) throw new InvalidDataException("Native pose result inconsistent.");
        foreach (var sample in native.GetProperty("poseSamples").EnumerateArray())
            if (sample.GetProperty("position").GetArrayLength() != 3 || sample.GetProperty("position").EnumerateArray().Any(x => !float.IsFinite(x.GetSingle())))
                throw new InvalidDataException("Non-finite native pose position.");
        return new { planPath, planSha256 = planHash, modelSha256 = plan.Model.Sha256, animationSha256 = plan.Animation.Sha256,
            helperSha256 = helperHash, native = native.Clone(), sourcePreserved = true, nativeHierarchyPlayback = true,
            texturesSubstituted = true, renderingTested = false, eventsTested = false, worldTested = false,
            scope = "Native DFF/ANM local pose only; no character bone scaling, equipped weapons, world transform, rendering or gameplay certification." };
    }
}
