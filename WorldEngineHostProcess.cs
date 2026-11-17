using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DboAssetsStudio;

// Owns only the world-host.exe process it starts (never a game client). JSON lines over stdin/stdout:
// every command carries an "id" and gets exactly one reply with that id; "state", "pick", "world-ready"
// are unsolicited events. The host never saves world data, so stopping it cannot change anything on disk.
internal sealed class WorldEngineHostProcess : IAsyncDisposable
{
    private Process? _process;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<string> _diagnostics = [];
    private readonly Queue<JsonElement> _picks = new();
    private JsonElement _state;
    private TaskCompletionSource<bool>? _ready;
    private Task? _read, _errors;
    private long _sequence, _window;
    private string _status = "stopped", _world = "";
    private bool _worldReady;

    internal bool Running => _process is { HasExited: false };
    internal string World => _world;

    internal object Status()
    {
        lock (_sync)
            return new
            {
                state = Running ? _status : (_status == "error" ? "error" : "stopped"),
                world = _world, worldReady = _worldReady, running = Running,
                nativeWindow = _window == 0 ? null : "0x" + _window.ToString("X"),
                engine = _state.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : _state,
                pendingPicks = _picks.Count,
                diagnostics = _diagnostics.TakeLast(30).ToArray(),
                processId = Running ? _process!.Id : (int?)null,
            };
    }

    internal async Task<object> StartAsync(string world, string cacheRoot, long parent, int width, int height, double[]? position, string camera = "free", bool edit = false)
    {
        await DisposeAsync();
        WorldEngineService.ValidWorld(world);
        WorldEngineService.GuardNotClient(cacheRoot, "La caché");
        string exe = WorldEngineService.HostPath();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        void Arg(params string[] a) { foreach (var s in a) start.ArgumentList.Add(s); }
        Arg("--root", cacheRoot, "--world", world, "--camera", camera);
        if (edit) Arg("--edit");   // workspace copy: saving allowed, fields leaving the ring are kept
        if (position is { Length: 3 }) Arg("--pos", string.Join(",", position.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))), "--ground");
        if (parent != 0) Arg("--parent", parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else Arg("--size", $"{Math.Clamp(width, 64, 4096)}x{Math.Clamp(height, 64, 2160)}", "--hidden");
        lock (_sync) { _status = "loading"; _world = world; _worldReady = false; _window = 0; _state = default; _diagnostics.Clear(); _picks.Clear(); }
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = Process.Start(start) ?? throw new IOException("No se pudo iniciar world-host.exe.");
        Process owned = _process;
        _read = ReadAsync(owned); _errors = ReadErrorsAsync(owned);
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(180)); return Status(); }
        catch { await DisposeAsync(); throw; }
    }

    private async Task ReadAsync(Process owned)
    {
        try
        {
            while (await owned.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) continue;
                JsonElement data;
                try { using var doc = JsonDocument.Parse(line); data = doc.RootElement.Clone(); } catch (JsonException) { lock (_sync) Note(line); continue; }
                string kind = data.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
                lock (_sync)
                {
                    if (data.TryGetProperty("hwnd", out var hwnd) && hwnd.ValueKind == JsonValueKind.String && long.TryParse(hwnd.GetString(), out long h)) _window = h;
                    switch (kind)
                    {
                        case "ready": _status = "ready"; break;
                        case "loaded": _status = "running"; _state = data; _ready?.TrySetResult(true); break;
                        case "world-ready": _worldReady = true; _state = data; break;
                        case "state": _state = data; if (data.TryGetProperty("worldReady", out var wr) && wr.ValueKind == JsonValueKind.True) _worldReady = true; break;
                        case "pick": if (_picks.Count < 64) _picks.Enqueue(data); break;
                        case "error": if (!data.TryGetProperty("id", out _)) { _status = "error"; Note(data.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error"); _ready?.TrySetException(new IOException(data.ToString())); } break;
                        case "exit": _status = "stopped"; break;
                    }
                }
                if (data.TryGetProperty("id", out var id) && _pending.TryRemove(id.ToString(), out var tcs))
                {
                    if (kind == "error") tcs.TrySetException(new IOException(data.TryGetProperty("message", out var msg) ? msg.GetString() : data.ToString()));
                    else tcs.TrySetResult(data);
                }
            }
            await owned.WaitForExitAsync();
            lock (_sync)
            {
                if (owned.ExitCode != 0) { _status = "error"; Note("world-host terminó con código " + owned.ExitCode); }
                _ready?.TrySetException(new IOException("world-host terminó antes de cargar el mundo. " + string.Join(" ", _diagnostics.TakeLast(4))));
            }
            FailPending(new IOException("world-host se cerró."));
        }
        catch (Exception ex) { lock (_sync) { _status = "error"; Note(ex.Message); _ready?.TrySetException(ex); } FailPending(ex); }
    }
    private void FailPending(Exception ex) { foreach (var p in _pending.Values) p.TrySetException(ex); _pending.Clear(); }
    private async Task ReadErrorsAsync(Process owned)
    {
        try
        {
            while (await owned.StandardError.ReadLineAsync() is { } line)
            {
                // Engine chatter ("Mod Loaded", texture traces) is noise; keep host markers, crashes and warnings.
                if (line.StartsWith("Mod Loaded", StringComparison.Ordinal)) continue;
                lock (_sync) Note(line);
            }
        }
        catch (ObjectDisposedException) { } catch (IOException) { }
    }
    private void Note(string message)
    {
        if (message.Length > 1024) message = message[..1024];
        if (_diagnostics.Count == 0 || _diagnostics[^1] != message) _diagnostics.Add(message);
        if (_diagnostics.Count > 200) _diagnostics.RemoveRange(0, _diagnostics.Count - 200);
    }

    internal async Task<JsonElement> SendAsync(string command, object? values = null, int timeoutSeconds = 30)
    {
        if (!Running) throw new IOException("El motor no está ejecutándose. Abrí un mundo primero.");
        var data = values == null ? new Dictionary<string, JsonElement>()
            : JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        string id = Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        data["cmd"] = JsonSerializer.SerializeToElement(command);
        data["id"] = JsonSerializer.SerializeToElement(id);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        await _write.WaitAsync();
        try { await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data)); await _process.StandardInput.FlushAsync(); }
        finally { _write.Release(); }
        try { return await reply.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        finally { _pending.TryRemove(id, out _); }
    }

    internal bool WorldReady { get { lock (_sync) return _worldReady; } }

    internal JsonElement[] TakePicks() { lock (_sync) { var all = _picks.ToArray(); _picks.Clear(); return all; } }

    public async ValueTask DisposeAsync()
    {
        Process? owned = _process; if (owned == null) return;
        try
        {
            if (!owned.HasExited)
            {
                try { using var cts = new CancellationTokenSource(2000); await SendAsync("close", null, 2); } catch (Exception) { }
                try { owned.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
                try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { owned.Kill(true); await owned.WaitForExitAsync(); }
            }
            if (_read != null) await _read.WaitAsync(TimeSpan.FromSeconds(3));
            if (_errors != null) await _errors.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { }
        finally { _process = null; owned.Dispose(); lock (_sync) { if (_status != "error") _status = "stopped"; } }
    }
}
