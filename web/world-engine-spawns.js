// World Editor tool module · spawns (mobs and NPCs) on the real NTL engine.
// The open map's spawn tables come from table_world_data; mob and NPC tables can be loaded together.
// Palette = Table_Mob_Data / Table_NPC_Data (SpawnCatalog). Every edit goes through SpawnEditSession
// (validation, undo/redo, candidate with audit), the same engine as the Three.js spawn editor.
const fmt = (n, d = 1) => Number.isFinite(Number(n)) ? Number(n).toFixed(d) : '—';
const KIND = {mob: 'Mobs', npc: 'NPC'};

export function installSpawnTools(ctx) {
 const {container, api, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool we-spawns';
 box.innerHTML = `
  <div class="map-history-heading"><h2>Spawns · mobs y NPC</h2></div>
  <div id="ws-tables" class="ws-tables"><p class="map-path">Abrí un mundo para ver sus tablas de spawn.</p></div>
  <div class="map-native-actions"><button id="ws-other" type="button" class="map-button">Otra tabla…</button></div>
  <div id="ws-layers" class="ws-layers"></div>
  <div class="ws-palette">
   <div class="we-tool-row" role="group" aria-label="Tipo">
    <button type="button" class="map-button" data-ws-kind="mob">Mobs</button><button type="button" class="map-button" data-ws-kind="npc">NPC</button>
   </div>
   <input id="ws-query" placeholder="Buscar por nombre o id (ej. wolf, 1411101)">
   <div class="ws-levels"><label>Nivel <input id="ws-min" type="number" min="0" max="255" value="0"></label><label>a <input id="ws-max" type="number" min="0" max="255" value="255"></label></div>
   <div id="ws-results" class="ws-results"><p class="map-empty">Cargá una tabla para usar la paleta.</p></div>
   <div class="ws-place">
    <label class="we-check"><input id="ws-placing" type="checkbox"> Colocar con clic en el terreno</label>
    <label>Orientación <input id="ws-yaw" type="number" step="15" value="0"> °</label>
   </div>
   <p id="ws-chosen" class="map-path">Ningún mob/NPC elegido.</p>
  </div>
  <div id="ws-selection" class="ws-selection"><p class="map-empty">Clic en un marcador del motor para seleccionarlo · Shift+clic en el terreno lo mueve.</p></div>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let tables = null, tablesWorld = '', layers = [], kind = 'mob', chosen = null, selected = null, searchTimer = null;
 const guard = fn => async (...a) => { try { await fn(...a); } catch (err) { toast(err.message || String(err), true); } };
 const loaded = k => layers.some(l => l.kind === k);

 async function refreshLayers() { layers = await api('world-engine-spawn-layers'); renderLayers(); }
 function renderLayers() {
  $('#ws-layers').innerHTML = layers.map(l => {
   const st = l.stats || {};
   return `<div class="ws-layer"><strong>${KIND[l.kind]}</strong> · ${e(l.rows)} filas <small>${e(l.sourcePath.split('\\').pop())}</small>
    <small>${st.modified || 0} modificados · ${st.added || 0} nuevos · ${st.deleted || 0} borrados</small>
    <div class="we-tool-row"><button type="button" class="map-button" data-ws-undo="${l.kind}">Deshacer</button><button type="button" class="map-button" data-ws-redo="${l.kind}">Rehacer</button>
    <button type="button" class="map-button" data-ws-save="${l.kind}">Guardar candidato…</button><button type="button" class="map-button" data-ws-unload="${l.kind}">Quitar</button></div></div>`;
  }).join('');
  box.querySelectorAll('[data-ws-undo]').forEach(b => b.onclick = guard(async () => { await api('world-engine-spawn-op', {kind: b.dataset.wsUndo, op: 'undo'}); selected = null; await refreshLayers(); renderSelection(); }));
  box.querySelectorAll('[data-ws-redo]').forEach(b => b.onclick = guard(async () => { await api('world-engine-spawn-op', {kind: b.dataset.wsRedo, op: 'redo'}); await refreshLayers(); }));
  box.querySelectorAll('[data-ws-save]').forEach(b => b.onclick = guard(async () => { const r = await api('world-engine-spawn-save', {kind: b.dataset.wsSave}); if (r) toast('Candidato de spawns creado (carpeta nueva con auditoría).'); }));
  box.querySelectorAll('[data-ws-unload]').forEach(b => b.onclick = guard(async () => { await api('world-engine-spawn-unload', {kind: b.dataset.wsUnload}); if (selected?.kind === b.dataset.wsUnload) selected = null; await refreshLayers(); renderSelection(); }));
  box.querySelectorAll('[data-ws-kind]').forEach(b => { b.classList.toggle('active', b.dataset.wsKind === kind); b.disabled = !loaded(b.dataset.wsKind); });
 }
 async function load(path, k) {
  const r = await api('world-engine-spawn-load', {path, kind: k});
  kind = r.kind; chosen = null; selected = null;
  toast(`${r.rows} spawns de ${KIND[r.kind]} cargados y dibujados en el motor.${r.catalogNote ? ' ' + r.catalogNote : ''}`);
  await refreshLayers(); await search(); renderSelection(); renderChosen();
 }
 async function loadTables(world) {
  tablesWorld = world;
  try { tables = await api('world-engine-spawn-tables', {world}); } catch (err) { $('#ws-tables').innerHTML = `<p class="map-path">${e(err.message)}</p>`; return; }
  const rows = tables.rows.length ? tables.rows : [{worldId: '—', name: world + ' (por nombre)', mobPath: tables.guess.mobPath, npcPath: tables.guess.npcPath, mobTable: '', npcTable: ''}];
  $('#ws-tables').innerHTML = rows.map((r, i) => `<div class="ws-table-row"><span><strong>${e(r.name || r.internalName || world)}</strong> <small>world ${e(r.worldId)}</small></span>
    <button type="button" class="map-button" data-ws-load="mob" data-ws-row="${i}" ${r.mobPath ? '' : 'disabled title="Sin tabla de mobs"'}>Mobs</button>
    <button type="button" class="map-button" data-ws-load="npc" data-ws-row="${i}" ${r.npcPath ? '' : 'disabled title="Sin tabla de NPC"'}>NPC</button></div>`).join('')
   + `<p class="map-path">Tablas del servidor (solo lectura): ${e(tables.tablesFolder)}</p>`;
  box.querySelectorAll('[data-ws-load]').forEach(b => b.onclick = guard(async () => { const r = rows[Number(b.dataset.wsRow)]; await load(b.dataset.wsLoad === 'npc' ? r.npcPath : r.mobPath, b.dataset.wsLoad); }));
 }
 async function search() {
  if (!loaded(kind)) { $('#ws-results').innerHTML = '<p class="map-empty">Cargá una tabla para usar la paleta.</p>'; return; }
  const r = await api('world-engine-spawn-catalog', {kind, query: $('#ws-query').value.trim(), minLevel: Number($('#ws-min').value) || 0, maxLevel: Number($('#ws-max').value) || 255, limit: 60});
  const notes = (r.notes || []).join(' · ');
  $('#ws-results').innerHTML = (r.rows || []).map(x => `<button type="button" class="ws-result ${chosen?.id === x.id ? 'active' : ''}" data-ws-ref="${x.id}" data-ws-name="${e(x.name)}" data-ws-level="${x.level}">
    <span>${e(x.name || '(sin nombre)')}</span><small>lv ${e(x.level)} · ${e(x.id)}${kind === 'mob' && x.level > r.levelCapWarning ? ' · ⚠ > cap ' + r.levelCapWarning : ''}</small></button>`).join('')
   || `<p class="map-empty">Sin resultados.${notes ? ' ' + e(notes) : ''}</p>`;
  box.querySelectorAll('[data-ws-ref]').forEach(b => b.onclick = () => { chosen = {id: Number(b.dataset.wsRef), name: b.dataset.wsName, level: b.dataset.wsLevel, kind}; $('#ws-placing').checked = true; renderChosen(); search(); });
 }
 function renderChosen() { $('#ws-chosen').textContent = chosen ? `Elegido: ${chosen.name || chosen.id} (${KIND[chosen.kind]}, lv ${chosen.level}) · ${$('#ws-placing').checked ? 'clic en el terreno para spawnear' : 'activá "Colocar con clic"'}` : 'Ningún mob/NPC elegido.'; }
 function renderSelection() {
  const s = selected, el = $('#ws-selection');
  if (!s) { el.innerHTML = '<p class="map-empty">Clic en un marcador del motor para seleccionarlo · Shift+clic en el terreno lo mueve.</p>'; return; }
  const field = (k, label) => `<label>${label}<input data-ws-field="${k}" type="number" value="${e(s[k] ?? 0)}"></label>`;
  el.innerHTML = `<dl class="we-spawn"><dt>${KIND[s.kind]}</dt><dd>${e(s.name || '')} ${s.level != null ? 'lv ' + e(s.level) : ''}</dd><dt>ref</dt><dd>${e(s.refTblidx)}</dd><dt>tblidx</dt><dd>${e(s.tblidx)} · uid ${e(s.uid)}</dd>
   <dt>posición</dt><dd>${fmt(s.x)}, ${fmt(s.y)}, ${fmt(s.z)}</dd></dl>
   <div class="ws-fields">${field('range', 'Rango')}${field('quantity', 'Cantidad')}${field('coolTime', 'Respawn (s)')}${field('moveType', 'Movimiento')}${field('wander', 'Wander')}${field('moveRange', 'Rango mov.')}</div>
   <div class="we-tool-row"><button type="button" class="map-button" id="ws-apply">Aplicar</button><button type="button" class="map-button" id="ws-rot">Rotar 45°</button>
   <button type="button" class="map-button" id="ws-clone">Clonar</button><button type="button" class="map-button" id="ws-del">Borrar</button><button type="button" class="map-button" id="ws-focus">Enfocar</button></div>`;
  const op = async (o, extra = {}) => { const r = await api('world-engine-spawn-op', {kind: s.kind, op: o, uids: [s.uid], ...extra}); await refreshLayers(); return r; };
  $('#ws-apply').onclick = guard(async () => {
   const fields = {}; box.querySelectorAll('[data-ws-field]').forEach(i => { const v = Number(i.value); if (Number(s[i.dataset.wsField]) !== v) fields[i.dataset.wsField] = v; });
   if (!Object.keys(fields).length) return toast('No hay cambios en los campos.');
   await op('set', {fields}); await reselect(s.kind, s.uid); toast('Campos aplicados al spawn.');
  });
  $('#ws-rot').onclick = guard(async () => { await op('rotate', {degrees: 45}); });
  $('#ws-clone').onclick = guard(async () => { await op('clone', {dx: 3, dz: 3}); toast('Spawn clonado a 3 m.'); });
  $('#ws-del').onclick = guard(async () => { await op('delete'); selected = null; renderSelection(); toast('Spawn borrado (Deshacer lo recupera).'); });
  $('#ws-focus').onclick = guard(async () => { await api('world-engine-spawn-select', {kind: s.kind, uids: [s.uid], focus: true}); });
 }
 async function reselect(k, uid) { const r = await api('world-engine-spawn-select', {kind: k, uids: [uid]}); selected = r.selected?.[0] || null; renderSelection(); }

 $('#ws-other').onclick = guard(async () => { const p = await api('world-engine-spawns-pick'); if (p?.path) await load(p.path); });
 box.querySelectorAll('[data-ws-kind]').forEach(b => b.onclick = guard(async () => { kind = b.dataset.wsKind; chosen = null; renderLayers(); renderChosen(); await search(); }));
 $('#ws-query').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(guard(search), 250); };
 $('#ws-min').onchange = $('#ws-max').onchange = guard(search);
 $('#ws-placing').onchange = renderChosen;

 return {
  render(status) {
   const running = !!status?.running, world = status?.world || '';
   box.classList.toggle('we-disabled', !running);
   if (running && world && world !== tablesWorld) { loadTables(world); refreshLayers().catch(() => {}); }
  },
  // Picks from the engine: place, move (Shift) or select. Returns true when consumed.
  async onPick(p) {
   if (!layers.length) return false;
   if (chosen && $('#ws-placing').checked && p.terrain && p.uid == null && !p.shift) {
    const [x, y, z] = p.terrain;
    await api('world-engine-spawn-add', {kind: chosen.kind, ref: chosen.id, x, y, z, yaw: Number($('#ws-yaw').value) || 0});
    await refreshLayers();
    toast(`${chosen.name || chosen.id} spawneado en ${fmt(x)}, ${fmt(z)}.`);
    return true;
   }
   if (p.shift && selected && p.terrain) {
    const [x, y, z] = p.terrain;
    await api('world-engine-spawn-move', {kind: selected.kind, uid: selected.uid, x, y, z});
    await reselect(selected.kind, selected.uid); await refreshLayers();
    return true;
   }
   if (p.uid != null) { await reselect(p.kind || 'mob', p.uid); return true; }
   return false;
  },
 };
}
