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
  <span class="eyebrow">WORLD EDITOR · MOTOR NTL REAL</span>
  <h2>Ver y editar con el render del juego</h2>
  <p>Abre el mundo con el motor del cliente (NtlPresentation en modo herramienta). Lee una caché privada extraída de los paks; no escribe en el cliente ni en runtime.</p>
  <span class="map-native-badge">Spawns: candidato en carpeta nueva con auditoría</span>
 </div>
 <div class="map-native-picker">
  <label for="we-world">Mundo</label>
  <input id="we-filter" class="we-filter" placeholder="Filtrar mundos (dbo_main…)">
  <select id="we-world" size="9"></select>
  <p id="we-world-note" class="map-path">Leyendo ter.pak…</p>
  <p id="we-prepare-note" class="map-path we-prepare-note" hidden></p>
  <div class="map-native-actions">
   <button id="we-prepare" class="map-button" type="button" disabled>Preparar caché</button>
   <button id="we-open" class="map-button map-button-primary" type="button" disabled>Abrir en el motor</button>
   <button id="we-close" class="map-button map-button-secondary" type="button" disabled>Cerrar motor</button>
  </div>
  <label class="we-check"><input id="we-edit" type="checkbox"> Abrir para editar (workspace privado: copia del mundo)</label>
  <div class="map-native-actions">
   <button id="we-save" class="map-button" type="button" disabled>Guardar en workspace</button>
   <button id="we-export" class="map-button map-button-secondary" type="button" disabled>Exportar candidato…</button>
  </div>
  <p id="we-edit-note" class="map-path">Modo visor: no escribe nada. La edición trabaja sobre una copia; el candidato lleva solo lo que cambió (audit.json).</p>
  <label>Ir a posición (x, z)</label>
  <div class="map-picker-row we-goto">
   <input id="we-x" type="number" step="1" placeholder="x"><input id="we-z" type="number" step="1" placeholder="z">
   <button id="we-goto" class="map-button" type="button" disabled>Ir</button>
  </div>
 </div>
 <div id="we-tools" class="we-tools"></div>
 <p id="we-pick-note" class="map-path"></p>
 </div>
 <div class="map-legacy-hint"><strong>Controles del visor</strong><small>Botón derecho: orbitar · rueda: zoom · botón medio o Shift+derecho: desplazar · WASD/QE: mover (clic en el visor para el foco).</small></div>`;
 main.innerHTML = `
 <header class="map-workshop-header"><div><span class="eyebrow">MOTOR NTL · CNtlPLWorldEntity + CNtlPLVisualManager</span><h2 id="we-title">Elegí un mundo</h2><p id="we-subtitle">Render real del cliente: terreno, objetos, agua, cielo, niebla y sombras.</p></div>
  <div class="we-toolbar">
   <div class="we-cams" role="group" aria-label="Cámara">
    ${['free','top','front','back','left','right'].map(m => `<button type="button" class="map-button" data-we-cam="${m}">${{free:'Libre',top:'Top',front:'Frente',back:'Atrás',left:'Izq.',right:'Der.'}[m]}</button>`).join('')}
   </div>
   <label class="we-check"><input id="we-overlay" type="checkbox" checked> Stats del motor</label>
   <button id="we-capture" class="map-button" type="button" disabled>Captura</button>
  </div>
 </header>
 <div class="we-stage"><div id="we-view" class="we-view"><p id="we-placeholder" class="map-empty">El motor se dibuja aquí.</p></div></div>
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
  $('#we-prepare').textContent = preparing ? 'Preparando…' : w?.cached ? 'Caché lista ✓' : 'Preparar caché';
  $('#we-open').title = w && !w.cached ? 'Primero prepará la caché de este mundo' : '';
  $('#we-open').disabled = busy || !w?.cached;
  $('#we-close').disabled = busy || !running();
  $('#we-goto').disabled = busy || !running();
  $('#we-capture').disabled = busy || !running();
  $('#we-save').disabled = busy || !editing();
  $('#we-export').disabled = busy || !editing();
  const es = status?.engine;
  if (editing() && es) $('#we-edit-note').textContent = `Editando en workspace · fields con cambios sin guardar: ${es.dirtyFields ?? 0} · editados en la sesión: ${es.editedFields ?? 0} · deshacer: ${es.undo ?? 0}`;
  for (const t of tools) t.render?.(status);
  main.querySelectorAll('[data-we-cam]').forEach(b => { b.disabled = busy || !running(); b.classList.toggle('active', b.dataset.weCam === (status?.engine?.camera || '')); });
  $('#we-placeholder').hidden = running();
  $('#we-title').textContent = status?.world ? status.world : (selectedWorld || 'Elegí un mundo');
  const s = status?.engine;
  $('#we-stats').textContent = s ? `${status.worldReady ? 'listo' : 'cargando…'} · cámara ${s.camera} · pos ${fmt(s.pos?.[0])}, ${fmt(s.pos?.[1])}, ${fmt(s.pos?.[2])} · field ${s.field} · sector ${s.sector} · sectores ${s.frustumSectors}/${s.visibleSectors} · polígonos ${s.polys} · ${fmt(s.fps, 0)} fps · spawns ${s.spawns}` : (status?.state === 'error' ? 'Error: ' + (status.diagnostics || []).slice(-2).join(' · ') : '—');
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
  $('#we-world-note').textContent = `${worlds.length} mundos en ${r.pack} · caché ${r.cacheRoot}`;
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
  if (p.terrain) $('#we-pick-note').textContent = `Terreno ${fmt(p.terrain[0])}, ${fmt(p.terrain[1])}, ${fmt(p.terrain[2])}`;
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
  show(`Preparando ${world}: leyendo los paks del cliente (solo lectura)…`);
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
    show(`Preparando ${world}: ${st.prepare || 'trabajando…'} · ${secs()} s`);
   }
   if (st.prepareError) throw new Error(st.prepareError);
   const r = st.prepareResult || {world};
   const n = (r.steps || []).reduce((a, s) => a + (s.extracted || 0), 0), bad = (r.steps || []).reduce((a, s) => a + (s.failed || 0), 0);
   const w = worlds.find(x => x.world === world); if (w) w.cached = true;
   renderWorlds();
   show(`Caché lista para ${world}: ${n} archivos extraídos${bad ? `, ${bad} ilegibles` : ''} en ${secs()} s. Ya podés abrirlo en el motor.`);
   toast(`Caché lista para ${world}.`);
  } catch (err) {
   show(`No se pudo preparar ${world}: ${err.message || err}`, true);
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
  if (args.edit) toast('Creando el workspace de edición (copia del mundo)…');
  status = await api('world-engine-open', args);
  startPoll();
  await sendViewport(true);
  if (args.edit) { for (let i = 0; i < 240 && !status?.worldReady; i++) { await new Promise(r => setTimeout(r, 500)); await refreshStatus(); } await api('world-engine-before'); }
 });
 $('#we-save').onclick = () => guarded(async () => { const r = await api('world-engine-save'); toast(`Guardado en el workspace: fields ${(r.saved || []).join(', ') || '—'}.`); });
 $('#we-export').onclick = () => guarded(async () => {
  const r = await api('world-engine-export'); if (!r) return;
  toast(`Candidato: ${r.filesChanged} archivos cambiados (${r.rewrittenIdentical} reescritos idénticos), solo lo editado: ${r.onlyEditedChanged ? 'sí' : 'NO'}.`);
 });
 $('#we-close').onclick = () => guarded(async () => { await sendViewport(true, true); status = await api('world-engine-close'); });
 $('#we-goto').onclick = () => guarded(async () => { await api('world-engine-goto', {x: Number($('#we-x').value), y: 0, z: Number($('#we-z').value), ground: true, dist: 120}); });
 main.querySelectorAll('[data-we-cam]').forEach(b => b.onclick = () => guarded(async () => { await api('world-engine-camera', b.dataset.weCam === 'free' ? {mode: 'free'} : {mode: b.dataset.weCam}); }));
 $('#we-overlay').onchange = () => guarded(async () => { await api('world-engine-overlay', {enabled: $('#we-overlay').checked}); });
 $('#we-capture').onclick = () => guarded(async () => { const r = await api('world-engine-capture'); toast('Captura del motor: ' + r.path); });

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
