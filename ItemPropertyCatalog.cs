using System.Diagnostics;
using System.Text;

namespace DboAssetsStudio;

internal static class ItemPropertyCatalog
{
    internal sealed record Entry(string NameHex, string Name, string MeshPathHex, string MeshPath,
        string MeshHex, string Mesh, int ResourceType, int EquipSlot, int AnimationCount);
    internal sealed record Catalog(string Source, string SourceSha256, string HelperSha256, int Version,
        string ItemPathHex, int Count, bool NativeReaderPass, bool ByteExactRoundtrip,
        bool RenderingTested, List<Entry> Entries)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<Dependency>? Dependencies { get; init; }
    }
    internal sealed record Dependency(string Item, string Kind, int Index, string Resource, string Bone, bool AttachBone);
    internal static string Helper => Path.Combine(AppContext.BaseDirectory, "item-property-reader.exe");
    internal static async Task<string> RunAsync(string cwd, params string[] arguments) => (await RunWithDiagnosticsAsync(cwd,arguments)).Output;
    internal static async Task<(string Output,string Diagnostic)> RunWithDiagnosticsAsync(string cwd, params string[] arguments)
    {
        ContentSafety.NoLinks(Helper);
        var start = new ProcessStartInfo(Helper) { WorkingDirectory = cwd, CreateNoWindow = true,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar lector ItemProperty.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Timeout ItemProperty45s."); }
        string text = await stdout, diagnostic = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("ItemProperty rechazado: " + diagnostic);
        return (text,diagnostic);
    }
    public static Task<Catalog> InspectAsync(string path, bool dependencies = false) => InspectCoreAsync(path, dependencies);
    private static async Task<Catalog> InspectCoreAsync(string path, bool dependencies = false)
    {
        string source = Path.GetFullPath(path); ContentSafety.NoLinks(source);
        if (Path.GetExtension(source).ToLowerInvariant() != ".rdf" || new FileInfo(source).Length is < 12 or > 128L * 1024 * 1024)
            throw new InvalidDataException("ItemProperty RDF requerido; límite128MiB.");
        string sourceHash = ContentSafety.Hash(source), helperHash = ContentSafety.Hash(Helper);
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsItemProperty", Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);
        try
        {
            string snapshot = Path.Combine(cache, "itemproperty.rdf"); File.Copy(source, snapshot);
            if (ContentSafety.Hash(snapshot) != sourceHash) throw new IOException("Fuente cambió durante snapshot.");
            var arguments = new List<string> { snapshot };
            if (dependencies) arguments.Add("--dependencies");
            string text = await RunAsync(cache, arguments.ToArray());
            if (ContentSafety.Hash(source) != sourceHash || ContentSafety.Hash(Helper) != helperHash) throw new IOException("Fuente/helper cambió.");
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);var header = lines[0].TrimEnd('\r').Split('\t');
            var properties = lines.Skip(1).Where(line => line.StartsWith("PROPERTY\t", StringComparison.Ordinal)).ToArray();
            var dependencyLines = lines.Skip(1).Where(line => line.StartsWith("DEPENDENCY\t", StringComparison.Ordinal)).ToArray();
            if (header.Length != 4 || header[0] != "HEADER" || header[1] != "2" || !int.TryParse(header[2], out int count) || properties.Length != count || lines.Length != 1 + properties.Length + dependencyLines.Length || (!dependencies && dependencyLines.Length != 0))
                throw new InvalidDataException("Cobertura ItemProperty inválida.");
            string Decode(string value) => Encoding.Latin1.GetString(Convert.FromHexString(value));
            var entries = new List<Entry>();var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in properties)
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length != 7 || c[0] != "PROPERTY" || c[1].Length == 0 || !names.Add(c[1])) throw new InvalidDataException("Clave ItemProperty inválida.");
                entries.Add(new(c[1], Decode(c[1]), c[2], Decode(c[2]), c[3], Decode(c[3]), int.Parse(c[4]), int.Parse(c[5]), int.Parse(c[6])));
            }
            var deps = new List<Dependency>(); var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in dependencyLines)
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length != 7 || !names.Contains(c[1]) || c[2] is not ("link-effect" or "upgrade-effect" or "trace-texture" or "animation") || !int.TryParse(c[3], out int index) || index < -1 || c[4].Length == 0 || c[6] is not ("0" or "1") || !identities.Add(c[1] + ":" + c[2] + ":" + c[3]))
                    throw new InvalidDataException("Dependencia ItemProperty inválida.");
                deps.Add(new(Decode(c[1]), c[2], index, Decode(c[4]), Decode(c[5]), c[6] == "1"));
            }
            return new Catalog(source, sourceHash, helperHash, 2, header[3], count, true, true, false, entries) { Dependencies = dependencies ? deps : null };
        }
        finally { Directory.Delete(cache, true); }
    }
}
