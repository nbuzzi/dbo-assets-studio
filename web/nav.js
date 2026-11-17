// Navegación del Studio: menú lateral agrupado y plegable, ruta Área › Herramienta › Pestaña
// y paleta de comandos (Ctrl+K). No reemplaza showView(): solo la llama.
const RAIL_KEY='dbo-assets.studio.rail-collapsed';
const TAB_SELECTOR='.studio-tabs button,.mc-tabs button';

const normalize=text=>String(text||'').normalize('NFD').replace(/[̀-ͯ]/g,'').toLowerCase();
const escapeHtml=text=>String(text??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

// Coincidencia simple: primero substring (mejor puntaje si empieza la palabra), después subsecuencia.
function match(label,query){
 if(!query)return {score:0,ranges:[]};
 const hay=normalize(label),needle=normalize(query).trim();
 if(!needle)return {score:0,ranges:[]};
 const at=hay.indexOf(needle);
 if(at>=0)return {score:(at===0?300:/[\s·›(-]/.test(hay[at-1]||'')?200:100)-at,ranges:[[at,at+needle.length]]};
 const ranges=[];let from=0;
 for(const ch of needle){if(ch===' ')continue;const i=hay.indexOf(ch,from);if(i<0)return null;if(ranges.length&&ranges[ranges.length-1][1]===i)ranges[ranges.length-1][1]=i+1;else ranges.push([i,i+1]);from=i+1}
 return {score:50-ranges.length*5-ranges[0][0],ranges};
}
function highlight(label,ranges){
 if(!ranges?.length)return escapeHtml(label);
 let out='',last=0;
 for(const [a,b] of ranges){out+=escapeHtml(label.slice(last,a))+'<mark>'+escapeHtml(label.slice(a,b))+'</mark>';last=b}
 return out+escapeHtml(label.slice(last));
}

export function installNav({showView,getView,focusSearch,icon,toast}){
 const app=document.querySelector('.app');
 const rail=document.getElementById('rail');
 const toggle=document.getElementById('rail-toggle');
 const tools=[...rail.querySelectorAll('.rail-scroll [data-view]')].map(button=>({view:button.dataset.view,label:button.querySelector('.rail-label')?.textContent||button.title,area:button.dataset.area||'',icon:button.dataset.icon||'file'}));
 const areas=[...new Set(tools.map(t=>t.area))];
 const toolFor=view=>tools.find(t=>t.view===view);

 // --- menú plegable (se recuerda por visor; sin preferencia se pliega en ventanas angostas)
 const readPref=()=>{try{return localStorage.getItem(RAIL_KEY)}catch{return null}};
 const writePref=value=>{try{localStorage.setItem(RAIL_KEY,value)}catch{}};
 function setCollapsed(collapsed,remember=true){
  app.classList.toggle('rail-collapsed',collapsed);
  const label=collapsed?'Expandir menú':'Plegar menú';
  toggle.title=label;toggle.setAttribute('aria-label',label);toggle.setAttribute('aria-expanded',String(!collapsed));
  if(remember)writePref(collapsed?'1':'0');
  window.dispatchEvent(new Event('resize'));
 }
 const pref=readPref();
 setCollapsed(pref===null?window.innerWidth<1280:pref==='1',false);
 toggle.addEventListener('click',()=>setCollapsed(!app.classList.contains('rail-collapsed')));

 // --- ruta Área › Herramienta › Pestaña
 const crumbArea=document.getElementById('crumb-area');
 const crumbTool=document.getElementById('crumb');
 const crumbTab=document.getElementById('crumb-tab');
 const crumbTabSep=document.getElementById('crumb-tab-sep');
 const viewTabs=view=>[...(document.getElementById(view)?.querySelectorAll(TAB_SELECTOR)||[])];
 const tabLabel=button=>button.textContent.replace(/\s+/g,' ').trim();
 function syncCrumb(){
  const view=getView();
  const tool=toolFor(view);
  crumbArea.textContent=tool?.area||'';
  if(tool)crumbTool.textContent=tool.label;
  const active=viewTabs(view).find(b=>b.classList.contains('active'));
  crumbTab.textContent=active?tabLabel(active):'';
  crumbTab.classList.toggle('hidden',!active);crumbTabSep.classList.toggle('hidden',!active);
 }
 let crumbFrame=0;
 const scheduleCrumb=()=>{if(!crumbFrame)crumbFrame=requestAnimationFrame(()=>{crumbFrame=0;syncCrumb()})};
 new MutationObserver(records=>{if(records.some(r=>r.target.matches?.(TAB_SELECTOR)||r.target.classList?.contains('workspace')||r.target.id===getView()))scheduleCrumb()}).observe(app,{subtree:true,attributes:true,attributeFilter:['class']});

 // --- paleta de comandos
 const scrim=document.createElement('div');
 scrim.className='palette-scrim hidden';
 scrim.innerHTML=`<div class="palette" role="dialog" aria-modal="true" aria-label="Paleta de comandos"><div class="palette-input">${icon('search')}<input id="palette-query" type="text" autocomplete="off" spellcheck="false" placeholder="Ir a una herramienta, pestaña o acción…" aria-controls="palette-list"></div><div class="palette-list" id="palette-list" role="listbox"></div><div class="palette-foot"><span><kbd>↑</kbd> <kbd>↓</kbd> moverse</span><span><kbd>Enter</kbd> abrir</span><span><kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>${areas.length}</kbd> áreas</span><span><kbd>Esc</kbd> cerrar</span></div></div>`;
 document.body.appendChild(scrim);
 const input=scrim.querySelector('input'),list=scrim.querySelector('.palette-list');
 let rows=[],selected=0,lastFocus=null;

 const go=async view=>{if(getView()!==view)await showView(view)};
 function commands(){
  const view=getView(),current=toolFor(view);
  const out=[];
  if(focusSearch.has(view))out.push({section:'Acciones',label:`Buscar en ${current?.label||'esta vista'}`,icon:'search',hint:'en la vista',run:query=>focusSearch.run(view,query)});
  for(const t of tools)out.push({section:'Herramientas',label:t.label,icon:t.icon,hint:t.area,current:t.view===view,run:()=>go(t.view)});
  for(const t of tools)for(const button of viewTabs(t.view)){
   const label=tabLabel(button);if(!label)continue;
   out.push({section:'Pestañas',label:`${t.label} › ${label}`,icon:t.icon,hint:'pestaña',run:async()=>{try{await go(t.view)}finally{button.click()}}});
  }
  out.push({section:'Acciones',label:'Abrir archivo…',icon:'plus',hint:'Ctrl+O',run:()=>document.getElementById('open')?.click()});
  out.push({section:'Acciones',label:'Guardar captura del workspace',icon:'camera',hint:'',run:()=>document.getElementById('capture')?.click()});
  out.push({section:'Acciones',label:app.classList.contains('rail-collapsed')?'Expandir menú lateral':'Plegar menú lateral',icon:'panel',hint:'',run:()=>toggle.click()});
  return out;
 }
 function render(){
  const query=input.value;
  const all=commands();
  if(query.trim()){
   rows=all.map(c=>({...c,m:match(c.label,query)})).filter(c=>c.m).sort((a,b)=>b.m.score-a.m.score).slice(0,40);
  }else{
   // Sin texto: buscar en la vista, herramientas y acciones (las pestañas aparecen al escribir)
   rows=all.filter(c=>c.section!=='Pestañas').map(c=>({...c,m:{ranges:[]}}));
  }
  selected=Math.min(selected,Math.max(rows.length-1,0));
  if(!rows.length){list.innerHTML=`<div class="palette-empty">Sin resultados para “${escapeHtml(query)}”.</div>`;return}
  let html='',section='';
  rows.forEach((row,i)=>{
   if(!query.trim()&&row.section!==section){section=row.section;html+=`<div class="palette-section">${escapeHtml(section)}</div>`}
   html+=`<button type="button" class="palette-row${i===selected?' selected':''}" role="option" aria-selected="${i===selected}" data-index="${i}">${icon(row.icon)}<span>${highlight(row.label,row.m.ranges)}</span><small>${escapeHtml(row.current?'actual':row.hint||'')}</small></button>`;
  });
  list.innerHTML=html;
  list.querySelector('.palette-row.selected')?.scrollIntoView({block:'nearest'});
 }
 function open(){
  lastFocus=document.activeElement;
  input.value='';selected=0;scrim.classList.remove('hidden');render();input.focus();
 }
 function close(restore=true){
  if(scrim.classList.contains('hidden'))return;
  scrim.classList.add('hidden');
  if(restore&&lastFocus?.isConnected)lastFocus.focus?.();
 }
 async function runSelected(index=selected){
  const row=rows[index];if(!row)return;
  const query=input.value;
  close(false);
  try{await row.run(query)}catch(e){toast(e.message,true)}
 }
 input.addEventListener('input',()=>{selected=0;render()});
 input.addEventListener('keydown',e=>{
  if(e.key==='ArrowDown'){e.preventDefault();selected=(selected+1)%Math.max(rows.length,1);render()}
  else if(e.key==='ArrowUp'){e.preventDefault();selected=(selected-1+rows.length)%Math.max(rows.length,1);render()}
  else if(e.key==='Enter'){e.preventDefault();runSelected()}
  else if(e.key==='Escape'){e.preventDefault();close()}
 });
 list.addEventListener('mousemove',e=>{const row=e.target.closest('.palette-row');if(row&&Number(row.dataset.index)!==selected){selected=Number(row.dataset.index);list.querySelectorAll('.palette-row').forEach((r,i)=>{r.classList.toggle('selected',i===selected);r.setAttribute('aria-selected',String(i===selected))})}});
 list.addEventListener('click',e=>{const row=e.target.closest('.palette-row');if(row)runSelected(Number(row.dataset.index))});
 scrim.addEventListener('mousedown',e=>{if(e.target===scrim)close()});
 document.getElementById('palette-open')?.addEventListener('click',open);

 // Ctrl+1…N: primera herramienta de cada área
 document.addEventListener('keydown',e=>{
  if(!e.ctrlKey||e.altKey||e.shiftKey||e.metaKey)return;
  const n=Number(e.key);
  if(!Number.isInteger(n)||n<1||n>areas.length)return;
  e.preventDefault();close(false);
  const target=tools.find(t=>t.area===areas[n-1]);
  if(target)go(target.view).catch(err=>toast(err.message,true));
 });

 return {open,close,toggle:()=>scrim.classList.contains('hidden')?open():close(),isOpen:()=>!scrim.classList.contains('hidden'),sync:syncCrumb,setCollapsed};
}
