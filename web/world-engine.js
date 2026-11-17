// Map tab · "Motor real": the client's NTL engine (world-host.exe) embedded as a native child window.
// The HTML only lays out the viewport rectangle; the C# side positions the native HWND over it.
// Spawn edits go through SpawnEditSession (same engine as the Three.js spawn editor).
import {installTerrainTools} from './world-engine-terrain.js';
import {installWaterTools} from './world-engine-water.js';
import {installLightFxTools} from './world-engine-lightfx.js';
import {installSpawnTools} from './world-engine-spawns.js';
import {installObjectTools} from './world-engine-objects.js';
import {installPropsTools} from './world-engine-props.js';
// Tool modules mount into #we-tools (one import + one entry per tool family: terrain, objects, water, ...).
const TOOL_MODULES = [installSpawnTools, installTerrainTools, installObjectTools, installWaterTools, installLightFxTools, installPropsTools];
const fmt = (n, d = 1) => Number.isFinite(Number(n)) ? Number(n).toFixed(d) : '—';

export function installWorldEngine({sidebar, main, api, toast, escape: e}) {
 sidebar.innerHTML = `
 <div class="map-native-intro">
  <span class="eyebrow">WORLD EDITOR · REAL NTL ENGINE</span>
  <h2>View and edit with the game renderer</h2>
  <p>Opens the world in the client engine (NtlPresentation in tool mode). Reads a private cache extracted from the paks; never writes to the client or runtime.</p>
  <span class="map-native-badge">Spawns: candidate in a new folder with audit</span>
 </div>
 <div class="map-native-picker">
  <label for="we-world">World</label>
  <input id="we-filter" class="we-filter" placeholder="Filter worlds (dbo_main…)">
  <select id="we-world" size="9"></select>
  <p id="we-world-note" class="map-path">Reading ter.pak…</p>
  <p id="we-prepare-note" class="map-path we-prepare-note" hidden></p>
  <div class="map-native-actions">
   <button id="we-prepare" class="map-button" type="button" disabled>Prepare cache</button>
   <button id="we-open" class="map-button map-button-primary" type="button" disabled>Open in engine</button>
   <button id="we-close" class="map-button map-button-secondary" type="button" disabled>Close engine</button>
  </div>
  <label class="we-check"><input id="we-edit" type="checkbox"> Open for editing (private workspace: copy of the world)</label>
  <div class="map-native-actions">
   <button id="we-save" class="map-button" type="button" disabled>Save to workspace</button>
   <button id="we-export" class="map-button map-button-secondary" type="button" disabled>Export candidate…</button>
  </div>
  <p id="we-edit-note" class="map-path">Viewer mode: nothing is written. Editing works on a copy; the candidate carries only what changed (audit.json).</p>
  <label>Go to position (x, z)</label>
  <div class="map-picker-row we-goto">
   <input id="we-x" type="number" step="1" placeholder="x"><input id="we-z" type="number" step="1" placeholder="z">
   <button id="we-goto" class="map-button" type="button" disabled>Go</button>
  </div>
 </div>
 <div id="we-tools" class="we-tools"></div>
 <p id="we-pick-note" class="map-path"></p>
 </div>
 <div class="map-legacy-hint"><strong>Viewer controls</strong><small>Right button: orbit · wheel: zoom · middle button or Shift+right: pan · WASD/QE: move (click the viewer to focus).</small></div>`;
 main.innerHTML = `
 <header class="map-workshop-header"><div><span class="eyebrow">NTL ENGINE · CNtlPLWorldEntity + CNtlPLVisualManager</span><h2 id="we-title">Choose a world</h2><p id="we-subtitle">Real client render: terrain, objects, water, sky, fog and shadows.</p></div>
  <div class="we-toolbar">
   <div class="we-cams" role="group" aria-label="Camera">
    ${['free','top','front','back','left','right'].map(m => `<button type="button" class="map-button" data-we-cam="${m}">${{free:'Free',top:'Top',front:'Front',back:'Back',left:'Left',right:'Right'}[m]}</button>`).join('')}
   </div>
   <label class="we-check"><input id="we-overlay" type="checkbox" checked> Engine stats</label>
   <button id="we-capture" class="map-button" type="button" disabled>Capture</button>
  </div>
 </header>
 <div class="we-stage"><div id="we-view" class="we-view"><p id="we-placeholder" class="map-empty">The engine draws here.</p></div></div>
 <footer id="we-stats" class="we-stats">—</footer>`;

 const $ = s => sidebar.querySelector(s) || main.querySelector(s);
 let worlds = [], selectedWorld = '', status = null, visible = false, busy = false, poll = null, preparing = false;
 let viewportKey = '', viewportQueue = Promise.resolve(), viewportTimer = null;
 const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(() => scheduleViewport()) : null;
 const running = () => !!status?.running;

 // The engine is a native child window drawn above the page: hide it while a modal (Ctrl+K palette, dialogs) is open.
 const overlayOpen = () => !!document.querySelector('.palette-scrim:not(.hidden), dialog[open]');
 if (typeof MutationObserver === 'function') new MutationObserver(() => { if (visible) scheduleViewport(); }).observe(document.body, {subtree: true, attributes: true, attributeFilter: ['class', 'open']});
 function rect(hidden = false) {
  const view = $('#we-view'), r = view?.getBoundingClientRect();
  if (!r) return {x: 0, y: 0, width: 0, height: 0, visible: false};
  const x = Math.max(0, r.left), y = Math.max(0, r.top), width = Math.max(0, Math.min(window.innerWidth, r.right) - x), height = Math.max(0, Math.min(window.innerHeight, r.bottom) - y);
  return {x, y, width, height, visible: !hidden && visible && running() && !document.hidden && !overlayOpen() && width >= 64 && height >= 64};
 }
 function sendViewport(force = false, hidden = false) {
  const r = rect(hidden), key = JSON.stringify(r);
  if (!force && key === viewportKey) return viewportQueue;
  viewportKey = key;
  viewportQueue = viewportQueue.then(() => api('world-engine-viewport', r).catch(() => {}));
  return viewportQueue;
 }
 function scheduleViewport() { if (viewportTimer) return; viewportTimer = setTimeout(() => { viewportTimer = null; sendViewport(); }, 80); }
 window.addEventListener('resize', scheduleViewport);
 document.addEventListener('visibilitychange', scheduleViewport);
 main.addEventListener('scroll', scheduleViewport, true);

 async function guarded(fn) {
  if (busy) return; busy = true; render();
  try { await fn(); } catch (err) { toast(err.message || String(err), true); } finally { busy = false; render(); }
 }
 function render() {
  const w = worlds.find(x => x.world === selectedWorld);
  $('#we-prepare').disabled = busy || !w;
  $('#we-prepare').textContent = preparing ? 'Preparing…' : w?.cached ? 'Cache ready ✓' : 'Prepare cache';
  $('#we-open').title = w && !w.cached ? 'Prepare the cache for this world first' : '';
  $('#we-open').disabled = busy || !w?.cached;
  $('#we-close').disabled = busy || !running();
  $('#we-goto').disabled = busy || !running();
  $('#we-capture').disabled = busy || !running();
  $('#we-save').disabled = busy || !editing();
  $('#we-export').disabled = busy || !editing();
  const es = status?.engine;
  if (editing() && es) $('#we-edit-note').textContent = `Editing in workspace · fields with unsaved changes: ${es.dirtyFields ?? 0} · edited this session: ${es.editedFields ?? 0} · undo: ${es.undo ?? 0}`;
  for (const t of tools) t.render?.(status);
  main.querySelectorAll('[data-we-cam]').forEach(b => { b.disabled = busy || !running(); b.classList.toggle('active', b.dataset.weCam === (status?.engine?.camera || '')); });
  $('#we-placeholder').hidden = running();
  $('#we-title').textContent = status?.world ? status.world : (selectedWorld || 'Choose a world');
  const s = status?.engine;
  $('#we-stats').textContent = s ? `${status.worldReady ? 'ready' : 'loading…'} · camera ${s.camera} · pos ${fmt(s.pos?.[0])}, ${fmt(s.pos?.[1])}, ${fmt(s.pos?.[2])} · field ${s.field} · sector ${s.sector} · sectors ${s.frustumSectors}/${s.visibleSectors} · polygons ${s.polys} · ${fmt(s.fps, 0)} fps · spawns ${s.spawns}` : (status?.state === 'error' ? 'Error: ' + (status.diagnostics || []).slice(-2).join(' · ') : '—');
 }
 function renderWorlds() {
  const q = $('#we-filter').value.trim().toLowerCase();
  const list = worlds.filter(w => !q || w.world.toLowerCase().includes(q));
  $('#we-world').innerHTML = list.map(w => `<option value="${e(w.world)}" ${w.world === selectedWorld ? 'selected' : ''}>${w.cached ? '● ' : '○ '}${e(w.world)} · ${w.fields} fields · ${fmt(w.megabytes, 0)} MB</option>`).join('');
 }
 async function loadWorlds() {
  const r = await api('world-engine-worlds');
  worlds = r.worlds || [];
  if (!selectedWorld) selectedWorld = worlds.find(w => w.world === 'dbo_main_world')?.world || worlds[0]?.world || '';
  $('#we-world-note').textContent = `${worlds.length} worlds in ${r.pack} · cache ${r.cacheRoot}`;
  renderWorlds(); render();
 }
 async function refreshStatus() {
  const r = await api('world-engine-status');
  status = r.status;
  for (const p of r.picks || []) await handlePick(p);
  render();
  if (!running()) sendViewport(true, true);
 }
 async function handlePick(p) {
  for (const t of tools) if (t.onPick && await t.onPick(p)) return;
  if (p.terrain) $('#we-pick-note').textContent = `Terrain ${fmt(p.terrain[0])}, ${fmt(p.terrain[1])}, ${fmt(p.terrain[2])}`;
 }
 function editing() { return running() && !!status?.engine?.editMode; }
 const tools = TOOL_MODULES.map(install => install({container: $('#we-tools'), api, toast, escape: e,
  host: (cmd, args = {}) => api('world-engine-host', {cmd, args}), editing, state: () => status}));
 function startPoll() { stopPoll(); poll = setInterval(() => { if (visible) refreshStatus().catch(() => {}); }, 400); }
 function stopPoll() { if (poll) clearInterval(poll); poll = null; }

 $('#we-filter').oninput = renderWorlds;
 $('#we-world').onchange = () => { selectedWorld = $('#we-world').value; render(); };
 $('#we-prepare').onclick = () => guarded(async () => {
  // Progress and errors stay visible in the panel (not only in a toast): the extraction can take a while.
  const world = selectedWorld, note = $('#we-prepare-note'), started = Date.now();
  const show = (text, error = false) => { note.hidden = false; note.textContent = text; note.classList.toggle('we-error', error); };
  show(`Preparing ${world}: reading the client paks (read-only)…`);
  preparing = true; render();
  const secs = () => Math.round((Date.now() - started) / 1000);
  try {
   // The Studio starts the extraction in the background and answers at once; the panel follows world-engine-state.
   await api('world-engine-prepare', {world});
   let st;
   for (;;) {
    await new Promise(r => setTimeout(r, 600));
    st = await api('world-engine-state');
    if (st.prepareWorld === world && !st.prepareRunning) break;
    show(`Preparing ${world}: ${st.prepare || 'working…'} · ${secs()} s`);
   }
   if (st.prepareError) throw new Error(st.prepareError);
   const r = st.prepareResult || {world};
   const n = (r.steps || []).reduce((a, s) => a + (s.extracted || 0), 0), bad = (r.steps || []).reduce((a, s) => a + (s.failed || 0), 0);
   const w = worlds.find(x => x.world === world); if (w) w.cached = true;
   renderWorlds();
   show(`Cache ready for ${world}: ${n} files extracted${bad ? `, ${bad} unreadable` : ''} in ${secs()} s. You can now open it in the engine.`);
   toast(`Cache ready for ${world}.`);
  } catch (err) {
   show(`Could not prepare ${world}: ${err.message || err}`, true);
   throw err;
  } finally { preparing = false; }
  // Refresh sizes/flags in the background; the world is already marked as cached.
  loadWorlds().catch(() => {});
 });
 $('#we-open').onclick = () => guarded(async () => {
  await sendViewport(true);
  const args = {world: selectedWorld};
  const x = Number($('#we-x').value), z = Number($('#we-z').value);
  if ($('#we-x').value !== '' && $('#we-z').value !== '') Object.assign(args, {x, y: 0, z});
  else if (selectedWorld === 'dbo_main_world') Object.assign(args, {x: 4583, y: 0, z: 4053});
  args.edit = $('#we-edit').checked;
  if (args.edit) toast('Creating the edit workspace (copy of the world)…');
  status = await api('world-engine-open', args);
  startPoll();
  await sendViewport(true);
  if (args.edit) { for (let i = 0; i < 240 && !status?.worldReady; i++) { await new Promise(r => setTimeout(r, 500)); await refreshStatus(); } await api('world-engine-before'); }
 });
 $('#we-save').onclick = () => guarded(async () => { const r = await api('world-engine-save'); toast(`Saved to the workspace: fields ${(r.saved || []).join(', ') || '—'}.`); });
 $('#we-export').onclick = () => guarded(async () => {
  const r = await api('world-engine-export'); if (!r) return;
  toast(`Candidate: ${r.filesChanged} files changed (${r.rewrittenIdentical} rewritten identical), only edits changed: ${r.onlyEditedChanged ? 'yes' : 'NO'}.`);
 });
 $('#we-close').onclick = () => guarded(async () => { await sendViewport(true, true); status = await api('world-engine-close'); });
 $('#we-goto').onclick = () => guarded(async () => { await api('world-engine-goto', {x: Number($('#we-x').value), y: 0, z: Number($('#we-z').value), ground: true, dist: 120}); });
 main.querySelectorAll('[data-we-cam]').forEach(b => b.onclick = () => guarded(async () => { await api('world-engine-camera', b.dataset.weCam === 'free' ? {mode: 'free'} : {mode: b.dataset.weCam}); }));
 $('#we-overlay').onchange = () => guarded(async () => { await api('world-engine-overlay', {enabled: $('#we-overlay').checked}); });
 $('#we-capture').onclick = () => guarded(async () => { const r = await api('world-engine-capture'); toast('Engine capture: ' + r.path); });

 return {
  show: async () => {
   visible = true; observer?.observe($('#we-view'));
   if (!worlds.length) await loadWorlds().catch(err => { $('#we-world-note').textContent = err.message; });
   await refreshStatus().catch(() => {});
   if (running()) startPoll();
   await sendViewport(true);
  },
  hide: () => { visible = false; observer?.disconnect(); stopPoll(); sendViewport(true, true); },
 };
}
