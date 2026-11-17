// World Editor tool module · water (one quad per sector) on the real NTL engine, through the engine's own
// CNtlWorldFieldManager::OnWaterProc (see native/world-host-water.h). Contract: see world-engine-terrain.js.
//   Selection lives in the host (drawn as cyan sector outlines): click in the viewport with "Elegir con clic"
//   (Shift+clic suma/quita), or select every loaded sector inside a radius around the camera target.
//   "Actualizar" only sends the fields you touched since the last "Leer", so you can change just the colour of many
//   sectors without flattening their heights. Every apply is one undo step (shared "undo" of the edit workspace).
const BLEND = [[1, 'ZERO'], [2, 'ONE'], [3, 'SRCCOLOR'], [4, 'INVSRCCOLOR'], [5, 'SRCALPHA'], [6, 'INVSRCALPHA'], [7, 'DESTALPHA'],
 [8, 'INVDESTALPHA'], [9, 'DESTCOLOR'], [10, 'INVDESTCOLOR'], [11, 'SRCALPHASAT']];
const hex = c => '#' + c.map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('');
const rgb = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));

export function installWaterTools(ctx) {
 const {container, host, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool wtr-tool';
 const blendOptions = sel => BLEND.map(([v, n]) => `<option value="${v}" ${v === sel ? 'selected' : ''}>${n}</option>`).join('');
 box.innerHTML = `
  <div class="map-history-heading"><h2>Agua</h2></div>
  <div class="we-tool-row" role="group" aria-label="Selección de sectores">
   <button type="button" class="map-button" id="wtr-pick">Elegir con clic</button>
   <button type="button" class="map-button" id="wtr-radius-sel">Radio</button>
   <button type="button" class="map-button" id="wtr-clear">Limpiar</button>
   <button type="button" class="map-button" id="wtr-read">Leer</button>
  </div>
  <label>Radio de selección <input id="wtr-radius" type="range" min="32" max="512" step="16" value="160"> <span id="wtr-radius-v">160 m</span></label>
  <p id="wtr-sel" class="map-path">Sin sectores seleccionados.</p>
  <div class="wtr-grid">
   <label>Altura <input id="wtr-height" type="number" step="0.5" value="0"></label>
   <button type="button" class="map-button" id="wtr-terrain" title="Promedio del terreno de los sectores elegidos">Tomar del terreno</button>
   <label>Tipo <select id="wtr-type"></select></label>
   <label>Color <input id="wtr-color" type="color" value="#4696d2"></label>
   <label>Alpha <input id="wtr-alpha" type="range" min="0" max="255" value="180"> <span id="wtr-alpha-v">180</span></label>
   <label>Blend src <select id="wtr-src">${blendOptions(5)}</select></label>
   <label>Blend dst <select id="wtr-dst">${blendOptions(6)}</select></label>
   <label>Rotación <input id="wtr-rot" type="range" min="0" max="360" value="0"> <span id="wtr-rot-v">0°</span></label>
   <label>Escala UV <input id="wtr-scale" type="number" min="0.1" max="64" step="0.5" value="1"></label>
  </div>
  <details class="wtr-depth"><summary>Depth map</summary>
   <label>Tamaño <select id="wtr-dsize">${[16, 32, 64, 128].map(n => `<option ${n === 32 ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
   <label>Variación de altura <span class="wtr-pair"><input id="wtr-dv0" type="number" value="0"><input id="wtr-dv1" type="number" value="255"></span></label>
   <div class="we-tool-row">
    <button type="button" class="map-button" id="wtr-depth">Generar</button>
    <button type="button" class="map-button" id="wtr-nodepth">Quitar</button>
   </div>
  </details>
  <div class="map-native-actions">
   <button type="button" class="map-button" id="wtr-create">Crear</button>
   <button type="button" class="map-button" id="wtr-update">Actualizar</button>
   <button type="button" class="map-button" id="wtr-delete">Borrar</button>
   <button type="button" class="map-button" id="wtr-undo">Deshacer</button>
  </div>
  <p id="wtr-info" class="map-path">Crear usa todos los valores; Actualizar solo los campos que cambiaste. El motor ignora el blend al dibujar (lo guarda igual).</p>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let picking = false, types = [], typesAsked = false, touched = new Set(), selected = [];
 const FIELD_IDS = ['height', 'type', 'color', 'alpha', 'src', 'dst', 'rot', 'scale', 'dsize', 'dv0', 'dv1'];
 for (const id of FIELD_IDS) $('#wtr-' + id).addEventListener('input', () => touched.add(id));
 const labels = () => {
  $('#wtr-radius-v').textContent = $('#wtr-radius').value + ' m';
  $('#wtr-alpha-v').textContent = $('#wtr-alpha').value;
  $('#wtr-rot-v').textContent = $('#wtr-rot').value + '°';
 };
 for (const id of ['#wtr-radius', '#wtr-alpha', '#wtr-rot']) $(id).addEventListener('input', labels);

 function attrs(only) {
  const want = id => !only || only.has(id);
  const a = {};
  if (want('height')) a.height = Number($('#wtr-height').value);
  if (want('type') && $('#wtr-type').value !== '') a.type = Number($('#wtr-type').value);
  if (want('color')) a.color = rgb($('#wtr-color').value);
  if (want('alpha')) a.alpha = Number($('#wtr-alpha').value);
  if (want('src')) a.blendSrc = Number($('#wtr-src').value);
  if (want('dst')) a.blendDst = Number($('#wtr-dst').value);
  if (want('rot')) a.rotation = Number($('#wtr-rot').value) * Math.PI / 180;
  if (want('scale')) a.scale = Number($('#wtr-scale').value);
  if (want('dsize')) a.depthSize = Number($('#wtr-dsize').value);
  if (want('dv0') || want('dv1')) a.depthVar = [Number($('#wtr-dv0').value), Number($('#wtr-dv1').value)];
  return a;
 }
 function fill(a) {
  $('#wtr-height').value = Number(a.height).toFixed(2);
  if (a.type != null) $('#wtr-type').value = String(a.type);
  if (a.color) $('#wtr-color').value = hex(a.color);
  $('#wtr-alpha').value = a.alpha; $('#wtr-src').value = String(a.blendSrc); $('#wtr-dst').value = String(a.blendDst);
  $('#wtr-rot').value = String(Math.round(((a.rotation * 180 / Math.PI) % 360 + 360) % 360));
  $('#wtr-scale').value = a.scale; $('#wtr-dsize').value = String(a.depthSize);
  $('#wtr-dv0').value = a.depthVar?.[0] ?? 0; $('#wtr-dv1').value = a.depthVar?.[1] ?? 255;
  labels(); touched = new Set();
 }
 async function loadTypes() {
  if (types.length) return;
  const r = await host('water-types');
  types = r.types || [];
  $('#wtr-type').innerHTML = types.map(t => `<option value="${t.index}">${e(t.name)}${t.specular ? ' · specular' : ''}</option>`).join('');
  const lake = types.find(t => /^lake/i.test(t.name)); if (lake) $('#wtr-type').value = String(lake.index);
 }
 async function read() {
  if (!selected.length) { $('#wtr-info').textContent = 'Elegí al menos un sector.'; return; }
  const r = await host('water-get', {sectors: selected});
  const list = r.sectors || [];
  const withWater = list.filter(s => s.water);
  if (withWater[0]) fill(withWater[0].attr);
  $('#wtr-info').textContent = `${list.length} sectores · con agua: ${withWater.length}` +
   (withWater[0] ? ` · ${e(withWater[0].attr.typeName || '')} a ${Number(withWater[0].attr.height).toFixed(1)} m${withWater[0].depthMap ? ' · depth map' : ''}` : '') +
   (list.some(s => !s.loaded) ? ' · hay sectores fuera del anillo cargado (se ignoran)' : '');
 }
 async function apply(op, payload = {}) {
  if (!selected.length) { toast('Elegí sectores primero (clic o radio).', true); return; }
  const r = await host('water-apply', {op, sectors: selected, ...payload});
  const n = (r.changed || []).length, skipped = (r.skipped || []).length;
  toast(`Agua · ${op}: ${n} sector(es)` + (skipped ? ` · ${skipped} sin cambios` : '') + '.');
  if (n && op !== 'delete') await read(); else if (op === 'delete') $('#wtr-info').textContent = `Borrada en ${n} sector(es).`;
  touched = new Set();
 }
 const guard = fn => async () => { try { await fn(); } catch (err) { toast(err.message || String(err), true); } };
 $('#wtr-pick').onclick = guard(async () => { await loadTypes(); picking = !picking; await host('water-tool', {enabled: picking}); render(); });
 $('#wtr-radius-sel').onclick = guard(async () => {
  await loadTypes();
  const s = ctx.state()?.engine; if (!s?.pos) return;
  const r = await host('water-select', {x: s.pos[0], z: s.pos[2], radius: Number($('#wtr-radius').value), mode: 'replace'});
  selected = r.water?.selected || []; render(); await read();
 });
 $('#wtr-clear').onclick = guard(async () => { const r = await host('water-select', {mode: 'clear'}); selected = r.water?.selected || []; render(); });
 $('#wtr-read').onclick = guard(read);
 $('#wtr-terrain').onclick = guard(async () => {
  if (!selected.length) return;
  const r = await host('water-terrain', {sectors: selected});
  if (!r.count) return;
  $('#wtr-height').value = Number(r.avg).toFixed(2); touched.add('height');
  $('#wtr-info').textContent = `Terreno: mín ${r.min.toFixed(1)} · prom ${r.avg.toFixed(1)} · máx ${r.max.toFixed(1)} m (se usa el promedio).`;
 });
 $('#wtr-create').onclick = guard(() => apply('create', attrs()));
 $('#wtr-update').onclick = guard(() => touched.size ? apply('update', attrs(touched)) : toast('No cambiaste ningún campo.', true));
 $('#wtr-delete').onclick = guard(() => apply('delete'));
 $('#wtr-depth').onclick = guard(() => apply('depth', attrs(new Set(['alpha', 'dsize', 'dv0', 'dv1']))));
 $('#wtr-nodepth').onclick = guard(() => apply('nodepth'));
 $('#wtr-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Deshecho.' : 'No hay nada para deshacer.'); if (r.undone) await read(); });

 let lastKey = '';
 function render(state) {
  const editing = ctx.editing();
  const w = (state || ctx.state())?.engine?.water;
  if (w) {
   picking = !!w.tool; selected = w.selected || [];
   const key = selected.join(',');
   if (key !== lastKey) { lastKey = key; if (selected.length && editing) read().catch(() => {}); }
  }
  box.classList.toggle('we-disabled', !editing);
  box.querySelectorAll('button,select,input').forEach(el => el.disabled = !editing);
  $('#wtr-pick').classList.toggle('active', picking);
  $('#wtr-sel').textContent = selected.length ? `${selected.length} sector(es): ${selected.slice(0, 12).join(', ')}${selected.length > 12 ? '…' : ''}` : 'Sin sectores seleccionados.';
  if (editing && !typesAsked) { typesAsked = true; loadTypes().catch(() => { typesAsked = false; }); }
 }
 labels(); render();
 return {render, reset: () => { picking = false; selected = []; lastKey = ''; render(); }};
}
