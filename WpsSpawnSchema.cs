namespace DboAssetsStudio;

internal static class WpsSpawnSchema
{
    // The dedicated WPS catalog and supplemental native catalog both register
    // MobSpawn. Accept equivalent declarations, never pick conflicting layouts.
    public static RdfSchema Select(IEnumerable<RdfSchema> schemas)
    {
        var candidates = schemas.Where(s => s.Name.Equals("MobSpawn", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0 || candidates.Any(s => s.RowSize != 84))
            throw new InvalidDataException("Taiwan MobSpawn requires the native 84-byte schema.");
        static string Layout(RdfSchema schema) => string.Join(";", schema.Fields.Where(f => !f.IsPadding)
            .Select(f => $"{f.Name.ToLowerInvariant()}:{f.Type}:{f.Count}:{f.Offset}"));
        string layout = Layout(candidates[0]);
        if (candidates.Any(s => Layout(s) != layout))
            throw new InvalidDataException("Some MobSpawn records have incompatible layouts; check the catalog before choosing groups.");
        return candidates[0];
    }
}
