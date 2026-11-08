namespace DboAssetsStudio;

internal static class WorkspaceContentValidation
{
    public static void Validate(string file, string destination)
    {
        ContentSafety.NoLinks(file);
        if (destination.EndsWith(".wps", StringComparison.OrdinalIgnoreCase))
        {
            var wps = WpsDocument.Load(file);
            if (!wps.CanMutate || wps.Diagnostics.Any(d => d.Severity == "error")) throw new IOException("WPS con errores estructurales/semánticos: " + destination);
            return;
        }
        if (destination.StartsWith("client/")) return; // Canonical PAK receipt validates these.
        if (Path.GetFileName(destination).Equals("characterproperty.rdf", StringComparison.OrdinalIgnoreCase))
        {
            // Shared complete Taiwan catalog, no dependency on Meshy/Python
            // settings or the workshop's narrower humanoid conversion profile.
            _ = CharacterPropertyCatalog.Inspect(file); return;
        }
        if (Path.GetFileName(destination).Equals("table_text_all_data.rdf", StringComparison.OrdinalIgnoreCase))
        {
            TextAllRdfService.ValidateFile(file); return;
        }
        var schemas = RdfSchema.LoadCatalog(Path.Combine(AppContext.BaseDirectory, "taiwan.txt"));
        var schema = RdfSchema.Detect(destination, schemas);
        // Registered Taiwan MOB contract: sMOB_TBLDAT 584 + file margin; server
        // extension is a separate 12-byte row. Never infer strides from contents.
        string leaf = Path.GetFileName(destination);
        if (schema == null && (leaf.Equals("Table_MOB_Data.rdf", StringComparison.OrdinalIgnoreCase) || leaf.Equals("table_mob_data_server.rdf", StringComparison.OrdinalIgnoreCase)))
        {
            int stride = leaf.Equals("table_mob_data_server.rdf", StringComparison.OrdinalIgnoreCase) ? 12 : 584;
            schema = new RdfSchema { Name = "TaiwanMOBWorkspace", FileName = leaf, Sheet = "MOB", RowSize = stride, Fields = [new("tblidx", "int", 1, 0), new("body", "byte", stride - 4, 4)] };
        }
        if (schema == null) throw new IOException("RDF sin esquema Taiwan registrado: " + destination + ". Extender el motor antes de incluirlo en una entrega.");
        var document = RdfDocument.Open(file, schema); var key = schema.Fields.Single(f => f.Name == schema.KeyField);
        var ids = new HashSet<string>();
        foreach (var row in document.Rows)
        {
            string id = schema.Decode(key, row.Original);
            if (!ids.Add(id)) throw new IOException("RDF con ID duplicado: " + destination + " / " + id);
        }
    }
}
