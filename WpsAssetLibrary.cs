namespace DboAssetsStudio;

/// <summary>Read-only asset lookup using the same Taiwan PAK reader as the resource workspace.</summary>
internal sealed class WpsAssetLibrary
{
    public string Root { get; }
    public List<string> Notes { get; } = [];
    private readonly Dictionary<string, PakArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PakArchive Archive, PakEntry Entry)> _entries = [];
    private WpsAssetLibrary(string root) => Root = root;
    public static async Task<WpsAssetLibrary> OpenAsync(string root)
    {
        root = Path.GetFullPath(root); ContentSafety.NoLinks(root);
        if (Directory.Exists(Path.Combine(root, "pack"))) root = Path.Combine(root, "pack");
        var library = new WpsAssetLibrary(root);
        foreach (string family in new[] { "obj", "tex" })
        {
            string path = Path.Combine(root, family + ".pak");
            if (!File.Exists(path)) { library.Notes.Add("Falta " + family + ".pak; conservar el índice y sus unidades."); continue; }
            var archive = await PakArchive.OpenAsync(path);
            library._archives[family] = archive;
            foreach (var entry in archive.Entries)
                library._entries[family + ":" + entry.Number] = (archive, entry);
        }
        if (library._archives.Count == 0) throw new IOException("Elegí una copia del cliente Taiwan con pack/obj.pak o pack/tex.pak.");
        return library;
    }
    public object[] Find(string model, string icon)
    {
        string modelStem = Path.GetFileNameWithoutExtension(model), iconStem = Path.GetFileNameWithoutExtension(icon);
        return _entries.Where(p =>
        {
            string ext = Path.GetExtension(p.Value.Entry.Name).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(p.Value.Entry.Name);
            return ext == ".dff" ? modelStem.Length > 0 && stem.Equals(modelStem, StringComparison.OrdinalIgnoreCase)
                : (ext is ".dds" or ".png" or ".bmp" or ".tga" or ".jpg") && iconStem.Length > 0 && stem.Equals(iconStem, StringComparison.OrdinalIgnoreCase);
        }).Take(50).Select(p => (object)new { key = p.Key, name = p.Value.Entry.Name, kind = Path.GetExtension(p.Value.Entry.Name).Equals(".dff", StringComparison.OrdinalIgnoreCase) ? "model" : "image", status = p.Value.Entry.Status }).ToArray();
    }
    public (PakArchive Archive, PakEntry Entry) Get(string key) => _entries.TryGetValue(key, out var entry) ? entry : throw new ArgumentException("Recurso ajeno a la biblioteca vinculada.");
    public async Task<Dictionary<string, byte[]>> ReadTexturesAsync(StudioModel model)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!_archives.TryGetValue("tex", out var textures)) return result;
        long budget = 64L * 1024 * 1024;
        foreach (string texture in model.TextureNames.Take(64))
        {
            var candidates = textures.Entries.Where(e => Path.GetFileNameWithoutExtension(e.Name).Equals(texture, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(e.Name).ToLowerInvariant() is ".dds" or ".png" or ".bmp" or ".tga").ToArray();
            // Ambiguous filenames are not silently replaced with a different material.
            if (candidates.Length != 1 || candidates[0].Status != "Disponible" || candidates[0].Size > 16 * 1024 * 1024 || candidates[0].Size > budget) continue;
            budget -= candidates[0].Size;
            result[texture] = await textures.ReadAsync(candidates[0]);
        }
        return result;
    }
    public string TextureExtension(string name) => _archives.TryGetValue("tex", out var archive)
        ? Path.GetExtension(archive.Entries.First(e => Path.GetFileNameWithoutExtension(e.Name).Equals(name, StringComparison.OrdinalIgnoreCase)).Name).ToLowerInvariant() : "";
}
