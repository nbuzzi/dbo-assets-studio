using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DboAssetsStudio;

// dbo-assets world-engine ...  (same engine and cache as the Studio's World Editor view)
//   worlds [--pack <dir>]
//   prepare <world> [--pack <dir>]
//   capture <world> <new.png> [--pos x,y,z] [--camera free|top|bottom|left|right|front|back] [--yaw d] [--pitch d] [--dist m]
//           [--size WxH] [--frames n] [--spawns <spawn.rdf> [--kind mob|npc]] [--no-overlay] [--no-sky]
//   pick <world> <x,y,z> --spawns <spawn.rdf> [--kind mob|npc] [--dist m]   (centres the camera on x,y,z and picks the screen centre)
internal static class WorldEngineCli
{
    public static async Task<object> RunAsync(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException(Usage);
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plain = new List<string>();
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--no-overlay" or "--no-sky" or "--keep") switches.Add(args[i]);
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) flags[args[i][2..]] = args[++i];
            else plain.Add(args[i]);
        }
        string? F(string k) => flags.TryGetValue(k, out var v) ? v : null;
        switch (plain[0])
        {
            case "worlds": return await WorldEngineService.ListWorldsAsync(F("pack"));
            case "prepare":
                if (plain.Count < 2) throw new ArgumentException(Usage);
                return await WorldEngineService.PrepareAsync(plain[1], F("pack"), new Progress<string>(n => Console.Error.WriteLine(n)));
            case "capture":
                {
                    if (plain.Count < 3) throw new ArgumentException(Usage);
                    string world = WorldEngineService.ValidWorld(plain[1]);
                    string output = FreshOutput(plain[2], ".png");
                    var a = BaseArgs(world, flags, switches);
                    a.AddRange(["--capture", output, "--stats", Path.ChangeExtension(output, ".json"), "--frames", F("frames") ?? "150"]);
                    var events = await RunHostAsync(a, null);
                    if (!File.Exists(output)) throw new IOException("world-host did not produce the capture. " + string.Join(" | ", events.TakeLast(3)));
                    return new { world, output, sha256 = ContentSafety.Hash(output), stats = Path.ChangeExtension(output, ".json"), engine = LastStats(events), clientWritten = false };
                }
            case "pick":
                {
                    if (plain.Count < 3 || F("spawns") == null) throw new ArgumentException(Usage);
                    string world = WorldEngineService.ValidWorld(plain[1]);
                    double[] p = plain[2].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (p.Length != 3) throw new ArgumentException("Position x,y,z.");
                    flags["pos"] = plain[2];
                    var a = BaseArgs(world, flags, switches, ground: false);
                    // Interactive session over the JSON protocol: load spawns, wait for the world, click the screen centre.
                    var events = await RunHostAsync(a, async (send, wait) =>
                    {
                        await wait("world-ready", 180);
                        await Task.Delay(800);
                        await send("{\"cmd\":\"pick-center\",\"id\":\"p\"}");
                        await wait("pick", 15);
                        await send("{\"cmd\":\"close\",\"id\":\"c\"}");
                    });
                    var pick = events.Select(e => { try { return JsonDocument.Parse(e).RootElement.Clone(); } catch { return default; } })
                        .LastOrDefault(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("event", out var ev) && ev.GetString() == "pick");
                    return new { world, position = p, pick = pick.ValueKind == JsonValueKind.Undefined ? (object)"no pick" : pick };
                }
            case "export":
                {   // export <workspace-id> <new-candidate-dir> [--fields 1,2]: diff an existing workspace against the cache
                    if (plain.Count < 3) throw new ArgumentException(Usage);
                    var ws = WorldEngineWorkspace.Open(plain[1]);
                    int[]? fields = F("fields") is string fl ? fl.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray() : null;
                    var extra = new Dictionary<string, string>();
                    string beforePng = Path.Combine(ws.Root, "captures", "before.png");
                    if (File.Exists(beforePng)) extra["evidence/before.png"] = beforePng;
                    return WorldEngineWorkspace.ExportCandidate(ws, plain[2], fields, extra, new { source = "cli export" });
                }
            case "spawn-tables":
                if (plain.Count < 2) throw new ArgumentException(Usage);
                return WorldEngineService.SpawnTables(WorldEngineService.ValidWorld(plain[1]));
            case "workspaces":
                return Directory.EnumerateDirectories(WorldEngineWorkspace.WorkspacesRoot()).Select(Path.GetFileName).ToArray();
            case "query":
                {
                    // query <workspace-id> <script.json> [--capture <new.png>]: reopens a saved workspace READ-ONLY (viewer mode, the
                    // engine never writes) and runs the script's "query" steps (or "steps"), e.g. light-list / effect-list /
                    // sound-list / light-field, to verify a round-trip after "edit". The host rejects edit commands without --edit.
                    if (plain.Count < 3) throw new ArgumentException(Usage);
                    var ws = WorldEngineWorkspace.Open(plain[1]);
                    using var scriptDoc = JsonDocument.Parse(File.ReadAllBytes(plain[2]));
                    var script = scriptDoc.RootElement;
                    string? capturePath = F("capture") is string cp ? FreshOutput(cp, ".png") : null;
                    double[]? pos = script.TryGetProperty("pos", out var pe) ? pe.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                    await using var host = new WorldEngineHostProcess();
                    await host.StartAsync(ws.World, ws.Root, 0, 1280, 720, pos, "free", edit: false);
                    for (int i = 0; i < 360 && !host.WorldReady; i++) await Task.Delay(500);
                    if (!host.WorldReady) throw new TimeoutException("The world did not finish loading.");
                    if (script.TryGetProperty("camera", out var cam)) await host.SendAsync("camera", cam);
                    await Task.Delay(1500);
                    var replies = new List<object?>();
                    foreach (var step in script.TryGetProperty("query", out var q) ? q.EnumerateArray() : script.GetProperty("steps").EnumerateArray())
                    {
                        string cmd = step.GetProperty("cmd").GetString()!;
                        var values = step.EnumerateObject().Where(p => p.Name != "cmd").ToDictionary(p => p.Name, p => p.Value);
                        var r = await host.SendAsync(cmd, values);
                        replies.Add(new { cmd, reply = JsonSerializer.Deserialize<object>(r.GetRawText()) });
                    }
                    if (capturePath != null) { await Task.Delay(1000); await host.SendAsync("capture", new { path = capturePath }); }
                    return new { workspace = ws.Root, readOnly = true, capture = capturePath, replies };
                }
            case "edit":
                {
                    // edit <world> <script.json> <new-candidate-dir>: workspace copy -> host --edit -> before capture -> steps
                    // (host commands, e.g. terrain-height/terrain-paint/tool) -> after capture -> save -> candidate + audit.
                    if (plain.Count < 4) throw new ArgumentException(Usage);
                    string world = WorldEngineService.ValidWorld(plain[1]);
                    using var scriptDoc = JsonDocument.Parse(File.ReadAllBytes(plain[2]));
                    var script = scriptDoc.RootElement;
                    string candidate = Path.GetFullPath(plain[3]);
                    WorldEngineService.GuardNotClient(candidate, "The candidate");
                    if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any()) throw new IOException("The candidate must be a new folder: " + candidate);
                    var ws = await WorldEngineWorkspace.CreateAsync(world, new Progress<string>(n => Console.Error.WriteLine(n)));
                    Console.Error.WriteLine("workspace: " + ws.Root);
                    double[]? pos = script.TryGetProperty("pos", out var pe) ? pe.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                    string temp = Path.Combine(ws.Root, "captures"); Directory.CreateDirectory(temp);
                    await using var host = new WorldEngineHostProcess();
                    await host.StartAsync(world, ws.Root, 0, 1280, 720, pos, "free", edit: true);
                    for (int i = 0; i < 360 && !host.WorldReady; i++) await Task.Delay(500);
                    if (!host.WorldReady) throw new TimeoutException("The world did not finish loading.");
                    if (script.TryGetProperty("camera", out var cam)) await host.SendAsync("camera", cam);
                    await Task.Delay(1500);
                    string before = Path.Combine(temp, "before.png"), after = Path.Combine(temp, "after.png");
                    await host.SendAsync("capture", new { path = before });
                    var replies = await RunStepsAsync(host, script);
                    await Task.Delay(1500);
                    await host.SendAsync("capture", new { path = after });
                    var saved = await host.SendAsync("save", null, 120);
                    int[] edited = saved.TryGetProperty("editedFields", out var ef) ? ef.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
                    await host.DisposeAsync();
                    var audit = WorldEngineWorkspace.ExportCandidate(ws, candidate, edited,
                        new Dictionary<string, string> { ["evidence/before.png"] = before, ["evidence/after.png"] = after },
                        new { script = JsonSerializer.Deserialize<object>(script.GetRawText()), replies = replies.Select(r => JsonSerializer.Deserialize<object>(r.GetRawText())).ToArray(), save = JsonSerializer.Deserialize<object>(saved.GetRawText()) });
                    return new { candidate, workspace = ws.Root, audit };
                }
            case "inspect":
                {
                    // inspect <workspace-id> <script.json> <new.png>: reopen a saved workspace in the VIEWER (no --edit, nothing is
                    // written), run read-only steps (water-get, terrain-layers, height-at, ...) and capture. Round-trip check.
                    if (plain.Count < 4) throw new ArgumentException(Usage);
                    var ws = WorldEngineWorkspace.Open(plain[1]);
                    using var scriptDoc = JsonDocument.Parse(File.ReadAllBytes(plain[2]));
                    var script = scriptDoc.RootElement;
                    string output = FreshOutput(plain[3], ".png");
                    double[]? pos = script.TryGetProperty("pos", out var pe) ? pe.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                    await using var host = new WorldEngineHostProcess();
                    await host.StartAsync(ws.World, ws.Root, 0, 1280, 720, pos, "free", edit: false);
                    for (int i = 0; i < 360 && !host.WorldReady; i++) await Task.Delay(500);
                    if (!host.WorldReady) throw new TimeoutException("The world did not finish loading.");
                    if (script.TryGetProperty("camera", out var cam)) await host.SendAsync("camera", cam);
                    await Task.Delay(1500);
                    var replies = new List<JsonElement>();
                    foreach (var step in script.TryGetProperty("inspect", out var ins) ? ins.EnumerateArray() : script.GetProperty("steps").EnumerateArray())
                    {
                        string cmd = step.GetProperty("cmd").GetString()!;
                        if (cmd is "save" || cmd.EndsWith("-apply", StringComparison.Ordinal) || cmd.StartsWith("terrain-height", StringComparison.Ordinal) || cmd.StartsWith("terrain-paint", StringComparison.Ordinal)) continue;
                        var values = step.EnumerateObject().Where(p => p.Name != "cmd").ToDictionary(p => p.Name, p => p.Value);
                        replies.Add(await host.SendAsync(cmd, values));
                    }
                    await host.SendAsync("capture", new { path = output });
                    await host.DisposeAsync();
                    return new { workspace = ws.Id, world = ws.World, output, replies = replies.Select(r => JsonSerializer.Deserialize<object>(r.GetRawText())).ToArray(), clientWritten = false };
                }
            case "verify":
                {
                    // verify <candidate-dir> <script.json> [--keep]: fresh workspace from the base cache + the candidate's
                    // world files on top -> NEW host in viewer mode (no --edit, never saves) -> steps (objects-list, capture...)
                    // -> verify.png. Round-trip check that what the candidate wrote loads back in the engine.
                    if (plain.Count < 3) throw new ArgumentException(Usage);
                    string candidate = Path.GetFullPath(plain[1]);
                    using var auditDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidate, "audit.json")));
                    string world = WorldEngineService.ValidWorld(auditDoc.RootElement.GetProperty("world").GetString()!);
                    using var scriptDoc = JsonDocument.Parse(File.ReadAllBytes(plain[2]));
                    var script = scriptDoc.RootElement;
                    var ws = await WorldEngineWorkspace.CreateAsync(world, new Progress<string>(n => Console.Error.WriteLine(n)));
                    string candWorld = Path.Combine(candidate, "world", world), wsWorld = Path.Combine(ws.Root, "world", world);
                    var applied = new List<string>();
                    if (Directory.Exists(candWorld))
                        foreach (string file in Directory.EnumerateFiles(candWorld, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(candWorld, file), dst = Path.Combine(wsWorld, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(file, dst, true); applied.Add(rel);
                        }
                    Console.Error.WriteLine($"verify workspace: {ws.Root} ({applied.Count} candidate files)");
                    double[]? pos = script.TryGetProperty("pos", out var pe) ? pe.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                    string evidence = Path.Combine(ws.Root, "captures"); Directory.CreateDirectory(evidence);
                    var host = new WorldEngineHostProcess();
                    List<JsonElement> replies;
                    string shot = Path.Combine(evidence, "verify.png");
                    try
                    {
                        await host.StartAsync(world, ws.Root, 0, 1280, 720, pos, "free", edit: false);
                        for (int i = 0; i < 360 && !host.WorldReady; i++) await Task.Delay(500);
                        if (!host.WorldReady) throw new TimeoutException("The world did not finish loading.");
                        if (script.TryGetProperty("camera", out var cam)) await host.SendAsync("camera", cam);
                        await Task.Delay(1500);
                        replies = await RunStepsAsync(host, script);
                        await Task.Delay(800);
                        await host.SendAsync("capture", new { path = shot });
                    }
                    finally { await host.DisposeAsync(); }
                    string? copied = null;
                    if (F("evidence") is string ev)
                    {
                        string dir = Path.GetFullPath(ev); WorldEngineService.GuardNotClient(dir, "The evidence");
                        Directory.CreateDirectory(dir); copied = Path.Combine(dir, "verify.png"); File.Copy(shot, copied, false);
                    }
                    bool kept = switches.Contains("--keep");
                    if (!kept) WorldEngineWorkspace.DeleteWorkspace(ws);
                    return new { candidate, world, appliedFiles = applied, workspace = kept ? ws.Root : null, capture = copied ?? (kept ? shot : null),
                        replies = replies.Select(r => JsonSerializer.Deserialize<object>(r.GetRawText())).ToArray(), hostMode = "viewer (no --edit)" };
                }
            default: throw new ArgumentException(Usage);
        }
    }

    // Script steps: {"cmd": "<host command>", ...args}. "expectError": true records the engine's refusal instead of failing
    // (e.g. R6: a name that is not in objectproperty.rdf must be rejected).
    private static async Task<List<JsonElement>> RunStepsAsync(WorldEngineHostProcess host, JsonElement script)
    {
        var replies = new List<JsonElement>();
        if (!script.TryGetProperty("steps", out var steps)) return replies;
        foreach (var step in steps.EnumerateArray())
        {
            string cmd = step.GetProperty("cmd").GetString()!;
            bool expectError = step.TryGetProperty("expectError", out var ee) && ee.ValueKind == JsonValueKind.True;
            int wait = step.TryGetProperty("waitMs", out var wm) && wm.ValueKind == JsonValueKind.Number ? wm.GetInt32() : 0;
            // "timeoutSeconds" (CLI only) lets long steps such as pvs-build {"wait":true} reply later than the default.
            int stepTimeout = step.TryGetProperty("timeoutSeconds", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 60;
            var values = step.EnumerateObject().Where(p => p.Name is not ("cmd" or "expectError" or "waitMs" or "timeoutSeconds")).ToDictionary(p => p.Name, p => p.Value);
            if (wait > 0) await Task.Delay(Math.Min(wait, 30000));
            try
            {
                var reply = await host.SendAsync(cmd, values, stepTimeout);
                if (expectError) throw new InvalidOperationException($"Step '{cmd}' was expected to fail but did not: {reply}");
                replies.Add(reply);
            }
            catch (IOException ex) when (expectError)
            {
                replies.Add(JsonSerializer.SerializeToElement(new { @event = "expected-error", cmd, message = ex.Message }));
            }
        }
        return replies;
    }

    private const string Usage = "Usage: world-engine worlds | prepare <world> | edit <world> <script.json> <new-candidate> | export <workspace> <new-candidate> [--fields a,b] | inspect <workspace> <script.json> <new.png> | verify <candidate> <script.json> [--keep] [--evidence dir] | workspaces | spawn-tables <world> | query <workspace> <script.json> [--capture new.png] | capture <world> <new.png> [--pos x,y,z] [--camera top] [--dist m] [--spawns spawn.rdf] | pick <world> <x,y,z> --spawns spawn.rdf";

    private static List<string> BaseArgs(string world, Dictionary<string, string> flags, HashSet<string> switches, bool ground = true)
    {
        if (!WorldEngineService.IsCached(world)) throw new InvalidOperationException($"{world} is not in the cache: run 'world-engine prepare {world}'.");
        var a = new List<string> { "--root", WorldEngineService.CacheRoot(), "--world", world, "--hidden" };
        foreach (string k in new[] { "camera", "yaw", "pitch", "dist", "size" }) if (flags.TryGetValue(k, out var v)) a.AddRange(["--" + k, v]);
        if (flags.TryGetValue("pos", out var pos)) { a.AddRange(["--pos", pos]); if (ground) a.Add("--ground"); }
        if (flags.TryGetValue("spawns", out var spawnPath))
        {
            string kind = flags.TryGetValue("kind", out var k) ? k : (Path.GetFileName(spawnPath).Contains("npc", StringComparison.OrdinalIgnoreCase) ? "npc" : "mob");
            a.AddRange(["--spawns", WorldEngineService.SpawnPayloadFile(spawnPath, kind)]);
        }
        foreach (string s in switches) a.Add(s);
        return a;
    }

    private static string FreshOutput(string path, string extension)
    {
        string full = ContentSafety.SafeOutput(path);
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output must end in " + extension);
        if (File.Exists(full)) throw new IOException("The output must be a new file: " + full);
        WorldEngineService.GuardNotClient(full, "The output");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static JsonElement? LastStats(List<string> events)
    {
        foreach (string e in Enumerable.Reverse(events))
            try { var el = JsonDocument.Parse(e).RootElement; if (el.TryGetProperty("event", out var ev) && ev.GetString() == "stats") return el.Clone(); } catch (JsonException) { }
        return null;
    }

    private static async Task<List<string>> RunHostAsync(List<string> arguments, Func<Func<string, Task>, Func<string, int, Task>, Task>? session)
    {
        var start = new ProcessStartInfo(WorldEngineService.HostPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new IOException("Could not start world-host.exe.");
        var events = new List<string>();
        var signal = new SemaphoreSlim(0);
        var errors = process.StandardError.ReadToEndAsync();
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line) { lock (events) events.Add(line); signal.Release(); }
        });
        if (session != null)
        {
            async Task Send(string line) { await process.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(); }
            async Task Wait(string evt, int seconds)
            {
                var until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until)
                {
                    lock (events) if (events.Any(e => e.Contains("\"event\":\"" + evt + "\"", StringComparison.Ordinal))) return;
                    if (process.HasExited) throw new IOException("world-host exited: " + string.Join(" | ", events.TakeLast(3)));
                    await signal.WaitAsync(TimeSpan.FromMilliseconds(250));
                }
                throw new TimeoutException("world-host did not emit " + evt);
            }
            await session(Send, Wait);
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(600_000)) { process.Kill(true); throw new TimeoutException("world-host did not finish."); }
        await reader;
        string err = await errors;
        if (process.ExitCode != 0)
            throw new IOException($"world-host exited with {process.ExitCode}: " + string.Join(" | ", events.TakeLast(2)) + " " + string.Join(" ", err.Split('\n').Where(l => l.Contains("[world-host]")).TakeLast(8)));
        return events;
    }
}
