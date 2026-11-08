using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DboAssetsStudio;

internal sealed record RdfCreatePlan(string Format, int Version, string Target, string TargetSha256,
    RdfRevision Source, string SchemaSha256, string Reason);

// Restore a registered fixed-width RDF at an explicitly absent logical target.
// The target is observed only. Apply writes a fresh private candidate, preserving
// every source byte; neither an empty historical base nor deployment is implied.
internal static class RdfCreateService
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string SchemaHash(RdfSchema s) => Hash(Encoding.UTF8.GetBytes(
        s.Name + ":" + s.RowSize + ":" + string.Join(';', s.Fields.Select(f => $"{f.Name}:{f.Type}:{f.Count}:{f.Offset}"))));
    private sealed class Input : IDisposable
    {
        private readonly FileStream handle;
        public RdfDocument Document { get; }
        public RdfField Key { get; }
        public Input(string path)
        {
            ContentSafety.NoLinks(path);
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (handle.Length is < 1 or > 128L * 1024 * 1024) throw new InvalidDataException("RDF must be1..128MiB.");
                (Document, Key) = RdfCli.Open(path);
                if (Document.Margin != 1) throw new InvalidDataException("Registered Taiwan RDF margin1 required.");
                RdfCli.ValidateFinalRows(Document, Key);
            }
            catch { handle.Dispose(); throw; }
        }
        public void Dispose() => handle.Dispose();
    }
    private static void Absent(string path)
    {
        ContentSafety.NoLinks(path);
        if (!Path.GetExtension(path).Equals(".rdf", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
            throw new IOException("Target RDF must be explicitly absent; existing files require three-way reconciliation.");
    }
    private static void Verify(Input input, RdfCreatePlan plan)
    {
        Absent(plan.Target);
        if (plan.Format != "taiwan-rdf-create-v1" || plan.Version != 1 || plan.TargetSha256 != "ABSENT" ||
            string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > 4096 ||
            !Path.GetFileName(plan.Target).Equals(Path.GetFileName(plan.Source.Path), StringComparison.OrdinalIgnoreCase) ||
            plan.Source.Sha256 != input.Document.OriginalHash || plan.SchemaSha256 != SchemaHash(input.Document.Schema))
            throw new InvalidDataException("Creation source/schema/target identity changed.");
    }
    public static object Plan(string source, string target, string reason, string output)
    {
        using var input = new Input(source);
        var plan = new RdfCreatePlan("taiwan-rdf-create-v1", 1, Path.GetFullPath(target), "ABSENT",
            new(Path.GetFullPath(source), input.Document.OriginalHash), SchemaHash(input.Document.Schema), reason);
        Verify(input, plan); ContentSafety.WriteJson(output, plan);
        return new { plan = Path.GetFullPath(output), planSha256 = ContentSafety.Hash(output), outputSha256 = input.Document.OriginalHash,
            rows = input.Document.Rows.Count, sourceRows = "byte-exact", installed = false };
    }
    private static (RdfCreatePlan, byte[]) ReadPlan(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Plan exceeds1MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        var options = new JsonSerializerOptions(ContentSafety.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        return (JsonSerializer.Deserialize<RdfCreatePlan>(bytes, options) ?? throw new InvalidDataException("Missing creation plan."), bytes);
    }
    public static object Preview(string path)
    {
        var (plan, _) = ReadPlan(path); using var input = new Input(plan.Source.Path); Verify(input, plan);
        return new { outputSha256 = input.Document.OriginalHash, rows = input.Document.Rows.Count, sourceRows = "byte-exact", installed = false };
    }
    public static object Apply(string path, string output)
    {
        var (plan, bytes) = ReadPlan(path); using var input = new Input(plan.Source.Path); Verify(input, plan);
        string destination = ContentSafety.SafeOutput(output);
        foreach (string source in new[] { path, plan.Source.Path, plan.Target })
            if (Path.GetFullPath(source).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output cannot contain creation inputs or the observed target.");
        string pending = ContentSafety.FreshDirectory(destination);
        try
        {
            string name = Path.GetFileName(plan.Target), candidate = Path.Combine(pending, name);
            File.WriteAllBytes(candidate, input.Document.OriginalBytes);
            var readback = RdfDocument.Open(candidate, input.Document.Schema); RdfCli.ValidateFinalRows(readback, input.Key);
            if (readback.OriginalHash != input.Document.OriginalHash) throw new IOException("RDF creation readback differs.");
            Verify(input, plan); // Fail if the observed target appeared during candidate preparation.
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new { version = 1, kind = plan.Format, utc = DateTime.UtcNow,
                schema = input.Document.Schema.Name, planSha256 = Hash(bytes), targetSha256 = "ABSENT",
                sourceSha256 = input.Document.OriginalHash, outputSha256 = readback.OriginalHash,
                rows = input.Document.Rows.Count, sourceRows = "byte-exact", targetAbsent = true, installed = false, worldTested = false });
            Directory.Move(pending, destination);
            return new { output = Path.Combine(destination, name), outputSha256 = readback.OriginalHash, installed = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
