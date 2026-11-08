using System.Text.Json;

namespace DboAssetsStudio;

internal static class ContentCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine(Help); return 0;
            }
            object result;
            if (args[0] == "workspace") result = await WorkspaceCli.RunAsync(args[1..]);
            else if (args[0] == "wps") result = WpsCli.Run(args[1..]);
            else if (args[0] == "tqs") result = TqsCli.Run(args[1..]);
            else if (args[0] == "quest") result = QuestCli.Run(args[1..]);
            else if (args[0] == "rdf") result = RdfCli.Run(args[1..]);
            else if (args[0] == "pak") result = await PakAsync(args[1..]);
            else if (args[0] == "asset") result = await AssetAsync(args[1..]);
            else if (args[0] == "map") result = args.Length switch
            {
                6 when args[1] == "plan" => MapWorkshopService.CreatePlan(args[2], args[3], args[4], args[5]),
                3 when args[1] == "preview" => await MapWorkshopService.RunAsync(args[2], null),
                4 when args[1] == "apply" => await MapWorkshopService.RunAsync(args[2], args[3]),
                7 when args[1] == "navi-plan" => MapNavigationService.CreatePlan(args[2], args[3], args[4], args[5], args[6]),
                8 when args[1] == "navi-plan" => MapNavigationService.CreatePlan(args[2], args[3], args[4], args[5], args[6], args[7]),
                3 when args[1] == "navi-preview" => await MapNavigationService.RunAsync(args[2], null),
                4 when args[1] == "navi-apply" => await MapNavigationService.RunAsync(args[2], args[3]),
                7 when args[1] == "world-plan" => StandaloneBspWorldService.CreatePowerPlan(args[2],args[3],args[4],args[5],args[6]),
                8 when args[1] == "world-plan" => StandaloneBspWorldService.CreatePowerPlan(args[2],args[3],args[4],args[5],args[6],args[7]),
                3 when args[1] == "world-preview" => await StandaloneBspWorldService.RunAsync(args[2],null),
                4 when args[1] == "world-apply" => await StandaloneBspWorldService.RunAsync(args[2],args[3]),
                8 when args[1] == "tables-plan" => NewMapTableService.CreatePlan(args[2],args[3],args[4],args[5],args[6],args[7]),
                3 when args[1] == "tables-preview" => NewMapTableService.Run(args[2],null),
                4 when args[1] == "tables-apply" => NewMapTableService.Run(args[2],args[3]),
                _ => throw new ArgumentException("map plan <recipe.json> <preview-template.html> <media-helper.exe|none> <new-plan.json> | map preview <plan> | map apply <plan> <fresh-candidate>")
            };
            else if (args[0] == "version") result = new { tool = "dbo-assets", version = "5.6.0", planVersion = 1 };
            else throw new ArgumentException("Comando desconocido. Ejecutá dbo-assets help.");
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result }, ContentSafety.Json));
            return result is RdfComparison comparison && comparison.HasConflicts ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = AppPaths.Redact(ex.Message), type = ex.GetType().Name }, ContentSafety.Json));
            return ex is ArgumentException ? 2 : 1;
        }
    }
    private static async Task<object> PakAsync(string[] args)
    {
        var positional = new List<string>(); var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--")) { string key = args[i]; if (i + 1 >= args.Length || args[i + 1].StartsWith("--") || !options.TryAdd(key, args[++i])) throw new ArgumentException("Opción inválida: " + key); }
            else positional.Add(args[i]);
        string Opt(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;
        if (positional.Count == 0) throw new ArgumentException("Falta comando pak.");
        string command = positional[0];
        string[] allowed = command == "library" ? ["--filter"] : command is "list" or "inspect" or "extract" or "extract-many" ? ["--format", "--family", "--filter"] : command == "fingerprint" ? ["--format", "--family"] : command == "material-references" ? ["--format", "--family"] : command == "plan" ? ["--format", "--family", "--mode", "--prefix", "--unit-mib", "--append-unit"] : command == "repack" ? ["--format", "--family", "--mode", "--prefix", "--unit-mib"] : command == "build" ? ["--unit-mib"] : [];
        foreach (var key in options.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Opción desconocida para " + command + ": " + key);
        void Count(int n) { if (positional.Count != n) throw new ArgumentException("Argumentos inválidos para pak " + command + ". Ver help."); }
        async Task<PakArchive> Open() => await PakArchive.OpenAsync(positional[1], Opt("--format", "taiwan"), options.GetValueOrDefault("--family"));
        switch (command)
        {
            case "compare": Count(4); return await ContentPakService.CompareAsync(positional[1], positional[2], positional[3]);
            case "library":
                Count(2); var library = await PackLibrary.OpenAsync(positional[1]);
                return new { root = library.Root, indices = library.Archives.Select(a => new { a.IndexPath, a.IndexHash, a.Prefix, count = a.Entries.Count }), total = library.Entries.Count, notes = library.Notes,
                    entries = library.Entries.Where(e => e.Name.Contains(Opt("--filter", ""), StringComparison.OrdinalIgnoreCase)).Select(e => new { e.Key, e.Name, archive = e.Archive.Prefix, e.Entry.Size, e.Entry.Status }) };
            case "list": case "inspect":
                Count(2); var archive = await Open();
                return new { source = archive.IndexPath, sha256 = archive.IndexHash, format = archive.Format, prefix = archive.Prefix, sourceBuildSha256 = archive.SourceBuildHash, sourceModuleSha256 = archive.SourceModuleHash, formatNotes = archive.FormatNotes, count = archive.Entries.Count,
                    payloadSemantics = "extracted_payload",
                    entries = archive.Entries.Where(e => e.Name.Contains(Opt("--filter", ""), StringComparison.OrdinalIgnoreCase)).Select(e => new { e.Name, e.Unit, e.Offset, e.Size, e.Status }) };
            case "extract": Count(4); return await ContentPakService.ExtractAsync(await Open(), positional[2], positional[3]);
            case "extract-many": Count(4); return await ContentPakService.ExtractManyAsync(await Open(), positional[2], positional[3]);
            case "fingerprint": Count(3); return await PakFingerprintService.InspectAsync(await Open(), positional[2]);
            case "material-references": Count(4); return await PakMaterialReferenceService.ScanAsync(await Open(), positional[2], positional[3]);
            case "plan":
                Count(4);
                var source = await Open();
                var recipe = ContentPakService.ReadPlan(positional[2]);
                recipe.Source = source.IndexPath; recipe.SourceSha256 = source.IndexHash; recipe.Format = source.Format; recipe.Family = source.Prefix;
                recipe.Prefix = Opt("--prefix", source.Prefix); recipe.Mode = Opt("--mode", recipe.Mode); recipe.UnitMaxMiB = int.Parse(Opt("--unit-mib", recipe.UnitMaxMiB.ToString()));
                recipe.Operations = SealChanges(recipe.Operations);
                if (options.TryGetValue("--append-unit", out var appendNumber))
                {
                    if (recipe.Mode != "append-overlay") throw new ArgumentException("--append-unit exige --mode append-overlay.");
                    recipe.Version = 2; recipe.AppendUnit = ContentPakService.SealAppendUnit(source, int.Parse(appendNumber));
                }
                object preview = await ContentPakService.InspectAsync(recipe);
                ContentSafety.WriteJson(positional[3], recipe); return new { plan = Path.GetFullPath(positional[3]), preview };
            case "preview": Count(2); return await ContentPakService.InspectAsync(ContentPakService.ReadPlan(positional[1]));
            case "apply": Count(3); return await ContentPakService.ApplyAsync(ContentPakService.ReadPlan(positional[1]), positional[2]);
            case "verify": Count(2); return ContentPakService.VerifyCandidate(positional[1]);
            case "repack":
                Count(3); var original = await Open();
                return await ContentPakService.ApplyAsync(new PakPlan { Source = original.IndexPath, SourceSha256 = original.IndexHash, Format = original.Format, Family = original.Prefix,
                    Prefix = Opt("--prefix", original.Prefix), Mode = "repack", UnitMaxMiB = int.Parse(Opt("--unit-mib", "1900")), Reason = "Compactación/reconstrucción completa del contenedor" }, positional[2]);
            case "build":
                Count(4); string root = Path.GetFullPath(positional[2]); ContentSafety.NoLinks(root);
                if (Path.GetFullPath(positional[3]).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Salida fuera de la carpeta de recursos requerida.");
                var files = Walk(root).ToArray();
                var build = new PakPlan { Prefix = positional[1], Mode = "repack", UnitMaxMiB = int.Parse(Opt("--unit-mib", "1900")), Reason = "Build desde carpeta explícita" };
                build.Operations = SealChanges(files.Select(f => new PakChange("add", ContentSafety.ResourcePath(Path.GetRelativePath(root, f)), f)).ToList());
                return await ContentPakService.ApplyAsync(build, positional[3]);
            default: throw new ArgumentException("Comando pak desconocido: " + command);
        }
    }
    private static IEnumerable<string> Walk(string directory)
    {
        ContentSafety.NoLinks(directory);
        foreach (string file in Directory.EnumerateFiles(directory)) { ContentSafety.NoLinks(file); yield return file; }
        foreach (string child in Directory.EnumerateDirectories(directory)) foreach (string file in Walk(child)) yield return file;
    }
    private static async Task<object> AssetAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "character-properties") return CharacterPropertyCatalog.Inspect(args[1]);
        if (args.Length == 4 && args[0] == "character-properties" && args[2] == "--model") return CharacterPropertyCatalog.Inspect(args[1], args[3]);
        if (args.Length == 2 && args[0] == "item-properties") return await ItemPropertyCatalog.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "object-properties") return await ObjectPropertyCatalog.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "uv-structure") return UvStructureCatalog.Inspect(args[1]);
        if (args.Length == 4 && args[0] == "bsp-runtime-read-plan") return SnakewayPvsWorldReadService.CreatePlan(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "bsp-runtime-read") return await SnakewayPvsWorldReadService.RunAsync(args[1], args[2]);
        if (args.Length == 8 && args[0] == "scene-portal-plan") return ScenePortalService.CreatePlan(args[1], args[2], uint.Parse(args[3]), uint.Parse(args[4]), uint.Parse(args[5]), uint.Parse(args[6]), args[7]);
        if (args.Length == 13 && args[0] == "scene-portal-clone-plan") return ScenePortalService.CreateClonePlan(args[1], args[2], uint.Parse(args[3]), uint.Parse(args[4]), uint.Parse(args[5]), uint.Parse(args[6]), uint.Parse(args[7]), uint.Parse(args[8]), float.Parse(args[9], System.Globalization.CultureInfo.InvariantCulture), float.Parse(args[10], System.Globalization.CultureInfo.InvariantCulture), float.Parse(args[11], System.Globalization.CultureInfo.InvariantCulture), args[12]);
        if (args.Length == 2 && args[0] == "scene-portal-preview") return await ScenePortalService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "scene-portal-apply") return await ScenePortalService.ApplyAsync(args[1], args[2]);
        if (args.Length == 8 && args[0] == "placed-floor-plan") return PlacedGroundNavigationService.CreatePlan(args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
        if (args.Length == 2 && args[0] == "placed-floor-preview") return await PlacedGroundNavigationService.RunAsync(args[1], null);
        if (args.Length == 3 && args[0] == "placed-floor-apply") return await PlacedGroundNavigationService.RunAsync(args[1], args[2]);
        if (args.Length == 5 && args[0] == "scene-ground-plan") return SceneNaviPieceService.CreatePlan(args[1], args[2], args[3], args[4]);
        if (args.Length == 2 && args[0] == "scene-ground-preview") return await SceneNaviPieceService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "scene-ground-apply") return await SceneNaviPieceService.ApplyAsync(args[1], args[2]);
        if (args.Length == 4 && args[0] == "scene-placement-plan") return SceneGeometryService.CreateObservationPlan(args[1], args[2], args[3]);
        if (args.Length == 5 && args[0] == "scene-geometry-plan") return SceneGeometryService.CreatePlan(args[1], args[2], args[3], args[4]);
        if (args.Length == 2 && args[0] == "scene-geometry-preview") return await SceneGeometryService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "scene-geometry-apply") return await SceneGeometryService.ApplyAsync(args[1], args[2]);
        if (args.Length == 4 && args[0] == "terrain-geometry-plan") return TerrainGeometryService.CreatePlan(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "terrain-geometry-compare") return TerrainGeometryService.CompareGeometry(args[1], args[2]);
        if (args.Length == 2 && args[0] == "effect-particle-trail-inspect") return await EffectMergeService.InspectParticleTrailsAsync(args[1]);
        if (args.Length == 2 && args[0] == "terrain-geometry-preview") return await TerrainGeometryService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "terrain-geometry-apply") return await TerrainGeometryService.ApplyAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "common-config-inspect") return CommonConfigRdfService.Inspect(args[1]);
        if (args.Length == 5 && args[0] == "common-config-plan") return CommonConfigRdfService.CreatePlan(args[1], args[2], args[3], args[4]);
        if (args.Length == 2 && args[0] == "common-config-preview") return CommonConfigRdfService.Run(args[1], null);
        if (args.Length == 3 && args[0] == "common-config-apply") return CommonConfigRdfService.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "item-dependencies") return await ItemPropertyCatalog.InspectAsync(args[1], true);
        if (args.Length == 3 && args[0] == "model-bsp") return await AssetBindingCatalog.InspectAsync(args[1], args[2]);
        if (args.Length == 3 && args[0] == "model-geometry-compare") return SceneGeometryService.CompareModelGeometry(args[1], args[2]);
        if (args.Length == 3 && args[0] == "snakeway-ramp-plan") return SnakewayRampService.CreatePlan(args[1],args[2]);
        if (args.Length == 3 && args[0] == "snakeway-ramp-apply") return SnakewayRampService.Apply(args[1],args[2]);
        if (args.Length == 6 && args[0] == "static-bsp-world-plan") return StandaloneBspWorldService.CreatePlan(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 2 && args[0] == "static-bsp-world-preview") return await StandaloneBspWorldService.RunAsync(args[1], null);
        if (args.Length == 3 && args[0] == "static-bsp-world-apply") return await StandaloneBspWorldService.RunAsync(args[1], args[2]);
        if (args.Length == 5 && args[0] == "static-bsp-navigation-plan") return StaticBspNavigationService.CreatePlan(args[1], args[2], args[3], args[4]);
        if (args.Length == 6 && args[0] == "static-bsp-navigation-ramp-plan") return StaticBspNavigationService.CreateRampPlan(args[1],args[2],args[3],args[4],args[5]);
        if (args.Length == 5 && args[0] == "static-bsp-navigation-envelope-plan") return StaticBspNavigationService.CreatePlan(args[1], args[2], args[3], args[4],true);
        if (args.Length == 2 && args[0] == "static-bsp-navigation-preview") return await StaticBspNavigationService.RunAsync(args[1], null);
        if (args.Length == 3 && args[0] == "static-bsp-navigation-apply") return await StaticBspNavigationService.RunAsync(args[1], args[2]);
        if (args.Length == 4 && args[0] == "animation-trim-preroll") return AnimationTimelineService.Trim(args[1],args[2],args[3]);
        if (args.Length == 2 && args[0] == "animation-callbacks") return await AnimationCallbackCatalog.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "character-toon") return await CharacterToonCatalog.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "character-rider-preview") return await CharacterAnimationMergeService.PreviewRiderAsync(args[1]);
        if (args.Length == 3 && args[0] == "character-rider-apply") return await CharacterAnimationMergeService.ApplyRiderAsync(args[1],args[2]);
        if (args.Length == 5 && args[0] == "item-alias-plan") return await ItemPropertyAliasService.CreatePlanAsync(args[1], args[2], args[3], args[4]);
        if (args.Length == 2 && args[0] == "item-alias-preview") return await ItemPropertyAliasService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "item-alias-apply") return await ItemPropertyAliasService.ApplyAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "effect-inspect") return await EffectMergeService.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "effect-merge-preview") return await EffectMergeService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-merge-apply") return await EffectMergeService.ApplyAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "model-bindings") return await AssetBindingCatalog.InspectAsync(args[1]);
        if (args.Length == 2 && args[0] == "effect-spline-bindings") return await EffectMergeService.InspectSplinesAsync(args[1]);
        if (args.Length == 2 && args[0] == "effect-spline-observations") return await EffectMergeService.InspectSplinesAsync(args[1], observations: true);
        if (args.Length == 2 && args[0] == "effect-spline-observed-playback") return await EffectMergeService.InspectSplinesAsync(args[1], observedOrder: true);
        if (args.Length == 2 && args[0] == "effect-resource-fields") return await EffectMergeService.InspectResourceFieldsAsync(args[1]);
        if (args.Length == 4 && args[0] == "effect-spline-alias-plan") return await EffectMergeService.CreateSplineAliasPlanAsync(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "effect-spline-alias-preview") return await EffectMergeService.PreviewSplineAliasesAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-spline-alias-apply") return await EffectMergeService.ApplySplineAliasesAsync(args[1], args[2]);
        if (args.Length == 4 && args[0] == "effect-key-alias-plan") return await EffectMergeService.CreateKeyAliasPlanAsync(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "effect-fallback-plan") return await EffectMergeService.CreateFallbackAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "effect-fallback-preview") return await EffectMergeService.PreviewFallbackAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-fallback-apply") return await EffectMergeService.ApplyFallbackAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "effect-key-alias-preview") return await EffectMergeService.PreviewKeyAliasesAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-key-alias-apply") return await EffectMergeService.ApplyKeyAliasesAsync(args[1], args[2]);
        if (args.Length == 4 && args[0] == "effect-mesh-alias-plan") return await EffectMergeService.CreateMeshAliasPlanAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "effect-texture-alias-plan") return await EffectMergeService.CreateTextureAliasPlanAsync(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "effect-texture-alias-preview") return await EffectMergeService.PreviewTextureAliasesAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-texture-alias-apply") return await EffectMergeService.ApplyTextureAliasesAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "effect-mesh-alias-preview") return await EffectMergeService.PreviewMeshAliasesAsync(args[1]);
        if (args.Length == 3 && args[0] == "effect-mesh-alias-apply") return await EffectMergeService.ApplyMeshAliasesAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "animation-poses") return await AnimationPoseCatalog.InspectAsync(args[1]);
        if (args.Length == 4 && args[0] == "texture-slice-plan") return await TextureUnitSliceService.CreatePlanAsync(args[1], long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]);
        if (args.Length == 2 && args[0] == "texture-slice-preview") return await TextureUnitSliceService.PreviewAsync(args[1]);
        if (args.Length == 3 && args[0] == "texture-slice-apply") return await TextureUnitSliceService.ApplyAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] is "model-dependencies" or "texture" or "texture-pixels" or "sound") return await AssetDependencyCatalog.InspectAsync(args[0], args[1]);
        if (args.Length == 3 && args[0] == "model-repair-plan") return DffRepairService.CreatePlan(args[1], args[2]);
        if (args.Length == 2 && args[0] == "model-boundary-observation") return DffRepairService.ObserveBoundaries(args[1]);
        if (args.Length == 2 && args[0] == "model-structure") return DffRepairService.InspectSource(args[1], false);
        if (args.Length == 3 && args[0] == "model-structure" && args[2] == "--allow-omissions") return DffRepairService.InspectSource(args[1], true);
        if (args.Length == 3 && args[0] == "model-uv-layout") return DffUvLayoutService.Export(args[1],args[2]);
        if (args.Length == 4 && args[0] == "model-texture-alias-plan") return DffTextureAliasService.CreatePlan(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "model-ntl-texture-size-plan") return NtlMaterialTextureSizeRepairService.CreatePlan(args[1], args[2]);
        if (args.Length == 2 && args[0] == "model-ntl-texture-size-preview") return NtlMaterialTextureSizeRepairService.Preview(args[1]);
        if (args.Length == 3 && args[0] == "model-ntl-texture-size-apply") return NtlMaterialTextureSizeRepairService.Apply(args[1], args[2]);
        if (args.Length == 2 && args[0] == "model-texture-alias-preview") return DffTextureAliasService.Preview(args[1]);
        if (args.Length == 3 && args[0] == "model-texture-alias-apply") return DffTextureAliasService.Apply(args[1], args[2]);
        if (args.Length == 3 && args[0] == "model-toon-selected-plan") return ToonTopologyService.CreateSelectedPlan(args[1],args[2]);
        if (args.Length == 2 && args[0] == "model-toon-selected-preview") return ToonTopologyService.PreviewSelected(args[1]);
        if (args.Length == 3 && args[0] == "model-toon-selected-apply") return ToonTopologyService.ApplySelected(args[1],args[2]);
        if (args.Length == 3 && args[0] == "model-toon-rebuild-plan") return ToonTopologyService.CreateRebuildPlan(args[1],args[2]);
        if (args.Length == 2 && args[0] == "model-toon-rebuild-preview") return ToonTopologyService.PreviewRebuild(args[1]);
        if (args.Length == 3 && args[0] == "model-toon-rebuild-apply") return ToonTopologyService.ApplyRebuild(args[1],args[2]);
        if (args.Length == 3 && args[0] == "model-toon-plan") return ToonTopologyService.CreatePlan(args[1],args[2]);
        if (args.Length == 2 && args[0] == "model-toon-preview") return ToonTopologyService.Preview(args[1]);
        if (args.Length == 3 && args[0] == "model-toon-apply") return ToonTopologyService.Apply(args[1],args[2]);
        if (args.Length == 2 && args[0] == "model-repair-preview") return DffRepairService.Preview(args[1]);
        if (args.Length == 3 && args[0] == "model-repair-apply") return DffRepairService.Apply(args[1], args[2]);
        if (args.Length != 3 || args[0] is not ("model" or "audio")) throw new ArgumentException("asset model <input.dff> <new.mesh> | asset audio <input.ogg|mp3|wav> <new.wav>");
        string output = ContentSafety.SafeOutput(args[2]);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("El destino ya existe.");
        if (new FileInfo(args[1]).Length > 128L * 1024 * 1024) throw new IOException("Límite multimedia: 128 MiB.");
        string ext = Path.GetExtension(args[1]).ToLowerInvariant();
        if (args[0] == "model" ? ext != ".dff" : ext is not (".wav" or ".ogg" or ".mp3")) throw new ArgumentException("Extensión no compatible.");
        byte[] input = await File.ReadAllBytesAsync(args[1]);
        byte[] result = await NativeMedia.ConvertAsync(args[0], input, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await stream.WriteAsync(result); stream.Flush(true); }
        return new { output, sha256 = ContentSafety.Hash(output), bytes = result.Length, format = args[0] == "model" ? "OSM1 static mesh for preview; not a DFF writer" : "WAV PCM16" };
    }
    public static List<PakChange> SealChanges(IEnumerable<PakChange> changes) => changes.Select(c => c.File == null ? c : c with { File = Path.GetFullPath(c.File), Sha256 = ContentSafety.Hash(c.File) }).ToList();
    private const string Help = """
    DBO Assets CLI 5.4 — Taiwan 2.0
    mob status | list | configure <kit-folder>
    mob import <model.glb> <name> | generate <prompt.txt> <name> --allow-paid
    mob run <job-id> | allow-rig <job-id> | recover <job-id> <phase> <task-id>
    mob key-set <meshy|openai> (stdin) | key-import-meshy <DPAPI-file>
    mob snapshot <pipeline-options> | rebind <pipeline-options>
    Salida JSON en stdout, error JSON en stderr. Exit: 0 éxito, 1 operación, 2 argumentos.
    originals are read-only; all output folders must be NEW. No deployment.

    dbo-assets version
    dbo-assets wps inspect <file.wps>
    dbo-assets encounter help
    dbo-assets tqs help
    dbo-assets quest help
    dbo-assets wps refs <RDF-folder|tbl.pak> <item|mob|group> [--query texto] [--spawns folder] [--context exact-context] [--page 0]
    dbo-assets wps export <file.wps> <new.json>
    dbo-assets wps validate <file.wps>
    dbo-assets wps plan <file.wps> <new-plan.json>
    dbo-assets wps apply <file.wps> <plan.json> <new-candidate-dir>
    WPS: edición estructurada sin ejecutar Lua; ver WPS-EDITOR.md y WPS-GM.md.
    dbo-assets pak library <pack-folder|client-root> [--filter texto]
    dbo-assets pak preview <plan.json>
    dbo-assets pak apply <plan.json> <carpeta-nueva>
    dbo-assets pak verify <carpeta-candidato>
    dbo-assets pak build <prefix> <carpeta-recursos> <carpeta-nueva> [--unit-mib 1900]

    dbo-assets asset item-owned-effect-alias-plan <mapping.json> <new-plan.json>
    dbo-assets asset item-owned-effect-alias-preview <sealed-plan.json>
    dbo-assets asset item-owned-effect-alias-apply <sealed-plan.json> <new-candidate-directory>

    changes.json: {"reason":"Ticket","dependencies":["Table_Item_Data.rdf"],"operations":[
      {"op":"add","path":"item/mesh_costume/new.dff","file":"new.dff"},
      {"op":"replace","path":"texture/item/example.dds","file":"example.dds"},
      {"op":"add","path":"new/example.dds","file":"candidate/example.dds"}]}
    Rutas file relativas al JSON; plan sella hashes y rutas absolutas. preview no escribe.
    Overlay genera índice + unidades nuevas y audit.json con dependencias originales.
    Repack genera todas las unidades de nuevo (más espacio/tiempo). No adapta modelos/tablas.
    Append-unit exige appendUnit/appendUnitBytes/appendUnitSha256 en la receta, sólo altas y sin rollover. Conserva preimagen/prefijo completos.

    dbo-assets pak compare <base/tbl.pak> <ours/tbl.pak> <theirs/tbl.pak>
    dbo-assets rdf schemas
    dbo-assets rdf compare <base.rdf> <ours.rdf> <theirs.rdf>
    dbo-assets rdf reconcile-plan <base.rdf> <ours.rdf> <theirs.rdf> <decisions.json> <new-plan.json>
    dbo-assets rdf reconcile-preview <plan.json>
    dbo-assets rdf reconcile-apply <plan.json> <new-output-dir>
    dbo-assets rdf identities <tabla.rdf>
    dbo-assets rdf inspect <tabla.rdf>
    dbo-assets rdf export <tabla.rdf> <nuevo.json|xml>
    dbo-assets rdf apply <tabla.rdf> <patch.json> <carpeta-nueva>
    dbo-assets rdf map-clone <table_text_all_data.rdf> <patch.json> <carpeta-nueva>
    dbo-assets rdf text-add <table_text_all_data.rdf> <reviewed-plan.json> <new-candidate-folder>
    Text add: sealed target SHA, reason, category item|mob|skill|map|title, records [{id:"123",text:"..."}]. Additive only.
    RDF: update/clone por IDs, sin bajas ni renombres implícitos; codecs Taiwan y backup/auditoría.
    dbo-assets asset model <input.dff> <new.mesh>
    dbo-assets asset gwp-resource-plan <project/#######.gwp> 6001 <new-plan.json>
    dbo-assets asset gwp-inspection-plan <project/#######.gwp> <new-plan.json>
    dbo-assets asset gwp-inspect <sealed-inspection-plan.json>
    dbo-assets asset gwp-resource-preview <sealed-plan.json>
    dbo-assets asset gwp-resource-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset bsp-runtime-read-plan <private-client-root> <private-core-root> <new-plan.json>
    dbo-assets asset bsp-runtime-read <sealed-plan.json> <new-output-folder> (read-only packed Snake Way BSP/PVS/collision)
    dbo-assets asset terrain-geometry-plan <project/#######.gwp> <project/fields> <new-plan.json>
    dbo-assets asset terrain-geometry-compare <source-terrain.tgv> <current-terrain.tgv>
    dbo-assets asset effect-particle-trail-inspect <native-version104.eff>
    dbo-assets asset terrain-geometry-preview <sealed-plan.json>
    dbo-assets asset terrain-geometry-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset terrain-navi-plan <terrain.tgv> <project/#######.gwp> <project/fields> <PathEngine_x64.dll> terrain-only-not-for-install <new-plan.json>
    dbo-assets asset scene-placement-plan <project/#######.gwp> <project/fields> <new-plan.json>
      Read-only native object census; preview/apply via scene-geometry commands; no meshes or collision readiness.

    dbo-assets asset scene-collision-plan <scene.json> <terrain.tgv> <explicit-role-rules.json> <new-plan.json>
    dbo-assets asset scene-surface-probe <world-baked-OSM1.mesh> <world-X> <world-Z>  (read-only ray hits; NOT gameplay QA)
    dbo-assets asset scene-collision-preview <sealed-plan.json>
    dbo-assets asset scene-collision-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset scene-navi-plan <collision-candidate/audit.json> <project/#######.gwp> <project/fields> <PathEngine_x64.dll> <new-plan.json>
    dbo-assets asset terrain-navi-preview <sealed-terrain-or-scene-plan.json>
    dbo-assets asset terrain-navi-apply <sealed-terrain-or-scene-plan.json> <new-candidate-folder>
    dbo-assets asset scene-geometry-plan <project/#######.gwp> <project/fields> <native-OSM1-meshes> <new-plan.json>
    dbo-assets asset scene-portal-plan <project/#######.gwp> <fields/wfifN/wfifN> <fieldID> <sectorID> <SID> <object-name-ID> <new-plan.json>
    dbo-assets asset scene-portal-clone-plan <project/#######.gwp> <fields/wfifN/wfifN> <fieldID> <sectorID> <source-SID> <object-name-ID> <new-SID> <new-object-ID> <delta-X> <delta-Y> <delta-Z> <new-plan.json>
    dbo-assets asset scene-portal-preview <plan.json>
    dbo-assets asset scene-portal-apply <plan.json> <new-candidate-dir>
    dbo-assets asset scene-geometry-preview <sealed-plan.json>
    dbo-assets asset scene-geometry-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset scene-ground-plan <scene.json> <explicit-requests.json> <PathEngine_x64.dll> <new-plan.json>
    dbo-assets asset scene-ground-preview <sealed-plan.json>
    dbo-assets asset scene-ground-apply <sealed-plan.json> <new-candidate-folder>  (selected TOKs ONLY; not complete Navi)
    dbo-assets asset placed-floor-plan <placements.json> <local.mesh> <explicit-request.json> <PathEngine_x64.dll> <scene-navi-piece-builder.exe> <placed-floor-navigation-builder.exe> <new-plan.json>
    dbo-assets asset placed-floor-preview <sealed-plan.json>
    dbo-assets asset placed-floor-apply <sealed-plan.json> <new-candidate-dir>  (private explicit floor Navi; no client/project binding, installation or gameplay acceptance)
    dbo-assets asset terrain-navi-preview <sealed-plan.json>
    dbo-assets asset terrain-navi-apply <sealed-plan.json> <new-candidate-folder>
    Navi: real native export/reload/query, TERRAIN ONLY; objects/references/client QA absent, NOT installable.
    dbo-assets asset item-dependencies <Taiwan-itemproperty.rdf>
    dbo-assets asset taiwan-character-reconcile-preview <sealed-plan.json>
    dbo-assets asset taiwan-character-reconcile-apply <sealed-plan.json> <new-output-directory>
    dbo-assets asset skill-tree-append-plan <explicit-selection.json> <new-plan.json>
    dbo-assets asset skill-tree-append-preview <sealed-plan.json>
    dbo-assets asset skill-tree-append-apply <sealed-plan.json> <new-folder>
    dbo-assets asset character-rider-preview <sealed-Taiwan-SRP3-plan.json>
    dbo-assets asset character-rider-apply <sealed-Taiwan-SRP3-plan.json> <new-folder>
    dbo-assets asset animation-trim-preroll <standard.anm> <sealed-plan.json> <new-folder>
    dbo-assets asset item-alias-plan <Taiwan-v2.rdf> <native-v2-source.rdf> <aliases.json> <new-plan.json>
    dbo-assets asset item-alias-preview <plan.json>
    dbo-assets asset item-alias-apply <plan.json> <new-folder>
    dbo-assets asset common-config-inspect <snapshot.rdf>
    dbo-assets asset common-config-plan <target.rdf> <source.bin> <selection.json> <new-plan.json>
    dbo-assets asset common-config-preview <sealed-plan.json>
    dbo-assets asset common-config-apply <sealed-plan.json> <new-folder>
    dbo-assets asset model-dependencies <input.dff>
    dbo-assets asset texture-pixels <input.dds|png|bmp>
    dbo-assets asset texture-slice-plan <raw-unit.pak> <decimal-offset> <new-plan.json>
    dbo-assets asset texture-slice-preview <plan.json>
    dbo-assets asset texture-slice-apply <plan.json> <new-candidate-dir>
    dbo-assets asset model-uv-layout <complete-model.dff> <fresh-evidence.json>
    dbo-assets asset model-texture-alias-plan <model.dff> <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset model-ntl-texture-size-plan <source.dff> <new-plan.json>
    dbo-assets asset model-ntl-texture-size-preview <sealed-plan.json>
    dbo-assets asset model-ntl-texture-size-apply <sealed-plan.json> <new-folder>
      Separate exact NTL v0 embedded Texture header-size repair; native reload/real bindings/QA still required.
    dbo-assets asset model-texture-alias-preview <sealed-plan.json>
    dbo-assets asset model-texture-alias-apply <sealed-plan.json> <new-candidate-dir>
    dbo-assets asset model-bsp <v1-static-bindings.json> <new-folder>
    dbo-assets asset model-bindings <sealed-bindings.json>
    dbo-assets asset static-bsp-world-plan <template.gwp> <template-wfp> <empty-field> <bsp-candidate-directory> <new-plan.json>
    dbo-assets asset static-bsp-world-preview <sealed-plan.json>
    dbo-assets asset static-bsp-world-apply <sealed-plan.json> <new-folder>
    dbo-assets asset static-bsp-navigation-plan <native-snakerun.mesh> <world-candidate-directory> <PathEngine_x64.dll> <new-plan.json>
    dbo-assets asset static-bsp-navigation-ramp-plan <native-snakerun.mesh> <world-candidate-directory> <PathEngine_x64.dll> <ramp.json> <new-plan.json>
    dbo-assets asset static-bsp-navigation-envelope-plan <native-snakerun.mesh> <world-candidate-directory> <PathEngine_x64.dll> <new-plan.json>
    dbo-assets asset static-bsp-navigation-preview <sealed-plan.json>
    dbo-assets asset static-bsp-navigation-apply <sealed-plan.json> <new-folder>
    dbo-assets asset effect-spline-bindings <sealed-spline-plan.json>
    dbo-assets asset effect-spline-observations <sealed-spline-plan.json>
    dbo-assets asset effect-spline-observed-playback <sealed-spline-plan.json>
    dbo-assets asset effect-resource-fields <sealed-resource-plan.json>
    dbo-assets asset effect-spline-alias-plan <source.eff> <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset effect-spline-alias-preview <sealed-plan.json>
    dbo-assets asset effect-spline-alias-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset effect-spline-live-alias-plan <source.eff> <observed-global-mapping.json> <new-plan.json>
    dbo-assets asset effect-spline-live-alias-preview <sealed-plan.json>
    dbo-assets asset effect-spline-live-alias-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset effect-key-alias-plan <source.eff> <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset effect-fallback-plan <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset effect-fallback-preview <sealed-plan.json>
    dbo-assets asset effect-fallback-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset effect-key-alias-preview <sealed-plan.json>
    dbo-assets asset effect-key-alias-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset effect-mesh-alias-plan <source.eff> <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset effect-texture-alias-plan <source.eff> <reviewed-mapping.json> <new-plan.json>
    dbo-assets asset effect-texture-alias-preview <sealed-plan.json>
    dbo-assets asset effect-texture-alias-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset effect-mesh-alias-preview <sealed-plan.json>
    dbo-assets asset effect-mesh-alias-apply <sealed-plan.json> <new-candidate-folder>
    dbo-assets asset animation-poses <sealed-pose-plan.json>
    dbo-assets asset animation-callbacks <plan.json>
    dbo-assets asset character-toon <plan.json>
    dbo-assets asset effect-inspect <input.eff>
    dbo-assets asset effect-merge-preview <sealed-plan.json>
    dbo-assets asset effect-merge-apply <sealed-plan.json> <new-folder>
    EFF104: append exact mesh-only definitions and validate native readers/bindings. See EFFECT-MERGE.md.
    dbo-assets asset texture <input.dds|png|bmp>
    dbo-assets asset model-repair-plan <input.dff> <new-plan.json>
    dbo-assets asset model-boundary-observation <input.dff>
      Diagnostic only: records parent/child mismatches; no repair or structural acceptance.
    dbo-assets asset model-structure <input.dff> [--allow-omissions]
    dbo-assets asset model-toon-selected-plan <own-metadata-selection.json> <fresh-plan.json>
    dbo-assets asset model-toon-selected-preview <sealed-plan.json>
    dbo-assets asset model-toon-selected-apply <sealed-plan.json> <fresh-folder>
    dbo-assets asset model-toon-rebuild-plan <explicit-selection.json> <fresh-plan.json>
    dbo-assets asset model-toon-rebuild-preview <sealed-plan.json>
    dbo-assets asset model-toon-rebuild-apply <sealed-plan.json> <fresh-folder>
    dbo-assets asset model-toon-plan <single-geometry.dff> <new-plan.json>
    dbo-assets asset model-toon-preview <sealed-plan.json>
    dbo-assets asset model-toon-apply <sealed-plan.json> <new-folder>
    dbo-assets asset model-repair-preview <plan.json>
    dbo-assets asset model-repair-apply <plan.json> <new-folder>
    DFF repair: headers omitidos auditados; plan sellado/candidato, sin instalar. Ver DFF-REPAIR.md.
    dbo-assets asset audio <input.ogg|mp3|wav> <new.wav>
    dbo-assets asset sound <input.wav>
    model exporta geometría estática OSM1 para inspección, no edita/reconstruye DFF.
    dbo-assets asset model-geometry-compare <source.mesh> <current.mesh>
    Ver CONTENT-CLI.md y rdf-cli.md para contratos y límites. XML es intercambio, no runtime.
    Workspace: workspace init|status|add|import-pak|import-mob|commit|restore|export|verify (see WORKSPACE.md)
    Meshy: meshy-creative status|actions|list|image|retexture|animate|run|recover|files (see MESHY-CREATIVE.md)
    Map Workshop: map plan <recipe.json> <preview-template.html> <media-helper.exe|none> <new-plan.json>
    map preview <sealed-plan.json> | map apply <sealed-plan.json> <fresh-candidate-folder>
    map navi-plan <candidate> <chunk-helper.exe> <map-navigation-helper.exe> <PathEngine_x64.dll> <new-plan.json> [identity.json]
    map navi-preview <sealed-plan.json> | map navi-apply <sealed-plan.json> <fresh-candidate-folder>
    map world-plan <template.gwp> <template.wfp> <empty-field> <map-candidate> <new-plan.json> [identity.json]
    map world-preview <sealed-plan.json> | map world-apply <sealed-plan.json> <fresh-candidate-folder>
    map tables-plan <identity.json> <world.rdf> <zone.rdf> <text.rdf> <scene.json> <new-plan.json>
    map tables-preview <sealed-plan.json> | map tables-apply <sealed-plan.json> <fresh-candidate-folder>
    Original power-arena candidates only; preview/DFF/BSP/Navi do not certify world entry. See MAP-WORKSHOP.md.
    """;
}
