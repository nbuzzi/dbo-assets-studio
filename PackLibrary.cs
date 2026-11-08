using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed record PackLibraryEntry(string Key, string Name, PakArchive Archive, PakEntry Entry);

/// <summary>All Taiwan PAK indices in a selected pack directory, using the canonical reader.</summary>
internal sealed class PackLibrary
{
    public string Root { get; }
    public List<string> Notes { get; } = [];
    public List<PakArchive> Archives { get; } = [];
    public List<PackLibraryEntry> Entries { get; } = [];
    private readonly Dictionary<string, PackLibraryEntry> _keys = new(StringComparer.Ordinal);
    private PackLibrary(string root) => Root = root;
    public static string ResolveRoot(string selected)
    {
        string root = Path.GetFullPath(selected); ContentSafety.NoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string child = Path.Combine(root, "pack");
        if (Directory.Exists(child)) { ContentSafety.NoLinks(child); return child; }
        return root;
    }
    public static bool HasIndices(string selected) => Directory.EnumerateFiles(ResolveRoot(selected), "*.pak", SearchOption.TopDirectoryOnly)
        .Any(IsIndexName);
    private static bool IsIndexName(string path) => !Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"\d+$", RegexOptions.CultureInvariant);
    public static async Task<PackLibrary> OpenAsync(string selected, CancellationToken token = default)
    {
        var library = new PackLibrary(ResolveRoot(selected));
        var indices = Directory.EnumerateFiles(library.Root, "*.pak", SearchOption.TopDirectoryOnly).Where(IsIndexName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (indices.Length == 0) throw new IOException("No se encontraron índices PAK. Elegí la carpeta pack con índices como obj.pak/tex.pak y sus unidades.");
        if (indices.Length > 256) throw new IOException("Demasiados índices en una carpeta (máximo 256). Elegí la carpeta pack del cliente.");
        foreach (string index in indices)
        {
            token.ThrowIfCancellationRequested();
            PakArchive archive;
            try { archive = await PakArchive.OpenAsync(index); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { library.Notes.Add(Path.GetFileName(index) + ": " + ex.Message); continue; }
            if (library.Entries.Count + archive.Entries.Count > 2_000_000) throw new IOException("La biblioteca supera dos millones de recursos; seleccionar una carpeta menor.");
            library.Archives.Add(archive);
            foreach (var entry in archive.Entries)
            {
                string key = archive.Prefix + ":" + entry.Number;
                var row = new PackLibraryEntry(key, archive.Prefix + "\\" + entry.Name, archive, entry);
                if (!library._keys.TryAdd(key, row)) throw new InvalidDataException("Clave de recurso duplicada: " + key);
                library.Entries.Add(row);
            }
        }
        if (library.Archives.Count == 0) throw new InvalidDataException("No se pudo leer ningún índice Taiwan de la carpeta.\n" + string.Join("\n", library.Notes.Take(10)));
        return library;
    }
    public PackLibraryEntry Get(string key) => _keys.TryGetValue(key, out var row) ? row : throw new ArgumentException("El recurso no pertenece a la biblioteca actual.");
}
