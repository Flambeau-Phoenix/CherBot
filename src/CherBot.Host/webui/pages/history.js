import { el, fill, guard, input, select, field, card, pageHead, fmtDate, toast } from '../lib/dom.js';
import { get, put, del } from '../lib/api.js';

export async function render(root, route) {
  const data = await get('/v1/tracking/settings');
  const settings = { ...data.settings };
  const enabled = el('input', { type: 'checkbox', checked: settings.enabled });
  const archive = el('input', { type: 'checkbox', checked: settings.chatArchive });
  const days = input(settings.detailDays, { type: 'number', min: 1, max: 3650 });
  const chatDays = input(settings.chatDays, { type: 'number', min: 1, max: 3650 });
  const user = input(route.query.get('userId') || '', { placeholder: 'Twitch user ID (blank = all)', 'aria-label': 'History user ID' });
  const kind = select([['', 'All activity'], ['message','Archived chat'], ['activity','Activity'], ['command','Commands'], ['points','Points']], '');
  const since = input('', { type: 'date' }); const until = input('', { type: 'date' });
  const stream = input('', { placeholder: 'Optional Twitch stream ID' });
  const search = input('', { type: 'search', placeholder: 'Search retained text' });
  const rows = el('div', { class: 'table-scroll' }); const status = el('p', { role: 'status' });
  let offset = 0, disposed = false;
  const load = guard(async () => {
    const qs = new URLSearchParams({ offset: String(offset), limit: '100' });
    for (const [key,value] of [['userId',user.value],['kind',kind.value],['streamId',stream.value],['search',search.value],['since',since.value && `${since.value}T00:00:00.0000000+00:00`],['until',until.value && `${until.value}T23:59:59.9999999+00:00`]]) if (value) qs.set(key,value);
    const r = await get(`/v1/tracking/history?${qs}`); if (disposed) return;
    fill(rows, r.history.length ? el('table', {}, el('thead', {}, el('tr', {}, ...['When','User','Stream','Type','Detail','Points change'].map(t => el('th',{},t)))), el('tbody', {}, r.history.map(h => el('tr', {}, el('td',{},fmtDate(h.timestamp)), el('td',{},h.userName || h.userId), el('td',{},h.streamId), el('td',{},h.kind), el('td',{},h.detail), el('td',{},h.kind === 'points' ? h.amount : ''))))) : el('p', { class:'muted' }, 'No retained history matches. Earlier balances and activity are not backfilled.'));
    previous.disabled = offset === 0; next.disabled = r.history.length < 100;
  });
  const previous = el('button', { onclick: () => { offset = Math.max(0,offset-100); load(); } }, 'Previous');
  const next = el('button', { onclick: () => { offset += 100; load(); } }, 'Next');
  const download = guard(async () => {
    if (!user.value.trim()) throw new Error('Enter a Twitch user ID to export.');
    const history = [];
    for (let start=0;; start+=1000) {
      const page = await get(`/v1/tracking/export/${encodeURIComponent(user.value.trim())}?offset=${start}`);
      history.push(...page.history); if (page.history.length < 1000) break;
    }
    const blob = new Blob([JSON.stringify({ userId:user.value.trim(), exportedAt:new Date().toISOString(), history }, null,2)], {type:'application/json'});
    const url = URL.createObjectURL(blob); const link=el('a',{href:url,download:'cherbot-user-history.json'}); link.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);
  });
  fill(root, pageHead('Data & History','Optional history stays in this engine’s local SQLite database.'),
    card('Recording & retention',null,
      el('p',{class:'muted'},'Basic mode keeps existing balances and operational diagnostics. Extended statistics and archived chat are separate opt-ins.'),
      el('label',{class:'inline'},enabled,'Enable extended statistics'), el('label',{class:'inline'},archive,'Also retain searchable chat text'),
      el('div',{class:'grid-2'},field('Detail retention (days)',days),field('Chat retention (days)',chatDays)),
      el('p',{},`Database and WAL: ${(data.storageBytes/1024/1024).toFixed(2)} MB · History began: ${settings.startedAt ? fmtDate(settings.startedAt) : 'Not enabled'}`),
      el('p',{class:'muted small'},`Existing event diagnostics: ${data.diagnostics.eventDays} days; action runs: ${data.diagnostics.runDays} days. Disabling extended history stops new recording; it does not delete retained data or disable diagnostics.`),
      data.error ? el('p',{class:'error'},data.error) : null,
      el('button',{class:'primary',onclick:guard(async()=>{ await put('/v1/tracking/settings',{...settings,enabled:enabled.checked,chatArchive:archive.checked,detailDays:Number(days.value),chatDays:Number(chatDays.value)});status.textContent='Recording settings saved.';})},'Save recording settings'),status),
    card('Retained user history',null,el('div',{class:'grid-3'},field('User ID',user),field('Type',kind),field('Stream',stream),field('From',since),field('Through',until),field('Text',search)),
      el('div',{class:'row wrap'},el('button',{onclick:()=>{offset=0;load();}},'Search'),el('button',{onclick:download},'Export user history'),el('button',{class:'danger',onclick:guard(async()=>{
        const id=user.value.trim();if(!confirm(id ? `Delete retained history and structured diagnostics for user ${id}? Balances stay unchanged.` : 'Purge ALL extended history and structured diagnostics? Balances stay unchanged.'))return;
        await del(`/v1/tracking/history?diagnostics=true${id ? `&userId=${encodeURIComponent(id)}` : ''}`);toast('History deleted');load();
      })},'Delete selected history')),rows,el('div',{class:'row'},previous,next)));
  await load(); return {dispose(){disposed=true;}};
}
