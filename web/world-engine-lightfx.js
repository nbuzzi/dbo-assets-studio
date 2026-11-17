// World Editor tool module · light, effects and sound on the real NTL engine (world-host-lightfx.h).
// Same contract as world-engine-terrain.js: installLightFxTools(ctx) -> {render(status), reset()}.
// Everything goes through whitelisted host families light-/effect-/sound-; entity references ("ref") come from *-list.
import {t as tr} from './i18n.js';
const KINDS = {
 light: [['light:point', 'Point light'], ['light:spot', 'Spot'], ['light:soft', 'Soft spot'], ['worldlight-sphere', 'World light · sphere'], ['worldlight-box', 'World light · box']],
 effect: [['effect', 'Effect']],
 sound: [['sound', 'Ambient sound'], ['bgm', 'Zone BGM']],
};
const LABEL = {light: 'Light', 'worldlight-sphere': 'World sphere', 'worldlight-box': 'World box', effect: 'Effect', sound: 'Sound', bgm: 'BGM'};
const hex = c => '#' + (c || [255, 255, 255]).map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('');
const rgb = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const f1 = n => Number.isFinite(Number(n)) ? Number(n).toFixed(1) : '—';

export function installLightFxTools(ctx) {
 const {container, api, toast, escape: e} = ctx;
 const host = async (cmd, args = {}) => { const r = await ctx.host(cmd, args); if (r?.event === 'error') throw new Error(r.message || 'engine error'); return r; };
 const box = document.createElement('section');
 box.className = 'we-tool we-lfx';
 box.innerHTML = `
  <div class="map-history-heading"><h2>Light, effects and sound</h2></div>
  <div class="we-lfx-tabs" role="tablist">
   <button type="button" class="map-button" data-tab="light">Light</button>
   <button type="button" class="map-button" data-tab="effect">Effects</button>
   <button type="button" class="map-button" data-tab="sound">Sound</button>
  </div>
  <div class="we-tool-row" role="group" aria-label="Tool">
   <button type="button" class="map-button" data-lt="none">None</button>
   <button type="button" class="map-button" data-lt="select">Select</button>
   <button type="button" class="map-button" data-lt="place">Place</button>
  </div>
  <label>What to place <select id="lfx-kind"></select></label>
  <div class="we-lfx-place" data-for="light">
   <div class="we-lfx-grid"><label>Color <input id="lfx-color" type="color" value="#ffb060"></label>
    <label>Radius <input id="lfx-radius" type="number" min="1" max="500" step="1" value="14"></label>
    <label>Angle <input id="lfx-angle" type="number" min="1" max="179" step="1" value="45"></label></div>
   <label class="we-check"><input id="lfx-real" type="checkbox" checked> Real light (RpLight)</label>
  </div>
  <div class="we-lfx-place" data-for="effect">
   <label>Effect (.eff catalog) <input id="lfx-effect" list="lfx-effects" placeholder="obj_fire_red_001"></label>
   <datalist id="lfx-effects"></datalist>
   <div class="we-lfx-grid"><label>Scale <input id="lfx-escale" type="number" min="0.05" max="50" step="0.05" value="1"></label>
    <label>Rot. Y <input id="lfx-erot" type="number" step="5" value="0"></label></div>
  </div>
  <div class="we-lfx-place" data-for="sound">
   <label>File (≤ 31 characters) <input id="lfx-sound" list="lfx-sounds" maxlength="31" placeholder="GENERAL_ENV_SHORE_LOOP.wav"></label>
   <datalist id="lfx-sounds"></datalist>
   <div class="we-lfx-grid"><label>Volume <input id="lfx-volume" type="number" min="0" max="1" step="0.05" value="0.8"></label>
    <label>Min radius <input id="lfx-min" type="number" min="0" step="1" value="8"></label>
    <label>Max radius / BGM <input id="lfx-max" type="number" min="1" step="1" value="40"></label></div>
  </div>
  <p class="map-path" id="lfx-hint">Select: click a gizmo · Shift+click on the terrain moves the selection. Place: click on the terrain.</p>
  <div id="lfx-sel" class="we-lfx-sel"></div>
  <div class="we-lfx-list-head"><strong>Near the camera</strong><button id="lfx-refresh" type="button" class="map-button">Refresh</button></div>
  <ul id="lfx-list" class="we-lfx-list"></ul>
  <details class="we-lfx-field" data-for="light"><summary>Field light (eNFP_LIGHT)</summary>
   <div id="lfx-fl"></div>
   <div class="map-native-actions"><button id="lfx-fl-read" type="button" class="map-button">Read field</button><button id="lfx-fl-apply" type="button" class="map-button">Apply to field</button></div>
  </details>
  <details class="we-lfx-field" data-for="sound"><summary>Field sound (eNFP_SOUND)</summary>
   <div class="we-lfx-grid"><label>BGM (table) <input id="lfx-fs-bgm" type="number" step="1"></label><label>Ambient (table) <input id="lfx-fs-env" type="number" step="1"></label></div>
   <label>Shared (5, comma-separated) <input id="lfx-fs-share" placeholder="-1,-1,-1,-1,-1"></label>
   <div class="map-native-actions"><button id="lfx-fs-read" type="button" class="map-button">Read field</button><button id="lfx-fs-apply" type="button" class="map-button">Apply to field</button></div>
  </details>
  <div class="map-native-actions"><button id="lfx-undo" type="button" class="map-button">Undo</button></div>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let tab = 'light', tool = 'none', generation = -1, items = [], selected = null, editingForm = false, catalogs = {};
 const guard = fn => async (...a) => { try { await fn(...a); } catch (err) { toast(err.message || String(err), true); } };

 function kindArgs() {
  const [kind, type] = $('#lfx-kind').value.split(':');
  const a = {kind};
  if (tab === 'light') Object.assign(a, {color: rgb($('#lfx-color').value), radius: Number($('#lfx-radius').value), angle: Number($('#lfx-angle').value), real: $('#lfx-real').checked});
  if (type) a.type = type;
  if (kind === 'worldlight-box') { const r = Number($('#lfx-radius').value); a.scale = [r, r / 2, r]; }
  if (tab === 'effect') Object.assign(a, {name: $('#lfx-effect').value.trim(), scale: Number($('#lfx-escale').value), rot: [0, Number($('#lfx-erot').value), 0]});
  if (tab === 'sound') Object.assign(a, {name: $('#lfx-sound').value.trim(), volume: Number($('#lfx-volume').value), min: Number($('#lfx-min').value), max: Number($('#lfx-max').value), radius: Number($('#lfx-max').value)});
  return a;
 }
 async function pushTool() {
  const r = await host(`${tab}-tool`, {tool, ...kindArgs()});
  generation = -1; return r;
 }
 async function loadCatalog() {
  if (catalogs[tab] || tab === 'light') return;
  const r = await host(`${tab}-catalog`, {limit: 8000});
  catalogs[tab] = r.names || [];
  $(tab === 'effect' ? '#lfx-effects' : '#lfx-sounds').innerHTML = catalogs[tab].map(n => `<option value="${e(n.replace(/^bgm:/, ''))}">`).join('');
 }
 async function refreshList() {
  if (!ctx.state()?.running) return;
  const s = ctx.state()?.engine; const at = s?.pos ? {x: s.pos[0], z: s.pos[2]} : {};
  const r = await host(`${tab}-list`, {...at, range: 160, limit: 200});
  items = r.items || [];
  const counts = r.loadedCounts || {};
  $('#lfx-list').innerHTML = items.length ? items.map(it => `<li><button type="button" data-ref="${it.ref}" class="${it.selected ? 'active' : ''}">
    <span class="we-lfx-dot" style="background:${it.color ? hex(it.color) : it.kind === 'effect' ? '#ff5ae6' : it.kind === 'bgm' ? '#7896ff' : '#6eff78'}"></span>
    <b>${e(LABEL[it.kind] || it.kind)}</b> ${e(it.name || it.type || '')} <small>${f1(it.pos[0])}, ${f1(it.pos[2])} · s${it.sector}</small></button></li>`).join('')
   : `<li class="map-empty">Nothing within 160 m.</li>`;
  const mine = {light: ['light', 'worldlight-sphere', 'worldlight-box'], effect: ['effect'], sound: ['sound', 'bgm']}[tab];
  $('#lfx-list').insertAdjacentHTML('beforeend', `<li class="map-path">Loaded in memory: ${mine.map(k => `${tr(LABEL[k])} ${counts[k] ?? 0}`).join(' · ')}</li>`);
  $('#lfx-list').querySelectorAll('[data-ref]').forEach(b => b.onclick = guard(async () => {
   const r2 = await host(`${tab}-select`, {ref: Number(b.dataset.ref), focus: true}); selected = r2.item; renderSelection(); await refreshList();
  }));
 }
 function renderSelection() {
  const s = selected, el = $('#lfx-sel');
  if (!s) { el.innerHTML = '<p class="map-empty">No selection.</p>'; return; }
  const v3 = (id, v) => `<div class="we-lfx-v3">${[0, 1, 2].map(i => `<input data-v3="${id}" data-i="${i}" type="number" step="0.5" value="${f1(v?.[i] ?? 0)}">`).join('')}</div>`;
  let extra = '';
  if (s.kind === 'light') extra = `<label>Color <input data-k="color" type="color" value="${hex(s.color)}"></label><label>Radius <input data-k="radius" type="number" step="1" value="${s.radius}"></label><label>Angle <input data-k="angle" type="number" step="1" value="${s.angle}"></label><label>Rotation</label>${v3('rot', s.rot)}<label class="we-check"><input data-k="real" type="checkbox" ${s.real ? 'checked' : ''}> Real light</label>`;
  if (s.kind === 'worldlight-sphere') extra = `<label>Color <input data-k="color" type="color" value="${hex(s.color)}"></label><label>Radius <input data-k="radius" type="number" step="1" value="${s.radius}"></label>`;
  if (s.kind === 'worldlight-box') extra = `<label>Color <input data-k="color" type="color" value="${hex(s.color)}"></label><label>Scale</label>${v3('scale', s.scale)}<label>Rotation</label>${v3('rot', s.rot)}`;
  if (s.kind === 'effect') extra = `<label>Scale <input data-k="scale" type="number" step="0.05" value="${s.scale}"></label><label>Rotation</label>${v3('rot', s.rot)}`;
  if (s.kind === 'sound') extra = `<label>Volume <input data-k="volume" type="number" min="0" max="1" step="0.05" value="${f1(s.volume)}"></label><label>Min radius <input data-k="min" type="number" step="1" value="${s.min}"></label><label>Max radius <input data-k="max" type="number" step="1" value="${s.max}"></label>`;
  if (s.kind === 'bgm') extra = `<label>Radius <input data-k="radius" type="number" step="1" value="${s.radius}"></label>`;
  el.innerHTML = `<div class="we-lfx-sel-head"><strong>${e(LABEL[s.kind] || s.kind)}</strong> <span>${e(s.name || s.type || '')}</span> <small>field ${s.field} · sector ${s.sector}</small></div>
   <label>Position</label>${v3('pos', s.pos)}${extra}
   <div class="we-tool-row"><button id="lfx-apply" type="button" class="map-button">Apply</button><button id="lfx-focus" type="button" class="map-button">Focus</button><button id="lfx-del" type="button" class="map-button map-button-danger">Delete</button></div>`;
  el.querySelectorAll('input').forEach(i => { i.onfocus = () => { editingForm = true; }; i.onblur = () => { editingForm = false; }; });
  $('#lfx-apply').onclick = guard(async () => {
   const a = {ref: s.ref};
   el.querySelectorAll('[data-v3]').forEach(i => { (a[i.dataset.v3] ||= [0, 0, 0])[Number(i.dataset.i)] = Number(i.value); });
   el.querySelectorAll('[data-k]').forEach(i => { const k = i.dataset.k; a[k] = i.type === 'checkbox' ? i.checked : i.type === 'color' ? rgb(i.value) : Number(i.value); });
   const r = await host(`${tab}-set`, a); selected = r.item; editingForm = false; renderSelection(); await refreshList();
  });
  $('#lfx-focus').onclick = guard(async () => { await host(`${tab}-select`, {ref: s.ref, focus: true}); });
  $('#lfx-del').onclick = guard(async () => { await host(`${tab}-delete`, {ref: s.ref}); selected = null; renderSelection(); await refreshList(); toast('Deleted (Undo restores it).'); });
 }
 function fieldLightForm(p) {
  $('#lfx-fl').innerHTML = `<label>Ambient <input id="lfx-fl-amb" type="color" value="${hex(p.ambient)}"></label>` +
   (p.directional || []).map((d, i) => `<div class="we-lfx-grid"><label>Directional ${i + 1} <input data-fl="${i}" type="color" value="${hex(d.color)}"></label><label>Angle X/Y <span class="we-lfx-v3"><input data-fla="${i}" data-i="0" type="number" step="5" value="${d.angle[0]}"><input data-fla="${i}" data-i="1" type="number" step="5" value="${d.angle[1]}"></span></label></div>`).join('');
 }
 const fieldAt = () => { const s = ctx.state()?.engine; return s?.pos ? {x: s.pos[0], z: s.pos[2]} : {}; };
 $('#lfx-fl-read').onclick = guard(async () => { const r = await host('light-field', fieldAt()); fieldLightForm(r.prop); toast(`Field ${r.field} read.`); });
 $('#lfx-fl-apply').onclick = guard(async () => {
  const directional = [...box.querySelectorAll('[data-fl]')].map(i => ({color: rgb(i.value), angle: [0, 1].map(k => Number(box.querySelector(`[data-fla="${i.dataset.fl}"][data-i="${k}"]`).value)).concat([0])}));
  const r = await host('light-field', {...fieldAt(), ambient: rgb($('#lfx-fl-amb').value), directional}); fieldLightForm(r.prop); toast(`Field ${r.field} light updated.`);
 });
 const soundForm = p => { $('#lfx-fs-bgm').value = p.bgm; $('#lfx-fs-env').value = p.env; $('#lfx-fs-share').value = (p.share || []).join(','); };
 $('#lfx-fs-read').onclick = guard(async () => { const r = await host('sound-field', fieldAt()); soundForm(r.prop); toast(`Field ${r.field} read.`); });
 $('#lfx-fs-apply').onclick = guard(async () => {
  const share = $('#lfx-fs-share').value.split(',').map(x => Number(x.trim())).filter(Number.isFinite);
  const r = await host('sound-field', {...fieldAt(), bgm: Number($('#lfx-fs-bgm').value), env: Number($('#lfx-fs-env').value), share}); soundForm(r.prop); toast(`Field ${r.field} sound updated.`);
 });
 box.querySelectorAll('[data-tab]').forEach(b => b.onclick = guard(async () => {
  tab = b.dataset.tab; selected = null;
  $('#lfx-kind').innerHTML = KINDS[tab].map(([v, l]) => `<option value="${v}">${l}</option>`).join('');
  render(); if (ctx.editing() || ctx.state()?.running) { if (tool !== 'none') await pushTool(); await loadCatalog(); await refreshList(); }
 }));
 box.querySelectorAll('[data-lt]').forEach(b => b.onclick = guard(async () => {
  tool = b.dataset.lt;
  if (tool === 'place' && tab === 'effect' && !$('#lfx-effect').value.trim()) { tool = 'none'; throw new Error('Choose an effect from the catalog before placing.'); }
  if (tool === 'place' && tab === 'sound' && !$('#lfx-sound').value.trim()) { tool = 'none'; throw new Error('Enter the sound file before placing.'); }
  await pushTool(); await loadCatalog(); render();
 }));
 for (const id of ['#lfx-kind', '#lfx-color', '#lfx-radius', '#lfx-angle', '#lfx-real', '#lfx-effect', '#lfx-escale', '#lfx-erot', '#lfx-sound', '#lfx-volume', '#lfx-min', '#lfx-max'])
  $(id).onchange = guard(async () => { if (tool !== 'none') await pushTool(); });
 $('#lfx-refresh').onclick = guard(refreshList);
 $('#lfx-undo').onclick = guard(async () => { const r = await host('undo'); selected = null; renderSelection(); await refreshList(); toast(r.undone ? 'Undone.' : 'Nothing to undo.'); });
 $('#lfx-kind').innerHTML = KINDS[tab].map(([v, l]) => `<option value="${v}">${l}</option>`).join('');

 let refreshing = false;
 function render(status) {
  const running = !!(status || ctx.state())?.running, editing = ctx.editing();
  box.classList.toggle('we-disabled', !running);
  box.querySelectorAll('[data-tab]').forEach(b => { b.classList.toggle('active', b.dataset.tab === tab); b.disabled = !running; });
  box.querySelectorAll('[data-lt]').forEach(b => { b.classList.toggle('active', b.dataset.lt === tool); b.disabled = !running || (b.dataset.lt === 'place' && !editing); });
  box.querySelectorAll('.we-lfx-place, .we-lfx-field').forEach(el => { el.hidden = el.dataset.for !== tab; });
  ['#lfx-apply', '#lfx-del', '#lfx-fl-apply', '#lfx-fs-apply', '#lfx-undo'].forEach(id => { const el = $(id); if (el) el.disabled = !editing; });
  const lfx = status?.engine?.lfx;
  if (lfx && lfx.generation !== generation && !refreshing && !editingForm) {
   generation = lfx.generation; refreshing = true;
   if (lfx.selected && lfx.selected.family === tab) { selected = lfx.selected; renderSelection(); }
   else if (!lfx.selected && selected) { selected = null; renderSelection(); }
   refreshList().catch(() => {}).finally(() => { refreshing = false; });
  }
  if (lfx && lfx.tool !== tool && !(lfx.tool === 'none' && tool === 'none')) { tool = lfx.tool; }
 }
 renderSelection(); render();
 return {render, reset: () => { tool = 'none'; selected = null; items = []; generation = -1; renderSelection(); render(); }};
}
