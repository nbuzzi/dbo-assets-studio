using System.Diagnostics;
using System.Security.Cryptography;

namespace DboAssetsStudio;

// Edit workspaces for the NTL World Editor.
// A workspace is a private copy of world\<name>\ (the engine rewrites fields in place while editing) plus
// junctions to the read-only shared cache folders. The base cache is never edited. Exporting compares the
// workspace against the cache byte by byte and copies only the files that really changed into a NEW candidate
// folder with an audit; nothing is installed, nothing touches the client or runtime.
internal static class WorldEngineWorkspace
{
    private static readonly string[] SharedJunctions = ["texture", "object", "effect", "property", "script"];

    internal static string WorkspacesRoot()
    {
        string root = Path.GetFullPath(Path.Combine(WorldEngineService.CacheRoot(), "..", "..", "workspaces"));
        WorldEngineService.GuardNotClient(root, "Los workspaces");
        Directory.CreateDirectory(root);
        return root;
    }

    internal sealed record Workspace(string Id, string Root, string World, DateTime CreatedUtc);

    internal static async Task<Workspace> CreateAsync(string world, IProgress<string>? progress = null)
    {
        WorldEngineService.ValidWorld(world);
        if (!WorldEngineService.IsCached(world)) throw new InvalidOperationException($"{world} no está en la caché.");
        string cache = WorldEngineService.CacheRoot();
        string id = $"{world}-{DateTime.Now:yyyyMMdd-HHmmss}";
        string root = Path.Combine(WorkspacesRoot(), id);
        if (Directory.Exists(root)) throw new IOException("El workspace ya existe: " + root);
        Directory.CreateDirectory(Path.Combine(root, "world"));
        foreach (string folder in SharedJunctions) Junction(Path.Combine(root, folder), Path.Combine(cache, folder));
        Junction(Path.Combine(root, "world", "mesh"), Path.Combine(cache, "world", "mesh"));
        progress?.Report("Copiando " + world + " al workspace…");
        string src = Path.Combine(cache, "world", world), dst = Path.Combine(root, "world", world);
        var robocopy = Process.Start(new ProcessStartInfo("robocopy", $"\"{src}\" \"{dst}\" /E /MT:16 /NFL /NDL /NJH /NJS /NP /R:1 /W:1") { UseShellExecute = false, CreateNoWindow = true })!;
        await robocopy.WaitForExitAsync();
        if (robocopy.ExitCode >= 8) throw new IOException("robocopy falló (" + robocopy.ExitCode + ") copiando el mundo al workspace.");
        // Logs the engine writes into the world folders are not world data.
        foreach (var trace in Directory.EnumerateFiles(dst, "*Trace.txt", SearchOption.AllDirectories)) File.Delete(trace);
        var created = DateTime.UtcNow;
        ContentSafety.WriteJson(Path.Combine(root, "workspace.json"), new { version = 1, id, world, createdUtc = created, cacheRoot = cache, baseIsReadOnly = true, clientWritten = false });
        return new Workspace(id, root, world, created);
    }

    internal static Workspace Open(string id)
    {
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..")) throw new InvalidDataException("Id de workspace inválido.");
        string root = Path.Combine(WorkspacesRoot(), id);
        string meta = Path.Combine(root, "workspace.json");
        if (!File.Exists(meta)) throw new FileNotFoundException("Workspace inexistente.", meta);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(meta));
        return new Workspace(id, root, doc.RootElement.GetProperty("world").GetString()!, doc.RootElement.GetProperty("createdUtc").GetDateTime());
    }

    /// <summary>Deletes a workspace (e.g. a verification one). Junctions into the read-only cache are unlinked first and
    /// the delete is refused if any reparse point is left, so the base cache can never be followed and emptied.</summary>
    internal static void DeleteWorkspace(Workspace ws)
    {
        string root = Path.GetFullPath(ws.Root);
        if (!root.StartsWith(Path.GetFullPath(WorkspacesRoot()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("No es un workspace: " + root);
        foreach (string link in SharedJunctions.Select(f => Path.Combine(root, f)).Append(Path.Combine(root, "world", "mesh")))
            if (Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link, false);
        if (Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Any(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("El workspace todavía tiene junctions; no se borra: " + root);
        Directory.Delete(root, true);
    }

    private static void Junction(string link, string target)
    {
        if (!Directory.Exists(target)) return;
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(link)) throw new IOException("No se pudo crear el junction " + link);
    }

    /// <summary>Copies the files that differ from the base cache into a NEW candidate folder and writes audit.json.</summary>
    internal static object ExportCandidate(Workspace ws, string candidateDir, IEnumerable<int>? editedFields = null, IDictionary<string, string>? extraFiles = null, object? notes = null)
    {
        string cache = WorldEngineService.CacheRoot();
        string full = Path.GetFullPath(candidateDir);
        WorldEngineService.GuardNotClient(full, "El candidato");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) throw new IOException("El candidato debe ser una carpeta nueva o vacía: " + full);
        string wsWorld = Path.Combine(ws.Root, "world", ws.World), baseWorld = Path.Combine(cache, "world", ws.World);
        var changed = new List<object>(); var changedPaths = new List<string>(); int rewrittenIdentical = 0, scanned = 0;
        var changedFields = new SortedSet<int>();
        foreach (string file in Directory.EnumerateFiles(wsWorld, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith("Trace.txt", StringComparison.OrdinalIgnoreCase)) continue;
            var fi = new FileInfo(file);
            if (fi.LastWriteTimeUtc <= ws.CreatedUtc.AddSeconds(-1)) continue;   // untouched since the copy
            scanned++;
            string rel = Path.GetRelativePath(wsWorld, file), orig = Path.Combine(baseWorld, rel);
            byte[] after = File.ReadAllBytes(file);
            byte[]? before = File.Exists(orig) ? File.ReadAllBytes(orig) : null;
            if (before != null && before.AsSpan().SequenceEqual(after)) { rewrittenIdentical++; continue; }
            int first = -1, last = -1, diffBytes = 0;
            if (before != null)
            {
                int n = Math.Min(before.Length, after.Length);
                for (int i = 0; i < n; i++) if (before[i] != after[i]) { if (first < 0) first = i; last = i; diffBytes++; }
                if (before.Length != after.Length) { if (first < 0) first = n; last = Math.Max(before.Length, after.Length) - 1; diffBytes += Math.Abs(before.Length - after.Length); }
            }
            string dst = Path.Combine(full, "world", ws.World, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, after);
            var m = System.Text.RegularExpressions.Regex.Match(rel, @"^fields\\wfif(\d+)\\");
            if (m.Success) changedFields.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            changedPaths.Add(@"world\" + ws.World + @"\" + rel);
            changed.Add(new
            {
                path = @"world\" + ws.World + @"\" + rel, isNew = before == null,
                sizeBefore = before?.Length, sizeAfter = after.Length,
                sha256Before = before == null ? null : Convert.ToHexString(SHA256.HashData(before)), sha256After = Convert.ToHexString(SHA256.HashData(after)),
                bytesChanged = diffBytes, firstDiffOffset = first, lastDiffOffset = last,
            });
        }
        Directory.CreateDirectory(full);
        if (extraFiles != null) foreach (var (target, source) in extraFiles) { string d = Path.Combine(full, target); Directory.CreateDirectory(Path.GetDirectoryName(d)!); File.Copy(source, d); }
        var edited = editedFields?.OrderBy(x => x).ToArray() ?? [];
        var unexpected = edited.Length == 0 ? [] : changedFields.Where(f => !edited.Contains(f)).ToArray();
        // P\------- (PVS) is the one world file outside the fields an edit may change on purpose (world-host pvs-build).
        var pvs = WorldEnginePvsAudit.Describe(ws.Root, baseWorld, wsWorld);
        // pak-tool replace-batch input (relative paths as stored in ter.pak)
        File.WriteAllLines(Path.Combine(full, "ter-replace-list.txt"), changedPaths.Select(c => ".\\" + c));
        var audit = new
        {
            version = 1, kind = "world-editor-candidate", world = ws.World, workspace = ws.Id, createdUtc = DateTime.UtcNow,
            baseCache = cache, filesScanned = scanned, filesChanged = changed.Count, rewrittenIdentical,
            editedFields = edited, changedFields = changedFields.ToArray(), changedOutsideEditedFields = unexpected,
            onlyEditedChanged = unexpected.Length == 0 && pvs.Ok,
            pvs = pvs.Detail,
            files = changed, notes,
            installAllowed = false, clientWritten = false, runtimeWritten = false,
        };
        ContentSafety.WriteJson(Path.Combine(full, "audit.json"), audit);
        return audit;
    }
}
