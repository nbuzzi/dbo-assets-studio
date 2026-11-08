namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private PackLibrary? _packLibrary;
    private async Task<object> OpenPackLibraryAsync(string selected)
    {
        // Build a complete next state before changing the visible workspace.
        var library = await PackLibrary.OpenAsync(selected);
        ResourceCatalog? references = null;
        var tables = library.Archives.FirstOrDefault(a => a.Prefix.Equals("tbl", StringComparison.OrdinalIgnoreCase));
        if (tables != null)
        {
            try { references = await ResourceCatalog.LoadPakAsync(tables, _schemas); }
            catch (Exception ex) { library.Notes.Add("Referencias RDF: " + ex.Message); }
        }
        _modernPreview?.Cancel(); _packLibrary = library; _pak = null; _looseResources = []; _modernSource = library.Root;
        _resourceCatalog = references; _textureArchive = null; _selectedResource = null;
        _selectedResourceKey = null;
        StudioLog($"Pack library: {library.Archives.Count} indices, {library.Entries.Count} resources; {library.Notes.Count} notices");
        return new { workspace = "resources", source = library.Root, archives = library.Archives.Count, total = library.Entries.Count, notes = library.Notes };
    }
    private (PakArchive? Archive, PakEntry? Entry) ResolveModernPakResource(string key)
    {
        if (_packLibrary != null) { var row = _packLibrary.Get(key); return (row.Archive, row.Entry); }
        if (_pak != null)
        {
            if (!int.TryParse(key, out int index) || index < 0 || index >= _pak.Entries.Count) throw new ArgumentException("Selección de recurso inválida.");
            return (_pak, _pak.Entries[index]);
        }
        return (null, null);
    }
}
