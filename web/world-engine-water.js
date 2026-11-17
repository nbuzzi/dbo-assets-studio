// World Editor tool module · water (one quad per sector) on the real NTL engine, through the engine's own
// CNtlWorldFieldManager::OnWaterProc (see native/world-host-water.h). Contract: see world-engine-terrain.js.
//   Selection lives in the host (drawn as cyan sector outlines): click in the viewport with "Elegir con clic"
//   (Shift+clic suma/quita), or select every loaded sector inside a radius around the camera target.
//   "Actualizar" only sends the fields you touched since the last "Leer", so you can change just the colour of many
//   sectors without flattening their heights. Every apply is one undo step (shared "undo" of the edit workspace).
import {t as tr} from './i18n.js';
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
  <div class="map-history-heading"><h2>Water</h2></div>
  <div class="we-tool-row" role="group" aria-label="Sector selection">
   <button type="button" class="map-button" id="wtr-pick">Pick by click</button>
   <button type="button" class="map-button" id="wtr-radius-sel">Radius</button>
   <button type="button" class="map-button" id="wtr-clear">Clear</button>
   <button type="button" class="map-button" id="wtr-read">Read</button>
  </div>
  <label>Selection radius <input id="wtr-radius" type="range" min="32" max="512" step="16" value="160"> <span id="wtr-radius-v">160 m</span></label>
  <p id="wtr-sel" class="map-path">No sectors selected.</p>
  <div class="wtr-grid">
   <label>Height <input id="wtr-height" type="number" step="0.5" value="0"></label>
   <button type="button" class="map-button" id="wtr-terrain" title="Average terrain height of the selected sectors">Take from terrain</button>
   <label>Type <select id="wtr-type"></select></label>
   <label>Color <input id="wtr-color" type="color" value="#4696d2"></label>
   <label>Alpha <input id="wtr-alpha" type="range" min="0" max="255" value="180"> <span id="wtr-alpha-v">180</span></label>
   <label>Blend src <select id="wtr-src">${blendOptions(5)}</select></label>
   <label>Blend dst <select id="wtr-dst">${blendOptions(6)}</select></label>
   <label>Rotation <input id="wtr-rot" type="range" min="0" max="360" value="0"> <span id="wtr-rot-v">0°</span></label>
   <label>UV scale <input id="wtr-scale" type="number" min="0.1" max="64" step="0.5" value="1"></label>
  </div>
  <details class="wtr-depth"><summary>Depth map</summary>
   <label>Size <select id="wtr-dsize">${[16, 32, 64, 128].map(n => `<option ${n === 32 ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
   <label>Height variation <span class="wtr-pair"><input id="wtr-dv0" type="number" value="0"><input id="wtr-dv1" type="number" value="255"></span></label>
   <div class="we-tool-row">
    <button type="button" class="map-button" id="wtr-depth">Generate</button>
    <button type="button" class="map-button" id="wtr-nodepth">Remove</button>
   </div>
  </details>
  <div class="map-native-actions">
   <button type="button" class="map-button" id="wtr-create">Create</button>
   <button type="button" class="map-button" id="wtr-update">Update</button>
   <button type="button" class="map-button" id="wtr-delete">Delete</button>
   <button type="button" class="map-button" id="wtr-undo">Undo</button>
  </div>
  <p id="wtr-info" class="map-path">Create uses every value; Update only sends the fields you changed. The engine ignores blend when drawing (it still saves it).</p>`;
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
  if (!selected.length) { $('#wtr-info').textContent = 'Choose at least one sector.'; return; }
  const r = await host('water-get', {sectors: selected});
  const list = r.sectors || [];
  const withWater = list.filter(s => s.water);
  if (withWater[0]) fill(withWater[0].attr);
  $('#wtr-info').textContent = `${list.length} sectors · with water: ${withWater.length}` +
   (withWater[0] ? tr(` · ${e(withWater[0].attr.typeName || '')} at ${Number(withWater[0].attr.height).toFixed(1)} m${withWater[0].depthMap ? ' · depth map' : ''}`) : '') +
   (list.some(s => !s.loaded) ? tr(' · some sectors are outside the loaded ring (ignored)') : '');
 }
 async function apply(op, payload = {}) {
  if (!selected.length) { toast('Choose sectors first (click or radius).', true); return; }
  const r = await host('water-apply', {op, sectors: selected, ...payload});
  const n = (r.changed || []).length, skipped = (r.skipped || []).length;
  toast(`Water · ${op}: ${n} sector(s)` + (skipped ? ` · ${skipped} unchanged` : '') + '.');
  if (n && op !== 'delete') await read(); else if (op === 'delete') $('#wtr-info').textContent = `Deleted in ${n} sector(s).`;
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
  $('#wtr-info').textContent = `Terrain: min ${r.min.toFixed(1)} · avg ${r.avg.toFixed(1)} · max ${r.max.toFixed(1)} m (using the average).`;
 });
 $('#wtr-create').onclick = guard(() => apply('create', attrs()));
 $('#wtr-update').onclick = guard(() => touched.size ? apply('update', attrs(touched)) : toast('You did not change any field.', true));
 $('#wtr-delete').onclick = guard(() => apply('delete'));
 $('#wtr-depth').onclick = guard(() => apply('depth', attrs(new Set(['alpha', 'dsize', 'dv0', 'dv1']))));
 $('#wtr-nodepth').onclick = guard(() => apply('nodepth'));
 $('#wtr-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Undone.' : 'Nothing to undo.'); if (r.undone) await read(); });

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
  $('#wtr-sel').textContent = selected.length ? `${selected.length} sector(s): ${selected.slice(0, 12).join(', ')}${selected.length > 12 ? '…' : ''}` : 'No sectors selected.';
  if (editing && !typesAsked) { typesAsked = true; loadTypes().catch(() => { typesAsked = false; }); }
 }
 labels(); render();
 return {render, reset: () => { picking = false; selected = []; lastKey = ''; render(); }};
}
