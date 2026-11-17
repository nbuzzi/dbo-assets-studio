using System.Diagnostics;
using System.Text;

namespace DboAssetsStudio;

/// <summary>Read-only presentation RDF through an isolated production Taiwan reader.</summary>
internal static class ObjectPropertyCatalog
{
    internal sealed record Entry(string NameHex, string Name, string ModelHex, string Model,
        string CollisionModelHex, string CollisionModel, uint Flags, bool Collision, bool NaviPress,
        int Animations, float[] Bounds);
    internal sealed record Dependency(string NameHex, string Kind, string PathHex, string Path);
    internal sealed record Catalog(string Source, string SourceSha256, string HelperSha256, int Version,
        string MeshPathHex, string MeshPath, int Count, int UniqueKeys, int DuplicateKeys, int EmptyKeys,
        bool NativeReaderPass, bool ByteExactRoundtrip, bool SourcesPreserved, bool RenderingTested,
        bool NavigationGenerated, List<Entry> Entries, List<Dependency> Dependencies);

    public static Task<Catalog> InspectAsync(string path) => InspectCoreAsync(path);

    private static async Task<Catalog> InspectCoreAsync(string path)
    {
        string source = Path.GetFullPath(path); ContentSafety.NoLinks(source);
        var info = new FileInfo(source);
        if (!info.Exists || info.Length <= 12 || info.Length > 128L * 1024 * 1024 ||
            !Path.GetExtension(source).Equals(".rdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ObjectProperty Taiwan RDF required; limit 128 MiB.");
        string before = ContentSafety.Hash(source);
        string helper = Path.Combine(AppContext.BaseDirectory, "object-property-reader.exe");
        if (!File.Exists(helper)) throw new IOException("object-property-reader.exe is missing; see OBJECT-PROPERTIES.md.");
        ContentSafety.NoLinks(helper);
        string helperHash = ContentSafety.Hash(helper);
        // Short ASCII snapshot paths match Taiwan's ANSI reader, without changing the original.
        string cache = Path.Combine(Path.GetTempPath(), "DboAssetsObjectProperties", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            string snapshot = Path.Combine(cache, "objectproperty.rdf"); File.Copy(source, snapshot);
            if (ContentSafety.Hash(snapshot) != before) throw new IOException("The input changed during the snapshot.");
            var start = new ProcessStartInfo(helper) { WorkingDirectory = cache, CreateNoWindow = true,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(snapshot);
            start.ArgumentList.Add("--dependencies");
            using var process = Process.Start(start) ?? throw new IOException("Could not start the Taiwan reader.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Taiwan reader timed out (45 s)."); }
            string text = await stdout, diagnostic = await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException("ObjectProperty rejected by the Taiwan reader: " + process.ExitCode + ". " + diagnostic);
            if (ContentSafety.Hash(source) != before || ContentSafety.Hash(helper) != helperHash)
                throw new IOException("Source/helper changed during the read.");
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) throw new InvalidDataException("Empty native catalog.");
            string[] header = lines[0].TrimEnd('\r').Split('\t');
            var propertyLines = lines.Skip(1).Where(l => l.StartsWith("PROPERTY\t")).ToArray();
            var dependencyLines = lines.Skip(1).Where(l => l.StartsWith("DEPENDENCY\t")).ToArray();
            if (header.Length != 4 || header[0] != "HEADER" || header[1] != "1" || !int.TryParse(header[2], out int count) || count != propertyLines.Length || lines.Length != 1 + propertyLines.Length + dependencyLines.Length)
                throw new InvalidDataException("Invalid native catalog coverage.");
            string Decode(string hex) => Encoding.Latin1.GetString(Convert.FromHexString(hex));
            var entries = new List<Entry>(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in propertyLines)
            {
                string[] c = line.TrimEnd('\r').Split('\t');
                if (c.Length != 14 || c[0] != "PROPERTY") throw new InvalidDataException("Invalid native record.");
                names.Add(c[1]);
                float[] bounds = c[8..14].Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (bounds.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite bounds.");
                entries.Add(new(c[1], Decode(c[1]), c[2], Decode(c[2]), c[3], Decode(c[3]), uint.Parse(c[4]),
                    c[5] != "0", c[6] != "0", int.Parse(c[7]), bounds));
            }
            var dependencies = new List<Dependency>();
            foreach (string line in dependencyLines)
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length != 4 || !names.Contains(c[1])) throw new InvalidDataException("Invalid native dependency.");
                dependencies.Add(new(c[1], c[2], c[3], Decode(c[3])));
            }
            return new(source, before, helperHash, 1, header[3], Decode(header[3]),
                count, names.Count, count - names.Count, entries.Count(e => e.NameHex.Length == 0),
                true, true, true, false, false, entries, dependencies);
        }
        finally { Directory.Delete(cache, true); }
    }
}
