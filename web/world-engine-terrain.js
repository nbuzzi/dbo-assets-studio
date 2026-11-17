// World Editor tool module · terrain (height brush + texture layer paint) on the real NTL engine.
// Contract for tool modules (objects, water, ...): export install<Name>Tools(ctx) returning {render(state)}.
//   ctx.container  element to fill inside the sidebar (#we-tools)
//   ctx.host(cmd, args)  -> world-engine-host passthrough (whitelisted command families)
//   ctx.api / ctx.toast / ctx.escape
//   ctx.editing()  true when the engine runs on an edit workspace
export function installTerrainTools(ctx) {
 const {container, host, api, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool';
 box.innerHTML = `
  <div class="map-history-heading"><h2>Terreno</h2></div>
  <div class="we-tool-row" role="group" aria-label="Herramienta de terreno">
   <button type="button" class="map-button" data-tt="none">Ninguna</button>
   <button type="button" class="map-button" data-tt="height">Altura</button>
   <button type="button" class="map-button" data-tt="paint">Textura</button>
  </div>
  <div class="we-tool-height">
   <label>Modo <select id="wt-mode"><option value="raise">Subir</option><option value="lower">Bajar</option><option value="smooth">Suavizar</option><option value="flatten">Aplanar</option></select></label>
  </div>
  <div class="we-tool-paint">
   <label>Capa <select id="wt-layer">${[0,1,2,3,4].map(i => `<option value="${i}">${i + 1}</option>`).join('')}</select></label>
   <label>Textura (para capa vacía) <input id="wt-texture" list="wt-texlist" placeholder="Tex_YahhoiE_Road_001"></label>
   <datalist id="wt-texlist"></datalist>
   <label class="we-check"><input id="wt-erase" type="checkbox"> Borrar</label>
   <p id="wt-layers" class="map-path"></p>
  </div>
  <label>Radio <input id="wt-radius" type="range" min="2" max="80" value="12"> <span id="wt-radius-v">12 m</span></label>
  <label>Fuerza <input id="wt-strength" type="range" min="1" max="100" value="30"> <span id="wt-strength-v">30</span></label>
  <label class="we-check"><input id="wt-slope" type="checkbox"> Recalcular slope lighting (motor)</label>
  <div class="map-native-actions">
   <button id="wt-undo" type="button" class="map-button">Deshacer trazo</button>
  </div>
  <p class="map-path">Clic izquierdo y arrastrar en el motor: aplica el pincel · Ctrl invierte (bajar/borrar).</p>`;
 container.appendChild(box);
 const $ = s => box.querySelector(s);
 let tool = 'none', loadedTextures = false;
 async function push() {
  if (!ctx.editing()) return;
  const strength = Number($('#wt-strength').value);
  await host('tool', {tool, mode: $('#wt-mode').value, radius: Number($('#wt-radius').value),
   strength: tool === 'paint' ? strength / 25 : strength / 10, layer: Number($('#wt-layer').value),
   texture: $('#wt-texture').value.trim(), erase: $('#wt-erase').checked, slopeLighting: $('#wt-slope').checked});
 }
 async function layers() {
  if (tool !== 'paint') return;
  const s = ctx.state()?.engine; if (!s?.pos) return;
  const r = await host('terrain-layers', {x: s.pos[0], z: s.pos[2]});
  $('#wt-layers').textContent = `Sector ${r.sector}: base ${r.base || '—'} · ` + (r.layers || []).map((n, i) => `${i + 1}: ${n || 'vacía'}`).join(' · ');
 }
 const guard = fn => async () => { try { await fn(); } catch (err) { toast(err.message || String(err), true); } };
 box.querySelectorAll('[data-tt]').forEach(b => b.onclick = guard(async () => {
  tool = b.dataset.tt; await push(); render();
  if (tool === 'paint' && !loadedTextures) {
   loadedTextures = true;
   const t = await api('world-engine-textures');
   $('#wt-texlist').innerHTML = (t.textures || []).map(n => `<option value="${e(n)}">`).join('');
  }
  await layers();
 }));
 for (const id of ['#wt-mode', '#wt-layer', '#wt-texture', '#wt-erase', '#wt-slope']) $(id).onchange = guard(push);
 for (const id of ['#wt-radius', '#wt-strength']) $(id).oninput = () => { $('#wt-radius-v').textContent = $('#wt-radius').value + ' m'; $('#wt-strength-v').textContent = $('#wt-strength').value; };
 for (const id of ['#wt-radius', '#wt-strength']) $(id).onchange = guard(push);
 $('#wt-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Trazo deshecho.' : 'No hay nada para deshacer.'); });
 function render() {
  const editing = ctx.editing();
  box.classList.toggle('we-disabled', !editing);
  box.querySelectorAll('button,select,input').forEach(el => el.disabled = !editing);
  box.querySelectorAll('[data-tt]').forEach(b => b.classList.toggle('active', b.dataset.tt === tool));
  $('.we-tool-height').hidden = tool !== 'height';
  $('.we-tool-paint').hidden = tool !== 'paint';
 }
 render();
 return {render, reset: () => { tool = 'none'; render(); }};
}
