// Quest scripts are data. Native classes/fields remain visible for unsupported legacy nodes.
export function installQuestEditor({api, toast, escape: e}) {
 const host = document.querySelector('#quests');
 if (!host) throw Error('Falta la sección de quests.');
 const labels = {CNtlTSTrigger:'Quest',CNtlTSGroup:'Grupo de flujo',CDboTSContStart:'Inicio',CDboTSContGAct:'Paso de acciones',CDboTSContGCond:'Decisión',CDboTSContEnd:'Final',CDboTSActPCConv:'Diálogo del personaje',CDboTSCheckLvl:'Comprobar nivel',CDboTSContReward:'Recompensa',CDboTSContProposal:'Propuesta',CDboTSClickNPC:'Interactuar con NPC',CDboTSActNPCConv:'Diálogo del NPC',CDboTSActQItem:'Objeto de misión',CDboTSCheckQItem:'Comprobar objetos de misión'};
 const fieldLabels = {tid:'ID de quest',gid:'ID de grupo',cid:'ID de paso',nextlnk:'Siguiente paso',elnk:'Paso si hay error',yeslnk:'Paso si se cumple',nolnk:'Paso si no se cumple',prelnk:'Pasos anteriores',title:'Texto del título',stdiag:'Texto de inicio',tblidx:'ID de diálogo',minlvl:'Nivel mínimo',maxlvl:'Nivel máximo',taid:'ID de acción',rm:'Mostrar marca de recompensa',sm:'Modo de ejecución',sq:'Quest secuencial',qc:'Categoría de quest',rq:'Quest repetible'};
 const links = new Set(['nextlnk','elnk','yeslnk','nolnk']);
 const templates = [
  {kind:'CNtlTSGroup',hint:'Agrupa un flujo independiente. Cada paso usa un ID local a este grupo.',fields:{gid:'0'}},
  {kind:'CDboTSContGAct',hint:'Agregá acciones dentro y elegí el siguiente paso. 255 significa sin enlace.',fields:{cid:'1',nextlnk:'255',elnk:'255',prelnk:'""'}},
  {kind:'CDboTSContGCond',hint:'Agregá una condición y elegí las rutas de sí y no.',fields:{cid:'1',yeslnk:'255',nolnk:'255',prelnk:'""'}},
  {kind:'CDboTSContEnd',hint:'Tipo 0 termina sin completar; tipo 1 completa la quest. Revisá sus requisitos antes de usarlo.',fields:{cid:'1',type:'0',prelnk:'""'}},
  {kind:'CDboTSActPCConv',hint:'El diálogo referencia una entrada de texto existente; no crea sus textos.',fields:{tblidx:'0'}},
  {kind:'CDboTSCheckLvl',hint:'Solo se cumple dentro del intervalo de niveles configurado.',fields:{minlvl:'1',maxlvl:'1'}}
 ];
 host.innerHTML = `<div class="quest-workspace"><aside class="quest-sidebar"><div class="wps-file-card"><strong id="quest-file">Editor de quests</strong><small id="quest-file-hint">Creá una quest o abrí un script .t.</small></div><button id="quest-open" class="button">Abrir quest…</button><button id="quest-new" class="button primary">Nueva quest…</button><button id="quest-template" class="button">Crear desde plantilla…</button><label class="quest-search-label" for="quest-search">Buscar pasos y campos</label><input id="quest-search" type="search" placeholder="Inicio, nivel, ID…"><nav id="quest-tree" class="quest-tree" aria-label="Estructura de la quest"></nav></aside><main class="quest-main"><header class="wps-header"><div><span class="eyebrow">QUESTS · TRIGGERS</span><h1 id="quest-title">Una quest, paso a paso</h1><p id="quest-subtitle">Seleccioná un bloque para entender sus condiciones y acciones.</p></div><div class="wps-toolbar"><button id="quest-undo" class="button">Deshacer</button><button id="quest-redo" class="button">Rehacer</button><button id="quest-source" class="button">Ver script</button><button id="quest-save" class="button primary">Exportar revisión…</button></div></header><div id="quest-body" class="quest-body"><p class="muted-note">Nueva quest prepara un borrador. Crear desde plantilla conserva la estructura de una quest existente con un ID nuevo.</p></div><details class="wps-diagnostics-wrap" open><summary id="quest-diagnostic-count">Sin script abierto</summary><div id="quest-diagnostics"></div></details><div id="quest-path" class="wps-path"></div></main></div>`;
 const dialogs = document.createElement('div');
 dialogs.innerHTML = `<dialog id="quest-create-dialog" class="wps-dialog"><h2 id="quest-create-title">Nueva quest</h2><p id="quest-create-note"></p><label class="wps-field"><span>ID nuevo de quest</span><input id="quest-create-id" type="number" min="1" max="65534" step="1" value="60000"><small>1–65534. No reemplaza una quest existente. Confirmá disponibilidad antes de instalar.</small></label><div class="actions"><button id="quest-create-cancel" class="button">Cancelar</button><button id="quest-create-confirm" class="button primary">Crear borrador</button></div></dialog><dialog id="quest-add-dialog" class="wps-dialog"><h2>Agregar un bloque</h2><p id="quest-add-parent"></p><label class="wps-field"><span>Qué querés agregar</span><select id="quest-add-kind"></select></label><p id="quest-add-hint"></p><div id="quest-add-fields"></div><div class="notice">El bloque se agrega al final del padre elegido. Conectá sus enlaces y revisá los diagnósticos antes de exportar.</div><div class="actions"><button id="quest-add-cancel" class="button">Cancelar</button><button id="quest-add-confirm" class="button primary">Agregar bloque</button></div></dialog><dialog id="quest-source-dialog" class="wps-dialog wps-code-dialog"><h2>Comparar script</h2><p>Vista de solo lectura. Los cambios se realizan en los campos del editor.</p><div class="wps-code-columns"><div><h3>Original</h3><pre id="quest-original"></pre></div><div><h3>Revisión actual</h3><pre id="quest-current"></pre></div></div><div class="actions"><button id="quest-source-close" class="button">Cerrar</button></div></dialog><dialog id="quest-saved-dialog" class="wps-dialog"><h2>Revisión exportada</h2><p>Se generó un candidato separado con original y auditoría. Validá la quest en juego antes de instalar.</p><pre id="quest-output"></pre><div class="actions"><button id="quest-saved-close" class="button">Cerrar</button></div></dialog>`;
 host.append(dialogs);
 const $ = selector => host.querySelector(selector);
 let doc = null, selected = null, busy = false, createMode = 'new', dirtySerial = 0, dirtyQueue = Promise.resolve();
 const nodes = () => doc?.nodes || [];
 const same = (a,b) => a != null && b != null && String(a) === String(b);
 const find = id => nodes().find(n => same(n.id,id));
 const field = (n,key) => n?.fields?.find(f => f.key === key);
 const value = (n,key) => field(n,key)?.value;
 const parent = n => find(n?.parentId);
 const quote = v => '"' + String(v).replace(/[\\"\n\r\t]/g,c=>({'\\':'\\\\','"':'\\"','\n':'\\n','\r':'\\r','\t':'\\t'}[c])) + '"';
 const title = n => { const key = n.kind === 'CNtlTSTrigger' ? 'tid' : n.kind === 'CNtlTSGroup' ? 'gid' : n.kind.startsWith('CDboTSCont') ? 'cid' : null; return (labels[n.kind] || n.kind) + (key && field(n,key) ? ' ' + value(n,key) : ''); };
 function depth(n) { const seen = new Set(); let d=0; while ((n=parent(n)) && !seen.has(n.id) && d<30) { seen.add(n.id); d++; } return d; }
 function groupOf(n) { const seen=new Set(); while(n && n.kind!=='CNtlTSGroup' && !seen.has(n.id)){seen.add(n.id);n=parent(n)}return n; }
 function summary(n) { if(n.kind==='CDboTSCheckLvl') return `Nivel ${value(n,'minlvl')??'?'} a ${value(n,'maxlvl')??'?'}`; if(n.kind==='CDboTSContEnd') return Number(value(n,'type'))===1?'Completar la quest':'Terminar sin completar'; return (n.fields||[]).filter(f=>!['prelnk','cid','gid','tid'].includes(f.key)).slice(0,3).map(f=>`${fieldLabels[f.key]||f.key}: ${f.value}`).join(' · '); }
 const inputs = () => [...$('#quest-body').querySelectorAll('[data-quest-key]')];
 const pending = () => inputs().filter(i=>i.value!==i.dataset.initial);
 function markDirty(dirty) { dirtyQueue=dirtyQueue.catch(()=>{}).then(()=>api('quest-dirty',{dirty})).catch(err=>{toast(err.message,true);throw err}); dirtyQueue.catch(()=>{}); return dirtyQueue; }
 function buttons() {
  for(const id of ['open','new','template']) $('#quest-'+id).disabled=busy;
  $('#quest-save').disabled=busy||!doc?.loaded||!doc.canMutate||(doc.diagnostics||[]).some(d=>d.severity==='error');
  $('#quest-source').disabled=busy||!doc?.loaded;
  $('#quest-undo').disabled=busy||!doc?.undo;
  $('#quest-redo').disabled=busy||!doc?.redo;
  for(const b of host.querySelectorAll('[data-quest-action]')) b.disabled=busy||!doc?.canMutate||(b.id==='quest-add'&&!available(find(selected)||{}).length);
  for(const input of host.querySelectorAll('[data-quest-key]')) input.disabled=busy||!doc?.canMutate;
  for(const id of ['quest-create-confirm','quest-add-confirm']) $('#'+id).disabled=busy;
 }
 async function guarded(fn) { if(busy)return;busy=true;buttons();try{return await fn()}catch(err){toast(err.message,true);return false}finally{busy=false;buttons()} }
 function setDocument(state) { if(!state)return false; doc=state; if(!find(selected))selected=nodes()[0]?.id??null; render(); return true; }
 function renderTree() {
  const q=$('#quest-search').value.trim().toLowerCase();
  $('#quest-tree').innerHTML=nodes().filter(n=>(title(n)+' '+n.kind+' '+summary(n)).toLowerCase().includes(q)).map(n=>`<button class="quest-tree-node ${same(n.id,selected)?'selected':''}" data-select="${e(String(n.id))}" style="--depth:${depth(n)}" aria-current="${same(n.id,selected)?'true':'false'}"><span>${e(title(n))}</span><small>L${e(String(n.line))}</small></button>`).join('')||'<p class="muted-note">Sin bloques para este filtro.</p>';
  for(const b of $('#quest-tree').querySelectorAll('[data-select]')) b.onclick=()=>guarded(async()=>{await flush();selected=b.dataset.select;render()});
 }
 function render() {
  buttons(); renderTree();
  if(!doc?.loaded) return;
  $('#quest-file').textContent=doc.name;
  $('#quest-file-hint').textContent=`${nodes().length} bloques · ${doc.encoding}${doc.modified?' · Revisión modificada':''}`;
  $('#quest-path').textContent=doc.source;
  $('#quest-subtitle').textContent=doc.canMutate?'Editá los pasos, conectá el flujo y exportá una revisión para probar.':'Hay observaciones que bloquean la exportación. Revisá los campos y enlaces indicados.';
  const ds=doc.diagnostics||[];
  $('#quest-diagnostic-count').textContent=`${ds.length} observaciones${ds.some(d=>d.severity==='error')?' · Exportación bloqueada':''}`;
  $('#quest-diagnostics').innerHTML=ds.length?ds.slice(0,150).map(d=>`<div class="wps-diagnostic ${d.severity==='error'?'error':'warning'}"><strong>${e(d.severity)} · L${e(String(d.line))}</strong><span>${e(d.message)}</span></div>`).join(''):'<p class="muted-note">Sin errores estáticos. Falta validar aceptación, progreso y recompensas dentro del juego.</p>';
  renderNode();
 }
 function renderNode() {
  const n=find(selected);if(!n){$('#quest-body').textContent='Seleccioná un bloque.';return}
  $('#quest-title').textContent=title(n);
  $('#quest-body').innerHTML=`<div class="quest-node-heading"><div><span class="quest-native"></span><p class="quest-description"></p></div><button class="button" id="quest-add" data-quest-action>Agregar dentro…</button></div><div id="quest-links" class="quest-links"></div><div class="quest-form-heading"><h2>Propiedades</h2><span id="quest-pending">Los cambios se aplican al cambiar de paso o exportar.</span></div><div id="quest-properties" class="quest-properties"></div><div class="actions quest-form-actions"><button id="quest-apply" class="button primary" data-quest-action>Aplicar valores</button></div><div id="quest-children" class="quest-children"></div>`;
  $('.quest-native').textContent=`${n.kind} · línea ${n.line}`;
  $('.quest-description').textContent=summary(n)||'Este bloque organiza los pasos de la quest.';
  const siblings=nodes().filter(x=>x.kind.startsWith('CDboTSCont') && same(groupOf(x)?.id,groupOf(n)?.id));
  for(const f of n.fields||[]) {
   const label=document.createElement('label');label.className='wps-field';
   const caption=document.createElement('span');caption.textContent=f.key==='type'&&n.kind==='CDboTSContEnd'?'Resultado (0: sin completar; 1: completar)':fieldLabels[f.key]||f.key;label.append(caption);
   let input;
   if(links.has(f.key) && f.valueKind==='number') {
    input=document.createElement('select'); input.add(new Option('Sin enlace (255)','255'));
    const ids=new Set(['255']);for(const target of siblings){const cid=String(value(target,'cid'));if(!ids.has(cid)){ids.add(cid);input.add(new Option(title(target),cid))}}
    if(!ids.has(String(f.value))) input.add(new Option(`Enlace sin resolver (${f.value})`,String(f.value)));
   } else input=document.createElement(String(f.value??'').length>100?'textarea':'input');
   input.value=String(f.value??'');input.dataset.questKey=f.key;input.dataset.kind=f.valueKind;input.dataset.initial=input.value;input.dataset.literal=f.literal;
   if(f.valueKind==='number'&&input.tagName==='INPUT')input.inputMode='decimal';
   input.addEventListener('input',()=>{dirtySerial++;markDirty(true);$('#quest-pending').textContent='Cambios pendientes en este paso.'});
   label.append(input);const hint=document.createElement('small');hint.textContent=f.key+(links.has(f.key)?' · ID dentro del mismo grupo; 255 = sin enlace':'');label.append(hint);$('#quest-properties').append(label);
   if(links.has(f.key)&&Number(f.value)!==255){const target=siblings.find(x=>Number(value(x,'cid'))===Number(f.value));const b=document.createElement('button');b.className='button';b.textContent=`${fieldLabels[f.key]} → ${target?title(target):'Sin resolver '+f.value}`;b.disabled=!target;b.onclick=()=>guarded(async()=>{await flush();selected=target.id;render()});$('#quest-links').append(b)}
  }
  if(!n.fields?.length)$('#quest-properties').innerHTML='<p class="muted-note">Sin campos directos. Agregá un bloque dentro para continuar.</p>';
  const children=nodes().filter(x=>same(x.parentId,n.id));
  if(children.length){const h=document.createElement('h2');h.textContent='Bloques dentro';$('#quest-children').append(h);for(const child of children){const b=document.createElement('button');b.className='quest-child';b.textContent=`${title(child)} · ${summary(child)||child.kind} · L${child.line}`;b.onclick=()=>guarded(async()=>{await flush();selected=child.id;render()});$('#quest-children').append(b)}}
  $('#quest-apply').onclick=()=>guarded(async()=>{if(await flush())toast('Valores aplicados a la revisión.');render()});
  $('#quest-add').disabled=available(n).length===0;
  $('#quest-add').onclick=()=>guarded(async()=>{await flush();showAdd()});
  buttons();
 }
 async function flush() {
  const changed=pending(); if(!changed.length){await dirtyQueue;return false}
  const id=selected, serial=dirtySerial;
  const updates=changed.map(i=>{
   const literal=i.dataset.kind==='string'?quote(i.value):i.value.trim();
   if(i.dataset.kind==='number'&&!/^[+-]?\d+$/.test(literal)){i.focus();throw Error(`Ingresá un entero válido para ${fieldLabels[i.dataset.questKey]||i.dataset.questKey}.`)}
   return {key:i.dataset.questKey,literal};
  });
  await dirtyQueue;
  const state=await api('quest-edit-many',{id,updates});
  if(!state)throw Error('No se aplicaron los campos. La entrada permanece pendiente.');
  if(serial!==dirtySerial)throw Error('Los campos cambiaron durante el guardado; revisalos antes de continuar.');
  await markDirty(false);setDocument(state);return true;
 }
 function available(n) { return templates.filter(t=>n.kind==='CNtlTSTrigger'?t.kind==='CNtlTSGroup':n.kind==='CNtlTSGroup'?t.kind.startsWith('CDboTSCont'):n.kind==='CDboTSContGAct'?t.kind==='CDboTSActPCConv':['CDboTSContGCond','CDboTSContStart'].includes(n.kind)?t.kind==='CDboTSCheckLvl':false); }
 function nextId(n,key){const used=new Set(nodes().filter(x=>same(x.parentId,n.id)).map(x=>Number(value(x,key))));for(let i=0;i<255;i++)if(!used.has(i))return i;throw Error('No quedan IDs libres entre 0 y 254 en este padre.');}
 function addFields() {
  const template=templates.find(t=>t.kind===$('#quest-add-kind').value);$('#quest-add-hint').textContent=template.hint;$('#quest-add-fields').replaceChildren();
  for(const [key,literal] of Object.entries(template.fields)) {
   const label=document.createElement('label');label.className='wps-field';const caption=document.createElement('span');caption.textContent=fieldLabels[key]||key;
   const input=document.createElement('input');input.dataset.key=key;input.dataset.kind=literal.startsWith('"')?'string':'number';input.value=key==='gid'||key==='cid'?String(nextId(find(selected),key)):literal==='""'?'':literal;
   const hint=document.createElement('small');hint.textContent=key+(key==='cid'||key==='gid'?' · ID libre sugerido, 0–254':'');label.append(caption,input,hint);$('#quest-add-fields').append(label);
  }
 }
 function showAdd() {const n=find(selected);const options=available(n);if(!options.length)throw Error('Este bloque no admite las plantillas disponibles.');$('#quest-add-parent').textContent='Dentro de '+title(n);$('#quest-add-kind').replaceChildren(...options.map(t=>new Option(labels[t.kind]||t.kind,t.kind)));addFields();$('#quest-add-dialog').showModal()}
 $('#quest-add-kind').onchange=addFields;
 $('#quest-add-confirm').onclick=()=>guarded(async()=>{const fields={};for(const i of $('#quest-add-fields').querySelectorAll('[data-key]')){if(i.dataset.kind==='number'&&!/^-?\d+$/.test(i.value.trim()))throw Error('Completá los campos numéricos con enteros.');fields[i.dataset.key]=i.dataset.kind==='string'?quote(i.value):i.value.trim()}const state=await api('quest-add',{parent:selected,kind:$('#quest-add-kind').value,fields});if(state){setDocument(state);$('#quest-add-dialog').close();toast('Bloque agregado. Revisá sus enlaces.')}});
 $('#quest-open').onclick=()=>guarded(async()=>{await flush();setDocument(await api('quest-open',{}))});
 for(const mode of ['new','template'])$('#quest-'+mode).onclick=()=>guarded(async()=>{await flush();createMode=mode;$('#quest-create-title').textContent=mode==='new'?'Nueva quest':'Crear desde una quest existente';$('#quest-create-note').textContent=mode==='new'?'Comenzá con un borrador y agregá sus condiciones, acciones y final.':'Se abrirá un selector de script .t. Conserva sus bloques y cambia únicamente la identidad a un ID nuevo; revisá textos, referencias y recompensas.';$('#quest-create-dialog').showModal()});
 $('#quest-create-confirm').onclick=()=>guarded(async()=>{const raw=$('#quest-create-id').value;if(!/^\d+$/.test(raw)||Number(raw)<1||Number(raw)>65534)throw Error('El ID de quest debe ser un entero entre 1 y 65534.');const state=await api(createMode==='new'?'quest-new':'quest-clone-template',{id:Number(raw)});if(state){setDocument(state);$('#quest-create-dialog').close();toast('Borrador de quest preparado.')}});
 for(const action of ['undo','redo'])$('#quest-'+action).onclick=()=>guarded(async()=>{await flush();setDocument(await api('quest-'+action,{}))});
 $('#quest-save').onclick=()=>guarded(async()=>{await flush();if(!doc?.canMutate||(doc.diagnostics||[]).some(d=>d.severity==='error'))throw Error('Corregí los errores indicados antes de exportar.');const result=await api('quest-save',{});if(result){$('#quest-output').textContent=JSON.stringify(result,null,2);$('#quest-saved-dialog').showModal();toast('Revisión exportada con original y auditoría.')}});
 $('#quest-source').onclick=()=>guarded(async()=>{await flush();const result=await api('quest-source',{});if(result){$('#quest-original').textContent=result.original;$('#quest-current').textContent=result.current;$('#quest-source-dialog').showModal()}});
 $('#quest-search').oninput=renderTree;
 for(const [button,dialog] of [['create-cancel','create'],['add-cancel','add'],['source-close','source'],['saved-close','saved']])$('#quest-'+button).onclick=()=>{if(!busy)$('#quest-'+dialog+'-dialog').close()};
 for(const dialog of host.querySelectorAll('dialog'))dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault()});
 buttons();
 return {async show(){if(busy)return;await flush();setDocument(await api('quest-state',{}))},hide(){for(const d of host.querySelectorAll('dialog'))if(d.open)d.close()},flush};
}
