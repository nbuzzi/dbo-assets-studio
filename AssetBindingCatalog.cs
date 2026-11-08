using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal static class AssetBindingCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Binding(string NameHex, string MaskHex, string Path, string Sha256);
    private sealed record Plan(int Version, Source Model, List<Binding> Textures, Source? Animation = null, Source? UvAnimation = null, string? UvProfile = null, Source? ModelAliasPlan = null, Source? AdditionalMesh = null,
        string? ConsumerProfile = null, List<Source>? ConsumerSources = null,
        Source? SourceTextureCatalog = null, Source? SourceTextureIndex = null);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<object> InspectAsync(string path, string? bspOutput = null)
    {
        string planPath = System.IO.Path.GetFullPath(path); ContentSafety.NoLinks(planPath);
        if (new FileInfo(planPath).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Plan de bindings máximo1MiB.");
        string planHash = ContentSafety.Hash(planPath);
        Plan plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllBytesAsync(planPath), Options) ?? throw new InvalidDataException("Plan requerido.");
        bool twoEntries = plan.Version == 3 && plan.UvProfile == "two-entry-two-frame-v1";
        bool twoUnused = plan.Version == 5 && plan.UvProfile == "two-entry-two-frame-linear-unused-map-v1";
        bool unusedMap = plan.Version == 4 && plan.UvProfile == "single-two-frame-unused-map-v1";
        bool windAux = plan.Version == 6 && plan.ConsumerProfile == "ext-wind1-inactive-auxiliary-v1";
        bool threeFrames = plan.Version == 7 && plan.UvProfile == "ext-single-param-three-frame-v1";
        bool sevenFrames = plan.Version == 7 && plan.UvProfile == "ext-single-param-seven-frame-v1";
        bool sourceMulti = threeFrames || sevenFrames;
        bool identicalNamed = plan.Version == 8 && plan.UvProfile == "ext-identical-named-param-pair-v1";
        bool popoAbsent = plan.Version == 9 && plan.ConsumerProfile == "ext-popo-source-absent-primary-v1";
        if (plan.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9) || (plan.Version == 1 && plan.UvAnimation is not null) ||
            (plan.Version == 9 && !popoAbsent) ||
            (plan.Version == 8 && (!identicalNamed || plan.UvAnimation is null)) ||
            (plan.Version == 7 && (!sourceMulti || plan.UvAnimation is null)) ||
            (plan.Version is 2 or 3 or 4 or 5 && plan.UvAnimation is null) || (plan.Version == 3 && !twoEntries) ||
            (plan.Version == 4 && !unusedMap) || (plan.Version == 5 && !twoUnused) || (plan.Version is 1 or 2 && plan.UvProfile is not null) || plan.Model is null || plan.Textures is null || plan.Textures.Count > 4096)
            throw new InvalidDataException("Plan v1 sin UV o v2 con UvAnimation; máximo4096 texturas.");
        if (popoAbsent ? plan.UvAnimation is not null || plan.UvProfile is not null || plan.ModelAliasPlan is not null ||
            plan.AdditionalMesh is not null || plan.Textures.Count != 4 || plan.ConsumerSources?.Count != 2 ||
            plan.SourceTextureCatalog is null || plan.SourceTextureIndex is null :
            plan.Version == 6 ? !windAux || plan.Animation is not null || plan.UvAnimation is not null || plan.UvProfile is not null ||
            plan.ModelAliasPlan is not null || plan.AdditionalMesh is not null || plan.Textures.Count != 1 || plan.ConsumerSources?.Count != 4 :
            plan.ConsumerProfile is not null || plan.ConsumerSources is not null)
            throw new InvalidDataException("Wind auxiliary v6 requires its exact static consumer profile and four source seals; no generic missing textures.");
        if (!popoAbsent && (plan.SourceTextureCatalog is not null || plan.SourceTextureIndex is not null))
            throw new InvalidDataException("Source absence evidence is exclusive to the exact Popo v9 diagnostic profile.");
        if (bspOutput != null && (plan.Version != 1 || plan.Animation != null || plan.UvAnimation != null))
            throw new InvalidDataException("Static BSP requires v1 bindings without animation or UV playback.");
        if(plan.AdditionalMesh!=null&&bspOutput==null)throw new InvalidDataException("Additive static mesh requires a private BSP candidate.");
        string helper = System.IO.Path.Combine(AppContext.BaseDirectory, "media-helper.exe"); ContentSafety.NoLinks(helper);
        string helperHash = ContentSafety.Hash(helper);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<byte[]> Snapshot(string sourcePath, string pin, string[] allowed)
        {
            sourcePath = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(sourcePath) ? sourcePath : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(planPath)!, sourcePath));
            ContentSafety.NoLinks(sourcePath);
            if (!allowed.Contains(System.IO.Path.GetExtension(sourcePath).ToLowerInvariant()) || new FileInfo(sourcePath).Length is < 1 or > 128L * 1024 * 1024 || pin is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new InvalidDataException("Fuente/pin/extensión fuera de contrato.");
            string hash = ContentSafety.Hash(sourcePath);
            if (!hash.Equals(pin, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 de fuente no coincide con plan.");
            byte[] bytes = await File.ReadAllBytesAsync(sourcePath);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(pin, StringComparison.OrdinalIgnoreCase) || ContentSafety.Hash(sourcePath) != hash) throw new IOException("Fuente cambió durante snapshot.");
            sources[sourcePath] = hash; return bytes;
        }
        byte[] model = await Snapshot(plan.Model.Path, plan.Model.Sha256, [".dff"]);
        if (popoAbsent)
        {
            const string sourceTex = "6C269F03451B839E16B78841A7834DE264A41EA6EBB35B70AAEB43EA4C051DB0";
            var sourceRasters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
                ["4e50435f506f706f5f3031"]="D275BFB65A29755D05AB84011E858A6018B125ED620B596B0D4020507571D5EF",
                ["4e50435f506f706f5f3032"]="F9085A8034027A45F9035EDF102424C662F8F9ED8EF5A5B1236A8043D0C70E0F",
                ["4e50435f506f706f5f3033"]="1FE8904936BAA6C4B03D9E11EEBF66CD222C0B21A1E9372BDE0B8B1C50585AAD",
                ["4e50435f506f706f5f3034"]="B42738B9B420E8F5296F2DF4BDDB05B4ABA5BAA3152A75CF642365B70477E83D" };
            if (!plan.Model.Sha256.Equals("D6D09E917AE7D6C43733D8EF633CC0091B0BF053ECD2976756DDC281349D6489", StringComparison.OrdinalIgnoreCase) ||
                !plan.SourceTextureIndex!.Sha256.Equals(sourceTex, StringComparison.OrdinalIgnoreCase) ||
                plan.Textures.Any(x=>!sourceRasters.Remove(x.NameHex,out string? hash)||!x.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase)) || sourceRasters.Count!=0)
                throw new InvalidDataException("Exact adapted Popo and its four original available raster names required.");
            await Snapshot(plan.SourceTextureIndex.Path, plan.SourceTextureIndex.Sha256, [".pak"]);
            byte[] catalog = await Snapshot(plan.SourceTextureCatalog!.Path, plan.SourceTextureCatalog.Sha256, [".json"]);
            using var doc = JsonDocument.Parse(catalog); TerrainGeometryService.NoDuplicateKeys(doc.RootElement);
            var cat = doc.RootElement.GetProperty("result");
            if (!doc.RootElement.GetProperty("ok").GetBoolean() || cat.GetProperty("format").GetString() != "ext-des" ||
                cat.GetProperty("sha256").GetString()?.ToUpperInvariant() != sourceTex || cat.GetProperty("prefix").GetString() != "tex" ||
                cat.GetProperty("entries").GetArrayLength() != cat.GetProperty("count").GetInt32() ||
                cat.GetProperty("entries").EnumerateArray().Any(x=>System.IO.Path.GetFileNameWithoutExtension(x.GetProperty("name").GetString()!.Replace('\\','/')).Equals("popo",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pinned complete Source TEX catalog must demonstrate absence of literal popo; no replacement raster.");
            var expected = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
                ["bamateri.c"]="1F0EE051D201B7BFEA417F345EF36E92F05534495F56ACE4C5EC1E3446A2FE1E",
                ["babintex.c"]="72B34AC6B96CD6F156CD62640CFAEAC48110C7267A3861CE930B3E2A2D7AF8EF" };
            foreach (var source in plan.ConsumerSources!)
            {
                if (!expected.Remove(System.IO.Path.GetFileName(source.Path),out string? hash) || !source.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Reviewed native NULL-texture stream contract differs.");
                await Snapshot(source.Path,source.Sha256,[".c"]);
            }
            if (expected.Count!=0) throw new InvalidDataException("Both native stream source seals required.");
        }
        if (windAux)
        {
            if (!plan.Model.Sha256.Equals("9C84C8A76D0A1676E79E930F2F3BF77B06B5BD6C23AF7A01393AD3BE4E607EA5", StringComparison.OrdinalIgnoreCase) || model.Length != 3632 ||
                !plan.Textures[0].NameHex.Equals("7531376D5F63636266333139656435386363326536", StringComparison.OrdinalIgnoreCase) || plan.Textures[0].MaskHex != "" ||
                !plan.Textures[0].Sha256.Equals("22A1D50FC328A3ABC3A55047B59EA544785BD172D121BE7A99E6B69EA3A15E77", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Exact owned wind1 model and actual primary raster required.");
            var expected = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
                ["NtlInstanceMeshSystem.cpp"]="CA7300792A78A89E1BFEC7B4AA933EA2E517109D26DE0F0C186000985B72F9AD",
                ["NtlMaterialExt.c"]="AE26809442CD2A6EF49DB76CB273817E35C21EF51635D449B7C234568DC6B023",
                ["NtlAtomic.cpp"]="0735BEB382E1A44FCBB51F44CD871AA9EAA21A0B23F7F3AF9EC57F93493D0880",
                ["NtlPLResourceManager.cpp"]="FB81560C91EBA7C3500F37001ACD71DFAE76A544BE998DDD7FE5A71FB99BD79D" };
            foreach (var source in plan.ConsumerSources!)
            {
                if (!expected.Remove(System.IO.Path.GetFileName(source.Path),out string? expectedHash) || !source.Sha256.Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Reviewed effect mesh consumer source differs.");
                await Snapshot(source.Path,source.Sha256,[".cpp",".c"]);
            }
            if(expected.Count!=0)throw new InvalidDataException("Complete consumer source seals required.");
        }
        object? aliasEvidence = null;
        if (plan.ModelAliasPlan != null)
        {
            if (bspOutput == null || plan.Version != 1) throw new InvalidDataException("Audited model aliases are restricted to static BSP candidates.");
            await Snapshot(plan.ModelAliasPlan.Path, plan.ModelAliasPlan.Sha256, [".json"]);
            string aliasPlanPath = System.IO.Path.GetFullPath(plan.ModelAliasPlan.Path, System.IO.Path.GetDirectoryName(planPath)!);
            aliasEvidence = DffTextureAliasService.Preview(aliasPlanPath);
            using var alias = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(aliasEvidence, ContentSafety.Json));
            var ap = alias.RootElement.GetProperty("plan");
            if (ap.GetProperty("targetSha256").GetString() != plan.Model.Sha256 || !alias.RootElement.GetProperty("evidence").GetProperty("nonNamePayloadsPreserved").GetBoolean())
                throw new InvalidDataException("Canonical alias target/preservation proof differs from the BSP model.");
            await Snapshot(ap.GetProperty("source").GetString()!, ap.GetProperty("sourceSha256").GetString()!, [".dff"]);
        }
        using var bundle = new MemoryStream();
        using var writer = new BinaryWriter(bundle, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes(popoAbsent ? "OMB9" : identicalNamed ? "OMB8" : sourceMulti ? "OMB7" : windAux ? "OMB6" : plan.AdditionalMesh!=null ? "OMB4" : twoUnused ? "OMB5" : unusedMap ? "OMB4" : plan.UvAnimation is not null ? "OMB3" : plan.Animation is null ? "OMB1" : "OMB2"));
        if (popoAbsent) writer.Write(plan.Animation is null ? 0u : 1u);
        if (plan.UvAnimation is not null) writer.Write(plan.Animation is null ? 2u : 3u);
        writer.Write((uint)model.Length); writer.Write(model); writer.Write((uint)plan.Textures.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new List<object>();
        foreach (Binding binding in plan.Textures)
        {
            byte[] name = Convert.FromHexString(binding.NameHex), mask = Convert.FromHexString(binding.MaskHex);
            if (name.Length is < 1 or > 31 || mask.Length != 0 || name.Any(x => x < 32 || x == 127)) throw new InvalidDataException("Nombre RenderWare máximo31bytes; máscara separada aún no soportada.");
            string resource = Encoding.Latin1.GetString(name); ContentSafety.ResourcePath(resource);
            if (!unique.Add(Convert.ToHexString(name))) throw new InvalidDataException("Binding de nombre duplicado.");
            byte[] texture = await Snapshot(binding.Path, binding.Sha256, [".dds", ".png", ".bmp"]);
            string ext = System.IO.Path.GetExtension(binding.Path).ToLowerInvariant();
            if (bundle.Length + texture.Length + name.Length + 16 > 128L * 1024 * 1024) throw new InvalidDataException("Bundle completo máximo128MiB.");
            writer.Write((uint)name.Length); writer.Write(name); writer.Write((uint)mask.Length); writer.Write(mask);
            writer.Write(ext == ".dds" ? 1u : ext == ".png" ? 2u : 3u); writer.Write((uint)texture.Length); writer.Write(texture);
            bindings.Add(new { nameHex = Convert.ToHexString(name), name = resource, maskHex = "", binding.Sha256, binding.Path });
        }
        if (plan.Animation is not null)
        {
            byte[] animation = await Snapshot(plan.Animation.Path, plan.Animation.Sha256, [".anm"]);
            if (bundle.Length + animation.Length + 4 > 128L * 1024 * 1024) throw new InvalidDataException("Bundle completo máximo128MiB.");
            writer.Write((uint)animation.Length); writer.Write(animation);
        }
        if (plan.UvAnimation is not null)
        {
            byte[] uv = await Snapshot(plan.UvAnimation.Path, plan.UvAnimation.Sha256, [".uva"]);
            int expectedUvBytes = threeFrames ? 224 : sevenFrames ? 352 : twoEntries || twoUnused || identicalNamed ? 356 : 192;
            string? expectedSourceUv = threeFrames ? "F731E1AC78C292D7C33753E0B090AF8C45D228895D0703252D587322BE7D22EA" :
                sevenFrames ? "054310199DA552A8E34D44C18D80DEA7F00D7426C69AAA6E47704DFECDED8A5C" :
                identicalNamed ? "93F32106456FEB197A069A25350D101F4C6FE1CF8A880F1E9F73D81EE5B3E5AD" : null;
            if (uv.Length != expectedUvBytes || bundle.Length + uv.Length + 4 > 128L * 1024 * 1024 ||
                (expectedSourceUv is not null && !plan.UvAnimation.Sha256.Equals(expectedSourceUv, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("UV bytes/hash differ from the explicit192/356-byte legacy profile or pinned224/352-byte Source profile.");
            writer.Write((uint)uv.Length); writer.Write(uv);
        }
        long additionalTriangles=0;
        if(plan.AdditionalMesh!=null){byte[] addition=await Snapshot(plan.AdditionalMesh.Path,plan.AdditionalMesh.Sha256,[".mesh"]);var stats=SceneGeometryService.BakeMesh(System.IO.Path.GetFullPath(plan.AdditionalMesh.Path,System.IO.Path.GetDirectoryName(planPath)!),null,null);if(stats.Meshes!=1||stats.Triangles>200||stats.Vertices>600)throw new InvalidDataException("Bounded single static addition required.");additionalTriangles=stats.Triangles;writer.Write((uint)addition.Length);writer.Write(addition);}
        if (bspOutput != null)
        {
            bspOutput = ContentSafety.SafeOutput(bspOutput);
            if (sources.Keys.Append(planPath).Append(helper).Any(x => x.Equals(bspOutput, StringComparison.OrdinalIgnoreCase) ||
                x.StartsWith(bspOutput.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("BSP output overlaps an input.");
            string pending = ContentSafety.FreshDirectory(bspOutput);
            try
            {
                byte[] native = await NativeMedia.ConvertAsync("model-bsp-bindings", bundle.ToArray(), ".omb");
                if (native.Length < 44 || !native.AsSpan(0, 4).SequenceEqual("SBP1"u8)) throw new InvalidDataException("Native static BSP proof header invalid.");
                uint Word(int n) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(4 + n * 4, 4));
                if (Word(0) is < 1 or > 128 || Word(1) is < 1 or > 60000 || Word(2) is < 1 or > 100000 || Word(2) != Word(4) ||
                    Word(5) is < 1 or > 4096 || (ulong)Word(6) + 32 != (ulong)native.Length ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(32, 4)) != 11 ||
                    (ulong)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(36, 4)) + 12 != Word(6))
                    throw new InvalidDataException("Native static BSP counts/root/EOF invalid.");
                if (ContentSafety.Hash(planPath) != planHash || ContentSafety.Hash(helper) != helperHash || sources.Any(x => ContentSafety.Hash(x.Key) != x.Value))
                    throw new IOException("Plan/helper/source changed during BSP conversion.");
                string file = System.IO.Path.Combine(pending, "scene.bsp"); await File.WriteAllBytesAsync(file, native.AsMemory(32).ToArray());
                string backup = System.IO.Path.Combine(pending, "snapshots"); Directory.CreateDirectory(backup);
                var snapshots = new List<object>(); int ordinal = 0;
                foreach (var source in sources)
                {
                    string dest = System.IO.Path.Combine(backup, ordinal++ + System.IO.Path.GetExtension(source.Key)); File.Copy(source.Key, dest);
                    if (ContentSafety.Hash(dest) != source.Value) throw new IOException("Source changed during BSP snapshot.");
                    snapshots.Add(new { source = source.Key, sha256 = source.Value, snapshot = System.IO.Path.GetRelativePath(pending, dest) });
                }
                File.Copy(planPath, System.IO.Path.Combine(pending, "bindings.json"));
                if (ContentSafety.Hash(System.IO.Path.Combine(pending, "bindings.json")) != planHash) throw new IOException("Bindings changed during copy.");
                var audit = new { profile = plan.AdditionalMesh==null?"taiwan-native-static-clump-bsp-v1":"taiwan-native-static-clump-additive-bsp-v2", status = "candidate_not_installed", planSha256 = planHash,
                    helperSha256 = helperHash, sourceAtomics = Word(0), sourceVertices = Word(1), sourceTriangles = Word(2),
                    originalSourceTriangles=Word(2)-additionalTriangles,additionalTriangles,
                    bspVertices = Word(3), bspTriangles = Word(4), materials = Word(5), bspSha256 = ContentSafety.Hash(file),
                    allOrientedTrianglesPositionsUvPrelightsMaterialsPreserved = true, normalTolerance = 0.02,
                    nativeBspStreamReadback = true, realTextureBindings = true, texturesSubstituted = false, snapshots, aliasEvidence,
                    navigationGenerated = false, renderingTested = false, worldTested = false, readyToInstall = false };
                ContentSafety.WriteJson(System.IO.Path.Combine(pending, "audit.json"), audit);
                Directory.Move(pending, bspOutput); return new { output = bspOutput, bsp = System.IO.Path.Combine(bspOutput, "scene.bsp"), audit };
            }
            catch { Directory.Delete(pending, true); throw; }
        }
        using var result = JsonDocument.Parse(await NativeMedia.ConvertAsync("model-bindings", bundle.ToArray(), ".omb"));
        if (popoAbsent && (!result.RootElement.TryGetProperty("sourceAbsentPrimary",out var missing) ||
            missing.GetProperty("profile").GetString()!=plan.ConsumerProfile || missing.GetProperty("missingRequests").GetInt32()!=1 ||
            !missing.GetProperty("primaryTextureNull").GetBoolean() || !missing.GetProperty("otherFourRastersBound").GetBoolean() ||
            missing.GetProperty("materialTriangles").GetInt32()!=2733 || !missing.GetProperty("nativeCloneValidated").GetBoolean()))
            throw new InvalidDataException("Exact Popo NULL primary material/readback differs.");
        if (windAux && (!result.RootElement.TryGetProperty("inactiveAuxiliary",out var aux) || aux.GetProperty("profile").GetString()!=plan.ConsumerProfile ||
            aux.GetProperty("missingRequests").GetInt32()!=1 || !aux.GetProperty("primaryRasterBound").GetBoolean() || !aux.GetProperty("auxiliaryTextureNull").GetBoolean() ||
            !aux.GetProperty("defaultRenderCallback").GetBoolean() || aux.GetProperty("materialFlags").GetUInt32()!=0 || aux.GetProperty("skinAtomics").GetInt32()!=0 ||
            aux.GetProperty("toonAtomics").GetInt32()!=0 || aux.GetProperty("customPipelines").GetInt32()!=0 || aux.GetProperty("matfxMode").GetInt32()!=5 ||
            !aux.GetProperty("uvMatricesNull").GetBoolean() || !aux.GetProperty("nativeCloneValidated").GetBoolean()))
            throw new InvalidDataException("Native wind inactive auxiliary material/renderer proof differs.");
        if (ContentSafety.Hash(planPath) != planHash || ContentSafety.Hash(helper) != helperHash || sources.Any(x => ContentSafety.Hash(x.Key) != x.Value)) throw new IOException("Plan/helper/fuente modificados durante inspección.");
        if (result.RootElement.GetProperty("texturesSubstituted").GetBoolean() || result.RootElement.GetProperty("renderingTested").GetBoolean()) throw new InvalidDataException("Metadata de bindings inconsistente.");
        if (plan.Animation is not null && (!result.RootElement.TryGetProperty("animationBinding", out var playback) ||
            !playback.GetProperty("nativeHierarchyPlayback").GetBoolean() || playback.GetProperty("steps").GetInt32() != 40 ||
            playback.GetProperty("nodes").GetInt32() < 1 || playback.GetProperty("eventsTested").GetBoolean() || playback.GetProperty("renderingTested").GetBoolean()))
            throw new InvalidDataException("Metadata de playback nativo inconsistente.");
        if (plan.UvAnimation is not null && (!result.RootElement.TryGetProperty("uvBinding", out var uvPlayback) ||
            !uvPlayback.GetProperty("nativeMaterialPlayback").GetBoolean() || uvPlayback.GetProperty("steps").GetInt32() != 40 ||
            uvPlayback.GetProperty("entries").GetInt32() != (twoEntries || twoUnused || identicalNamed ? 2 : 1) || uvPlayback.GetProperty("frames").GetInt32() != (threeFrames ? 3 : sevenFrames ? 7 : 2) ||
            uvPlayback.GetProperty("profile").GetString() != (sourceMulti || identicalNamed ? plan.UvProfile : twoUnused ? "two-entry-two-frame-linear-unused-map-v1" : unusedMap ? "single-two-frame-unused-map-v1" : twoEntries ? "two-entry-two-frame-v1" : "single-two-frame-v1") ||
            uvPlayback.GetProperty("materials").GetInt32() < 1 || uvPlayback.GetProperty("renderingTested").GetBoolean()))
            throw new InvalidDataException("Metadata de playback UV nativo inconsistente.");
        return new { planPath, planSha256 = planHash, modelSha256 = plan.Model.Sha256, helperSha256 = helperHash,
            nativeReaderPass = true, realTextureBindings = !popoAbsent, allAvailableSourceRastersBound = true,
            sourceAbsentPrimaryPreserved = popoAbsent, texturesSubstituted = false, sourcePreserved = true,
            bindings, consumerProfile = plan.ConsumerProfile, consumerSources = plan.ConsumerSources, animationSha256 = plan.Animation?.Sha256, uvAnimationSha256 = plan.UvAnimation?.Sha256,
            native = result.RootElement.Clone(), renderingTested = false, nativeMaterialPlayback = plan.UvAnimation is not null,
            nativeHierarchyPlayback = plan.Animation is not null, animationTested = false, eventsTested = false, worldTested = false,
            scope = popoAbsent ? "Exact Popo clump, four original available rasters and source-absent primary left NULL; optional native ANM playback. Source visual equivalence, draw, events, PAK lookup and gameplay remain unproven." :
                "Native clump with exact textures and optional ANM/UV matrix playback. No draw, Toon/effects, events, PAK lookup or gameplay certification." };
    }
}
