// World Editor tool module · objects (ornaments from obj.pak) on the real NTL engine.
// Host commands: objects-tool / objects-catalog / objects-list / objects-select / objects-create / objects-transform /
// objects-delete (+ the shared "undo"). The catalog is objectproperty.rdf as the engine loaded it; the host refuses any
// other name (R6: an unknown name breaks the sector stream on load).
// In the engine window: select tool = click picks (triangle-accurate), drag the selected object moves it on the terrain,
// Shift+drag lifts it, Ctrl+drag rotates it, Supr deletes it. Place tool = click on the terrain drops the catalog object.
export function installObjectTools(ctx) {
 const {container, host, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool wo-tool';
 const vec = (id, label, step) => `<div class="wo-vec"><span>${label}</span>${['x', 'y', 'z'].map(a => `<input id="${id}-${a}" type="number" step="${step}" aria-label="${label} ${a}">`).join('')}</div>`;
 box.innerHTML = `
  <div class="map-history-heading"><h2>Objetos</h2></div>
  <div class="we-tool-row" role="group" aria-label="Herramienta de objetos">
   <button type="button" class="map-button" data-ot="none">Ninguna</button>
   <button type="button" class="map-button" data-ot="select">Seleccionar</button>
   <button type="button" class="map-button" data-ot="place">Colocar</button>
  </div>
  <div class="wo-place">
   <label>Catálogo (objectproperty.rdf) <input id="wo-filter" placeholder="Buscar: tree, house, 01_C_…" autocomplete="off"></label>
   <select id="wo-catalog" size="8" aria-label="Objetos del catálogo"></select>
   <p id="wo-catalog-note" class="map-path"></p>
   <div class="wo-inline">
    <label>Giro Y <input id="wo-yaw" type="number" step="15" value="0"></label>
    <label>Escala <input id="wo-scale" type="number" step="0.1" min="0.05" value="1"></label>
   </div>
   <label class="we-check"><input id="wo-random" type="checkbox"> Giro aleatorio</label>
  </div>
  <div id="wo-sel" class="wo-sel">
   <p class="map-empty">Sin objeto seleccionado.</p>
  </div>
  <div class="wo-fields" hidden>
   ${vec('wo-pos', 'Posición', '0.1')}
   ${vec('wo-rot', 'Rotación °', '1')}
   ${vec('wo-scl', 'Escala', '0.05')}
   <div class="map-native-actions">
    <button id="wo-apply" type="button" class="map-button map-button-primary">Aplicar</button>
    <button id="wo-ground" type="button" class="map-button">Al suelo</button>
    <button id="wo-focus" type="button" class="map-button">Enfocar</button>
    <button id="wo-delete" type="button" class="map-button map-button-secondary">Borrar</button>
   </div>
  </div>
  <div class="map-native-actions">
   <button id="wo-undo" type="button" class="map-button">Deshacer</button>
   <button id="wo-near" type="button" class="map-button">Objetos cercanos</button>
  </div>
  <ol id="wo-list" class="wo-list"></ol>
  <p class="map-path">Seleccionar: clic elige · arrastrar mueve sobre el terreno · Shift+arrastrar sube/baja · Ctrl+arrastrar gira · Supr borra. Colocar: clic en el terreno.</p>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let tool = 'none', lastGen = -1, lastErr = null, selection = null, catalogTimer = null, catalogLoaded = false;
 const guard = fn => async () => { try { await fn(); } catch (err) { toast(err.message || String(err), true); } };
 const num = id => Number($(id).value);
 const fmt = (v, d = 2) => Number.isFinite(Number(v)) ? Number(v).toFixed(d) : '—';

 async function loadCatalog() {
  const r = await host('objects-catalog', {filter: $('#wo-filter').value.trim(), limit: 300});
  const current = $('#wo-catalog').value;
  $('#wo-catalog').innerHTML = (r.items || []).map(i => `<option value="${e(i.name)}" title="${e(i.dff)}">${e(i.name)}</option>`).join('');
  if (current && [...$('#wo-catalog').options].some(o => o.value === current)) $('#wo-catalog').value = current;
  $('#wo-catalog-note').textContent = `${r.matched} de ${r.total} nombres${r.matched > (r.items || []).length ? ` · mostrando ${(r.items || []).length}` : ''}`;
  catalogLoaded = true;
 }
 async function pushTool() {
  const args = {tool, yaw: num('#wo-yaw') || 0, scale: num('#wo-scale') || 1, randomYaw: $('#wo-random').checked};
  const name = $('#wo-catalog').value; if (name) args.name = name;
  if (tool === 'place' && !name) { tool = 'none'; render(); throw new Error('Elegí un objeto del catálogo para colocar.'); }
  await host('objects-tool', args);
 }
 function fill(o) {
  selection = o;
  $('#wo-sel').innerHTML = o ? `<dl class="we-spawn"><dt>objeto</dt><dd>${e(o.name)}</dd><dt>serial</dt><dd>${e(o.serial)}${o.sid !== 4294967295 ? ' · trigger ' + e(o.sid) : ''}</dd>
   <dt>sector</dt><dd>${e(o.sector)} · field ${e(o.field)} · #${e(o.index)}</dd><dt>radio</dt><dd>${fmt(o.radius)} m</dd></dl>` : '<p class="map-empty">Sin objeto seleccionado.</p>';
  $('.wo-fields').hidden = !o;
  if (!o) return;
  // keep what the user is typing
  for (const [id, arr] of [['wo-pos', o.pos], ['wo-rot', o.rot], ['wo-scl', o.scale]])
   ['x', 'y', 'z'].forEach((a, i) => { const el = $(`#${id}-${a}`); if (document.activeElement !== el) el.value = Number(arr[i]).toFixed(id === 'wo-rot' ? 1 : 3); });
 }
 const target = () => selection ? {sector: selection.sector, index: selection.index} : {};
 async function after(r, msg) { if (r?.object) fill(r.object); if (msg) toast(msg); }

 box.querySelectorAll('[data-ot]').forEach(b => b.onclick = guard(async () => {
  tool = b.dataset.ot;
  if (tool === 'place' && !catalogLoaded) await loadCatalog();
  await pushTool(); render();
 }));
 $('#wo-filter').oninput = () => { clearTimeout(catalogTimer); catalogTimer = setTimeout(guard(loadCatalog), 220); };
 $('#wo-catalog').onchange = guard(async () => { if (tool === 'place') await pushTool(); });
 for (const id of ['#wo-yaw', '#wo-scale', '#wo-random']) $(id).onchange = guard(async () => { if (tool === 'place') await pushTool(); });
 $('#wo-apply').onclick = guard(async () => {
  const r = await host('objects-transform', {...target(), pos: ['x', 'y', 'z'].map(a => num(`#wo-pos-${a}`)), rot: ['x', 'y', 'z'].map(a => num(`#wo-rot-${a}`)), scale: ['x', 'y', 'z'].map(a => num(`#wo-scl-${a}`))});
  await after(r, r.rebucketed ? `Objeto movido al sector ${r.object.sector} (antes ${r.fromSector}).` : 'Objeto actualizado.');
 });
 $('#wo-ground').onclick = guard(async () => { const r = await host('objects-transform', {...target(), ground: true}); await after(r, 'Objeto apoyado en el terreno.'); });
 $('#wo-focus').onclick = guard(async () => { if (selection) await host('objects-select', {...target(), focus: true}); });
 $('#wo-delete').onclick = guard(async () => {
  if (!selection) return;
  const r = await host('objects-delete', target()); fill(null);
  toast(`Borrado ${r.deleted?.name} (serial ${r.deleted?.serial}). Deshacer lo restaura.`);
 });
 $('#wo-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Paso deshecho.' : 'No hay nada para deshacer.'); lastGen = -1; });
 $('#wo-near').onclick = guard(async () => {
  const s = ctx.state()?.engine; if (!s?.pos) return;
  const r = await host('objects-list', {x: s.pos[0], z: s.pos[2], radius: 60, limit: 60});
  $('#wo-list').innerHTML = (r.objects || []).map(o => `<li><button type="button" class="wo-item" data-sector="${o.sector}" data-index="${o.index}">${e(o.name)} <small>${fmt(o.pos[0], 0)}, ${fmt(o.pos[2], 0)} · #${e(o.serial)}</small></button></li>`).join('')
   || '<li class="map-empty">Sin objetos en 60 m.</li>';
  $('#wo-list').querySelectorAll('.wo-item').forEach(b => b.onclick = guard(async () => {
   const r2 = await host('objects-select', {sector: Number(b.dataset.sector), index: Number(b.dataset.index), focus: true}); fill(r2.object);
  }));
 });

 function render(state) {
  const editing = ctx.editing(), es = state?.engine;
  box.classList.toggle('we-disabled', !ctx.state()?.running);
  box.querySelectorAll('button,select,input').forEach(el => el.disabled = !ctx.state()?.running);
  // edits need the workspace; selecting and browsing work in the viewer too
  for (const id of ['#wo-apply', '#wo-ground', '#wo-delete', '#wo-undo']) $(id).disabled = !editing;
  box.querySelector('[data-ot="place"]').disabled = !editing;
  if (es && es.objectTool && es.objectTool !== tool && !(es.tool && es.tool !== 'none')) tool = es.objectTool;
  box.querySelectorAll('[data-ot]').forEach(b => b.classList.toggle('active', b.dataset.ot === tool && !(es?.tool && es.tool !== 'none')));
  $('.wo-place').hidden = tool !== 'place';
  if (es && es.objectGeneration !== lastGen) { lastGen = es.objectGeneration; fill(es.objectSelection || null); }
  if (es && es.objectErrorGeneration != null) { if (lastErr != null && es.objectErrorGeneration !== lastErr && es.objectError) toast('Objetos: ' + es.objectError, true); lastErr = es.objectErrorGeneration; }
 }
 render();
 return {render, reset: () => { tool = 'none'; lastGen = -1; fill(null); render(); }};
}
