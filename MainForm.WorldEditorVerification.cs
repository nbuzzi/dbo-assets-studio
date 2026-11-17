using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    // Opt-in check of the REAL Studio: real WebView2, the real page and the real JS->C# bridge
    // (HandleStudioMessage -> StudioCommandAsync -> map-studio-spawns-*). Reads the spawn table,
    // edits only in memory and writes evidence into a fresh folder. Never touches the source or runtime.
    internal async Task VerifyWorldEditorAsync(string spawnFile, string tablesFolder, string output)
    {
        ContentSafety.SafeOutput(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("La evidencia debe usar una carpeta nueva.");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        object? failure = null;
        string sourceSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(spawnFile)));
        string? sessionId = null;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (_browser?.CoreWebView2 == null) { if (DateTime.UtcNow > until) throw new IOException("Timeout: WebView2"); await Task.Delay(100); }
            until = DateTime.UtcNow.AddSeconds(60);
            string ready = "";
            while (ready != "\"true\"")
            {
                if (DateTime.UtcNow > until) { string diag = await _browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({url:location.href,ready:document.readyState,rail:document.querySelectorAll('[data-view]').length,mw:!!document.querySelector('#map-workshop'),mwHtml:(document.querySelector('#map-workshop')?.innerHTML||'').length,err:document.title})"); throw new IOException("Timeout: la página real no cargó el Map Workshop. " + diag); }
                await Task.Delay(300);
                await _browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=\"map-workshop\"]')?.click()");
                ready = await _browser.CoreWebView2.ExecuteScriptAsync("String(!!document.querySelector('#native-spawns')&&!!document.querySelector('#native-spawns-new'))");
            }
            checks.Add(new { name = "real-page-has-spawn-buttons", ok = true });

            // JS helper living in the real page: posts through the real bridge and stores the reply.
            await _browser.CoreWebView2.ExecuteScriptAsync(@"
window.__res={};window.__n=0;
window.chrome.webview.addEventListener('message',e=>{const m=e.data;if(m&&typeof m.id==='string'&&m.id.startsWith('we-')){window.__res[m.id]=m}});
window.__rpc=(action,args)=>{const id='we-'+(++window.__n);window.chrome.webview.postMessage({id,action,args:args||{}});return id};");

            async Task<JsonElement> Rpc(string action, object args, bool expectError = false)
            {
                string raw = await _browser!.CoreWebView2.ExecuteScriptAsync($"window.__rpc({JsonSerializer.Serialize(action)},{JsonSerializer.Serialize(args)})");
                string id = JsonSerializer.Deserialize<string>(raw)!;
                var wait = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    string r = await _browser.CoreWebView2.ExecuteScriptAsync($"JSON.stringify(window.__res[{JsonSerializer.Serialize(id)}]||null)");
                    string json = JsonSerializer.Deserialize<string>(r) ?? "null";
                    if (json != "null")
                    {
                        var root = JsonDocument.Parse(json).RootElement.Clone();
                        bool hasError = root.TryGetProperty("error", out _);
                        if (hasError != expectError) throw new IOException($"{action}: " + (hasError ? "error inesperado " + root.GetProperty("error") : "debía fallar y no falló"));
                        return expectError ? root : root.GetProperty("result");
                    }
                    if (DateTime.UtcNow > wait) throw new IOException("Timeout en " + action);
                    await Task.Delay(100);
                }
            }
            void Ok(string name, bool cond, string detail = "") { if (!cond) throw new IOException("Falló: " + name + " " + detail); checks.Add(new { name, ok = true, detail }); }

            var open = await Rpc("map-studio-spawns-edit-open", new { path = spawnFile, kind = "mob", tablesFolder });
            sessionId = open.GetProperty("session").GetString();
            int count = open.GetProperty("payload").GetProperty("count").GetInt32();
            Ok("bridge-edit-open", count > 1000 && sessionId != null, $"{count} spawns");
            Ok("payload-has-uids-and-dirs", open.GetProperty("payload").TryGetProperty("uid", out _) && open.GetProperty("payload").TryGetProperty("dirX", out _));

            var cat = await Rpc("map-studio-spawns-catalog", new { session = sessionId, query = "", limit = 5 });
            Ok("bridge-catalog", cat.GetProperty("rows").GetArrayLength() == 5 && cat.GetProperty("total").GetInt32() > 1000, $"total {cat.GetProperty("total").GetInt32()}");

            var mv = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "move", items = new[] { new { uid = 0, x = 100.5, y = 20.0, z = 200.25 } } });
            Ok("bridge-move", mv.GetProperty("stats").GetProperty("modified").GetInt32() == 1);
            var cl = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "clone", uids = new[] { 0, 1 }, dx = 10, dz = 10 });
            Ok("bridge-clone", cl.GetProperty("changed").GetArrayLength() == 2 && cl.GetProperty("stats").GetProperty("added").GetInt32() == 2);
            var rt = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "rotate", uids = new[] { 2 }, degrees = 30 });
            Ok("bridge-rotate", rt.GetProperty("changed").GetArrayLength() == 1);
            var set = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "set", uids = new[] { 3 }, fields = new { quantity = 3, wander = 7 } });
            Ok("bridge-set-fields", set.GetProperty("changed")[0].GetProperty("quantity").GetInt32() == 3);
            await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "set", uids = new[] { 3 }, fields = new { quantity = 9999 } }, expectError: true);
            Ok("bridge-invalid-field-rejected", true);

            uint refId = (uint)open.GetProperty("payload").GetProperty("refId")[0].GetUInt32();
            var add = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "add", items = new[] { new { @ref = refId, x = 50.0, y = 5.0, z = 60.0, dirX = 1.0, dirZ = 0.0 } } });
            Ok("bridge-add-same-ref", add.GetProperty("changed").GetArrayLength() == 1);
            await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "add", items = new[] { new { @ref = 999999999u, x = 1.0, y = 1.0, z = 1.0 } } }, expectError: true);
            Ok("bridge-add-unknown-ref-rejected", true);

            var del = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "delete", uids = new[] { 4 } });
            Ok("bridge-delete", del.GetProperty("stats").GetProperty("deleted").GetInt32() == 1);
            var undo = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "undo" });
            Ok("bridge-undo", undo.GetProperty("stats").GetProperty("deleted").GetInt32() == 0 && undo.GetProperty("stats").GetProperty("canRedo").GetBoolean());
            var redo = await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "redo" });
            Ok("bridge-redo", redo.GetProperty("stats").GetProperty("deleted").GetInt32() == 1);
            await Rpc("map-studio-spawns-edit-op", new { session = sessionId, op = "undo" });

            var row = await Rpc("map-studio-spawns-table-get", new { session = sessionId, id = refId });
            var levelField = row.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "byLevel");
            Ok("bridge-table-get", row.GetProperty("spawnsUsing").GetInt32() > 0 && levelField.GetProperty("editable").GetBoolean(), $"{row.GetProperty("spawnsUsing").GetInt32()} spawns usan la fila");
            string key = levelField.GetProperty("key").GetString()!;
            var tset = await Rpc("map-studio-spawns-table-set", new { session = sessionId, id = refId, fields = new Dictionary<string, string> { [key] = "20" } });
            Ok("bridge-table-set", tset.GetProperty("stats").GetProperty("tableModified").GetInt32() == 1);
            await Rpc("map-studio-spawns-table-set", new { session = sessionId, id = refId, fields = new Dictionary<string, string> { [key] = "9999" } }, expectError: true);
            Ok("bridge-table-set-out-of-range-rejected", true);

            var portals = await Rpc("map-studio-spawns-portals", new { session = sessionId });
            Ok("bridge-portals", portals.GetProperty("total").GetInt32() > 0 && portals.GetProperty("rows").GetArrayLength() > 0, $"{portals.GetProperty("total").GetInt32()} portales");

            // Save goes through a folder dialog in the UI, so check the same session object directly.
            var session = SpawnEditSession.Get(sessionId!);
            string candidate = Path.Combine(output, "candidate-out");
            session.SaveCandidate(candidate);
            Ok("candidate-written", File.Exists(Path.Combine(candidate, "candidate", Path.GetFileName(spawnFile))) && File.Exists(Path.Combine(candidate, "audit.json")));
            Ok("candidate-includes-table", File.Exists(Path.Combine(candidate, "candidate", "Table_Mob_Data.rdf")));

            // The real page must also be able to load every module the editor needs.
            await _browser.CoreWebView2.ExecuteScriptAsync("window.__mods='pending';Promise.all([import('./spawn-editor-ui.js'),import('./native-world-spawns.js'),import('./native-world-viewer.js')]).then(()=>{window.__mods='ok'}).catch(e=>{window.__mods='ERR '+e.message})");
            await Task.Delay(1500);
            string mods = JsonSerializer.Deserialize<string>(await _browser.CoreWebView2.ExecuteScriptAsync("window.__mods")) ?? "";
            Ok("real-page-loads-editor-modules", mods == "ok", mods);

            await Rpc("map-studio-spawns-edit-close", new { session = sessionId });
            sessionId = null;
            Ok("bridge-edit-close", true);
            string after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(spawnFile)));
            Ok("source-untouched", after == sourceSha);
        }
        catch (Exception ex) { failure = new { type = ex.GetType().Name, message = ex.Message }; Environment.ExitCode = 1; }
        finally
        {
            if (sessionId != null) SpawnEditSession.Close(sessionId);
            ContentSafety.WriteJson(Path.Combine(output, "result.json"), new { utc = DateTime.UtcNow, success = failure == null, checks, failure });
            Close();
        }
    }
}
