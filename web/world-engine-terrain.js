// World Editor tool module · terrain (height brush + texture layer paint) on the real NTL engine.
// Contract for tool modules (objects, water, ...): export install<Name>Tools(ctx) returning {render(state)}.
//   ctx.container  element to fill inside the sidebar (#we-tools)
//   ctx.host(cmd, args)  -> world-engine-host passthrough (whitelisted command families)
//   ctx.api / ctx.toast / ctx.escape
//   ctx.editing()  true when the engine runs on an edit workspace
import {t as tr} from './i18n.js';
export function installTerrainTools(ctx) {
 const {container, host, api, toast, escape: e} = ctx;
 const box = document.createElement('section');
 box.className = 'we-tool';
 box.innerHTML = `
  <div class="map-history-heading"><h2>Terrain</h2></div>
  <div class="we-tool-row" role="group" aria-label="Terrain tool">
   <button type="button" class="map-button" data-tt="none">None</button>
   <button type="button" class="map-button" data-tt="height">Height</button>
   <button type="button" class="map-button" data-tt="paint">Texture</button>
  </div>
  <div class="we-tool-height">
   <label>Mode <select id="wt-mode"><option value="raise">Raise</option><option value="lower">Lower</option><option value="smooth">Smooth</option><option value="flatten">Flatten</option></select></label>
  </div>
  <div class="we-tool-paint">
   <label>Layer <select id="wt-layer">${[0,1,2,3,4].map(i => `<option value="${i}">${i + 1}</option>`).join('')}</select></label>
   <label>Texture (for an empty layer) <input id="wt-texture" list="wt-texlist" placeholder="Tex_YahhoiE_Road_001"></label>
   <datalist id="wt-texlist"></datalist>
   <label class="we-check"><input id="wt-erase" type="checkbox"> Erase</label>
   <p id="wt-layers" class="map-path"></p>
  </div>
  <label>Radius <input id="wt-radius" type="range" min="2" max="80" value="12"> <span id="wt-radius-v">12 m</span></label>
  <label>Strength <input id="wt-strength" type="range" min="1" max="100" value="30"> <span id="wt-strength-v">30</span></label>
  <label class="we-check"><input id="wt-slope" type="checkbox"> Recalculate slope lighting (engine)</label>
  <div class="map-native-actions">
   <button id="wt-undo" type="button" class="map-button">Undo stroke</button>
  </div>
  <p class="map-path">Left-click and drag in the engine: applies the brush · Ctrl inverts (lower/erase).</p>`;
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
  $('#wt-layers').textContent = `Sector ${r.sector}: base ${r.base || '—'} · ` + (r.layers || []).map((n, i) => `${i + 1}: ${n || tr('empty')}`).join(' · ');
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
 $('#wt-undo').onclick = guard(async () => { const r = await host('undo'); toast(r.undone ? 'Stroke undone.' : 'Nothing to undo.'); });
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
