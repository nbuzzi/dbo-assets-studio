using System.Text.Json;

namespace DboAssetsStudio;

// Map tab · "Motor real": the client's NTL engine (world-host.exe) embedded as a child HWND next to the WebView2.
// World list, cameras, engine stats overlay and spawn picking. Spawn edits go through SpawnEditSession
// (SpawnEditorService), exactly like the Three.js spawn editor; the host only draws and picks.
internal sealed partial class MainForm
{
    private readonly WorldEngineHostProcess _worldEngine = new();
    private Panel? _worldEngineViewport;
    private bool _worldEngineViewportRequested, _worldEngineHook;
    private readonly SemaphoreSlim _worldEnginePrepare = new(1, 1);
    private string _worldEnginePrepareNote = "";

    private void EnsureWorldEngineHook()
    {
        if (_worldEngineHook) return;
        _worldEngineHook = true;
        FormClosed += async (_, _) => { try { await _worldEngine.DisposeAsync(); } catch { } };
    }

    private async Task<object?> WorldEngineCommandAsync(string action, JsonElement args)
    {
        EnsureWorldEngineHook();
        string S(string name, string fallback = "") => args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()) : fallback;
        double N(string name, double fallback) => args.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : fallback;
        bool B(string name, bool fallback = false) => args.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
        bool Has(string name) => args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

        switch (action)
        {
            case "world-engine-state":
                {
                    string? host = null, hostError = null, pack = null;
                    try { host = WorldEngineService.HostPath(); } catch (Exception ex) { hostError = ex.Message; }
                    try { pack = WorldEngineService.PackDirectory(); } catch (Exception ex) { hostError ??= ex.Message; }
                    return new { host, hostError, pack, cacheRoot = WorldEngineService.CacheRoot(), prepare = _worldEnginePrepareNote,
                        prepareWorld = _worldEnginePrepareWorld, prepareRunning = _worldEnginePrepareTask is { IsCompleted: false },
                        prepareError = _worldEnginePrepareError, prepareResult = _worldEnginePrepareResult,
                        spawns = _worldEngineSpawnLayers.ToDictionary(kv => kv.Key, kv => kv.Value.Rows?.Rows.Length ?? 0), status = _worldEngine.Status() };
                }
            case "world-engine-worlds":
                return await WorldEngineService.ListWorldsAsync();
            case "world-engine-prepare":
                {
                    // Runs in the background and returns at once: the bridge answers one call at a time, so a long
                    // extraction would otherwise block the progress polls (world-engine-state) and look frozen.
                    string world = WorldEngineService.ValidWorld(S("world"));
                    if (!await _worldEnginePrepare.WaitAsync(0)) throw new InvalidOperationException("A cache preparation is already running (" + _worldEnginePrepareWorld + ").");
                    _worldEnginePrepareWorld = world; _worldEnginePrepareError = null; _worldEnginePrepareResult = null; _worldEnginePrepareNote = "reading the pak indices…";
                    _worldEnginePrepareTask = Task.Run(async () =>
                    {
                        try { _worldEnginePrepareResult = await WorldEngineService.PrepareAsync(world, null, new InlineProgress(note => _worldEnginePrepareNote = note)); }
                        catch (Exception ex) { _worldEnginePrepareError = ex.Message; }
                        finally { _worldEnginePrepareNote = ""; _worldEnginePrepare.Release(); }
                    });
                    return new { started = true, world };
                }
            case "world-engine-open":
                {
                    string world = WorldEngineService.ValidWorld(S("world"));
                    if (!WorldEngineService.IsCached(world)) throw new InvalidOperationException("World " + world + " is not in the cache. Prepare the cache first.");
                    if (_worldEngineViewport == null) CreateWorldEngineViewport();
                    double[]? pos = Has("x") && Has("z") ? [N("x", 0), N("y", 0), N("z", 0)] : null;
                    if (IsDisposed || Disposing) throw new OperationCanceledException("Studio is closing.");
                    // Edit mode runs on a workspace copy (never the base cache); the viewer runs read-only on the cache.
                    bool edit = B("edit");
                    string root = WorldEngineService.CacheRoot();
                    _worldEngineWorkspace = null;
                    if (edit)
                    {
                        _worldEngineWorkspace = S("workspace").Length > 0 ? WorldEngineWorkspace.Open(S("workspace"))
                            : await WorldEngineWorkspace.CreateAsync(world, new Progress<string>(n => _worldEnginePrepareNote = n));
                        if (!_worldEngineWorkspace.World.Equals(world, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The workspace belongs to another world.");
                        root = _worldEngineWorkspace.Root;
                        _worldEnginePrepareNote = "";
                    }
                    var status = await _worldEngine.StartAsync(world, root, _worldEngineViewport!.Handle.ToInt64(), _worldEngineViewport.Width, _worldEngineViewport.Height, pos, S("camera", "free"), edit);
                    _worldEngineViewport.Visible = _worldEngineViewportRequested;
                    await PushAllWorldEngineSpawnsAsync();
                    return status;
                }
            case "world-engine-viewport":
                {
                    double x = N("x", 0), y = N("y", 0), width = N("width", 0), height = N("height", 0);
                    if (width < 0 || width > 8192 || height < 0 || height > 8192 || Math.Abs(x) > 16384 || Math.Abs(y) > 16384) throw new InvalidDataException("Viewport rectangle out of range.");
                    if (_worldEngineViewport == null) CreateWorldEngineViewport();
                    float scale = (_browser?.DeviceDpi ?? DeviceDpi) / 96f;
                    int w = Math.Clamp((int)Math.Round(width * scale), 64, 4096), h = Math.Clamp((int)Math.Round(height * scale), 64, 2160);
                    bool changed = _worldEngineViewport!.Width != w || _worldEngineViewport.Height != h;
                    _worldEngineViewport.Bounds = new Rectangle((int)Math.Round(x * scale), (int)Math.Round(y * scale), w, h);
                    _worldEngineViewportRequested = B("visible") && width >= 64 && height >= 64;
                    _worldEngineViewport.Visible = _worldEngineViewportRequested && _worldEngine.Running;
                    if (_worldEngineViewport.Visible) _worldEngineViewport.BringToFront();
                    if (changed && _worldEngine.Running) await _worldEngine.SendAsync("resize", new { width = w, height = h });
                    return new { embedded = true, width = w, height = h };
                }
            case "world-engine-camera":
                {
                    var values = new Dictionary<string, object>();
                    if (Has("mode")) values["mode"] = S("mode");
                    foreach (string k in new[] { "yaw", "pitch", "dist" }) if (Has(k)) values[k] = N(k, 0);
                    return await _worldEngine.SendAsync("camera", values);
                }
            case "world-engine-goto":
                return await _worldEngine.SendAsync("goto", new { x = N("x", 0), y = N("y", 0), z = N("z", 0), ground = B("ground", true), dist = N("dist", 80) });
            case "world-engine-overlay":
                return await _worldEngine.SendAsync("overlay", new { enabled = B("enabled", true) });
            case "world-engine-capture":
                {
                    string dir = Path.Combine(WorldEngineService.CacheRoot(), "..", "captures");
                    Directory.CreateDirectory(dir);
                    string path = Path.GetFullPath(Path.Combine(dir, $"{_worldEngine.World}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));
                    var reply = await _worldEngine.SendAsync("capture", new { path });
                    bool ok = reply.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
                    if (!ok || !File.Exists(path)) throw new IOException("The engine capture failed.");
                    return new { path, url = MediaUrl(await File.ReadAllBytesAsync(path), "image/png") };
                }
            case "world-engine-status":
                {
                    if (!_worldEngine.Running && _worldEngineViewport != null) _worldEngineViewport.Visible = false;
                    var picks = _worldEngine.TakePicks().Select(DescribePick).ToArray();
                    return new { status = _worldEngine.Status(), picks, layers = _worldEngineSpawnLayers.Keys.ToArray() };
                }
            case "world-engine-close":
                if (_worldEngineViewport != null) _worldEngineViewport.Visible = false;
                await _worldEngine.DisposeAsync();
                return _worldEngine.Status();
            default:
                if (action.StartsWith("world-engine-spawn", StringComparison.Ordinal)) return await WorldEngineSpawnCommandAsync(action, args);
                return await WorldEngineEditCommandAsync(action, args);
        }
    }

    private Task? _worldEnginePrepareTask;
    private volatile string? _worldEnginePrepareWorld, _worldEnginePrepareError;
    private volatile object? _worldEnginePrepareResult;
    // Progress<T> would post to the UI thread; the note is a plain field read by world-engine-state.
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }

    private void CreateWorldEngineViewport()
    {
        _worldEngineViewport = new Panel { Visible = false, BackColor = Color.FromArgb(13, 19, 25), Name = "WorldEngineViewport" };
        Controls.Add(_worldEngineViewport);
    }
}
