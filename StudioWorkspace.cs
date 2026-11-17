using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal sealed record WorkspaceFile(string Path, long Bytes, string? Sha256, long ModifiedTicks = 0);
internal sealed record WorkspaceRevision(string Id, string? Parent, string Message, DateTimeOffset Created, List<WorkspaceFile> Files, Dictionary<string, WorkspacePak> Packages, List<string>? Removals = null);
internal sealed record WorkspacePak(string Family, string? BaseIndexSha256, string Mode, List<WorkspaceFile> RequiredUnits, List<WorkspaceFile> Outputs, List<PakChange> Operations, List<string> Dependencies, string Reason);
internal sealed class WorkspaceConfig
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Client { get; set; }
    public string? Server { get; set; }
    public string? Head { get; set; }
    public List<WorkspaceFile> Baseline { get; set; } = [];
    public Dictionary<string, WorkspacePak> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Local content-addressed history. Every write is confined to the project; the
// client/server bindings are read-only and are never a deployment destination.
internal sealed class StudioWorkspace
{
    public string Root { get; }
    public string Work => Path.Combine(Root, "work");
    private string Meta => Path.Combine(Root, ".dbo-assets");
    private string ConfigPath => Path.Combine(Meta, "project.json");
    private readonly AsyncLocal<FileStream?> _heldLock = new();
    private StudioWorkspace(string root) => Root = ContentSafety.SafeOutput(root);
    private WorkspaceConfig Read() => ReadJson<WorkspaceConfig>(ConfigPath);
    private static T ReadJson<T>(string path)
    {
        ContentSafety.NoLinks(path);
        if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new IOException("Project metadata exceeds 32 MiB.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContentSafety.Json) ?? throw new InvalidDataException("Empty metadata.");
    }
    private static void WriteAtomic(string path, object value)
    {
        path = ContentSafety.SafeOutput(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        ContentSafety.WriteJson(temp, value); File.Move(temp, path, true);
    }
    private sealed class Guard(Action release) : IDisposable { public void Dispose() => release(); }
    private IDisposable Lock()
    {
        if (_heldLock.Value != null) return new Guard(() => { });
        string path = Path.Combine(Meta, "write.lock"); ContentSafety.NoLinks(path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); _heldLock.Value = stream;
        return new Guard(() => { _heldLock.Value = null; stream.Dispose(); });
    }
    public static StudioWorkspace Open(string root)
    {
        var project = new StudioWorkspace(Path.GetFullPath(root)); var config = project.Read();
        if (config.Version != 1 || !Directory.Exists(project.Work)) throw new InvalidDataException("Not a compatible project.");
        foreach (var row in config.Baseline) Target(row.Path);
        return project;
    }
    public static StudioWorkspace Create(string root, string name, string? client = null, string? server = null)
    {
        var project = new StudioWorkspace(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Project name must be 1–100 characters.");
        if (Directory.Exists(project.Root) || File.Exists(project.Root)) throw new IOException("The project needs a new folder.");
        var config = new WorkspaceConfig { Name = name };
        foreach (var binding in new[] { (client, "client"), (server, "server") })
        {
            if (string.IsNullOrWhiteSpace(binding.Item1)) continue;
            string source = Path.GetFullPath(binding.Item1); ContentSafety.NoLinks(source);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            if (Inside(project.Root, source) || Inside(source, project.Root)) throw new IOException("Project and base must be in separate folders.");
            if (binding.Item2 == "client") config.Client = source; else config.Server = source;
            string[] scopes = binding.Item2 == "client" ? ["pack"] : ["resource/server_data/table/rdf", "resource/property", "resource/server_data/wps/wps"];
            foreach (string scope in scopes)
            {
                string directory = Path.Combine(source, scope);
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    ContentSafety.NoLinks(file); string rel = binding.Item2 + "/" + Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (!Allowed(rel)) continue;
                    var info = new FileInfo(file);
                    // Baseline units can be 18+ GB: capture metadata, hash indices
                    // and server files. Canonical overlay receipts pin used units.
                    bool unit = Regex.IsMatch(Path.GetFileName(file), @"\d+\.pak$", RegexOptions.IgnoreCase);
                    config.Baseline.Add(new(rel, info.Length, unit ? null : ContentSafety.Hash(file), info.LastWriteTimeUtc.Ticks));
                }
            }
        }
        if (config.Client != null && !config.Baseline.Any(f => f.Path.StartsWith("client/pack/") && f.Sha256 != null)) throw new IOException("The client base must be the client root and contain pack/*.pak indexes.");
        Directory.CreateDirectory(project.Meta); Directory.CreateDirectory(project.Work);
        foreach (string dir in new[] { "objects", "revisions", "receipts" }) Directory.CreateDirectory(Path.Combine(project.Meta, dir));
        Directory.CreateDirectory(Path.Combine(project.Work, "client", "pack"));
        Directory.CreateDirectory(Path.Combine(project.Work, "server", "resource", "server_data", "table", "rdf"));
        Directory.CreateDirectory(Path.Combine(project.Work, "server", "resource", "server_data", "wps", "wps"));
        WriteAtomic(project.ConfigPath, config); return project;
    }
    private static bool Inside(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Target(string path)
    {
        string rel = ContentSafety.ResourcePath(path).Replace('\\', '/');
        if (!Allowed(rel)) throw new InvalidDataException("Destination not allowed. Use client/pack/*.pak, server/resource/.../*.rdf or *.wps: " + rel);
        return rel;
    }
    private static bool Allowed(string path) => Regex.IsMatch(path, @"^client/pack/[a-z][a-z_]*\d*\.pak$", RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, @"^server/resource/server_data/table/rdf/[a-z0-9_./-]+\.rdf$", RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, @"^server/resource/property/[a-z0-9_./-]+\.rdf$", RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, @"^server/resource/server_data/wps/wps/[a-z0-9_./-]+\.wps$", RegexOptions.IgnoreCase);
    private string Local(string rel) { string path = Path.Combine(Work, Target(rel)); ContentSafety.NoLinks(path); return path; }
    private string Blob(string hash)
    {
        if (!Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("Invalid object hash.");
        string path = Path.Combine(Meta, "objects", hash.ToUpperInvariant()); ContentSafety.NoLinks(path); return path;
    }
    private WorkspaceFile Store(string file, string rel)
    {
        ContentSafety.NoLinks(file); rel = Target(rel);
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(source)); string blob = Blob(hash);
        if (!File.Exists(blob))
        {
            source.Position = 0; string temp = blob + "." + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(output); output.Flush(true); }
            if (ContentSafety.Hash(temp) != hash) throw new IOException("The object does not match the source.");
            File.Move(temp, blob, true);
        }
        else if (ContentSafety.Hash(blob) != hash) throw new IOException("Damaged history object: " + hash);
        return new(rel, source.Length, hash);
    }
    private List<WorkspaceFile> Scan()
    {
        ContentSafety.NoLinks(Work); var files = new List<WorkspaceFile>();
        foreach (string path in Directory.EnumerateFiles(Work, "*", SearchOption.AllDirectories))
        {
            string rel = Target(Path.GetRelativePath(Work, path)); ContentSafety.NoLinks(path);
            var info = new FileInfo(path); files.Add(new(rel, info.Length, ContentSafety.Hash(path)));
        }
        if (files.Count > 20000) throw new IOException("The project exceeds 20,000 files.");
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
    private WorkspaceRevision? Head(WorkspaceConfig config) => config.Head == null ? null : Revision(config.Head);
    private WorkspaceRevision Revision(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new ArgumentException("Invalid version ID.");
        return ReadJson<WorkspaceRevision>(Path.Combine(Meta, "revisions", id + ".json"));
    }
    private static object[] Diff(List<WorkspaceFile> current, List<WorkspaceFile> previous)
    {
        var now = current.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase); var old = previous.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return now.Keys.Union(old.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            old.TryGetValue(path, out var before); now.TryGetValue(path, out var after);
            return before?.Sha256 == after?.Sha256 && before?.Bytes == after?.Bytes ? null : (object)new { path, change = before == null ? "add" : after == null ? "remove" : "modify", before = before?.Sha256, after = after?.Sha256, bytes = after?.Bytes ?? 0 };
        }).OfType<object>().ToArray();
    }
    public object Status()
    {
        using var guard = Lock();
        var config = Read(); var files = Scan(); var head = Head(config);
        return new { loaded = true, root = Root, work = Work, config.Name, config.Client, config.Server, config.Head, files = files.Count,
            changes = Diff(files, head?.Files ?? []), history = Directory.EnumerateFiles(Path.Combine(Meta, "revisions"), "*.json").Select(ReadJson<WorkspaceRevision>).OrderByDescending(r => r.Created).Select(r => new { r.Id, r.Parent, r.Message, r.Created, files = r.Files.Count }).ToArray() };
    }
    private void CheckBaseline(WorkspaceConfig config, string rel)
    {
        var initial = config.Baseline.SingleOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
        string? binding = rel.StartsWith("client/") ? config.Client : config.Server;
        if (binding == null) return;
        string source = Path.Combine(binding, rel[(rel.IndexOf('/') + 1)..]); ContentSafety.NoLinks(source);
        if (initial == null) { if (File.Exists(source)) throw new IOException("The base gained a file outside the snapshot: " + rel); return; }
        if (!File.Exists(source)) throw new IOException("Base file missing: " + rel);
        var info = new FileInfo(source);
        if (info.Length != initial.Bytes || (initial.Sha256 != null ? ContentSafety.Hash(source) != initial.Sha256 : info.LastWriteTimeUtc.Ticks != initial.ModifiedTicks)) throw new IOException("The base changed since the project was created: " + rel);
    }
    public object Adopt(string file, string destination, bool replace = false, string? expectedSource = null)
    {
        destination = Target(destination);
        if (destination.StartsWith("client/")) throw new IOException("PAK requires Import candidate to register the index, units and dependencies together.");
        using var guard = Lock(); var config = Read(); CheckBaseline(config, destination);
        var baseFile = config.Baseline.FirstOrDefault(f => f.Path.Equals(destination, StringComparison.OrdinalIgnoreCase));
        if (expectedSource != null && (baseFile != null || File.Exists(Local(destination))) && expectedSource != (File.Exists(Local(destination)) ? ContentSafety.Hash(Local(destination)) : baseFile?.Sha256)) throw new IOException("The revision does not derive from the base or the current copy: " + destination);
        WorkspaceContentValidation.Validate(file, destination);
        var stored = Store(file, destination); WriteWork(stored, replace);
        return new { destination, stored.Sha256, stored.Bytes, project = Root };
    }
    private void WriteWork(WorkspaceFile file, bool replace)
    {
        string path = Local(file.Path);
        if (File.Exists(path) && !replace && ContentSafety.Hash(path) != file.Sha256) throw new IOException("Conflict in " + file.Path + ". Review it and choose to replace explicitly.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(Blob(file.Sha256!), temp); File.Move(temp, path, true);
    }
    public object Commit(string message)
    {
        using var guard = Lock(); return CommitLocked(Read(), message);
    }
    private WorkspaceRevision CommitLocked(WorkspaceConfig config, string message, List<string>? restoredRemovals = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500) throw new ArgumentException("Write a version summary (1–500 characters).");
        var files = Scan(); foreach (var file in files) { var stored = Store(Local(file.Path), file.Path); if (stored.Sha256 != file.Sha256 || stored.Bytes != file.Bytes) throw new IOException("File changed while the version was being saved: " + file.Path); }
        if (Head(config) is { } head && Diff(files, head.Files).Length == 0 && JsonSerializer.Serialize(config.Packages, ContentSafety.Json) == JsonSerializer.Serialize(head.Packages, ContentSafety.Json) && (restoredRemovals == null || restoredRemovals.Order().SequenceEqual((head.Removals ?? []).Order()))) throw new IOException("No changes since the last version.");
        var previous = Head(config);
        var removals = (previous?.Removals ?? []).Concat((previous?.Files ?? []).Where(f => f.Path.StartsWith("server/") && !files.Any(n => n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))).Select(f => f.Path))
            .Where(path => config.Baseline.Any(b => b.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) && !files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var revision = new WorkspaceRevision(Guid.NewGuid().ToString("N"), config.Head, message, DateTimeOffset.Now, files, config.Packages, restoredRemovals ?? removals);
        ContentSafety.WriteJson(Path.Combine(Meta, "revisions", revision.Id + ".json"), revision);
        config.Head = revision.Id; WriteAtomic(ConfigPath, config); return revision;
    }
    public object Restore(string id)
    {
        using var guard = Lock(); var config = Read(); var revision = Revision(id); var current = Scan();
        foreach (var file in revision.Files) { Target(file.Path); if (ContentSafety.Hash(Blob(file.Sha256!)) != file.Sha256) throw new IOException("Damaged object in version: " + file.Path); }
        if (Diff(current, Head(config)?.Files ?? []).Length != 0) CommitLocked(config, "Automatic backup before restoring " + id);
        foreach (var file in current.Where(f => !revision.Files.Any(r => r.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)))) File.Delete(Local(file.Path));
        foreach (var file in revision.Files) WriteWork(file, true);
        config.Packages = revision.Packages;
        if (Diff(Scan(), Head(config)?.Files ?? []).Length == 0 && JsonSerializer.Serialize(config.Packages, ContentSafety.Json) == JsonSerializer.Serialize(Head(config)?.Packages, ContentSafety.Json) && (revision.Removals ?? []).Order().SequenceEqual((Head(config)?.Removals ?? []).Order())) return new { restored = id, unchanged = true };
        return CommitLocked(config, "Restore version " + id, revision.Removals ?? []);
    }
    public async Task<object> ImportPakAsync(string candidate, bool replace = false)
    {
        using var guard = Lock(); var config = Read(); candidate = Path.GetFullPath(candidate); ContentSafety.NoLinks(candidate);
        ContentPakService.VerifyCandidate(candidate);
        var incoming = ContentPakService.ReadPlan(Path.Combine(candidate, "plan.json"));
        if (incoming.Format != "taiwan") throw new IOException("The project accepts adapted Taiwan candidates, not container conversions without semantics.");
        string indexRel = Target("client/pack/" + incoming.Prefix + ".pak"); CheckBaseline(config, indexRel);
        var baseline = config.Baseline.FirstOrDefault(f => f.Path.Equals(indexRel, StringComparison.OrdinalIgnoreCase));
        if (incoming.Source != null && baseline?.Sha256 != incoming.SourceSha256) throw new IOException("The PAK candidate derives from another base. Redo it on the registered base; do not overlay incompatible indexes.");
        if (incoming.Source == null && baseline != null) throw new IOException("A new package cannot replace a base family without a plan on that base.");
        config.Packages.TryGetValue(incoming.Prefix, out var previous);
        var combined = previous?.Operations.ToDictionary(o => ContentSafety.ResourcePath(o.Path), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, PakChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in incoming.Operations)
        {
            string resource = ContentSafety.ResourcePath(op.Path);
            if (combined.TryGetValue(resource, out var old) && (old.Op != op.Op || old.Sha256 != op.Sha256) && !replace) throw new IOException("Resource conflict " + resource + "; review the cumulative revision and enable explicit replace.");
            string? blob = null;
            if (op.File != null)
            {
                // Resource payloads live in CAS, but aren't independent patch files.
                ContentSafety.NoLinks(op.File); string hash = ContentSafety.Hash(op.File);
                if (hash != op.Sha256) throw new IOException("The plan payload changed: " + resource);
                blob = Blob(hash); if (!File.Exists(blob)) File.Copy(op.File, blob); if (ContentSafety.Hash(blob) != hash) throw new IOException("Payload CAS distinto.");
            }
            string verb = op.Op;
            if (old?.Op == "add" && op.Op == "remove") { combined.Remove(resource); continue; }
            if (old?.Op == "add" && op.Op == "replace") verb = "add";
            if (old?.Op == "remove" && op.Op == "add") verb = "replace";
            combined[resource] = op with { Op = verb, Path = resource, File = blob };
        }
        string final = candidate;
        if (previous != null)
        {
            incoming.Operations = combined.Values.ToList(); incoming.Source = incoming.SourceSha256 == null ? null : config.Client == null ? incoming.Source : Path.Combine(config.Client, "pack", incoming.Prefix + ".pak");
            incoming.Mode = incoming.Mode == "repack" || previous.Mode == "repack" ? "repack" : "overlay"; incoming.Dependencies = previous.Dependencies.Union(incoming.Dependencies).ToList();
            final = Path.Combine(Meta, "receipts", "pak-" + Guid.NewGuid().ToString("N")); await ContentPakService.ApplyAsync(incoming, final);
        }
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(final, "audit.json"))); var root = audit.RootElement;
        var outputs = new List<WorkspaceFile>();
        foreach (var row in new[] { root.GetProperty("index") }.Concat(root.GetProperty("writtenUnits").EnumerateArray()))
            outputs.Add(Store(Path.Combine(final, row.GetProperty("file").GetString()!), "client/pack/" + row.GetProperty("file").GetString()));
        // Do not overwrite external GM edits while advancing a candidate family.
        foreach (var file in outputs)
        {
            if (!File.Exists(Local(file.Path))) continue;
            var known = previous?.Outputs.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)) ?? Head(config)?.Files.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (!replace && ContentSafety.Hash(Local(file.Path)) != file.Sha256 && (known == null || ContentSafety.Hash(Local(file.Path)) != known.Sha256)) throw new IOException("Pending external changes in " + file.Path + "; save a version or review the replace.");
        }
        foreach (var file in outputs)
        {
            CheckBaseline(config, file.Path);
            var baseUnit = config.Baseline.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (baseUnit != null && baseUnit.Sha256 == null && config.Client != null)
            {
                config.Baseline.Remove(baseUnit);
                config.Baseline.Add(baseUnit with { Sha256 = ContentSafety.Hash(Path.Combine(config.Client, file.Path[7..])) });
            }
        }
        foreach (var file in outputs) WriteWork(file, true);
        foreach (var obsolete in previous?.Outputs.Where(old => !outputs.Any(n => n.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase))) ?? [])
        {
            if (File.Exists(Local(obsolete.Path)) && ContentSafety.Hash(Local(obsolete.Path)) == obsolete.Sha256) File.Delete(Local(obsolete.Path));
            else if (File.Exists(Local(obsolete.Path))) throw new IOException("Obsolete unit has external changes: " + obsolete.Path);
        }
        var units = root.GetProperty("requiredOriginalUnits").EnumerateArray().Select(row => new WorkspaceFile("client/pack/" + row.GetProperty("file").GetString(), row.GetProperty("bytes").GetInt64(), row.GetProperty("sha256").GetString())).ToList();
        config.Packages[incoming.Prefix] = new(incoming.Prefix, incoming.SourceSha256, incoming.Mode, units, outputs, combined.Values.ToList(), incoming.Dependencies, incoming.Reason);
        WriteAtomic(ConfigPath, config);
        return new { family = incoming.Prefix, files = outputs.Select(f => f.Path), resources = combined.Count, cumulative = previous != null };
    }
    public object Export(string destination)
    {
        using var guard = Lock(); var config = Read(); var revision = Head(config) ?? throw new IOException("Save a version before exporting.");
        if (Diff(Scan(), revision.Files).Length != 0 || JsonSerializer.Serialize(config.Packages, ContentSafety.Json) != JsonSerializer.Serialize(revision.Packages, ContentSafety.Json)) throw new IOException("There are unversioned changes. Save a version for a reproducible delivery.");
        destination = ContentSafety.SafeOutput(destination);
        if (Inside(destination, Root) || File.Exists(destination)) throw new IOException("The ZIP must be new and outside the project.");
        foreach (var file in revision.Files) CheckBaseline(config, file.Path);
        foreach (var package in revision.Packages.Values)
        {
            string index = "client/pack/" + package.Family + ".pak";
            foreach (var output in package.Outputs)
                if (revision.Files.SingleOrDefault(f => f.Path.Equals(output.Path, StringComparison.OrdinalIgnoreCase))?.Sha256 != output.Sha256) throw new IOException("PAK/unit changed outside the canonical candidate: " + output.Path);
            foreach (var unit in package.RequiredUnits)
            {
                CheckBaseline(config, unit.Path);
                if (config.Client == null || ContentSafety.Hash(Path.Combine(config.Client, unit.Path[7..])) != unit.Sha256) throw new IOException("Overlay dependency differs: " + unit.Path);
            }
        }
        foreach (var file in revision.Files.Where(f => f.Path.StartsWith("client/")))
        {
            string family = Regex.Match(Path.GetFileName(file.Path), @"^([a-z_]+)", RegexOptions.IgnoreCase).Value;
            if (!revision.Packages.ContainsKey(family)) throw new IOException("External PAK without a canonical receipt: " + file.Path + ". Import its audited candidate.");
        }
        foreach (var file in revision.Files.Where(f => f.Path.StartsWith("server/"))) WorkspaceContentValidation.Validate(Blob(file.Sha256!), file.Path);
        var removals = (revision.Removals ?? []).Select(path => config.Baseline.Single(b => b.Path.Equals(Target(path), StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var removed in removals) CheckBaseline(config, removed.Path);
        var payload = revision.Files.Where(f => config.Baseline.FirstOrDefault(b => b.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))?.Sha256 != f.Sha256).ToList();
        var manifest = new { format = "assets-delivery-v1", product = "DBO Assets Studio Taiwan 2.0", project = config.Name, revision = revision.Id, revision.Parent, revision.Message, revision.Created,
            files = payload.Select(f => new { path = f.Path, f.Bytes, sha256 = f.Sha256, expectedBaseSha256 = config.Baseline.FirstOrDefault(b => b.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))?.Sha256 }),
            requiredOriginalUnits = revision.Packages.Values.SelectMany(p => p.RequiredUnits).DistinctBy(u => u.Path),
            packages = revision.Packages.Values.Select(p => new { p.Family, p.BaseIndexSha256, p.Mode, p.Dependencies, p.Reason, operations = p.Operations.Select(o => new { o.Op, o.Path, o.Sha256 }) }),
            removals = removals.Select(b => new { path = b.Path, expectedBaseSha256 = b.Sha256 }),
            status = "review_required_not_installed", compatibility = "Client/gameplay QA pending; no deployment or publication authorized by this ZIP" };
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); string temp = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var file in payload)
            {
                string blob = Blob(file.Sha256!); if (ContentSafety.Hash(blob) != file.Sha256) throw new IOException("Damaged history object: " + file.Path);
                zip.CreateEntryFromFile(blob, file.Path, CompressionLevel.Fastest);
            }
            ZipText(zip, "manifest.json", JsonSerializer.Serialize(manifest, ContentSafety.Json));
            ZipText(zip, "history.json", JsonSerializer.Serialize(Directory.EnumerateFiles(Path.Combine(Meta, "revisions"), "*.json").Select(ReadJson<WorkspaceRevision>).OrderBy(r => r.Created).Select(r => new { r.Id, r.Parent, r.Message, r.Created }), ContentSafety.Json));
            ZipText(zip, "INTEGRATION.md", "# DBO Assets Studio delivery\n\nClient: client/pack/*.pak; keep the original units listed in manifest.json. Server: server/resource/.../*.rdf and *.wps.\n\n1. Verify hashes and the manifest base before integrating.\n2. Back up affected files and review dependencies/IDs/scripts.\n3. Test loading in the real client and gameplay.\n4. Record the PAK in the ledger and get acceptance; the server requires explicit maintenance.\n5. Prepare the launcher release: blobs first, latest.json last.\n\nThis ZIP does not install, delete, restart or publish files. An overlay depends on its base units. Do not mix versions from another base.\n");
        }
        var verified = VerifyZip(temp); File.Move(temp, destination);
        return new { zip = destination, sha256 = ContentSafety.Hash(destination), bytes = new FileInfo(destination).Length, revision = revision.Id, payloadFiles = payload.Count, verified };
    }
    private static void ZipText(ZipArchive zip, string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
    public string? ServerDestination(string original)
    {
        var config = Read(); original = Path.GetFullPath(original);
        if (Inside(original, Path.Combine(Work, "server"))) return Target(Path.GetRelativePath(Work, original));
        if (config.Server != null && Inside(original, config.Server)) return Target("server/" + Path.GetRelativePath(config.Server, original));
        return null;
    }
    public byte[]? CurrentResource(PakArchive archive, PakEntry entry)
    {
        var config = Read(); if (!config.Packages.TryGetValue(archive.Prefix, out var package)) return null;
        if (archive.IndexHash != package.BaseIndexSha256) throw new IOException("The open PAK derives from a different base than the project.");
        var operation = package.Operations.FirstOrDefault(o => ContentSafety.ResourcePath(o.Path).Equals(ContentSafety.ResourcePath(entry.Name), StringComparison.OrdinalIgnoreCase));
        if (operation == null) return null;
        if (operation.Op == "remove") throw new IOException("This resource is removed in the project.");
        string blob = Blob(operation.Sha256!); if (ContentSafety.Hash(blob) != operation.Sha256) throw new IOException("Damaged project resource.");
        if (new FileInfo(blob).Length > 128L * 1024 * 1024) throw new IOException("Project preview exceeds 128 MiB.");
        return File.ReadAllBytes(blob);
    }
    public async Task<object> ImportMobAsync(string candidate, bool replace = false)
    {
        using var transaction = Lock();
        candidate = Path.GetFullPath(candidate); ContentSafety.NoLinks(candidate);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate, "manifest.json")));
        var root = manifest.RootElement;
        if (root.GetProperty("schema").GetString() != "assets.studio-mob-candidate/v1") throw new IOException("Not a canonical mob candidate.");
        foreach (var file in root.GetProperty("files").EnumerateObject())
        {
            string rel = ContentSafety.ResourcePath(file.Name); string source = Path.Combine(candidate, rel); ContentSafety.NoLinks(source);
            if (new FileInfo(source).Length != file.Value.GetProperty("bytes").GetInt64() || ContentSafety.Hash(source) != file.Value.GetProperty("sha256").GetString()) throw new IOException("Mob candidate file differs: " + rel);
        }
        var serverFiles = new Dictionary<string, string> { ["server-mob.rdf"] = "server/resource/server_data/table/rdf/Table_MOB_Data.rdf", ["server-mob-only.rdf"] = "server/resource/server_data/table/rdf/table_mob_data_server.rdf", ["server-characterproperty.rdf"] = "server/resource/property/characterproperty.rdf" };
        var manifestPaths = root.GetProperty("files").EnumerateObject().Select(f => ContentSafety.ResourcePath(f.Name).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in serverFiles.Keys) if (!manifestPaths.Contains("server-candidatefiles/" + file)) throw new IOException("Server file hash missing in manifest: " + file);
        var config = Read();
        foreach (var file in serverFiles) { CheckBaseline(config, file.Value); WorkspaceContentValidation.Validate(Path.Combine(candidate, "server-candidatefiles", file.Key), file.Value); }
        foreach (string family in new[] { "tbl", "prop", "obj", "tex" })
        {
            string directory = Path.Combine(candidate, "client-paks", family); ContentPakService.VerifyCandidate(directory);
            var plan = ContentPakService.ReadPlan(Path.Combine(directory, "plan.json"));
            var baseFile = config.Baseline.FirstOrDefault(f => f.Path.Equals("client/pack/" + family + ".pak", StringComparison.OrdinalIgnoreCase));
            if (baseFile?.Sha256 != plan.SourceSha256) throw new IOException("Mob candidate requires a different PAK base: " + family);
        }
        // Snapshot before a multi-family import. On failure restore the project,
        // retaining failed work in history rather than yielding a half delivery.
        WorkspaceRevision checkpoint;
        using (var guard = Lock())
        {
            config = Read(); var head = Head(config);
            checkpoint = head == null || Diff(Scan(), head.Files).Length > 0 ? CommitLocked(config, "Before importing mob " + root.GetProperty("model").GetString()) : head;
        }
        try
        {
            var packages = new List<object>();
            foreach (string family in new[] { "tbl", "prop", "obj", "tex" }) packages.Add(await ImportPakAsync(Path.Combine(candidate, "client-paks", family), replace));
            foreach (var file in serverFiles) Adopt(Path.Combine(candidate, "server-candidatefiles", file.Key), file.Value, replace);
            return new { model = root.GetProperty("model").GetString(), mobId = root.GetProperty("mob_id").GetInt64(), packages, server = serverFiles.Values, project = Root };
        }
        catch { Restore(checkpoint.Id); throw; }
    }
    public static object VerifyZip(string path)
    {
        ContentSafety.NoLinks(path); using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("manifest.json") ?? throw new IOException("manifest.json is missing."); if (entry.Length > 16 * 1024 * 1024) throw new IOException("Manifest is too large.");
        using var input = entry.Open(); using var document = JsonDocument.Parse(input); var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "assets-delivery-v1") throw new IOException("Unknown delivery format.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int count = 0;
        if (root.GetProperty("files").GetArrayLength() > 20000) throw new IOException("Delivery exceeds 20,000 files.");
        foreach (var unit in root.GetProperty("requiredOriginalUnits").EnumerateArray()) { string rel = Target(unit.GetProperty("path").GetString()!); if (!rel.StartsWith("client/")) throw new IOException("PAK dependency with a server path."); }
        foreach (var removed in root.GetProperty("removals").EnumerateArray()) { string rel = Target(removed.GetProperty("path").GetString()!); if (!rel.StartsWith("server/")) throw new IOException("PAK removal outside a canonical plan."); }
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            string rel = Target(file.GetProperty("path").GetString()!); if (!names.Add(rel)) throw new IOException("Duplicate path in manifest.");
            var part = zip.GetEntry(rel) ?? throw new IOException("Missing file: " + rel);
            if (part.Length != file.GetProperty("bytes").GetInt64()) throw new IOException("Size differs: " + rel);
            using var stream = part.Open(); if (Convert.ToHexString(SHA256.HashData(stream)) != file.GetProperty("sha256").GetString()) throw new IOException("Hash differs: " + rel); count++;
        }
        foreach (var part in zip.Entries) if (!names.Contains(part.FullName) && part.FullName is not ("manifest.json" or "history.json" or "INTEGRATION.md")) throw new IOException("Entry outside the manifest: " + part.FullName);
        if (zip.Entries.Count != count + 3) throw new IOException("Duplicate entries or missing metadata.");
        return new { valid = true, files = count, integrity = "zip_payload_hashes_match", gameCompatibility = "not_tested" };
    }
}
