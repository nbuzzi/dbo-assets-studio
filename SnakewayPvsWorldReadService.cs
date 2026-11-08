using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

// Read-only, native client PAK/BSP probe. The full client remains the renderer QA.
internal static class SnakewayPvsWorldReadService
{
    private const string Profile = "snakeway-v4-packed-bsp-runtime-read-v1";
    private const string ResourcePath = "world/dbo_assets_snakeway_001/dbo_assets_snakeway_001.bsp";
    private const string ResourceSha256 = "A65C0EB6E12DE54B4C231D42D185C327306D3BD93327FB850203BA86CDDEA888";
    private static readonly string[] PackHeaders = ["tex.pak", "obj.pak", "ter.pak", "gui.pak", "flash.pak", "sound.pak", "lang.pak", "scr.pak", "tbl.pak", "prop.pak"];
    private static readonly string[] SourceFiles = [
        "NtlPLVisualManager.cpp", "NtlPLRenderCallback.cpp", "NtlWorldFieldManager4RWWorld.cpp"];
    private static readonly JsonSerializerOptions Strict = new(ContentSafety.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "snakeway-pvs-world-reader.exe");

    internal sealed record Pin(string Path, string Sha256, long Bytes);
    internal sealed record Plan(string Profile, string ClientRoot, string SourceRoot, string ResourcePath,
        string ResourceSha256, string HelperSha256, Pin[] PackHeaders, Pin[] SourceFiles);
    private sealed record Bounds(float[] Inf, float[] Sup);
    private sealed record Collision(string Probe, bool Attempted, float X, float Z,
        float RayStartY, float RayEndY, bool Hit, float? Y, uint? MaterialIndex);
    private sealed record NativeProbe(string ResourcePath, string PackFile, uint PayloadOffset, uint PayloadBytes,
        bool HasPvs, Bounds Bbox, uint Sectors, uint Vertices, uint Triangles, int Materials,
        uint TextureCount, string[] Textures, Collision[] Collision);

    private static string FullAsciiPath(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Any(c => c > 127 || c < 32 || c == '|')) throw new InvalidDataException("Native probe paths must be ASCII without controls.");
        ContentSafety.NoLinks(path);
        return path;
    }

    private static Pin PinFile(string path)
    {
        path = FullAsciiPath(path);
        long bytes = new FileInfo(path).Length;
        if (bytes < 1 || bytes > 512L * 1024 * 1024) throw new InvalidDataException("Native probe input size is outside bounds.");
        return new(path, ContentSafety.Hash(path), bytes);
    }

    private static string SourcePath(string root, string file) => Path.Combine(root,
        "DBO-Client-2.0", "Lib", "NtlPresentation", file);

    private static void VerifySourceFix(string path, string file)
    {
        byte[] source = File.ReadAllBytes(path);
        bool valid = file switch
        {
            "NtlPLVisualManager.cpp" => Contains(source, "if (pWorld)") && Contains(source, "const RwBool bHasPVS = RpPVSQuery(pWorld);") && Contains(source, "pWorldManager->EndRenderSector();"),
            "NtlPLRenderCallback.cpp" => Contains(source, "pWorld && RpPVSQuery(pWorld)") && Contains(source, "if (!bHasPVS ||"),
            "NtlWorldFieldManager4RWWorld.cpp" => Contains(source, "if (RpPVSQuery(m_pRpWorld))") && Contains(source, "RpPVSHook(m_pRpWorld);") && Contains(source, "RpPVSUnhook(m_pRpWorld);"),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Pinned native PVS source fix is absent: " + file);
    }

    private static bool Contains(byte[] source, string marker) =>
        source.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(marker)) >= 0;

    internal static object CreatePlan(string clientRoot, string sourceRoot, string output)
    {
        clientRoot = FullAsciiPath(clientRoot);
        sourceRoot = FullAsciiPath(sourceRoot);
        ContentSafety.NoLinks(Helper);
        string packRoot = Path.Combine(clientRoot, "pack");
        ContentSafety.NoLinks(packRoot);
        if (!Directory.Exists(packRoot)) throw new DirectoryNotFoundException("Private client pack directory is missing.");

        var headers = PackHeaders.Select(name => PinFile(Path.Combine(packRoot, name))).ToArray();
        var sources = SourceFiles.Select(name =>
        {
            string path = FullAsciiPath(SourcePath(sourceRoot, name));
            VerifySourceFix(path, name);
            return PinFile(path);
        }).ToArray();
        var plan = new Plan(Profile, clientRoot, sourceRoot, ResourcePath, ResourceSha256,
            ContentSafety.Hash(Helper), headers, sources);
        output = ContentSafety.SafeOutput(output);
        if (IsWithin(output, clientRoot) || IsWithin(output, sourceRoot) || plan.PackHeaders.Concat(plan.SourceFiles).Any(x => x.Path.Equals(output, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Plan output overlaps read-only inputs.");
        ContentSafety.WriteJson(output, plan);
        return new { plan = output, profile = Profile, resourcePath = ResourcePath, expectedResourceSha256 = ResourceSha256,
            packHeadersPinned = headers.Length, sourceFixFilesPinned = sources.Length, nativeReadOnly = true };
    }

    internal static async Task<object> RunAsync(string planPath, string output)
    {
        planPath = FullAsciiPath(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 64 * 1024) throw new InvalidDataException("Native BSP plan size.");
        var locks = new List<FileStream>();
        string? pending = null;
        try
        {
            locks.Add(new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            byte[] planBytes = File.ReadAllBytes(planPath);
            using (var document = JsonDocument.Parse(planBytes)) TerrainGeometryService.NoDuplicateKeys(document.RootElement);
            var plan = JsonSerializer.Deserialize<Plan>(planBytes, Strict) ?? throw new InvalidDataException("Missing native BSP plan.");
            ValidatePlan(plan);
            if (plan.PackHeaders.Any(x => x == null) || plan.SourceFiles.Any(x => x == null)) throw new InvalidDataException("Missing input pin.");

            var pins = plan.PackHeaders.Concat(plan.SourceFiles).ToArray();
            if (pins.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Length)
                throw new InvalidDataException("Duplicate native input paths.");
            foreach (var pin in pins)
            {
                ContentSafety.NoLinks(pin.Path);
                locks.Add(new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (PinFile(pin.Path) != pin) throw new InvalidDataException("Stale native BSP input pin.");
            }
            ContentSafety.NoLinks(Helper);
            locks.Add(new FileStream(Helper, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (ContentSafety.Hash(Helper) != plan.HelperSha256) throw new InvalidDataException("Native BSP helper changed; reseal the plan.");

            output = ContentSafety.SafeOutput(output);
            if (IsWithin(output, plan.ClientRoot) || IsWithin(output, plan.SourceRoot) ||
                pins.Any(x => IsWithin(output, x.Path)) || IsWithin(output, Helper) || IsWithin(output, planPath))
                throw new IOException("Native BSP output must be a fresh directory outside all read-only inputs.");
            pending = ContentSafety.FreshDirectory(output);

            var start = new ProcessStartInfo(Helper)
            {
                WorkingDirectory = plan.ClientRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(plan.ClientRoot);
            using var process = Process.Start(start) ?? throw new IOException("Native Snake Way BSP helper is unavailable.");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                throw new IOException("Native Snake Way BSP probe timed out after 120 seconds.");
            }
            string nativeJson = await stdoutTask;
            string nativeLog = await stderrTask;
            File.WriteAllText(Path.Combine(pending, "native.log"), nativeLog, System.Text.Encoding.UTF8);
            if (process.ExitCode != 0) throw new InvalidDataException("Native Snake Way BSP probe failed: " + nativeLog);
            using var probeDoc = JsonDocument.Parse(nativeJson);
            TerrainGeometryService.NoDuplicateKeys(probeDoc.RootElement);
            NativeProbe probe = JsonSerializer.Deserialize<NativeProbe>(nativeJson, Strict) ?? throw new InvalidDataException("Native probe returned no result.");
            ValidateProbe(probe, plan);

            string packFile = ResolvePackedFile(plan.ClientRoot, probe.PackFile);
            ContentSafety.NoLinks(packFile);
            using var packLock = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
            if ((ulong)packLock.Length < (ulong)probe.PayloadOffset + probe.PayloadBytes || probe.PayloadBytes is < 1 or > 4 * 1024 * 1024)
                throw new InvalidDataException("Native BSP payload range exceeds the resolved pack file.");
            string payloadHash = HashRange(packLock, probe.PayloadOffset, probe.PayloadBytes);
            if (!payloadHash.Equals(plan.ResourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded PAK BSP payload does not match the reviewed v4 SHA-256.");

            foreach (var pin in pins) if (PinFile(pin.Path) != pin) throw new IOException("Native BSP source changed during read.");
            if (ContentSafety.Hash(Helper) != plan.HelperSha256) throw new IOException("Native BSP helper changed during read.");
            foreach (var file in SourceFiles)
            {
                string path = SourcePath(plan.SourceRoot, file);
                VerifySourceFix(path, file);
                if (PinFile(path) != plan.SourceFiles.Single(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("PVS source fix changed during read.");
            }

            File.WriteAllText(Path.Combine(pending, "native.json"), nativeJson, System.Text.Encoding.UTF8);
            var audit = new
            {
                profile = Profile,
                at = DateTimeOffset.UtcNow,
                planSha256 = ContentSafety.Hash(planPath),
                helperSha256 = plan.HelperSha256,
                sourceFixPins = plan.SourceFiles,
                packHeaderPins = plan.PackHeaders,
                resourcePath = plan.ResourcePath,
                packedResource = new { path = Path.GetRelativePath(plan.ClientRoot, packFile).Replace('\\', '/'), offset = probe.PayloadOffset,
                    bytes = probe.PayloadBytes, sha256 = payloadHash },
                pvsPluginPresent = probe.HasPvs,
                bbox = probe.Bbox,
                sectors = probe.Sectors,
                vertices = probe.Vertices,
                triangles = probe.Triangles,
                materials = probe.Materials,
                textures = probe.Textures,
                collision = probe.Collision,
                nativeClientPackManager = true,
                realPackedTextureLoads = true,
                runtimeModified = false,
                renderingTested = false,
                fullClientWorldTested = false,
                installed = false,
                published = false
            };
            File.Copy(planPath, Path.Combine(pending, "plan.json"));
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), audit);
            Directory.Move(pending, output);
            pending = null;
            return audit;
        }
        finally
        {
            foreach (var stream in locks) stream.Dispose();
            if (pending != null) Directory.Delete(pending, true);
        }
    }

    private static void ValidatePlan(Plan p)
    {
        if (p.Profile != Profile || p.ResourcePath != ResourcePath || p.ResourceSha256 == null || !p.ResourceSha256.Equals(ResourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(p.ClientRoot) || !Path.IsPathFullyQualified(p.SourceRoot) ||
            p.PackHeaders == null || p.PackHeaders.Length != PackHeaders.Length || p.SourceFiles == null || p.SourceFiles.Length != SourceFiles.Length ||
            p.HelperSha256 == null || p.HelperSha256.Length != 64 || !p.HelperSha256.All(Uri.IsHexDigit) ||
            FullAsciiPath(p.ClientRoot) != p.ClientRoot || FullAsciiPath(p.SourceRoot) != p.SourceRoot)
            throw new InvalidDataException("Exact sealed Snake Way v4 BSP read plan required.");
        if (p.PackHeaders.Any(x => x == null) || p.SourceFiles.Any(x => x == null)) throw new InvalidDataException("Missing native BSP input pin.");
        if (!p.PackHeaders.Select(x => Path.GetFileName(x.Path)).SequenceEqual(PackHeaders, StringComparer.Ordinal) ||
            !p.SourceFiles.Select(x => Path.GetFileName(x.Path)).SequenceEqual(SourceFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Native BSP plan pack/source file list differs.");
        for (int i = 0; i < PackHeaders.Length; ++i)
            if (!Path.GetFullPath(p.PackHeaders[i].Path).Equals(Path.GetFullPath(Path.Combine(p.ClientRoot, "pack", PackHeaders[i])), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native BSP plan pack header escaped the selected client root.");
        for (int i = 0; i < SourceFiles.Length; ++i)
            if (!Path.GetFullPath(p.SourceFiles[i].Path).Equals(Path.GetFullPath(SourcePath(p.SourceRoot, SourceFiles[i])), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native BSP plan PVS source escaped the selected core root.");
        if (p.PackHeaders.Concat(p.SourceFiles).Any(x => x == null || x.Sha256 == null || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit) || x.Bytes < 1))
            throw new InvalidDataException("Invalid native BSP input pin.");
    }

    private static void ValidateProbe(NativeProbe p, Plan plan)
    {
        if (p.ResourcePath != plan.ResourcePath || p.PayloadBytes != 345274 || p.HasPvs ||
            p.Sectors == 0 || p.Vertices != 6138 || p.Triangles != 5824 || p.Materials != 10 || p.TextureCount != 7 ||
            p.Textures == null || p.Textures.Length != 7 || p.Collision == null || p.Collision.Length != 5 ||
            p.Bbox?.Inf == null || p.Bbox.Sup == null || p.Bbox.Inf.Length != 3 || p.Bbox.Sup.Length != 3 ||
            p.Bbox.Inf.Concat(p.Bbox.Sup).Any(x => !float.IsFinite(x)) ||
            Enumerable.Range(0, 3).Any(i => p.Bbox.Inf[i] > p.Bbox.Sup[i]))
            throw new InvalidDataException("Native result differs from reviewed Snake Way v4 BSP/PVS expectations.");
        Collision bboxSpawn = p.Collision[0], bboxReview = p.Collision[1];
        Collision spawn = p.Collision[2], review = p.Collision[3], fallback = p.Collision[4];
        if (bboxSpawn.Probe != "bbox-all-geometry" || bboxReview.Probe != "bbox-all-geometry" ||
            !bboxSpawn.Attempted || !bboxReview.Attempted || bboxSpawn.X != 20.96935f || bboxSpawn.Z != -83.27369f ||
            bboxReview.X != 21.8f || bboxReview.Z != -82.7f ||
            bboxSpawn.RayStartY != p.Bbox.Sup[1] || bboxSpawn.RayEndY != p.Bbox.Inf[1] ||
            bboxReview.RayStartY != p.Bbox.Sup[1] || bboxReview.RayEndY != p.Bbox.Inf[1] ||
            spawn.Probe != "production-spawn" || !spawn.Attempted || spawn.X != 20.96935f || spawn.Z != -83.27369f ||
            spawn.RayStartY != 49.739807f + 2.0f || spawn.RayEndY != 49.739807f - 200.0f ||
            review.Probe != "production-review" || !review.Attempted || review.X != 21.8f || review.Z != -82.7f ||
            review.RayStartY != 40.8f + 2.0f || review.RayEndY != 40.8f - 200.0f ||
            fallback.Probe != "production-review-expected-height-fallback" || fallback.X != 21.8f || fallback.Z != -82.7f ||
            fallback.RayStartY != 49.74f + 2.0f || fallback.RayEndY != 49.74f - 200.0f ||
            fallback.Attempted != !review.Hit ||
            (!fallback.Attempted && (fallback.Hit || fallback.Y.HasValue || fallback.MaterialIndex.HasValue)) ||
            p.Collision.Any(x => x.Hit && (!x.Y.HasValue || !float.IsFinite(x.Y.Value) ||
                !x.MaterialIndex.HasValue || x.MaterialIndex.Value >= (uint)p.Materials)) ||
            p.Collision.Any(x => !x.Hit && (x.Y.HasValue || x.MaterialIndex.HasValue)))
            throw new InvalidDataException("Native collision probe changed or returned an invalid hit.");
    }

    private static string ResolvePackedFile(string root, string packFile)
    {
        if (string.IsNullOrWhiteSpace(packFile) || packFile.Any(c => c > 127 || c < 32)) throw new InvalidDataException("Invalid native resolved pack path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.IsPathFullyQualified(packFile) ? packFile : Path.Combine(root, packFile.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Resolved BSP pack file escaped client root.");
        return full;
    }

    private static string HashRange(FileStream stream, uint offset, uint bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        stream.Position = offset;
        byte[] buffer = new byte[64 * 1024];
        uint remaining = bytes;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min((uint)buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("Truncated resolved BSP payload.");
            hash.AppendData(buffer, 0, read);
            remaining -= (uint)read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
