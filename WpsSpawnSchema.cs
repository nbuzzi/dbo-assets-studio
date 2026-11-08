namespace DboAssetsStudio;

internal static class WpsSpawnSchema
{
    // The dedicated WPS catalog and supplemental native catalog both register
    // MobSpawn. Accept equivalent declarations, never pick conflicting layouts.
    public static RdfSchema Select(IEnumerable<RdfSchema> schemas)
    {
        var candidates = schemas.Where(s => s.Name.Equals("MobSpawn", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0 || candidates.Any(s => s.RowSize != 84))
            throw new InvalidDataException("MobSpawn Taiwan requiere el esquema nativo de 84 bytes.");
        static string Layout(RdfSchema schema) => string.Join(";", schema.Fields.Where(f => !f.IsPadding)
            .Select(f => $"{f.Name.ToLowerInvariant()}:{f.Type}:{f.Count}:{f.Offset}"));
        string layout = Layout(candidates[0]);
        if (candidates.Any(s => Layout(s) != layout))
            throw new InvalidDataException("Hay registros MobSpawn con layouts incompatibles; revisar el catálogo antes de elegir grupos.");
        return candidates[0];
    }
}
