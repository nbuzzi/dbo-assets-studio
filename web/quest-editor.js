// Quest scripts are data. Native classes/fields remain visible for unsupported legacy nodes.
export function installQuestEditor({api, toast, escape: e}) {
 const host = document.querySelector('#quests');
 if (!host) throw Error('The quests section is missing.');
 const labels = {CNtlTSTrigger:'Quest',CNtlTSGroup:'Flow group',CDboTSContStart:'Start',CDboTSContGAct:'Action step',CDboTSContGCond:'Decision',CDboTSContEnd:'End',CDboTSActPCConv:'Character dialog',CDboTSCheckLvl:'Check level',CDboTSContReward:'Reward',CDboTSContProposal:'Proposal',CDboTSClickNPC:'Interact with NPC',CDboTSActNPCConv:'NPC dialog',CDboTSActQItem:'Quest item',CDboTSCheckQItem:'Check quest items'};
 const fieldLabels = {tid:'Quest ID',gid:'Group ID',cid:'Step ID',nextlnk:'Next step',elnk:'Step on error',yeslnk:'Step if met',nolnk:'Step if not met',prelnk:'Previous steps',title:'Title text',stdiag:'Start text',tblidx:'Dialog ID',minlvl:'Minimum level',maxlvl:'Maximum level',taid:'Action ID',rm:'Show reward mark',sm:'Run mode',sq:'Sequential quest',qc:'Quest category',rq:'Repeatable quest'};
 const links = new Set(['nextlnk','elnk','yeslnk','nolnk']);
 const templates = [
  {kind:'CNtlTSGroup',hint:'Groups an independent flow. Each step uses an ID local to this group.',fields:{gid:'0'}},
  {kind:'CDboTSContGAct',hint:'Add actions inside and choose the next step. 255 means no link.',fields:{cid:'1',nextlnk:'255',elnk:'255',prelnk:'""'}},
  {kind:'CDboTSContGCond',hint:'Add a condition and choose the yes and no paths.',fields:{cid:'1',yeslnk:'255',nolnk:'255',prelnk:'""'}},
  {kind:'CDboTSContEnd',hint:'Type 0 ends without completing; type 1 completes the quest. Check its requirements before using it.',fields:{cid:'1',type:'0',prelnk:'""'}},
  {kind:'CDboTSActPCConv',hint:'The dialog references an existing text entry; it does not create its texts.',fields:{tblidx:'0'}},
  {kind:'CDboTSCheckLvl',hint:'Only met within the configured level range.',fields:{minlvl:'1',maxlvl:'1'}}
 ];
 host.innerHTML = `<div class="quest-workspace"><aside class="quest-sidebar"><div class="wps-file-card"><strong id="quest-file">Quest editor</strong><small id="quest-file-hint">Create a quest or open a .t script.</small></div><button id="quest-open" class="button">Open quest…</button><button id="quest-new" class="button primary">New quest…</button><button id="quest-template" class="button">Create from template…</button><label class="quest-search-label" for="quest-search">Search steps and fields</label><input id="quest-search" type="search" placeholder="Start, level, ID…"><nav id="quest-tree" class="quest-tree" aria-label="Quest structure"></nav></aside><main class="quest-main"><header class="wps-header"><div><span class="eyebrow">QUESTS · TRIGGERS</span><h1 id="quest-title">A quest, step by step</h1><p id="quest-subtitle">Select a block to understand its conditions and actions.</p></div><div class="wps-toolbar"><button id="quest-undo" class="button">Undo</button><button id="quest-redo" class="button">Redo</button><button id="quest-source" class="button">View script</button><button id="quest-save" class="button primary">Export revision…</button></div></header><div id="quest-body" class="quest-body"><p class="muted-note">New quest prepares a draft. Create from template keeps the structure of an existing quest with a new ID.</p></div><details class="wps-diagnostics-wrap" open><summary id="quest-diagnostic-count">No script open</summary><div id="quest-diagnostics"></div></details><div id="quest-path" class="wps-path"></div></main></div>`;
 const dialogs = document.createElement('div');
 dialogs.innerHTML = `<dialog id="quest-create-dialog" class="wps-dialog"><h2 id="quest-create-title">New quest</h2><p id="quest-create-note"></p><label class="wps-field"><span>New quest ID</span><input id="quest-create-id" type="number" min="1" max="65534" step="1" value="60000"><small>1–65534. Does not replace an existing quest. Confirm availability before installing.</small></label><div class="actions"><button id="quest-create-cancel" class="button">Cancel</button><button id="quest-create-confirm" class="button primary">Create draft</button></div></dialog><dialog id="quest-add-dialog" class="wps-dialog"><h2>Add a block</h2><p id="quest-add-parent"></p><label class="wps-field"><span>What to add</span><select id="quest-add-kind"></select></label><p id="quest-add-hint"></p><div id="quest-add-fields"></div><div class="notice">The block is added at the end of the chosen parent. Connect its links and check the diagnostics before exporting.</div><div class="actions"><button id="quest-add-cancel" class="button">Cancel</button><button id="quest-add-confirm" class="button primary">Add block</button></div></dialog><dialog id="quest-source-dialog" class="wps-dialog wps-code-dialog"><h2>Compare script</h2><p>Read-only view. Changes are made in the editor fields.</p><div class="wps-code-columns"><div><h3>Original</h3><pre id="quest-original"></pre></div><div><h3>Current revision</h3><pre id="quest-current"></pre></div></div><div class="actions"><button id="quest-source-close" class="button">Close</button></div></dialog><dialog id="quest-saved-dialog" class="wps-dialog"><h2>Revision exported</h2><p>A separate candidate was generated with the original and an audit. Validate the quest in game before installing.</p><pre id="quest-output"></pre><div class="actions"><button id="quest-saved-close" class="button">Close</button></div></dialog>`;
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
 function summary(n) { if(n.kind==='CDboTSCheckLvl') return `Level ${value(n,'minlvl')??'?'} to ${value(n,'maxlvl')??'?'}`; if(n.kind==='CDboTSContEnd') return Number(value(n,'type'))===1?'Complete the quest':'End without completing'; return (n.fields||[]).filter(f=>!['prelnk','cid','gid','tid'].includes(f.key)).slice(0,3).map(f=>`${fieldLabels[f.key]||f.key}: ${f.value}`).join(' · '); }
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
  $('#quest-tree').innerHTML=nodes().filter(n=>(title(n)+' '+n.kind+' '+summary(n)).toLowerCase().includes(q)).map(n=>`<button class="quest-tree-node ${same(n.id,selected)?'selected':''}" data-select="${e(String(n.id))}" style="--depth:${depth(n)}" aria-current="${same(n.id,selected)?'true':'false'}"><span>${e(title(n))}</span><small>L${e(String(n.line))}</small></button>`).join('')||'<p class="muted-note">No blocks match this filter.</p>';
  for(const b of $('#quest-tree').querySelectorAll('[data-select]')) b.onclick=()=>guarded(async()=>{await flush();selected=b.dataset.select;render()});
 }
 function render() {
  buttons(); renderTree();
  if(!doc?.loaded) return;
  $('#quest-file').textContent=doc.name;
  $('#quest-file-hint').textContent=`${nodes().length} blocks · ${doc.encoding}${doc.modified?' · Revision modified':''}`;
  $('#quest-path').textContent=doc.source;
  $('#quest-subtitle').textContent=doc.canMutate?'Edit the steps, connect the flow and export a revision to test.':'Some findings block the export. Check the fields and links shown.';
  const ds=doc.diagnostics||[];
  $('#quest-diagnostic-count').textContent=`${ds.length} findings${ds.some(d=>d.severity==='error')?' · Export blocked':''}`;
  $('#quest-diagnostics').innerHTML=ds.length?ds.slice(0,150).map(d=>`<div class="wps-diagnostic ${d.severity==='error'?'error':'warning'}"><strong>${e(d.severity)} · L${e(String(d.line))}</strong><span>${e(d.message)}</span></div>`).join(''):'<p class="muted-note">No static errors. Acceptance, progress and rewards still need validation in game.</p>';
  renderNode();
 }
 function renderNode() {
  const n=find(selected);if(!n){$('#quest-body').textContent='Select a block.';return}
  $('#quest-title').textContent=title(n);
  $('#quest-body').innerHTML=`<div class="quest-node-heading"><div><span class="quest-native"></span><p class="quest-description"></p></div><button class="button" id="quest-add" data-quest-action>Add inside…</button></div><div id="quest-links" class="quest-links"></div><div class="quest-form-heading"><h2>Properties</h2><span id="quest-pending">Changes apply when you switch steps or export.</span></div><div id="quest-properties" class="quest-properties"></div><div class="actions quest-form-actions"><button id="quest-apply" class="button primary" data-quest-action>Apply values</button></div><div id="quest-children" class="quest-children"></div>`;
  $('.quest-native').textContent=`${n.kind} · line ${n.line}`;
  $('.quest-description').textContent=summary(n)||'This block organizes the quest steps.';
  const siblings=nodes().filter(x=>x.kind.startsWith('CDboTSCont') && same(groupOf(x)?.id,groupOf(n)?.id));
  for(const f of n.fields||[]) {
   const label=document.createElement('label');label.className='wps-field';
   const caption=document.createElement('span');caption.textContent=f.key==='type'&&n.kind==='CDboTSContEnd'?'Result (0: not completed; 1: completed)':fieldLabels[f.key]||f.key;label.append(caption);
   let input;
   if(links.has(f.key) && f.valueKind==='number') {
    input=document.createElement('select'); input.add(new Option('No link (255)','255'));
    const ids=new Set(['255']);for(const target of siblings){const cid=String(value(target,'cid'));if(!ids.has(cid)){ids.add(cid);input.add(new Option(title(target),cid))}}
    if(!ids.has(String(f.value))) input.add(new Option(`Unresolved link (${f.value})`,String(f.value)));
   } else input=document.createElement(String(f.value??'').length>100?'textarea':'input');
   input.value=String(f.value??'');input.dataset.questKey=f.key;input.dataset.kind=f.valueKind;input.dataset.initial=input.value;input.dataset.literal=f.literal;
   if(f.valueKind==='number'&&input.tagName==='INPUT')input.inputMode='decimal';
   input.addEventListener('input',()=>{dirtySerial++;markDirty(true);$('#quest-pending').textContent='Pending changes in this step.'});
   label.append(input);const hint=document.createElement('small');hint.textContent=f.key+(links.has(f.key)?' · ID within the same group; 255 = no link':'');label.append(hint);$('#quest-properties').append(label);
   if(links.has(f.key)&&Number(f.value)!==255){const target=siblings.find(x=>Number(value(x,'cid'))===Number(f.value));const b=document.createElement('button');b.className='button';b.textContent=`${fieldLabels[f.key]} → ${target?title(target):'Unresolved '+f.value}`;b.disabled=!target;b.onclick=()=>guarded(async()=>{await flush();selected=target.id;render()});$('#quest-links').append(b)}
  }
  if(!n.fields?.length)$('#quest-properties').innerHTML='<p class="muted-note">No direct fields. Add a block inside to continue.</p>';
  const children=nodes().filter(x=>same(x.parentId,n.id));
  if(children.length){const h=document.createElement('h2');h.textContent='Blocks inside';$('#quest-children').append(h);for(const child of children){const b=document.createElement('button');b.className='quest-child';b.textContent=`${title(child)} · ${summary(child)||child.kind} · L${child.line}`;b.onclick=()=>guarded(async()=>{await flush();selected=child.id;render()});$('#quest-children').append(b)}}
  $('#quest-apply').onclick=()=>guarded(async()=>{if(await flush())toast('Values applied to the revision.');render()});
  $('#quest-add').disabled=available(n).length===0;
  $('#quest-add').onclick=()=>guarded(async()=>{await flush();showAdd()});
  buttons();
 }
 async function flush() {
  const changed=pending(); if(!changed.length){await dirtyQueue;return false}
  const id=selected, serial=dirtySerial;
  const updates=changed.map(i=>{
   const literal=i.dataset.kind==='string'?quote(i.value):i.value.trim();
   if(i.dataset.kind==='number'&&!/^[+-]?\d+$/.test(literal)){i.focus();throw Error(`Enter a valid integer for ${fieldLabels[i.dataset.questKey]||i.dataset.questKey}.`)}
   return {key:i.dataset.questKey,literal};
  });
  await dirtyQueue;
  const state=await api('quest-edit-many',{id,updates});
  if(!state)throw Error('The fields were not applied. The input is still pending.');
  if(serial!==dirtySerial)throw Error('The fields changed while saving; check them before continuing.');
  await markDirty(false);setDocument(state);return true;
 }
 function available(n) { return templates.filter(t=>n.kind==='CNtlTSTrigger'?t.kind==='CNtlTSGroup':n.kind==='CNtlTSGroup'?t.kind.startsWith('CDboTSCont'):n.kind==='CDboTSContGAct'?t.kind==='CDboTSActPCConv':['CDboTSContGCond','CDboTSContStart'].includes(n.kind)?t.kind==='CDboTSCheckLvl':false); }
 function nextId(n,key){const used=new Set(nodes().filter(x=>same(x.parentId,n.id)).map(x=>Number(value(x,key))));for(let i=0;i<255;i++)if(!used.has(i))return i;throw Error('No free IDs left between 0 and 254 in this parent.');}
 function addFields() {
  const template=templates.find(t=>t.kind===$('#quest-add-kind').value);$('#quest-add-hint').textContent=template.hint;$('#quest-add-fields').replaceChildren();
  for(const [key,literal] of Object.entries(template.fields)) {
   const label=document.createElement('label');label.className='wps-field';const caption=document.createElement('span');caption.textContent=fieldLabels[key]||key;
   const input=document.createElement('input');input.dataset.key=key;input.dataset.kind=literal.startsWith('"')?'string':'number';input.value=key==='gid'||key==='cid'?String(nextId(find(selected),key)):literal==='""'?'':literal;
   const hint=document.createElement('small');hint.textContent=key+(key==='cid'||key==='gid'?' · suggested free ID, 0–254':'');label.append(caption,input,hint);$('#quest-add-fields').append(label);
  }
 }
 function showAdd() {const n=find(selected);const options=available(n);if(!options.length)throw Error('This block does not accept the available templates.');$('#quest-add-parent').textContent='Inside '+title(n);$('#quest-add-kind').replaceChildren(...options.map(t=>new Option(labels[t.kind]||t.kind,t.kind)));addFields();$('#quest-add-dialog').showModal()}
 $('#quest-add-kind').onchange=addFields;
 $('#quest-add-confirm').onclick=()=>guarded(async()=>{const fields={};for(const i of $('#quest-add-fields').querySelectorAll('[data-key]')){if(i.dataset.kind==='number'&&!/^-?\d+$/.test(i.value.trim()))throw Error('Fill the numeric fields with integers.');fields[i.dataset.key]=i.dataset.kind==='string'?quote(i.value):i.value.trim()}const state=await api('quest-add',{parent:selected,kind:$('#quest-add-kind').value,fields});if(state){setDocument(state);$('#quest-add-dialog').close();toast('Block added. Check its links.')}});
 $('#quest-open').onclick=()=>guarded(async()=>{await flush();setDocument(await api('quest-open',{}))});
 for(const mode of ['new','template'])$('#quest-'+mode).onclick=()=>guarded(async()=>{await flush();createMode=mode;$('#quest-create-title').textContent=mode==='new'?'New quest':'Create from an existing quest';$('#quest-create-note').textContent=mode==='new'?'Start with a draft and add its conditions, actions and end.':'A .t script picker will open. It keeps the blocks and only changes the identity to a new ID; check texts, references and rewards.';$('#quest-create-dialog').showModal()});
 $('#quest-create-confirm').onclick=()=>guarded(async()=>{const raw=$('#quest-create-id').value;if(!/^\d+$/.test(raw)||Number(raw)<1||Number(raw)>65534)throw Error('The quest ID must be an integer between 1 and 65534.');const state=await api(createMode==='new'?'quest-new':'quest-clone-template',{id:Number(raw)});if(state){setDocument(state);$('#quest-create-dialog').close();toast('Quest draft prepared.')}});
 for(const action of ['undo','redo'])$('#quest-'+action).onclick=()=>guarded(async()=>{await flush();setDocument(await api('quest-'+action,{}))});
 $('#quest-save').onclick=()=>guarded(async()=>{await flush();if(!doc?.canMutate||(doc.diagnostics||[]).some(d=>d.severity==='error'))throw Error('Fix the errors shown before exporting.');const result=await api('quest-save',{});if(result){$('#quest-output').textContent=JSON.stringify(result,null,2);$('#quest-saved-dialog').showModal();toast('Revision exported with original and audit.')}});
 $('#quest-source').onclick=()=>guarded(async()=>{await flush();const result=await api('quest-source',{});if(result){$('#quest-original').textContent=result.original;$('#quest-current').textContent=result.current;$('#quest-source-dialog').showModal()}});
 $('#quest-search').oninput=renderTree;
 for(const [button,dialog] of [['create-cancel','create'],['add-cancel','add'],['source-close','source'],['saved-close','saved']])$('#quest-'+button).onclick=()=>{if(!busy)$('#quest-'+dialog+'-dialog').close()};
 for(const dialog of host.querySelectorAll('dialog'))dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault()});
 buttons();
 return {async show(){if(busy)return;await flush();setDocument(await api('quest-state',{}))},hide(){for(const d of host.querySelectorAll('dialog'))if(d.open)d.close()},flush};
}
