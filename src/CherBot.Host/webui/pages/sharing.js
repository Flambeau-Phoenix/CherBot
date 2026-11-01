import { el, fill, guard, input, field, card, pageHead, copy, toast } from '../lib/dom.js';
import { get, post, invalidate } from '../lib/api.js';

export async function render(root) {
  const [a,s,g] = await Promise.all([get('/v1/actions'),get('/v1/packs/settings'),get('/v1/globals')]);
  const name=input('My stream features'), author=input(''), version=input('1.0.0'), description=input('');
  const choices = (values,label) => values.map(v=>({value:v,box:el('input',{type:'checkbox','aria-label':label(v)})}));
  const actions=choices(a.actions,a=>a.name), settings=choices(s.settings,s=>s), globals=choices(g.globals,g=>g.name);
  const checks = (items,label) => el('div',{class:'stack'},items.map(x=>el('label',{class:'inline'},x.box,label(x.value))));
  const output=el('textarea',{class:'code',rows:8,'aria-label':'Exported feature pack',readonly:true});
  const source=el('textarea',{class:'code',rows:10,'aria-label':'Feature pack to import',placeholder:'Paste a CherBot feature pack JSON file here.'});
  const file=el('input',{type:'file',accept:'.json,application/json','aria-label':'Choose feature pack'});
  const replace=el('input',{type:'checkbox'});
  const mappings=el('textarea',{class:'code',rows:4,'aria-label':'Dependency mappings'},'{}');
  const report=el('div'); const mappingForm=el('div',{class:'stack'});
  let mappingValues={}, lastPreview='', currentPackId=null;
  const payload=()=>({pack:JSON.parse(source.value),replace:replace.checked,mappings:JSON.parse(mappings.value)});
  const invalidatePreview=()=>{apply.disabled=true;lastPreview='';};
  [source,mappings,replace].forEach(control=>control.addEventListener('input',invalidatePreview));
  file.addEventListener('change',guard(async()=>{if(file.files[0]){source.value=await file.files[0].text();mappingValues={};invalidatePreview();}}));
  const draw = r => {
    fill(report,...[['Changes',r.changes],['Conflicts (kept unless replacement is selected)',r.conflicts],['Resolve dependencies',r.missing],['Review',r.review],['Errors',r.errors]].map(([title,items])=>items.length ? el('section',{},el('h4',{},title),el('ul',{},items.map(t=>el('li',{},t)))):null),r.alreadyInstalled ? el('p',{class:'ok-text'},'This exact pack is already installed; applying again does nothing.'):null);
    fill(mappingForm,r.missing.filter(m=>m.startsWith('Map ')).map(m=>{
      const key=m.slice(4);const control=input(mappingValues[key]||'',{'aria-label':key});
      control.addEventListener('input',()=>{mappingValues[key]=control.value;mappings.value=JSON.stringify(mappingValues,null,2);invalidatePreview();});
      return field(key,control,'Enter the target engine’s exact ID/name/path. Re-enter the same value to explicitly accept it.');
    }));
  };
  const preview=guard(async()=>{const body=payload();const r=await post('/v1/packs/preview',body);draw(r);lastPreview=JSON.stringify(body);apply.disabled=!r.canApply;});
  const apply=el('button',{class:'primary',disabled:true,onclick:guard(async()=>{
    const body=payload();if(JSON.stringify(body)!==lastPreview)throw new Error('Preview the changed pack before applying.');
    await post('/v1/packs/apply',body);invalidate('');toast('Pack applied. Enable imported actions, commands and timers when ready.');invalidatePreview();
  })},'Apply reviewed pack');
  const download = () => {const url=URL.createObjectURL(new Blob([output.value],{type:'application/json'}));el('a',{href:url,download:'cherbot-feature-pack.json'}).click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
  fill(root,pageHead('Sharing','Share selected workflows and settings as portable feature packs.'),
    card('Export features',null,el('div',{class:'grid-2'},field('Pack name',name),field('Author',author),field('Pack version',version),field('Description',description)),
      el('details',{},el('summary',{},'Actions (referenced commands, timers, queues and custom definitions are included)'),checks(actions,a=>a.name)),
      el('details',{},el('summary',{},'Module settings'),checks(settings,s=>s)),
      el('details',{},el('summary',{},'Explicitly selected globals — review values before sharing'),checks(globals,g=>g.name)),
      el('p',{class:'muted small'},'Account secrets, engine tokens, viewer records and balances are excluded. Scripts, response text and selected globals may contain private values you entered; review them before sharing.'),
      el('div',{class:'row wrap'},el('button',{onclick:guard(async()=>{
        const pack=await post('/v1/packs/export',{actions:actions.filter(x=>x.box.checked).map(x=>x.value.id),settings:settings.filter(x=>x.box.checked).map(x=>x.value),globals:globals.filter(x=>x.box.checked).map(x=>x.value.name),name:name.value,author:author.value,description:description.value,version:version.value,id:currentPackId});
        currentPackId=pack.id;output.value=JSON.stringify(pack,null,2);
      })},'Build export'),el('button',{onclick:()=>copy(output.value)},'Copy pack'),el('button',{onclick:()=>{if(output.value)download();}},'Download JSON')),output),
    card('Import features',null,file,source,el('label',{class:'inline'},replace,'Replace conflicting existing items'),
      el('p',{class:'muted small'},'Imported actions, commands and timers start disabled. Select replacement only after reviewing conflicts and code. Enable them from Actions and Commands after applying.'),
      mappingForm,el('details',{},el('summary',{},'Dependency mappings JSON'),mappings),
      el('div',{class:'row'},el('button',{onclick:preview},'Preview pack'),apply),report),
    el('p',{class:'muted'},'Existing Streamer.bot exports remain available on Actions → Import/Export.'));
}
