// UI Maker: edit .uilayout.json windows and preview them with the client's real GUI (NtlGui via ui-render.exe).
export function installUiMaker({api,toast,escape:esc}){
 const root=document.querySelector('#ui-maker');if(!root)throw new Error('The #ui-maker area is missing.');
 const sizes=['1024x768','1280x720','1366x768','1600x900','1920x1080'];
 root.innerHTML=`<div class="um-shell">
 <header class="um-head"><div><div class="eyebrow">UI MAKER · REAL NTLGUI RENDER</div><h1 id="um-name">Game windows</h1><p id="um-path">Open or create a layout. The view uses the client's GUI library (RenderWare + game fonts).</p></div>
 <div class="um-actions"><button class="button" id="um-open">Open</button><button class="button" id="um-new">New</button><button class="button" id="um-import">From the game (.frm / Flash)</button><button class="button" id="um-save" disabled>Save</button><button class="button" id="um-review" disabled>Generate review</button><button class="button primary" id="um-approve" disabled>Approve</button><button class="button" id="um-codegen" disabled>Generate C++</button><button class="button subtle" id="um-folder" disabled>Folder</button></div></header>
 <div class="um-grid">
  <section class="um-editor"><div class="um-caption">LAYOUT JSON <span id="um-dirty"></span></div><textarea id="um-text" spellcheck="false" placeholder="{ &quot;schema&quot;: &quot;dbo-ui-layout/1&quot; … }" disabled></textarea><div id="um-json-error" class="um-error hidden"></div></section>
  <main class="um-main"><div class="um-toolbar"><label>Variant <select id="um-variant"></select></label><label>Screen <select id="um-size">${sizes.map(s=>`<option>${s}</option>`).join('')}</select></label><label class="um-check"><input type="checkbox" id="um-boxes" checked> Boxes</label><label class="um-check" title="Window built by the client from its original .frm"><input type="checkbox" id="um-orig" disabled> Original</label><label>Zoom <select id="um-zoom"><option value="window">Window</option><option value="screen">Screen</option><option value="1">100%</option><option value="2">200%</option></select></label><span id="um-render-state" class="um-muted"></span></div>
   <div class="um-stage" id="um-stage"><div class="um-empty"><p>No layout open.</p><p class="um-muted">Drag a box to move it; arrows = 1 px, Shift = 10 px, Alt = resize.</p></div></div></main>
  <aside class="um-side"><div class="um-caption">STATUS</div><div id="um-status" class="um-status">—</div><div class="um-caption">SELECTION</div><div id="um-selection" class="um-muted">Click a component.</div><div class="um-caption">AUTOMATIC REVIEW <span id="um-issue-count"></span></div><ul id="um-issues" class="um-issues"></ul></aside>
 </div>
 <dialog id="um-catalog" class="um-dialog"><form method="dialog"><h2>Client windows and Flash</h2><p class="um-muted">Importing a window (.frm) turns it into an editable layout and measures the difference from the original. A Flash movie is drawn with GFx just like in the game.</p>
  <input id="um-cat-filter" placeholder="Search: bag, status, award…" autocomplete="off"><div class="um-cat-cols"><div><div class="um-caption">WINDOWS (.frm)</div><ul id="um-cat-frm" class="um-cat-list"></ul></div><div><div class="um-caption">FLASH (.swf)</div><ul id="um-cat-swf" class="um-cat-list"></ul><div class="um-cat-swf-opts"><label>Frame <input id="um-swf-frame" type="number" min="0" placeholder="1"></label><label>Time (s) <input id="um-swf-time" type="number" min="0" step="0.5" value="1"></label></div></div></div>
  <p id="um-cat-status" class="um-muted"></p><div class="um-dialog-actions"><button class="button" value="close">Close</button></div></form></dialog></div>`;
 const $=s=>root.querySelector(s);let state=null,render=null,selected=null,saveTimer=null,renderSeq=0,dirty=false,busy=false;
 const run=(sel,fn)=>$(sel).onclick=async()=>{try{await fn()}catch(e){toast(e.message||String(e),true)}};
 function setState(s){if(!s)return;state=s;$('#um-name').textContent=s.name;$('#um-path').textContent=s.path;if(document.activeElement!==$('#um-text')||!dirty)$('#um-text').value=s.text;$('#um-text').disabled=false;dirty=false;$('#um-dirty').textContent='';
  const current=$('#um-variant').value;$('#um-variant').innerHTML=s.variants.map(v=>`<option ${v===current?'selected':''}>${esc(v)}</option>`).join('');
  const size=`${s.width}x${s.height}`;if(!sizes.includes(size))$('#um-size').insertAdjacentHTML('afterbegin',`<option>${size}</option>`);if(!render)$('#um-size').value=size;
  for(const id of ['#um-save','#um-review','#um-approve','#um-codegen','#um-folder'])$(id).disabled=false;
  $('#um-orig').disabled=!s.source;if(!s.source)$('#um-orig').checked=false;
  const st=s.status||{};$('#um-status').innerHTML=st.approved?`<b class="um-ok">Approved</b><br><small>${esc(st.approvedBy||'')} · ${esc((st.approvedAt||'').slice(0,16).replace('T',' '))}</small><br><small>hash ${esc(st.layoutHash)}</small>`:`<b class="um-warn">Not approved</b><br><small>${esc(st.reason||'')}</small><br><small>hash ${esc(s.hash)}</small>`;
 }
 async function doRender(){if(!state)return;const seq=++renderSeq;const [w,h]=$('#um-size').value.split('x').map(Number);$('#um-render-state').textContent='Rendering with NtlGui…';
  try{const r=await api('ui-maker-render',{path:state.path,variant:$('#um-variant').value||'default',width:w,height:h});if(seq!==renderSeq)return;render=r;draw();$('#um-render-state').textContent=`${r.width}×${r.height} · window ${r.dialog.w}×${r.dialog.h}`;issues(r.issues)}
  catch(e){if(seq===renderSeq){$('#um-render-state').textContent='Render error';toast(e.message||String(e),true)}}}
 function issues(list){const errors=list.filter(i=>i.severity==='error').length;$('#um-issue-count').textContent=list.length?`${errors} errors · ${list.length-errors} warnings`:'no findings';
  $('#um-issues').innerHTML=list.length?list.map(i=>`<li class="${i.severity}" data-id="${esc(i.id.split('…')[0])}"><b>${esc(i.code)}</b> <code>${esc(i.id)}</code><br>${esc(i.message)}</li>`).join(''):'<li class="um-muted">The render found no problems.</li>';
  root.querySelectorAll('#um-issues li[data-id]').forEach(li=>li.onclick=()=>select(li.dataset.id))}
 function scale(){if(!render)return 1;const z=$('#um-zoom').value,st=$('#um-stage');if(z==='screen')return Math.min(1,(st.clientWidth-24)/render.width,(st.clientHeight-24)/render.height);if(z==='window')return Math.max(.25,Math.min(2,(st.clientWidth-40)/render.dialog.w,(st.clientHeight-40)/render.dialog.h));return Number(z)}
 function draw(){const st=$('#um-stage');if(!render){return}const k=scale();const show=$('#um-boxes').checked;
  st.innerHTML=`<div class="um-canvas" style="width:${render.width*k}px;height:${render.height*k}px"><img src="${render.image}" width="${render.width*k}" height="${render.height*k}" draggable="false"><div class="um-overlay ${show?'':'hidden'}">${render.components.filter(c=>!['group'].includes(c.type)).map(c=>`<div class="um-box ${c.editable?'editable':''} ${c.id===selected?'selected':''} t-${c.type}" data-id="${esc(c.id)}" title="${esc(c.id)} · ${esc(c.type)} [${c.x},${c.y},${c.w},${c.h}]" style="left:${(render.dialog.x+c.x)*k}px;top:${(render.dialog.y+c.y)*k}px;width:${c.w*k}px;height:${c.h*k}px"></div>`).join('')}</div></div>`;
  st.querySelectorAll('.um-box').forEach(box=>box.onpointerdown=e=>startDrag(e,box));if($('#um-zoom').value!=='screen'){st.scrollLeft=(render.dialog.x+render.dialog.w/2)*k-st.clientWidth/2;st.scrollTop=(render.dialog.y+render.dialog.h/2)*k-st.clientHeight/2}}
 function select(id){selected=id;const c=render?.components.find(x=>x.id===id);root.querySelectorAll('.um-box').forEach(b=>b.classList.toggle('selected',b.dataset.id===id));
  $('#um-selection').innerHTML=c?`<b>${esc(c.id)}</b> · ${esc(c.type)}<br><code>[${c.x}, ${c.y}, ${c.w}, ${c.h}]</code>${c.text?`<br><small>${esc(c.text)}</small>`:''}${c.editable?'<p class="um-muted">Drag or use the arrows (Shift ×10, Alt resizes).</p>':'<p class="um-muted">Generated by repeat: edit it in the JSON.</p>'}`:'Click a component.';
  const text=$('#um-text'),i=text.value.indexOf(`"id": "${id}"`);if(i>=0){text.setSelectionRange(i,i+6+id.length);}}
 async function move(id,dx,dy,dw=0,dh=0){if(busy||!state)return;busy=true;try{setState(await api('ui-maker-move',{path:state.path,id,dx,dy,dw,dh}));await doRender();select(id)}catch(e){toast(e.message||String(e),true)}finally{busy=false}}
 function startDrag(e,box){const id=box.dataset.id;select(id);const c=render.components.find(x=>x.id===id);if(!c?.editable)return;e.preventDefault();const k=scale(),x0=e.clientX,y0=e.clientY,l0=parseFloat(box.style.left),t0=parseFloat(box.style.top);box.setPointerCapture(e.pointerId);
  box.onpointermove=m=>{box.style.left=l0+(m.clientX-x0)+'px';box.style.top=t0+(m.clientY-y0)+'px'};
  box.onpointerup=async u=>{box.onpointermove=box.onpointerup=null;const dx=Math.round((u.clientX-x0)/k),dy=Math.round((u.clientY-y0)/k);if(dx||dy)await move(id,dx,dy)}}
 root.addEventListener('keydown',async e=>{if(e.target===$('#um-text')){if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='s'){e.preventDefault();await save()}return}
  if(!selected||!e.key.startsWith('Arrow'))return;e.preventDefault();const s=e.shiftKey?10:1,d={ArrowLeft:[-s,0],ArrowRight:[s,0],ArrowUp:[0,-s],ArrowDown:[0,s]}[e.key];if(e.altKey)await move(selected,0,0,d[0],d[1]);else await move(selected,d[0],d[1])});
 root.tabIndex=-1;
 async function save(){if(!state)return;clearTimeout(saveTimer);try{JSON.parse($('#um-text').value.replace(/^\s*\/\/.*$/gm,''))}catch(e){$('#um-json-error').textContent='Invalid JSON: '+e.message;$('#um-json-error').classList.remove('hidden');return}
  try{const s=await api('ui-maker-save',{path:state.path,text:$('#um-text').value});$('#um-json-error').classList.add('hidden');dirty=false;setState(s);await doRender()}catch(e){$('#um-json-error').textContent=e.message||String(e);$('#um-json-error').classList.remove('hidden')}}
 $('#um-text').oninput=()=>{dirty=true;$('#um-dirty').textContent='· unsaved';clearTimeout(saveTimer);saveTimer=setTimeout(save,900)};
 run('#um-open',async()=>{const s=await api('ui-maker-open');if(s){render=null;selected=null;setState(s);await doRender()}});
 run('#um-new',async()=>{const s=await api('ui-maker-new');if(s){render=null;selected=null;setState(s);await doRender()}});
 run('#um-save',save);
 run('#um-review',async()=>{const r=await api('ui-maker-review',{path:state.path});if(r)toast(`Review generated: ${r.images.length} captures, ${r.errors} errors, ${r.warnings} warnings. Opened in the browser.`)});
 run('#um-approve',async()=>{if(dirty)await save();const s=await api('ui-maker-approve',{path:state.path,hash:state.hash});if(s){setState(s);toast('Layout approved. The C++ can now be generated.')}});
 run('#um-codegen',async()=>{const r=await api('ui-maker-codegen',{path:state.path});if(r)toast(`Header generated: ${r.header}${r.notes?.length?` · ${r.notes.length} notes`:''}`)});
 run('#um-folder',async()=>await api('ui-maker-folder',{path:state.path}));
 // Catalog of client windows (.frm) and Flash movies; import or preview.
 let catalog=null,catTimer=null;
 function showImage(src,w,h,label){render=null;const st=$('#um-stage'),k=Math.min(1,(st.clientWidth-24)/w,(st.clientHeight-24)/h);st.innerHTML=`<div class="um-canvas"><img src="${src}" width="${Math.round(w*k)}" height="${Math.round(h*k)}" draggable="false"></div>`;$('#um-render-state').textContent=label}
 function listCatalog(){if(!catalog)return;const q=$('#um-cat-filter').value.trim().toLowerCase();const f=a=>a.filter(n=>!q||n.toLowerCase().includes(q)).slice(0,200);
  $('#um-cat-frm').innerHTML=f(catalog.frm).map(n=>`<li><button type="button" data-frm="${esc(n)}">${esc(n)}</button></li>`).join('')||'<li class="um-muted">No results</li>';
  $('#um-cat-swf').innerHTML=f(catalog.flash).map(n=>`<li><button type="button" data-swf="${esc(n)}">${esc(n)}</button></li>`).join('')||'<li class="um-muted">No results</li>';
  root.querySelectorAll('[data-frm]').forEach(b=>b.onclick=()=>importFrm(b.dataset.frm));root.querySelectorAll('[data-swf]').forEach(b=>b.onclick=()=>previewSwf(b.dataset.swf))}
 async function importFrm(frm){const [w,h]=$('#um-size').value.split('x').map(Number);$('#um-cat-status').textContent=`Importing ${frm} with NtlGui…`;
  try{const r=await api('ui-maker-import',{frm,width:w,height:h});if(!r){$('#um-cat-status').textContent='';return}$('#um-catalog').close();render=null;selected=null;setState(r.state);await doRender();
   const d=r.import.diff;toast(`${frm} imported: ${r.import.components} components, ${r.import.assets} textures · difference from the original ${d?.percent!=null?d.percent.toFixed(2)+' %':'not measured'}${r.import.notes?.length?` · ${r.import.notes.length} notes`:''}`)}
  catch(e){$('#um-cat-status').textContent=e.message||String(e)}}
 async function previewSwf(name){const fr=$('#um-swf-frame').value,t=Number($('#um-swf-time').value||0);$('#um-cat-status').textContent=`Drawing ${name} with GFx…`;
  try{const r=await api('ui-maker-swf',{name,frame:fr===''?-1:Number(fr),time:t,width:1024,height:768});$('#um-catalog').close();$('#um-orig').checked=false;showImage(r.image,1024,768,`${name} · ${r.info.frameCount} frames · time ${t}s (Flash preview)`)}
  catch(e){$('#um-cat-status').textContent=e.message||String(e)}}
 run('#um-import',async()=>{$('#um-catalog').showModal();if(!catalog){$('#um-cat-status').textContent='Reading the client gui.pak and flash.pak…';catalog=await api('ui-maker-catalog',{});$('#um-cat-status').textContent=`${catalog.frm.length} windows · ${catalog.flash.length} Flash movies`}listCatalog();$('#um-cat-filter').focus()});
 $('#um-cat-filter').oninput=()=>{clearTimeout(catTimer);catTimer=setTimeout(listCatalog,120)};
 $('#um-orig').onchange=async()=>{if(!state)return;if(!$('#um-orig').checked){await doRender();return}const [w,h]=$('#um-size').value.split('x').map(Number);$('#um-render-state').textContent='Building the original window from its .frm…';
  try{const r=await api('ui-maker-original',{path:state.path,width:w,height:h});showImage(r.image,w,h,`Original (${state.source}) · uncheck "Original" to return to the layout`);render=null}
  catch(e){$('#um-orig').checked=false;toast(e.message||String(e),true)}};
 $('#um-variant').onchange=doRender;$('#um-size').onchange=doRender;$('#um-boxes').onchange=draw;$('#um-zoom').onchange=draw;
 new ResizeObserver(()=>draw()).observe($('#um-stage'));
 return{async show(){if(state&&!render)await doRender()},hide(){}};
}
