using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// One exact Source definition whose external raster lives in its embedded
// particle trail structure. This does not permit arbitrary dependency-free EFFs.
internal static partial class EffectMergeService
{
    // Separate v9 contract for owned new-mob definitions. Native inspection
    // supplies every embedded texture/system association; the plan seals whole
    // source records and exact raster bytes. Legacy v8 keeps its fixed scope.
    private static async Task<List<object>> ValidateMobParticleTrailPolicyAsync(AdvancedPlan plan, Catalog source,
        string folder, Dictionary<string, string> pins)
    {
        if (plan.Version != 9 || plan.Keys.Any(k => !Regex.IsMatch(k, "^U17M_[0-9a-f]{20}$")) ||
            plan.EmbeddedTrailRecords is null || plan.EmbeddedTrailRecords.Count > 64 ||
            plan.EmbeddedTrailTextures is null || plan.EmbeddedTrailTextures.Count > 512 ||
            plan.EmbeddedTrailRecords.Any(r => r is null || r.Effect is null || !plan.Keys.Contains(r.Effect) ||
                r.RecordSha256 is null || r.RecordSha256.Length != 64 || !r.RecordSha256.All(Uri.IsHexDigit)) ||
            plan.EmbeddedTrailRecords.Select(r => r.Effect).Distinct(StringComparer.Ordinal).Count() != plan.EmbeddedTrailRecords.Count ||
            plan.EmbeddedTrailTextures.Any(t => t is null || t.Name is null || t.Source is null) ||
            plan.EmbeddedTrailTextures.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.EmbeddedTrailTextures.Count)
            throw new InvalidDataException("EFF v9 requires bounded owned mob keys, whole-record pins and unique embedded raster pins.");
        var proof = JsonSerializer.SerializeToElement(await InspectParticleTrailsAsync(plan.Source.Path), ContentSafety.Json);
        if (!proof.GetProperty("nativeReaderPass").GetBoolean() ||
            !Equal(proof.GetProperty("sourceSha256").GetString()!, plan.Source.Sha256) ||
            !Equal(proof.GetProperty("helperSha256").GetString()!, plan.EffectHelperSha256))
            throw new InvalidDataException("EFF v9 exact native source/helper trail inspection required.");
        var trails = proof.GetProperty("particleTrails").EnumerateArray()
            .Where(t => plan.Keys.Contains(t.GetProperty("effect").GetString()!)).ToArray();
        var effects = trails.Select(t => t.GetProperty("effect").GetString()!).ToHashSet(StringComparer.Ordinal);
        if (trails.Length > 4096 || !effects.SetEquals(plan.EmbeddedTrailRecords.Select(r => r.Effect)) ||
            plan.Keys.Any(k => !effects.Contains(k) && !source.Dependencies.Any(d => d.Effect == k)))
            throw new InvalidDataException("Every selected trail record must be sealed; empty definitions remain rejected.");
        foreach (var record in plan.EmbeddedTrailRecords)
        {
            var row = source.Effects.Single(r => r.Name == record.Effect);
            string hash = Hash(source.Raw.AsSpan(row.Offset, row.Length).ToArray());
            var selected = trails.Where(t => t.GetProperty("effect").GetString() == record.Effect).ToArray();
            if (!Equal(hash, record.RecordSha256) || selected.Any(t => !Equal(t.GetProperty("effectRecordSha256").GetString()!, hash)) ||
                selected.Any(t => source.SystemMetadata.Count(s => s.Effect == record.Effect &&
                    s.SystemIndex == t.GetProperty("systemIndex").GetInt32() && s.SystemType == 1 &&
                    s.Name == t.GetProperty("systemName").GetString() && (s.EmitterFlags & 16384) != 0) != 1))
                throw new InvalidDataException("Embedded trail whole-record/native particle system identity differs.");
        }
        var names = trails.Select(t => LiteralResource(t.GetProperty("texture").GetString()!, ".dds", ".png", ".bmp"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.SetEquals(plan.EmbeddedTrailTextures.Select(t => t.Name)))
            throw new InvalidDataException("Every native embedded texture requires one pin, with no extras.");
        var result = new List<object>();
        foreach (var texture in plan.EmbeddedTrailTextures)
        {
            LiteralResource(texture.Name, ".dds", ".png", ".bmp");
            var raster = texture.Source with { Path = Resolve(texture.Source.Path, folder) };
            if (!Equal(Path.GetFileName(raster.Path), texture.Name))
                throw new InvalidDataException("Embedded raster basename differs from native trail lookup.");
            Snapshot(raster, pins);
            result.Add(new { kind = "native-owned-mob-particle-embedded-trail", texture = texture.Name,
                sourceSha256 = raster.Sha256, nativeTrails = trails.Where(t => Equal(t.GetProperty("texture").GetString()!, texture.Name)).ToArray(),
                nativeRaster = await AssetDependencyCatalog.InspectAsync("texture", raster.Path),
                sourceRecordPreserved = true, packedNamespaceAndCurrentPayloadMustBeVerified = true, renderingTested = false });
        }
        return result;
    }

    private static async Task<List<object>> ValidateParticleTrailPolicyAsync(AdvancedPlan plan, Catalog source,
        string folder, Dictionary<string, string> pins)
    {
        const string key = "U17S_769b96e0659fb66e5ab2";
        const string recordHash = "44827EF9A5545A9FD9D7FE893C722803E9994A18D2572F9D3C1213DCC12668FA";
        const string rasterHash = "FFB054352516121C49C2F0E2F6444C6F83855B30407AFEF263F00B8AF0313C67";
        if (plan.Version != 8 || !plan.Keys.SequenceEqual(new[] { key }, StringComparer.Ordinal) ||
            plan.EmbeddedTrailTextures is not { Count: 1 } || plan.Systems.Count != 0 || plan.Textures.Count != 0 ||
            plan.Splines.Count != 0 || plan.Sounds is not { Count: 0 } || plan.ObservedOrderSplines is not { Count: 0 })
            throw new InvalidDataException("EFF v8 requires the one reviewed two-particle embedded-trail definition.");
        var effect = source.Effects.Single(e => e.Name == key);
        var systems = source.SystemMetadata.Where(s => s.Effect == key).OrderBy(s => s.SystemIndex).ToArray();
        if (Hash(source.Raw.AsSpan(effect.Offset, effect.Length).ToArray()) != recordHash || effect.Length != 2381 ||
            source.Dependencies.Any(d => d.Effect == key) || source.MeshSystems.Any(s => s.Effect == key) || systems.Length != 2 ||
            systems[0].SystemIndex != 0 || systems[0].Name != "trail" || systems[1].SystemIndex != 1 || systems[1].Name != "trail_03" ||
            systems.Any(s => s.SystemType != 1 || s.EmitterFlags != 16384 || s.SubSystem || s.PathFlags != 0 || s.Eternity ||
                s.FadePresent || s.Mesh.Length != 0 || s.Animation.Length != 0 || s.Uv.Length != 0 || s.ParticleChild.Length != 0 || s.BeamHead.Length != 0))
            throw new InvalidDataException("EFF v8 native record/system identity differs from reviewed Source.");
        var texture = plan.EmbeddedTrailTextures[0];
        if (texture is null || texture.Source is null || texture.Name != "particle_Y.dds" || !Equal(texture.Source.Sha256, rasterHash))
            throw new InvalidDataException("EFF v8 requires the exact Source particle_Y.dds payload.");
        var raster = texture.Source with { Path = Resolve(texture.Source.Path, folder) };
        if (!Equal(Path.GetFileName(raster.Path), texture.Name)) throw new InvalidDataException("Embedded trail raster basename differs.");
        Snapshot(raster, pins);
        var proof = JsonSerializer.SerializeToElement(await InspectParticleTrailsAsync(plan.Source.Path), ContentSafety.Json);
        var trails = proof.GetProperty("particleTrails").EnumerateArray().Where(t => t.GetProperty("effect").GetString() == key)
            .OrderBy(t => t.GetProperty("systemIndex").GetInt32()).ToArray();
        if (proof.GetProperty("profile").GetString() != "taiwan-eff104-particle-embedded-trails-v1" ||
            !proof.GetProperty("nativeReaderPass").GetBoolean() || trails.Length != 2 ||
            trails.Any(t => t.GetProperty("texture").GetString() != texture.Name || t.GetProperty("effectRecordSha256").GetString() != recordHash) ||
            trails[0].GetProperty("systemIndex").GetInt32() != 0 || trails[1].GetProperty("systemIndex").GetInt32() != 1)
            throw new InvalidDataException("EFF v8 embedded trail texture association differs from native Source.");
        return new List<object> { new { kind = "native-particle-embedded-trail", effect = key, sourceEffectRecordSha256 = recordHash,
            texture = texture.Name, sourceSha256 = rasterHash, nativeTrails = trails,
            nativeRaster = await AssetDependencyCatalog.InspectAsync("texture", raster.Path), sourceRecordPreserved = true,
            packedNamespaceAndCurrentPayloadMustBeVerified = true, renderingTested = false } };
    }
}
