namespace DboAssetsStudio;

// EFF104 v6 records a verified no-resource mesh slot as serialized and inactive.
// The allowlist is intentionally exact; it does not turn empty mesh systems into
// a generally supported content shape.
internal static partial class EffectMergeService
{
    private static List<object> ValidateEmptyMeshPolicies(AdvancedPlan plan, Catalog source)
    {
        const string effect = "U17S_0209101231bdd9b75653";
        const int systemIndex = 9;
        const string systemName = "MeshSystem00";
        if (plan.Version != 6 || !plan.Keys.SequenceEqual(new[] { effect }, StringComparer.Ordinal) ||
            plan.InactiveMeshSystems is not { Count: 1 })
            throw new InvalidDataException("EFF v6 empty-mesh profile is limited to the one pinned owned effect definition.");

        var policy = plan.InactiveMeshSystems[0];
        if (policy.Effect != effect || policy.SystemIndex != systemIndex || policy.SystemType != 2 ||
            policy.SystemName != systemName || policy.Mesh.Length != 0 || policy.Animation.Length != 0 ||
            policy.Uv.Length != 0 || policy.Spline.Length != 0 || policy.SubSystem)
            throw new InvalidDataException("Inactive empty-mesh identity or empty-reference policy differs from the reviewed slot.");

        var effects = source.Effects.Where(r => r.Name == effect).ToArray();
        var meshSystems = source.MeshSystems.Where(s => s.Effect == effect && s.SystemIndex == systemIndex).ToArray();
        var metadata = source.SystemMetadata.Where(s => s.Effect == effect).ToArray();
        var slot = metadata.Where(s => s.SystemIndex == systemIndex).ToArray();
        int[] expectedIndices = Enumerable.Range(0, 16).ToArray();
        if (effects.Length != 1 || meshSystems.Length != 1 || slot.Length != 1 || metadata.Length != expectedIndices.Length ||
            !metadata.Select(s => s.SystemIndex).Order().SequenceEqual(expectedIndices))
            throw new InvalidDataException("Native EFF reader did not identify one exact system-9 record.");

        var native = slot[0];
        var sourceRecordHash = Hash(source.Raw.AsSpan(effects[0].Offset, effects[0].Length).ToArray());
        if (!Equal(sourceRecordHash, policy.EffectRecordSha256) || native.SystemType != policy.SystemType ||
            native.Name != policy.SystemName || native.SubSystem || native.Mesh.Length != 0 || native.Animation.Length != 0 ||
            native.Uv.Length != 0 || native.PathFlags != 0 || meshSystems[0].Mesh.Length != 0 ||
            meshSystems[0].Animation.Length != 0 || meshSystems[0].Uv.Length != 0 || meshSystems[0].Spline.Length != 0)
            throw new InvalidDataException("Native decoded fields/record seal differ from the declared inactive slot.");

        var incoming = metadata.Where(s =>
            (s.FadePresent && s.FadeFollow && string.Equals(s.FadeTarget, systemName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(s.ParticleChild, systemName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.BeamHead, systemName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (incoming.Length != 0)
            throw new InvalidDataException("Empty mesh slot is referenced by a particle child, active fade-follow, or beam-head system.");

        return new List<object> { new
        {
            kind = "inactive-empty-mesh-system",
            effect,
            systemIndex,
            systemType = native.SystemType,
            systemName = native.Name,
            nativeSubSystem = native.SubSystem,
            mesh = native.Mesh,
            animation = native.Animation,
            uv = native.Uv,
            spline = meshSystems[0].Spline,
            sourceEffectRecordSha256 = sourceRecordHash,
            sourceSystemCount = metadata.Length,
            sourceSystemIndices = metadata.Select(s => s.SystemIndex).Order().ToArray(),
            incomingParticleChildReferences = 0,
            incomingActiveFadeFollowReferences = 0,
            incomingBeamHeadReferences = 0,
            runtimeBehavior = "CNtlInstanceMeshSystem::Create returns FALSE for empty mesh; CNtlInstanceEffect drops the instance after Reset/Delete",
            disposition = "inactive-by-native-create-guard; serialized system record retained byte-exact; no DFF is synthesized"
        } };
    }
}
