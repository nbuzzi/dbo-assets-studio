// World Editor tool module · field props, tile props, game property volumes and sector PVS (host commands prop-* / pvs-*).
// Field props are written only to LOADED fields (memory -> SaveField). Light and sound belong to their own tool: read-only.
// PVS: "Construir PVS" recalculates the selected sectors with CNtlWorldSector::CreateSectorPVS; saving rewrites
// P\------- with only those sector records changed (the candidate audit checks it record by record).
import {t as tr} from './i18n.js';
const GROUP_FLAGS = [
 [0, 'Town'], [1, 'Swim'], [2, 'No PC PvP'], [3, 'No PC shop'], [4, 'No movement (all)'],
 [5, 'No movement (mobs)'], [6, 'Lava'], [7, 'Lava + swim'], [8, 'Dragon Ball Scramble safe zone'], [9, 'Free PvP'],
 [13, 'Mini indoor (tile)'], [14, 'Mini indoor (door) / transparency'], [15, 'World shadow'],
];
const hex = n => '0x' + (Number(n) >>> 0).toString(16).toUpperCase().padStart(8, '0');
const toHexColor = c => '#' + (c || [0, 0, 0]).slice(0, 3).map(v => Number(v).toString(16).padStart(2, '0')).join('');
const fromHexColor = s => [1, 3, 5].map(i => parseInt(s.slice(i, i + 2), 16));
const num = (v, d = 0) => { const n = Number(v); return Number.isFinite(n) ? n : d; };

export function installPropsTools(ctx) {
 const {container, host, api, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool wp-props';
 box.innerHTML = `
  <div class="map-history-heading"><h2>Properties · tiles · PVS</h2></div>
  <details class="wp-block" open>
   <summary>Field <span id="wp-field-id" class="wp-dim">—</span></summary>
   <div class="map-native-actions"><button id="wp-read" type="button" class="map-button">Read camera field</button></div>
   <p id="wp-field-note" class="map-path">Only loaded fields can be edited (the ring around the camera).</p>
   <div id="wp-form" class="wp-form" hidden>
    <label>Name (map index) <input id="wp-name" maxlength="63"></label>
    <fieldset><legend>Fog</legend>
     <div class="wp-row"><input id="wp-fog-color" type="color"><label>Near <input id="wp-fog-near" type="number" step="1"></label><label>Far <input id="wp-fog-far" type="number" step="1"></label></div>
    </fieldset>
    <fieldset><legend>Sky</legend>
     <div class="wp-row"><label>Mode <input id="wp-sky-mode" type="number" step="1"></label><label>New value <input id="wp-sky-new" type="number" step="1"></label></div>
     <div class="wp-row"><input id="wp-sky-c0" type="color" title="Color 1"><input id="wp-sky-c1" type="color" title="Color 2"><input id="wp-sky-c2" type="color" title="Color 3"></div>
     <div class="wp-row"><label>Base texture <input id="wp-sky-tex" maxlength="9"></label><label>Speed <input id="wp-sky-speed" type="number" step="0.01"></label></div>
     <div class="wp-row"><label>Blend 1 <input id="wp-sky-b0" maxlength="9"></label><label>Blend 2 <input id="wp-sky-b1" maxlength="9"></label></div>
     <div class="wp-row"><label>Speed 1 <input id="wp-sky-s0" type="number" step="0.01"></label><label>Speed 2 <input id="wp-sky-s1" type="number" step="0.01"></label></div>
    </fieldset>
    <fieldset><legend>Bloom · color · weather</legend>
     <div class="wp-row"><label>Mono power <input id="wp-bloom" type="number" step="0.1"></label><label>Field color <input id="wp-fcolor" type="color"></label></div>
     <label>Weather (5 indices, -1 = none) <input id="wp-weather" placeholder="-1,-1,-1,-1,-1"></label>
    </fieldset>
    <fieldset><legend>Heat haze</legend>
     <div class="wp-row"><label class="we-check"><input id="wp-hh-on" type="checkbox"> Enabled</label><label>m/s <input id="wp-hh-mps" type="number" step="0.01"></label></div>
     <div class="wp-row"><label>Extent <input id="wp-hh-ext" type="number" step="0.01"></label><label>Random <input id="wp-hh-rand" type="number" step="0.01"></label><label>Scale <input id="wp-hh-scale" type="number" step="0.01"></label></div>
    </fieldset>
    <fieldset><legend>Specular</legend>
     <div class="wp-row"><label class="we-check"><input id="wp-sp-on" type="checkbox"> Enabled</label><label>Power <input id="wp-sp-pow" type="number" min="0" max="255"></label></div>
     <label>Direction x,y,z <input id="wp-sp-dir"></label><label>Scale x,y <input id="wp-sp-scale"></label>
    </fieldset>
    <fieldset><legend>Planets</legend><div id="wp-planets" class="wp-planets"></div></fieldset>
    <p id="wp-ro" class="map-path"></p>
    <label>Also apply to fields (optional) <input id="wp-fields" placeholder="3058, 3121"></label>
    <div class="map-native-actions"><button id="wp-apply" type="button" class="map-button map-button-primary">Apply to field</button><button id="wp-undo" type="button" class="map-button">Undo</button></div>
   </div>
  </details>
  <details class="wp-block">
   <summary>Tiles (attributes and material)</summary>
   <div class="wp-row"><label>Overlay <select id="wp-ov"><option value="none">None</option><option value="bit">Attribute</option><option value="material">Material</option></select></label>
    <label>Attribute <select id="wp-flag">${GROUP_FLAGS.map(([b, n]) => `<option value="${b}">${b} · ${e(n)}</option>`).join('')}<option value="custom">Mask…</option></select></label></div>
   <label id="wp-mask-row" hidden>Mask (hex) <input id="wp-mask" value="0x00000010"></label>
   <div class="we-tool-row" role="group" aria-label="Tile brush"><button type="button" class="map-button" data-wp-tool="none">No brush</button><button type="button" class="map-button" data-wp-tool="tile">Tile brush</button></div>
   <div class="wp-row"><label>Target <select id="wp-target"><option value="bit">Attribute</option><option value="material">Material</option></select></label>
    <label>Operation <select id="wp-op"><option value="set">Set</option><option value="clear">Remove</option></select></label>
    <label>Material <input id="wp-mat" type="number" min="0" max="255" value="1"></label></div>
   <label>Radius <input id="wp-radius" type="range" min="1" max="40" value="6"> <span id="wp-radius-v">6 m</span></label>
   <div class="map-native-actions"><button id="wp-tile-read" type="button" class="map-button">Tile at camera</button><button id="wp-tile-stats" type="button" class="map-button">Field summary</button></div>
   <p id="wp-tile-note" class="map-path">Click and drag in the engine with the brush active. Attributes are used by the client (GetWorldNormalAttribute) and by navigation.</p>
  </details>
  <details class="wp-block">
   <summary>Sector PVS</summary>
   <div class="wp-row"><label>Area <select id="wp-pvs-area"><option value="field">Camera field (4 sectors)</option><option value="radius">Radius around the camera</option></select></label>
    <label>Radius (m) <input id="wp-pvs-r" type="number" value="200" min="64" max="600" step="32"></label></div>
   <div class="wp-row"><label>Character height (eps) <input id="wp-pvs-eps" type="number" value="10" step="1"></label>
    <label>Layer <select id="wp-pvs-layer">${[0,1,2,3,4,5].map(i => `<option value="${i}">${i} (+${i * 10} m)</option>`).join('')}</select></label></div>
   <label class="we-check"><input id="wp-pvs-ov" type="checkbox"> Visibility overlay (13×13 grid in the engine)</label>
   <div class="map-native-actions"><button id="wp-pvs-build" type="button" class="map-button map-button-primary">Build area PVS</button><button id="wp-pvs-read" type="button" class="map-button">Camera sector</button></div>
   <div id="wp-pvs-grid" class="wp-pvs-grid" aria-label="Sectors visible from the current sector"></div>
   <p id="wp-pvs-note" class="map-path">Saving rewrites P\\------- only in the recalculated sectors; the rest stays byte for byte.</p>
  </details>
  <details class="wp-block">
   <summary>Game property (indoor volumes)</summary>
   <div class="map-native-actions"><button id="wp-gp-list" type="button" class="map-button">List around the camera</button></div>
   <div id="wp-gp" class="wp-gp"><p class="map-empty">Only indoor worlds (RpWorld) load volumes.</p></div>
  </details>
  <details class="wp-block">
   <summary>Navigation</summary>
   <p class="map-path">The engine's PathEngine export is not usable (the NaviModel models are missing and the pak's swap\\pe heights are 4 bytes). Server navigation comes from <code>asset terrain-navi-plan</code> on the workspace:</p>
   <pre id="wp-nav" class="wp-pre">${e(tr('Open an edit workspace.'))}</pre>
  </details>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let field = -1, loaded = null, tool = 'none', pvsBusy = false, navShown = false;
 const guard = fn => async () => { try { await fn(); } catch (err) { toast(err.message || String(err), true); } };
 const enginePos = () => ctx.state()?.engine?.pos || null;
 const mask = () => $('#wp-flag').value === 'custom' ? ($('#wp-mask').value.trim() || '0') : (1 << Number($('#wp-flag').value)) >>> 0;

 // ---- field props ----
 function fill(p) {
  $('#wp-name').value = p.name || '';
  $('#wp-fog-color').value = toHexColor(p.fog.color); $('#wp-fog-near').value = p.fog.near; $('#wp-fog-far').value = p.fog.far;
  $('#wp-sky-mode').value = p.sky.mode; $('#wp-sky-new').value = p.sky.newSkyValue;
  p.sky.colors.forEach((c, i) => $(`#wp-sky-c${i}`).value = toHexColor(c));
  $('#wp-sky-tex').value = p.sky.baseTexture; $('#wp-sky-speed').value = p.sky.baseSpeed;
  $('#wp-sky-b0').value = p.sky.blendTextures[0]; $('#wp-sky-b1').value = p.sky.blendTextures[1];
  $('#wp-sky-s0').value = p.sky.blendSpeeds[0]; $('#wp-sky-s1').value = p.sky.blendSpeeds[1];
  $('#wp-bloom').value = p.bloom.monoPower; $('#wp-fcolor').value = toHexColor(p.fieldColor); $('#wp-weather').value = p.weather.join(',');
  $('#wp-hh-on').checked = p.heatHaze.enabled; $('#wp-hh-mps').value = p.heatHaze.metersPerSec; $('#wp-hh-ext').value = p.heatHaze.extent;
  $('#wp-hh-rand').value = p.heatHaze.extentRand; $('#wp-hh-scale').value = p.heatHaze.scale;
  $('#wp-sp-on').checked = p.specular.enabled; $('#wp-sp-pow').value = p.specular.power; $('#wp-sp-dir').value = p.specular.direction.join(','); $('#wp-sp-scale').value = p.specular.scale.join(',');
  $('#wp-planets').innerHTML = p.planet.map((pl, i) => `<div class="wp-row" data-planet="${i}"><label>Tex ${i + 1} <input data-k="texture" maxlength="9" value="${e(pl.texture)}"></label><label>Scale <input data-k="scale" type="number" value="${pl.scale}"></label><label>Pos <input data-k="pos" value="${pl.pos.map(v => +Number(v).toFixed(3)).join(',')}"></label><label class="we-check"><input data-k="lensFlare" type="checkbox" ${pl.lensFlare ? 'checked' : ''}> Flare</label></div>`).join('');
  const l = p.light, s = p.sound;
  $('#wp-ro').textContent = `Read-only · ambient light ${l.ambient.map(v => Number(v).toFixed(2)).join('/')} · BGM ${s.bgm} · ambient ${s.env} (light and sound tool).`;
 }
 const vec = (s, n) => { const a = String(s).split(',').map(x => Number(x.trim())); if (a.length !== n || a.some(x => !Number.isFinite(x))) throw new Error(`Expected ${n} comma-separated numbers.`); return a; };
 function collect() {
  const planet = [...box.querySelectorAll('[data-planet]')].map(r => ({texture: r.querySelector('[data-k=texture]').value.trim(), scale: num(r.querySelector('[data-k=scale]').value, 100), pos: vec(r.querySelector('[data-k=pos]').value, 3), lensFlare: r.querySelector('[data-k=lensFlare]').checked}));
  return {
   name: $('#wp-name').value.trim(),
   fog: {color: fromHexColor($('#wp-fog-color').value), near: num($('#wp-fog-near').value), far: num($('#wp-fog-far').value)},
   sky: {mode: num($('#wp-sky-mode').value), newSkyValue: num($('#wp-sky-new').value), colors: [0, 1, 2].map(i => fromHexColor($(`#wp-sky-c${i}`).value)),
    baseTexture: $('#wp-sky-tex').value.trim(), baseSpeed: num($('#wp-sky-speed').value), blendTextures: [$('#wp-sky-b0').value.trim(), $('#wp-sky-b1').value.trim()], blendSpeeds: [num($('#wp-sky-s0').value), num($('#wp-sky-s1').value)]},
   bloom: {monoPower: num($('#wp-bloom').value)}, fieldColor: fromHexColor($('#wp-fcolor').value), weather: vec($('#wp-weather').value, 5),
   heatHaze: {enabled: $('#wp-hh-on').checked, metersPerSec: num($('#wp-hh-mps').value), extent: num($('#wp-hh-ext').value), extentRand: num($('#wp-hh-rand').value), scale: num($('#wp-hh-scale').value)},
   specular: {enabled: $('#wp-sp-on').checked, power: num($('#wp-sp-pow').value), direction: vec($('#wp-sp-dir').value, 3), scale: vec($('#wp-sp-scale').value, 2)},
   planet,
  };
 }
 // Only groups that differ from what was read are sent, so untouched groups keep their exact bytes.
 const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
 const approxSame = (a, b) => typeof a === 'number' && typeof b === 'number' ? Math.abs(a - b) < 1e-4 * Math.max(1, Math.abs(b))
  : Array.isArray(a) && Array.isArray(b) ? a.length === b.length && a.every((x, i) => approxSame(x, b[i]))
  : a && b && typeof a === 'object' && typeof b === 'object' ? Object.keys(a).every(k => approxSame(a[k], b[k])) : same(a, b);
 function changedGroups() {
  const next = collect(), out = {};
  for (const [k, v] of Object.entries(next)) if (!approxSame(v, loaded[k])) out[k] = v;
  return out;
 }
 async function read() {
  const pos = enginePos(); if (!pos) throw new Error('Open a world in the engine.');
  const r = await host('prop-get', {x: pos[0], z: pos[2]});
  field = r.field; loaded = r.props || null;
  $('#wp-field-id').textContent = `${r.field}${r.loaded ? '' : ' (not loaded)'}`;
  $('#wp-form').hidden = !loaded; if (loaded) fill(loaded);
  $('#wp-field-note').textContent = r.loaded ? `Field ${r.field} · origin ${r.spos.map(v => Math.round(v)).join(', ')} · ${r.size} m.` : 'The field is not loaded: move the camera to it to edit it.';
  render();
 }
 $('#wp-read').onclick = guard(read);
 $('#wp-apply').onclick = guard(async () => {
  if (!loaded) return;
  const props = changedGroups(); if (!Object.keys(props).length) { toast('No changes in the field.'); return; }
  const extra = $('#wp-fields').value.split(',').map(s => s.trim()).filter(Boolean).map(Number);
  const r = await host('prop-set', {fields: [field, ...extra.filter(f => f !== field)], props});
  loaded = r.props; fill(loaded);
  toast(`Field ${r.fields.join(', ')}: ${Object.keys(props).join(', ')} applied (unsaved).`);
 });
 $('#wp-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Change undone.' : 'Nothing to undo.'); if (loaded) await read(); });

 // ---- tiles ----
 async function pushTool() {
  await host('prop-tool', {tool, target: $('#wp-target').value, mask: mask(), op: $('#wp-op').value, value: num($('#wp-mat').value), radius: num($('#wp-radius').value, 6)});
  if (tool !== 'none') await pushOverlay();
 }
 async function pushOverlay() {
  let mode = $('#wp-ov').value;
  if (tool !== 'none' && mode === 'none') { mode = $('#wp-target').value; $('#wp-ov').value = mode; }
  await host('prop-overlay', {mode, mask: mask(), radius: 96});
 }
 box.querySelectorAll('[data-wp-tool]').forEach(b => b.onclick = guard(async () => {
  tool = b.dataset.wpTool; await pushTool(); render();
  if (tool !== 'none') toast('Tile brush on: the terrain brush is now off.');
 }));
 $('#wp-flag').onchange = guard(async () => { $('#wp-mask-row').hidden = $('#wp-flag').value !== 'custom'; await pushOverlay(); if (tool !== 'none') await pushTool(); });
 for (const id of ['#wp-target', '#wp-op', '#wp-mat', '#wp-mask']) $(id).onchange = guard(pushTool);
 $('#wp-ov').onchange = guard(pushOverlay);
 $('#wp-radius').oninput = () => { $('#wp-radius-v').textContent = $('#wp-radius').value + ' m'; };
 $('#wp-radius').onchange = guard(pushTool);
 $('#wp-tile-read').onclick = guard(async () => {
  const pos = enginePos(); if (!pos) return;
  const t = await host('prop-tile-get', {x: pos[0], z: pos[2]});
  const flags = GROUP_FLAGS.filter(([b]) => (t.bits >>> 0) & (1 << b)).map(([, n]) => tr(n));
  $('#wp-tile-note').textContent = t.loaded ? `Field ${t.field} · tile ${t.tx},${t.tz} · attribute ${hex(t.bits)} (${flags.join(', ') || 'none'}) · special ${t.special} · material ${t.material}` : 'Tile outside a loaded field.';
 });
 $('#wp-tile-stats').onclick = guard(async () => {
  const pos = enginePos(); if (!pos) return;
  const s = await host('prop-tile-stats', {x: pos[0], z: pos[2]});
  const bits = Object.entries(s.bitCounts).map(([b, n]) => `bit ${b}: ${n}`).join(' · ') || 'no attributes';
  const mats = Object.entries(s.materials).map(([m, n]) => `${m}: ${n}`).join(' · ');
  $('#wp-tile-note').textContent = `Field ${s.field} (${s.tiles} tiles) · ${bits} · materials ${mats}`;
 });

 // ---- PVS ----
 function grid(p) {
  const el = $('#wp-pvs-grid');
  if (!p?.enabled) { el.innerHTML = `<p class="map-empty">Sector ${p?.sector ?? '—'} has no PVS.</p>`; return; }
  // cells: row i = world +z, column j = world +x (CreatePVS order); drawn like the engine's top view (+z up, +x left)
  const cells = 13, half = 6; let html = '';
  for (let i = cells - 1; i >= 0; i--) for (let j = cells - 1; j >= 0; j--) {
   const cls = i === half && j === half ? 'self' : p.cells[i * cells + j] === '1' ? 'vis' : 'culled';
   html += `<i class="${cls}"></i>`;
  }
  el.innerHTML = html;
  $('#wp-pvs-note').textContent = `Sector ${p.sector}${p.dirty ? ' (recalculated, unsaved)' : ''} · hidden on layer ${p.layer}: ${p.culled.length} · per layer ${p.culledPerLayer.join('/')}`;
 }
 async function readPvs() { const pos = enginePos(); if (!pos) return; grid(await host('pvs-get', {x: pos[0], z: pos[2], layer: num($('#wp-pvs-layer').value)})); }
 $('#wp-pvs-read').onclick = guard(readPvs);
 $('#wp-pvs-ov').onchange = guard(async () => { await host('pvs-overlay', {enabled: $('#wp-pvs-ov').checked, layer: num($('#wp-pvs-layer').value)}); });
 $('#wp-pvs-layer').onchange = guard(async () => { await host('pvs-overlay', {enabled: $('#wp-pvs-ov').checked, layer: num($('#wp-pvs-layer').value)}); await readPvs(); });
 $('#wp-pvs-build').onclick = guard(async () => {
  const pos = enginePos(); if (!pos || pvsBusy) return;
  const args = {x: pos[0], z: pos[2], eps: num($('#wp-pvs-eps').value, 10)};
  if ($('#wp-pvs-area').value === 'radius') args.radius = num($('#wp-pvs-r').value, 200);
  const r = await host('pvs-build', args);
  pvsBusy = true; render(); $('#wp-pvs-note').textContent = `Computing PVS for ${r.queued.length} sectors…`;
  const t0 = Date.now();
  try { for (;;) { await new Promise(res => setTimeout(res, 400)); const s = await host('pvs-state'); if (!s.pvsBuilding) { toast(`PVS recalculated: ${r.queued.length} sectors in ${((Date.now() - t0) / 1000).toFixed(1)} s (unsaved).`); break; } } }
  finally { pvsBusy = false; render(); }
  await readPvs();
 });

 // ---- game property ----
 $('#wp-gp-list').onclick = guard(async () => {
  const pos = enginePos(); if (!pos) return;
  const r = await host('prop-gameprop-list', {x: pos[0], z: pos[2], radius: 300});
  if (!r.volumes.length) { $('#wp-gp').innerHTML = `<p class="map-empty">No volumes loaded (${r.worldType === 0 ? 'outdoor world: does not use them' : 'indoor world'}).</p>`; return; }
  $('#wp-gp').innerHTML = r.volumes.map((v, k) => `<div class="wp-row" data-gp="${k}"><span class="wp-dim">S${v.sector}#${v.index} ${v.shape === 1 ? 'circle' : 'plane'}</span><input data-k="prop" value="${hex(v.property)}"><button type="button" class="map-button">Apply</button></div>`).join('');
  $('#wp-gp').querySelectorAll('[data-gp]').forEach(row => row.querySelector('button').onclick = guard(async () => {
   const v = r.volumes[Number(row.dataset.gp)];
   const x = await host('prop-gameprop-set', {sector: v.sector, index: v.index, property: row.querySelector('[data-k=prop]').value.trim()});
   toast(`Volume S${v.sector}#${v.index}: property ${hex(x.volume.property)} (unsaved).`);
  }));
 });

 async function navHint() {
  try {
   const s = await api('world-engine-edit-state');
   if (!s?.workspaceRoot) return;
   const w = ctx.state()?.engine?.world || 'dbo_main_world', root = `${s.workspaceRoot}\\world\\${w}`;
   $('#wp-nav').textContent = `dbo-assets asset terrain-navi-plan <terrain.tgv> "${root}\\#######.gwp" "${root}\\fields" <PathEngine_x64.dll> terrain-only-not-for-install <new-plan.json>`; navShown = true;
  } catch { /* viewer mode */ }
 }

 function render() {
  const editing = ctx.editing();
  box.classList.toggle('we-disabled', !editing);
  for (const el of box.querySelectorAll('#wp-apply,#wp-undo,[data-wp-tool],#wp-pvs-build,#wp-gp button')) el.disabled = !editing || (el.id === 'wp-pvs-build' && pvsBusy);
  box.querySelectorAll('[data-wp-tool]').forEach(b => b.classList.toggle('active', b.dataset.wpTool === tool));
  if (editing && !navShown) navHint();
 }
 render();
 return {render, reset: () => { tool = 'none'; field = -1; loaded = null; $('#wp-form').hidden = true; render(); }};
}
