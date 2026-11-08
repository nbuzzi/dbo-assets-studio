using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed record RdfRevision(string Path, string Sha256);
internal sealed record RdfResolution(string Id, string Field, string Choice);
// Reserved storage is never a semantic field choice. These raw hashes bind an
// explicit ours-preserve decision to the exact schema slices under review.
internal sealed record RdfPaddingPreservation(string Id, string Field,
    int Offset, int Size, string? BaseSha256, string? OursSha256, string? TheirsSha256, string Choice);
internal sealed record RdfDifference(string Id, string Field, string Status,
    string? Base, string? Ours, string? Theirs, bool Reserved);
internal sealed record RdfPaddingDifference(string Id, string Field, int Offset, int Size,
    string? BaseSha256, string? OursSha256, string? TheirsSha256);
// A single historic all-ones ID is accepted only as an unchanged raw row in
// these two native map tables. Reconciliation decisions must still opt in.
internal sealed record RdfLegacySentinelPreservation(string Id, string Schema,
    string SchemaSha256, string RowSha256, string Choice);
internal sealed record RdfComparison(string Schema, string SchemaSha256,
    RdfRevision Base, RdfRevision Ours, RdfRevision Theirs,
    IReadOnlyList<RdfDifference> Changes, IReadOnlyList<RdfPaddingDifference> PaddingDifferences,
    IReadOnlyList<RdfLegacySentinelPreservation> LegacySentinelPreservations)
{
    public bool HasConflicts => Changes.Any(c => c.Status == "conflict" || c.Reserved);
    public IReadOnlyList<string> RemovedIds => Changes.Where(c => c.Field == "$row" && c.Theirs == null && (c.Base != null || c.Ours != null)).Select(c => c.Id).ToArray();
    public bool SafeToReplaceOurs => !HasConflicts && RemovedIds.Count == 0 && !Changes.Any(c => c.Status == "ours_only");
}
internal sealed record RdfReconcilePlan(string Format, int Version, string SchemaSha256,
    RdfRevision Base, RdfRevision Ours, RdfRevision Theirs,
    IReadOnlyList<RdfResolution> Resolutions,
    IReadOnlyList<RdfPaddingPreservation>? PaddingPreservations = null,
    IReadOnlyList<RdfLegacySentinelPreservation>? LegacySentinelPreservations = null);

/// <summary>Schema-aware three-way candidates; never installs or edits an input.</summary>
internal static class RdfReconcileService
{
    private const int MaxDecisionOrPlanBytes = 32 * 1024 * 1024;
    private sealed class Inputs : IDisposable
    {
        private readonly List<FileStream> locks = [];
        public RdfDocument Base { get; }
        public RdfDocument Ours { get; }
        public RdfDocument Theirs { get; }
        public RdfField Key { get; }
        public Dictionary<string, byte[]> B { get; }
        public Dictionary<string, byte[]> O { get; }
        public Dictionary<string, byte[]> T { get; }
        public Inputs(string b, string o, string t)
        {
            try
            {
                long size = 0;
                foreach (string path in new[] { b, o, t }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ContentSafety.NoLinks(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(stream); size += stream.Length;
                }
                if (size > 512L * 1024 * 1024) throw new IOException("Three RDF inputs exceed 512 MiB combined.");
                (Base, Key) = RdfCli.Open(b);
                (Ours, _) = RdfCli.Open(o);
                (Theirs, _) = RdfCli.Open(t);
                if (new[] { Base, Ours, Theirs }.Any(d => d.Margin != 1 || Fingerprint(d.Schema) != Fingerprint(Base.Schema)))
                    throw new InvalidDataException("Inputs require the same registered Taiwan schema and margin 1.");
                foreach (var doc in new[] { Base, Ours, Theirs }) ValidateReconcileRows(doc, Key);
                B = Rows(Base); O = Rows(Ours); T = Rows(Theirs);
            }
            catch { Dispose(); throw; }
        }
        private Dictionary<string, byte[]> Rows(RdfDocument doc)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var row in doc.Rows)
            {
                string id = doc.Schema.Decode(Key, row.Current);
                if (id == "0" || id == "4294967295" && !IsPinnedLegacySentinel(doc.Schema, row.Current) || !result.TryAdd(id, row.Current))
                    throw new InvalidDataException("Invalid or duplicate RDF ID: " + id);
            }
            return result;
        }
        public void Dispose() { foreach (var stream in locks) stream.Dispose(); }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static readonly IReadOnlyDictionary<string, string> LegacySentinelRowHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QuestItem"] = "AB52E6CC530D98D6D251796A899884A4A54202BB3D30D6CE27A1F24FAE92D43A",
            ["ItemEnchantData"] = "0881C5B766F00781285BC7AEB18F465DEF395BD1DA29A139243F53F02A8429DB"
        };
    private static bool IsPinnedLegacySentinel(RdfSchema schema, byte[] row) =>
        LegacySentinelRowHashes.TryGetValue(schema.Name, out string? expected) && Hash(row) == expected;
    private static void ValidateReconcileRows(RdfDocument doc, RdfField key)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.Rows)
        {
            string id = doc.Schema.Decode(key, row.Current);
            if (id == "0" || id == "4294967295" && !IsPinnedLegacySentinel(doc.Schema, row.Current) || !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate RDF ID: " + id);
            if (doc.Schema.Name == "CharTitle" && (!uint.TryParse(id, out uint titleId) || titleId > 512))
                throw new InvalidDataException("CharTitle ID must fit Taiwan ownership bits: 1..512.");
            foreach (var field in doc.Schema.Fields.Where(f => f.IsPadding))
                if (!row.Current.AsSpan(field.Offset, field.Size).SequenceEqual(row.Original.AsSpan(field.Offset, field.Size)))
                    throw new InvalidDataException("Reserved RDF padding was edited in a reconciliation input.");
        }
    }
    private static string Fingerprint(RdfSchema schema) => Hash(Encoding.UTF8.GetBytes(
        schema.Name + ":" + schema.RowSize + ":" + string.Join(';', schema.Fields.Select(f => $"{f.Name}:{f.Type}:{f.Count}:{f.Offset}"))));
    private static RdfRevision Revision(RdfDocument doc) => new(Path.GetFullPath(doc.Path), doc.OriginalHash);
    private static bool Equal(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    private static string Status(byte[]? b, byte[]? o, byte[]? t) => Equal(o, t) ? "same_change" :
        Equal(o, b) ? "theirs_only" : Equal(t, b) ? "ours_only" : "conflict";
    private static byte[] Slice(byte[] row, RdfField field) => row.AsSpan(field.Offset, field.Size).ToArray();

    private static RdfComparison Analyze(Inputs inputs)
    {
        var changes = new List<RdfDifference>();
        var schema = inputs.Base.Schema;
        foreach (string id in inputs.B.Keys.Concat(inputs.O.Keys).Concat(inputs.T.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            inputs.B.TryGetValue(id, out var b); inputs.O.TryGetValue(id, out var o); inputs.T.TryGetValue(id, out var t);
            if (Equal(b, o) && Equal(b, t)) continue;
            if (b == null || o == null || t == null)
            {
                bool reserved = b != null && schema.Fields.Where(f => f.IsPadding).Any(f =>
                    o != null && !Equal(Slice(b, f), Slice(o, f)) || t != null && !Equal(Slice(b, f), Slice(t, f)));
                changes.Add(new(id, "$row", Status(b, o, t), b == null ? null : Hash(b), o == null ? null : Hash(o), t == null ? null : Hash(t), reserved));
                // A row-existence conflict still has one $row resolution, but
                // each changed padding slice needs its own explicit byte-preservation record.
                // A row absent from both base and ours is a genuinely new
                // incoming ID, so its padding is source content to retain.
                foreach (var field in schema.Fields.Where(f => f.IsPadding && (b != null || o != null)))
                {
                    byte[]? bs = b == null ? null : Slice(b, field);
                    byte[]? os = o == null ? null : Slice(o, field);
                    byte[]? ts = t == null ? null : Slice(t, field);
                    var presentSlices = new[] { bs, os, ts }.Where(x => x != null).Cast<byte[]>().ToArray();
                    if (presentSlices.Length < 2 || presentSlices.Skip(1).All(x => Equal(presentSlices[0], x))) continue;
                    changes.Add(new(id, field.Name, Status(bs, os, ts),
                        b == null ? null : schema.Decode(field, b),
                        o == null ? null : schema.Decode(field, o),
                        t == null ? null : schema.Decode(field, t), true));
                }
                continue;
            }
            foreach (var field in schema.Fields)
            {
                var bs = Slice(b, field); var os = Slice(o, field); var ts = Slice(t, field);
                if (Equal(bs, os) && Equal(bs, ts)) continue;
                // Compare raw bytes, not normalized strings/floats. Padding is never a semantic field merge.
                changes.Add(new(id, field.Name, Status(bs, os, ts),
                    schema.Decode(field, b), schema.Decode(field, o), schema.Decode(field, t), field.IsPadding));
            }
        }
        var paddingDifferences = changes.Where(c => c.Reserved && c.Field != "$row").Select(c =>
        {
            var field = schema.Fields.Single(f => f.Name == c.Field && f.IsPadding);
            inputs.B.TryGetValue(c.Id, out var b); inputs.O.TryGetValue(c.Id, out var o); inputs.T.TryGetValue(c.Id, out var t);
            return new RdfPaddingDifference(c.Id, field.Name, field.Offset, field.Size,
                PaddingHash(b, field), PaddingHash(o, field), PaddingHash(t, field));
        }).ToArray();
        var sentinelRows = new[] { inputs.B, inputs.O, inputs.T }.SelectMany(rows => rows.TryGetValue("4294967295", out var row) ? new[] { row } : Array.Empty<byte[]>()).ToArray();
        var sentinelPreservations = sentinelRows.Length == 0 ? Array.Empty<RdfLegacySentinelPreservation>() : new[]
        {
            new RdfLegacySentinelPreservation("4294967295", schema.Name, Fingerprint(schema), Hash(sentinelRows[0]), "preserve-existing")
        };
        if (sentinelRows.Skip(1).Any(row => !Equal(sentinelRows[0], row)))
            throw new InvalidDataException("Legacy sentinel row differs between inputs; explicit preservation is not permitted.");
        return new(schema.Name, Fingerprint(schema), Revision(inputs.Base), Revision(inputs.Ours), Revision(inputs.Theirs), changes, paddingDifferences, sentinelPreservations);
    }

    public static RdfComparison Compare(string b, string o, string t)
    {
        using var inputs = new Inputs(b, o, t);
        return Analyze(inputs);
    }

    private static string? PaddingHash(byte[]? row, RdfField field) => row == null ? null : Hash(Slice(row, field));
    private static bool SameOptionalHash(string? actual, string? expected) =>
        actual == null ? expected == null : expected != null && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void ValidateDecisions(Inputs inputs, RdfComparison comparison,
        IReadOnlyList<RdfResolution> decisions, IReadOnlyList<RdfPaddingPreservation>? paddingPreservations,
        IReadOnlyList<RdfLegacySentinelPreservation>? legacySentinelPreservations)
    {
        var paddingChanges = comparison.PaddingDifferences;
        // Preserve the existing fail-closed default. The opt-in is narrow and
        // can only retain ours' raw bytes; it cannot select another branch.
        if (paddingChanges.Count != 0 && paddingPreservations == null)
            throw new InvalidDataException("Reserved padding changed; manual schema investigation required. Explicit paddingPreservations are required to preserve ours bytes.");
        paddingPreservations ??= Array.Empty<RdfPaddingPreservation>();
        var wantedPadding = paddingChanges.Select(c => (c.Id, c.Field)).ToHashSet();
        var seenPadding = new HashSet<(string, string)>();
        foreach (var decision in paddingPreservations)
        {
            if (!wantedPadding.Contains((decision.Id, decision.Field)) || !seenPadding.Add((decision.Id, decision.Field)) ||
                decision.Choice != "ours-preserve")
                throw new InvalidDataException("Padding preservation must enumerate each changed registered padding field exactly once and choose ours-preserve.");
            var field = inputs.Base.Schema.Fields.SingleOrDefault(f => f.Name == decision.Field && f.IsPadding)
                ?? throw new InvalidDataException("Padding decision names a field that is not registered reserved padding.");
            if (decision.Offset != field.Offset || decision.Size != field.Size)
                throw new InvalidDataException("Padding decision offset/size does not match the registered Taiwan schema.");
            inputs.B.TryGetValue(decision.Id, out var b);
            inputs.O.TryGetValue(decision.Id, out var o);
            inputs.T.TryGetValue(decision.Id, out var t);
            if (!SameOptionalHash(PaddingHash(b, field), decision.BaseSha256) ||
                !SameOptionalHash(PaddingHash(o, field), decision.OursSha256) ||
                !SameOptionalHash(PaddingHash(t, field), decision.TheirsSha256))
                throw new InvalidDataException("Padding preservation source hashes do not match the pinned raw field bytes.");
        }
        if (!wantedPadding.SetEquals(seenPadding))
            throw new InvalidDataException("Every changed reserved padding field requires an explicit ours-preserve decision.");
        var requiredSentinels = comparison.LegacySentinelPreservations.ToDictionary(x => x.Id, StringComparer.Ordinal);
        legacySentinelPreservations ??= Array.Empty<RdfLegacySentinelPreservation>();
        var seenSentinels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in legacySentinelPreservations)
        {
            if (!requiredSentinels.TryGetValue(decision.Id, out var expected) || !seenSentinels.Add(decision.Id) ||
                decision.Schema != expected.Schema || decision.SchemaSha256 != expected.SchemaSha256 ||
                !decision.RowSha256.Equals(expected.RowSha256, StringComparison.OrdinalIgnoreCase) ||
                decision.Choice != "preserve-existing")
                throw new InvalidDataException("Legacy sentinel preservation must exactly pin the registered schema, ID and unchanged raw row hash.");
        }
        if (!requiredSentinels.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(seenSentinels))
            throw new InvalidDataException("Every registered legacy sentinel row requires an explicit preserve-existing decision.");
        var wanted = comparison.Changes.Where(c => c.Status == "conflict" && (c.Field == "$row" || !c.Reserved))
            .Select(c => (c.Id, c.Field)).ToHashSet();
        var seen = new HashSet<(string, string)>();
        foreach (var decision in decisions)
            if (decision.Choice is not ("ours" or "theirs") || !wanted.Contains((decision.Id, decision.Field)) || !seen.Add((decision.Id, decision.Field)))
                throw new InvalidDataException("Resolution must name one actual conflict exactly once and choose ours/theirs.");
        if (!wanted.SetEquals(seen)) throw new InvalidDataException("Unresolved RDF conflicts. Resolve every conflict before applying.");
    }

    public static object Plan(string b, string o, string t, string decisionsPath, string output)
    {
        using var inputs = new Inputs(b, o, t);
        var comparison = Analyze(inputs);
        ContentSafety.NoLinks(decisionsPath);
        if (new FileInfo(decisionsPath).Length > MaxDecisionOrPlanBytes) throw new IOException("Decisions exceed 32 MiB.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(decisionsPath));
        TerrainGeometryService.NoDuplicateKeys(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            json.RootElement.EnumerateObject().Any(p => p.Name is not ("resolutions" or "paddingPreservations" or "legacySentinelPreservations")))
            throw new InvalidDataException("Decisions must contain resolutions and optional paddingPreservations/legacySentinelPreservations.");
        var resolutionElement = json.RootElement.GetProperty("resolutions");
        if (resolutionElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("resolutions must be an array.");
        foreach (var resolutionElementItem in resolutionElement.EnumerateArray())
            if (resolutionElementItem.ValueKind != JsonValueKind.Object ||
                resolutionElementItem.EnumerateObject().Any(p => p.Name is not ("id" or "field" or "choice")) ||
                resolutionElementItem.EnumerateObject().Count() != 3)
                throw new InvalidDataException("Each resolution must contain exactly id, field and choice.");
        var decisions = resolutionElement.Deserialize<List<RdfResolution>>(ContentSafety.Json)
            ?? throw new InvalidDataException("Missing resolutions.");
        List<RdfPaddingPreservation>? paddingPreservations = null;
        if (json.RootElement.TryGetProperty("paddingPreservations", out var paddingElement))
        {
            if (paddingElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("paddingPreservations must be an array.");
            foreach (var paddingElementItem in paddingElement.EnumerateArray())
                if (paddingElementItem.ValueKind != JsonValueKind.Object ||
                    paddingElementItem.EnumerateObject().Any(p => p.Name is not ("id" or "field" or "offset" or "size" or "baseSha256" or "oursSha256" or "theirsSha256" or "choice")) ||
                    paddingElementItem.EnumerateObject().Count() != 8)
                    throw new InvalidDataException("Each paddingPreservation must contain exactly id, field, offset, size, baseSha256, oursSha256, theirsSha256 and choice.");
            paddingPreservations = paddingElement.Deserialize<List<RdfPaddingPreservation>>(ContentSafety.Json)
                ?? throw new InvalidDataException("Invalid paddingPreservations array.");
        }
        List<RdfLegacySentinelPreservation>? legacySentinelPreservations = null;
        if (json.RootElement.TryGetProperty("legacySentinelPreservations", out var sentinelElement))
        {
            if (sentinelElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("legacySentinelPreservations must be an array.");
            foreach (var item in sentinelElement.EnumerateArray())
                if (item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Any(p => p.Name is not ("id" or "schema" or "schemaSha256" or "rowSha256" or "choice")) ||
                    item.EnumerateObject().Count() != 5)
                    throw new InvalidDataException("Each legacySentinelPreservation must contain exactly id, schema, schemaSha256, rowSha256 and choice.");
            legacySentinelPreservations = sentinelElement.Deserialize<List<RdfLegacySentinelPreservation>>(ContentSafety.Json)
                ?? throw new InvalidDataException("Invalid legacySentinelPreservations array.");
        }
        ValidateDecisions(inputs, comparison, decisions, paddingPreservations, legacySentinelPreservations);
        var plan = new RdfReconcilePlan("dbo-taiwan-rdf-reconcile", 1, comparison.SchemaSha256,
            comparison.Base, comparison.Ours, comparison.Theirs, decisions, paddingPreservations, legacySentinelPreservations);
        Candidate(inputs, plan, comparison); // Reject ID loss before sealing a plan.
        ContentSafety.WriteJson(output, plan);
        return new { plan = Path.GetFullPath(output), planSha256 = Hash(File.ReadAllBytes(output)), comparison };
    }

    private static (RdfReconcilePlan Plan, byte[] Bytes) ReadPlan(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > MaxDecisionOrPlanBytes) throw new IOException("Plan exceeds 32 MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        var plan = JsonSerializer.Deserialize<RdfReconcilePlan>(bytes, ContentSafety.Json)
            ?? throw new InvalidDataException("Empty reconcile plan.");
        if (plan.Format != "dbo-taiwan-rdf-reconcile" || plan.Version != 1 || plan.Resolutions == null)
            throw new InvalidDataException("Unknown reconcile plan.");
        return (plan, bytes);
    }

    private static RdfComparison Verify(Inputs inputs, RdfReconcilePlan plan)
    {
        var comparison = Analyze(inputs);
        if (comparison.Base != plan.Base || comparison.Ours != plan.Ours || comparison.Theirs != plan.Theirs || comparison.SchemaSha256 != plan.SchemaSha256)
            throw new IOException("An RDF input or its schema changed since plan creation.");
        ValidateDecisions(inputs, comparison, plan.Resolutions, plan.PaddingPreservations, plan.LegacySentinelPreservations);
        return comparison;
    }

    private static byte[] Candidate(Inputs inputs, RdfReconcilePlan plan, RdfComparison comparison)
    {
        var decisions = plan.Resolutions.ToDictionary(d => (d.Id, d.Field), d => d.Choice);
        var changes = comparison.Changes.ToLookup(c => c.Id);
        using var output = new MemoryStream(); output.WriteByte(1);
        // Preserve ours' ordering, append incoming IDs in their original ordering.
        var ids = inputs.Ours.Rows.Select(r => inputs.Ours.Schema.Decode(inputs.Key, r.Current))
            .Concat(inputs.Theirs.Rows.Select(r => inputs.Theirs.Schema.Decode(inputs.Key, r.Current)))
            .Concat(inputs.B.Keys).Distinct();
        foreach (string id in ids)
        {
            inputs.B.TryGetValue(id, out var b); inputs.O.TryGetValue(id, out var o); inputs.T.TryGetValue(id, out var t);
            var rowChange = changes[id].FirstOrDefault(c => c.Field == "$row");
            byte[]? result;
            if (rowChange != null)
            {
                string choice = rowChange.Status == "conflict" ? decisions[(id, "$row")] : rowChange.Status == "theirs_only" ? "theirs" : "ours";
                result = choice == "ours" ? o : t;
                if (result == null && rowChange.Status == "conflict")
                    throw new InvalidDataException("Additive preservation: explicit $row resolution selects absence for RDF ID " + id + ". Restore the row from a verified input; no deletion choice is allowed.");
                if (result == null && rowChange.Status == "same_change")
                    throw new InvalidDataException("Additive preservation: both branches selected absence for existing RDF ID " + id + ". Restore the row from a verified input; no deletion choice is allowed.");
                if (result == null)
                    result = o ?? t ?? b; // A unilateral omission cannot erase a known ID.
            }
            else
            {
                result = o == null ? null : (byte[])o.Clone();
                foreach (var change in changes[id])
                {
                    var field = inputs.Ours.Schema.Fields.Single(f => f.Name == change.Field);
                    if (field.IsPadding) continue; // Explicit preservation mode below owns reserved bytes.
                    string choice = change.Status == "conflict" ? decisions[(id, change.Field)] : change.Status == "theirs_only" ? "theirs" : "ours";
                    if (choice == "theirs")
                    {
                        t!.AsSpan(field.Offset, field.Size).CopyTo(result!.AsSpan(field.Offset, field.Size));
                    }
                }
            }
            if (result != null)
            {
                // Existing rows keep ours' raw padding. A base row restored
                // after ours omitted it uses base padding. Newly added IDs
                // retain their incoming/template padding.
                byte[] paddingSource = o ?? b ?? result;
                foreach (var field in inputs.Ours.Schema.Fields.Where(f => f.IsPadding))
                    paddingSource.AsSpan(field.Offset, field.Size).CopyTo(result.AsSpan(field.Offset, field.Size));
            }
            if (result == null)
                throw new InvalidDataException("Additive preservation: reconciliation would delete RDF ID " + id + ". Restore the row from a verified input; no deletion choice is allowed.");
            output.Write(result);
        }
        if (output.Length > 512L * 1024 * 1024) throw new IOException("Candidate exceeds 512 MiB.");
        return output.ToArray();
    }

    public static object Preview(string planPath)
    {
        var (plan, _) = ReadPlan(planPath);
        using var inputs = new Inputs(plan.Base.Path, plan.Ours.Path, plan.Theirs.Path);
        var comparison = Verify(inputs, plan);
        byte[] candidate = Candidate(inputs, plan, comparison);
        return new { comparison, outputSha256 = Hash(candidate), bytes = candidate.Length,
            rowCount = (candidate.Length - 1) / inputs.Ours.Schema.RowSize, worldTested = false };
    }

    public static object Apply(string planPath, string destination)
    {
        var (plan, planBytes) = ReadPlan(planPath);
        using var inputs = new Inputs(plan.Base.Path, plan.Ours.Path, plan.Theirs.Path);
        var comparison = Verify(inputs, plan);
        byte[] candidate = Candidate(inputs, plan, comparison);
        string target = ContentSafety.SafeOutput(destination);
        foreach (var revision in new[] { plan.Base, plan.Ours, plan.Theirs })
            if (revision.Path.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Candidate folder cannot contain an input.");
        string pending = ContentSafety.FreshDirectory(target);
        try
        {
            string name = Path.GetFileName(inputs.Ours.Path);
            File.WriteAllBytes(Path.Combine(pending, name), candidate);
            foreach (var pair in new[] { ("base", inputs.Base), ("ours", inputs.Ours), ("theirs", inputs.Theirs) })
            {
                string folder = Path.Combine(pending, "inputs", pair.Item1); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, Path.GetFileName(pair.Item2.Path)), pair.Item2.OriginalBytes);
            }
            var readback = RdfDocument.Open(Path.Combine(pending, name), inputs.Ours.Schema);
            if (readback.OriginalHash != Hash(candidate)) throw new IOException("Candidate readback hash differs.");
            // Include the reviewed comparison and exact selected conflict decisions.
            ContentSafety.WriteJson(Path.Combine(pending, "audit.json"), new {
                version = 1, kind = "rdf-reconcile", utc = DateTime.UtcNow, schema = comparison.Schema,
                planSha256 = Hash(planBytes), sourceSha256 = inputs.Ours.OriginalHash,
                outputSha256 = Hash(candidate), comparison, resolutions = plan.Resolutions,
                paddingPreservations = plan.PaddingPreservations ?? Array.Empty<RdfPaddingPreservation>(),
                legacySentinelPreservations = plan.LegacySentinelPreservations ?? Array.Empty<RdfLegacySentinelPreservation>(),
                dependencies = new[] { comparison.Base, comparison.Ours, comparison.Theirs }, worldTested = false
            });
            Directory.Move(pending, target);
            return new { output = Path.Combine(target, name), outputSha256 = Hash(candidate),
                audit = Path.Combine(target, "audit.json"), worldTested = false };
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }
}
