// Monitor: queues, the chat simulator, the live event log and recent runs.

import { el, fill, guard, toast, fmtTime, fmtVal, card, pageHead, select, input, checkbox } from '../lib/dom.js';
import { api, get, post, onEvent, simulate } from '../lib/api.js';

export function summarize(ev) {
  const d = ev.data || {};
  if (d.eventName) return `${d.eventName}${d.user ? ` · ${d.user}` : ''}`;
  if (d.message) return `${d.user || d.userName || ''}: ${d.message}`;
  if (d.actionName) return `${d.actionName}${d.status ? ` → ${d.status}` : ''}`;
  if (d.name !== undefined && d.newValue !== undefined) return `${d.name} = ${fmtVal(d.newValue)}`;
  if (d.sceneName) return d.sceneName;
  if (d.user) return d.user;
  return Object.keys(d).slice(0, 4).join(', ');
}

export function eventItem(ev) {
  return el('li', {}, el('details', {},
    el('summary', {}, el('span', { class: 'ts' }, fmtTime(ev.ts)), el('span', { class: 'src' }, `${ev.source}.${ev.type}`), ' ',
      el('span', { class: 'muted' }, summarize(ev))),
    el('pre', {}, JSON.stringify(ev.data, null, 2))));
}

export async function render(root) {
  const queueBody = el('tbody');
  const runLog = el('ol', { class: 'log' });
  const eventLog = el('ol', { class: 'log', 'aria-live': 'off' });
  const filter = el('input', { type: 'search', placeholder: 'Filter (e.g. Twitch)', class: 'small-input', 'aria-label': 'Filter events' });
  const pause = el('input', { type: 'checkbox' });

  const loadQueues = guard(async () => {
    const r = await get('/v1/queues');
    const key = (q) => q.id === '00000000-0000-0000-0000-000000000000' ? 'default' : q.id;
    fill(queueBody, r.queues.map(q => el('tr', {},
      el('td', {}, q.name), el('td', {}, q.blocking ? 'Blocking' : 'Concurrent'),
      el('td', { class: q.paused ? 'status-break' : 'status-ok' }, q.paused ? 'Paused' : 'Running'),
      el('td', {}, String(q.pending)), el('td', {}, String(q.running)),
      el('td', { class: 'actions' },
        el('button', { class: 'small', onclick: guard(async () => { await post(`/v1/queues/${key(q)}/${q.paused ? 'resume' : 'pause'}`); loadQueues(); }) }, q.paused ? 'Resume' : 'Pause'), ' ',
        el('button', { class: 'small ghost', onclick: guard(async () => { const c = await post(`/v1/queues/${key(q)}/clear`); toast(`Cleared ${c.cleared}`); loadQueues(); }) }, 'Clear')))));
  });

  const loadRuns = guard(async () => {
    const r = await get('/v1/runs?limit=40');
    fill(runLog, r.runs.map(run => el('li', {}, el('details', {},
      el('summary', {}, el('span', { class: 'ts' }, fmtTime(run.started)),
        el('span', { class: `status-${run.status === 'ok' ? 'ok' : run.status === 'error' ? 'error' : 'break'}` }, run.status), ' ',
        el('strong', {}, run.actionName), el('span', { class: 'muted' }, ` · ${run.trigger} · ${run.ms} ms`)),
      el('pre', {}, JSON.stringify(run.detail, null, 2))))));
  });

  const simMessage = input('', { placeholder: '!points', required: true });
  const simUser = input('tester');
  const simRole = select([['viewer', 'Viewer'], ['subscriber', 'Subscriber'], ['vip', 'VIP'], ['moderator', 'Moderator'], ['broadcaster', 'Broadcaster']], 'viewer');
  const simEmotes = input('', { placeholder: 'Kappa, PogChamp' });
  const simResult = el('p', { class: 'small muted' });

  fill(root,
    pageHead('Monitor', 'Queues, simulated chat, live events and recent runs.'),
    el('div', { class: 'grid-2' },
      card('Queues', [
        el('button', { class: 'small', onclick: guard(async () => { await post('/v1/queues/all/pause'); loadQueues(); }) }, 'Pause all'),
        el('button', { class: 'small', onclick: guard(async () => { await post('/v1/queues/all/resume'); loadQueues(); }) }, 'Resume all'),
      ], el('table', {}, el('thead', {}, el('tr', {}, ['Queue', 'Mode', 'State', 'Pending', 'Running', ''].map(h => el('th', {}, h)))), queueBody)),
      card('Chat simulator', null, el('form', {
        class: 'stack', onsubmit: guard(async (ev) => {
          ev.preventDefault();
          const emotes = simEmotes.value.split(/[,\s]+/).filter(Boolean);
          const r = await simulate(simMessage.value, simUser.value, simRole.value, emotes.length ? emotes : undefined);
          fill(simResult, r.filtered ? el('span', { class: 'warn-text' }, `Blocked by the spam filter: ${r.filterReason}`) : 'Sent.');
        }),
      },
      el('label', {}, 'Message', simMessage),
      el('div', { class: 'row wrap' }, el('label', {}, 'User', simUser), el('label', {}, 'Role', simRole),
        el('label', { class: 'grow' }, 'Emotes in the message (optional)', simEmotes), el('button', { type: 'submit', class: 'primary' }, 'Send')),
      simResult,
      el('p', { class: 'muted small' }, 'Simulated messages go through the spam filter, points, commands and chat triggers, but never reach Twitch chat. Replies are sent for real when Twitch is linked.')))),
    el('div', { class: 'grid-2' },
      card('Live events', [filter, el('label', { class: 'inline' }, pause, 'Pause')], eventLog),
      card('Recent runs', [el('button', { class: 'small ghost', onclick: loadRuns }, 'Refresh')], runLog)));

  loadQueues();
  loadRuns();
  const recent = await get('/v1/events/recent?limit=60').catch(() => ({ events: [] }));
  for (const ev of recent.events) eventLog.prepend(eventItem(ev));

  const debouncedRuns = (() => { let t; return () => { clearTimeout(t); t = setTimeout(loadRuns, 500); }; })();
  const off = onEvent((ev) => {
    if (pause.checked) return;
    const f = filter.value.toLowerCase();
    const label = `${ev.source}.${ev.type}`;
    if (f && !label.toLowerCase().includes(f) && !JSON.stringify(ev.data).toLowerCase().includes(f)) return;
    eventLog.prepend(eventItem(ev));
    while (eventLog.children.length > 300) eventLog.lastChild.remove();
    if (ev.type === 'ActionCompleted') debouncedRuns();
  });
  const timer = setInterval(loadQueues, 3000);
  return { dispose() { off(); clearInterval(timer); } };
}
