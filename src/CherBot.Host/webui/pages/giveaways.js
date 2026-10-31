// Raffles & Giveaways: cards with live status and controls, a sectioned editor (basics, activation,
// entry rules, messages with previews) and a run view (entries, winners, history).

import { el, fill, guard, toast, card, pageHead, fmtDuration, fmtDate, go, debounce } from '../lib/dom.js';
import { get, api, post, put, del, onEvent, simulate, lists, invalidate } from '../lib/api.js';
import { formGrid, messageEditor } from '../lib/forms.js';

const TOKENS = [
  ['name', 'Giveaway name'], ['prize', 'Prize'], ['command', 'Entry command (or keyword)'], ['user', 'The viewer'], ['tickets', 'Their tickets'],
  ['entries', 'Total tickets'], ['entrants', 'People entered'], ['winner', 'The winner'], ['winners', 'Everyone drawn this time'], ['cost', 'Cost per ticket'],
  ['currency', 'Loyalty currency name'], ['duration', 'How long it runs'], ['remaining', 'Time left'], ['claimSeconds', 'Seconds to claim'],
].map(([name, desc]) => ({ name, desc }));

const MESSAGES = [
  ['started', 'Started'], ['entered', 'Entry confirmed'], ['alreadyEntered', 'Already entered'], ['notEligible', 'Not eligible'], ['notEnoughPoints', 'Not enough points'],
  ['reminder', 'Reminder'], ['closed', 'Entries closed'], ['winner', 'Winner'], ['winnerClaim', 'Winner (claim timer on)'], ['noEntries', 'No entries'],
  ['claimed', 'Claimed'], ['unclaimed', 'Unclaimed, drawing again'], ['cancelled', 'Cancelled'],
];

const PRESETS = [[0, 'Until stopped'], [60, '1 min'], [300, '5 min'], [600, '10 min'], [1800, '30 min']];
const STATUS_CLASS = { open: 'ok', drawn: 'accent', closed: 'warn', cancelled: 'bad', draft: '' };

const blank = () => ({
  name: 'New giveaway', prize: '', commands: ['!join'], durationSeconds: 0, autoDraw: true, winnerCount: 1, excludePreviousWinners: true,
  entryCost: 0, maxTickets: 1, subscriberBonusTickets: 0, vipBonusTickets: 0, minRole: 'everyone', followersOnly: false, keyword: '',
  claimSeconds: 0, reminderSeconds: 0, messages: null,
});

export async function render(root, route) {
  const listNode = el('div', { class: 'grid-cards' });
  const detail = el('div');
  const selected = route.rest[0] || null;
  let views = [];
  let defaults = await get('/v1/giveaways/messages/defaults').catch(() => ({}));
  const loyalty = await get('/v1/loyalty/settings').catch(() => ({ settings: { currencyName: 'points' } }));
  const currency = loyalty.settings.currencyName || 'points';

  const control = (id, op, label, cls = '') => el('button', {
    class: `small ${cls}`, onclick: guard(async () => {
      if (op === 'cancel' && !confirm('Cancel this giveaway? Paid entries are refunded.')) return;
      if (op === 'reset' && !confirm('Clear all entries and the winner history?')) return;
      const r = await api('POST', `/v1/giveaways/${id}/${op}`, op === 'start' ? {} : undefined, { allowError: true });
      toast(r.data?.message || r.data?.error || op, !r.ok);
      await load();
    }),
  }, label);

  const load = guard(async () => {
    views = (await get('/v1/giveaways')).giveaways;
    invalidate('giveaways');
    drawList();
    if (selected && selected !== 'new') drawRun();
  });

  function drawList() {
    fill(listNode, views.length ? views.map(v => {
      const g = v.giveaway;
      const ends = g.status === 'open' && v.remainingSeconds !== null ? Date.now() + v.remainingSeconds * 1000 : null;
      return el('div', { class: `mod-card${g.status === 'open' ? ' on' : ''}${g.id === selected ? ' selected' : ''}` },
        el('h3', {}, el('a', { href: `#/giveaways/${g.id}` }, g.name), el('span', { class: `chip ${STATUS_CLASS[g.status] || ''}` }, g.status)),
        el('p', { class: 'muted small' }, g.prize ? `Prize: ${g.prize}` : 'No prize set',
          ` · ${g.commands.length ? g.commands.join(', ') : g.keyword ? `keyword "${g.keyword}"` : 'no entry command'}`),
        g.status === 'open' ? el('div', { class: 'countdown', dataset: { ends: ends || '' } }, ends ? fmtDuration(v.remainingSeconds) : 'until stopped') : null,
        el('p', { class: 'small' }, `${v.entrants} entrants · ${v.entries} tickets`,
          v.winners?.length ? ` · winner${v.winners.length > 1 ? 's' : ''}: ${v.winners.map(w => `${w.userName}${w.status === 'claimed' ? ' ✓' : w.status === 'pending' ? ' (waiting to claim)' : ''}`).join(', ')}` : ''),
        el('div', { class: 'row wrap' },
          g.status === 'open' ? [control(g.id, 'stop', 'Stop'), control(g.id, 'draw', 'Draw', 'primary')] : control(g.id, 'start', g.status === 'draft' ? 'Start' : 'Start again', 'primary'),
          ['closed', 'drawn'].includes(g.status) ? [control(g.id, 'draw', 'Draw more'), control(g.id, 'reroll', 'Reroll')] : null,
          ['open', 'closed'].includes(g.status) ? control(g.id, 'cancel', 'Cancel', 'ghost danger') : null,
          el('a', { href: `#/giveaways/${g.id}`, class: 'small' }, 'Open')));
    }) : el('p', { class: 'muted', style: 'grid-column:1/-1' }, 'No giveaways yet. Create one, then start it whenever you like; definitions are kept for reuse.'));
  }

  // ── editor ──
  function editor(def, isNew) {
    def.messages ||= { ...defaults };
    const save = el('button', { class: isNew ? 'primary' : '', onclick: guard(async () => {
      const clash = await clashes(def);
      if (clash.length && !confirm(`These commands are already used: ${clash.join(', ')}. Save anyway?`)) return;
      const saved = isNew ? await post('/v1/giveaways', def) : await put(`/v1/giveaways/${def.id}`, def);
      save.classList.remove('primary');
      toast('Giveaway saved');
      invalidate('commands');
      if (isNew) go(`giveaways/${saved.giveaway.id}`); else load();
    }) }, isNew ? 'Create' : 'Save');
    const previews = [];
    const mark = () => { save.classList.add('primary'); previews.forEach(p => p.refreshPreview()); };
    const durationPreset = el('div', { class: 'seg' });
    const custom = el('input', { type: 'number', min: 0, style: 'width:110px', 'aria-label': 'Duration in seconds', value: def.durationSeconds });
    const drawPresets = () => fill(durationPreset, PRESETS.map(([s, l]) => el('button', {
      type: 'button', class: def.durationSeconds === s ? 'on' : '', onclick: () => { def.durationSeconds = s; custom.value = s; drawPresets(); mark(); },
    }, l)));
    custom.addEventListener('input', () => { def.durationSeconds = Number(custom.value) || 0; drawPresets(); mark(); });
    drawPresets();
    const sample = () => ({ name: def.name, prize: def.prize || def.name, command: def.commands[0] || def.keyword || '!join', user: 'CoolViewer', tickets: 2, entries: 34, entrants: 21,
      winner: 'LuckyViewer', winners: 'LuckyViewer', cost: def.entryCost, currency, duration: def.durationSeconds ? fmtDuration(def.durationSeconds) : 'until stopped', remaining: '2:30', claimSeconds: def.claimSeconds || 60 });

    return el('div', {},
      el('details', { class: 'section', open: true }, el('summary', {}, 'Basics'), el('div', { class: 'body' },
        formGrid(def, [
          { key: 'name', label: 'Name' },
          { key: 'prize', label: 'Prize', placeholder: 'e.g. a Steam key' },
          { key: 'commands', label: 'Entry commands (one per line)', type: 'chips', rows: 2, hint: 'Viewers type one of these to enter; "!join 3" buys 3 tickets when allowed. The command is only on while the giveaway is open.' },
          { key: 'keyword', label: 'Or a keyword', hint: 'Any chat message containing it counts as an entry' },
        ], mark))),
      el('details', { class: 'section', open: true }, el('summary', {}, 'Activation'), el('div', { class: 'body' },
        el('label', {}, 'How long entries stay open', el('div', { class: 'row center wrap' }, durationPreset, custom, el('span', { class: 'hint' }, 'seconds (0 = until you stop it)'))),
        formGrid(def, [
          { key: 'autoDraw', label: 'Draw automatically when it closes', type: 'bool' },
          { key: 'winnerCount', label: 'Winners', type: 'number', min: 1 },
          { key: 'claimSeconds', label: 'Claim timeout (s)', type: 'number', min: 0, hint: '0 = off; otherwise the winner must chat in time or a new winner is drawn' },
          { key: 'reminderSeconds', label: 'Reminder every (s)', type: 'number', min: 0, hint: '0 = off (minimum 30)' },
        ], mark))),
      el('details', { class: 'section' }, el('summary', {}, 'Entry rules'), el('div', { class: 'body' },
        formGrid(def, [
          { key: 'entryCost', label: `Cost per ticket (${currency})`, type: 'number', min: 0, hint: '0 = free; refunded if cancelled' },
          { key: 'maxTickets', label: 'Max tickets per viewer', type: 'number', min: 1, hint: '1 = plain raffle' },
          { key: 'subscriberBonusTickets', label: 'Bonus tickets for subs', type: 'number', min: 0 },
          { key: 'vipBonusTickets', label: 'Bonus tickets for VIPs', type: 'number', min: 0 },
          { key: 'minRole', label: 'Who can enter', type: 'select', options: [['everyone', 'Everyone'], ['subscriber', 'Subscribers and up'], ['vip', 'VIPs and up'], ['moderator', 'Moderators']] },
          { key: 'followersOnly', label: 'Followers only', type: 'bool', hint: 'Checked through Twitch when the follower scope is linked' },
          { key: 'excludePreviousWinners', label: 'Skip people who won this giveaway before', type: 'bool' },
        ], mark))),
      el('details', { class: 'section' }, el('summary', {}, 'Messages'), el('div', { class: 'body' },
        el('p', { class: 'muted small' }, 'Leave a message empty to stay silent. Placeholders are filled from the giveaway; %args% and ~globals~ work too.'),
        el('div', { class: 'grid-2' }, MESSAGES.map(([key, label]) => {
          const editorNode = messageEditor(def.messages, key, { label, tokens: TOKENS, sample, defaultValue: defaults[key] ?? '', onChange: () => save.classList.add('primary') });
          previews.push(editorNode);
          return editorNode;
        })))),
      el('div', { class: 'row', style: 'margin-top:10px' }, save, isNew ? el('a', { href: '#/giveaways' }, 'Cancel') : null));
  }

  async function clashes(def) {
    const cmds = await lists.commands();
    const mine = new Set(def.commandId ? [def.commandId] : []);
    const words = new Set(def.commands.map(c => c.toLowerCase()));
    return cmds.filter(c => !mine.has(c.id) && c.enabled && c.command.split('\n').some(w => words.has(w.trim().toLowerCase()))).map(c => c.name);
  }

  // ── run view (refreshed live) next to the settings (rendered once, so edits survive refreshes) ──
  let entriesSearch = '';
  const runNode = el('div');
  let settingsShown = false;
  async function drawRun() {
    const view = views.find(v => v.giveaway.id === selected);
    if (!view) { fill(detail, card('Giveaway not found', null, el('a', { href: '#/giveaways' }, 'Back to the list'))); return; }
    const g = view.giveaway;
    if (!settingsShown) {
      settingsShown = true;
      fill(detail, el('div', { class: 'grid-2' }, runNode, card('Settings', null, editor(JSON.parse(JSON.stringify(g)), false))));
    }
    const [entries, winners] = await Promise.all([get(`/v1/giveaways/${g.id}/entries`), get(`/v1/giveaways/${g.id}/winners`)]);
    const searchBox = el('input', { type: 'search', placeholder: 'Search entrants…', value: entriesSearch, 'aria-label': 'Search entrants' });
    const tbody = el('tbody');
    const drawEntries = () => fill(tbody, entries.entries.filter(e => !searchBox.value || `${e.userName} ${e.userLogin}`.toLowerCase().includes(searchBox.value.toLowerCase())).map(e => el('tr', {},
      el('td', {}, e.userName || e.userLogin), el('td', {}, String(e.tickets), e.bonus ? el('span', { class: 'muted small' }, ` +${e.bonus} bonus`) : null),
      el('td', {}, e.paid ? `${e.paid} ${currency}` : '—'), el('td', { class: 'small' }, fmtDate(e.timestamp)),
      el('td', { class: 'actions' }, el('button', { class: 'small ghost danger', onclick: guard(async () => { await del(`/v1/giveaways/${g.id}/entries/${e.platform}/${encodeURIComponent(e.userId)}`); load(); }) }, 'Remove')))));
    searchBox.addEventListener('input', debounce(() => { entriesSearch = searchBox.value; drawEntries(); }, 200));
    drawEntries();
    const runs = new Map();
    for (const w of winners.winners) { if (!runs.has(w.run)) runs.set(w.run, []); runs.get(w.run).push(w); }
    const simName = el('input', { value: 'tester', style: 'width:120px', 'aria-label': 'Test user name' });
    const simRole = el('select', { 'aria-label': 'Test user role' }, ['viewer', 'subscriber', 'vip', 'moderator'].map(r => el('option', { value: r }, r)));

    fill(runNode,
        card(`Run: ${g.name}`, [el('span', { class: `chip ${STATUS_CLASS[g.status] || ''}` }, g.status), el('a', { href: '#/giveaways', class: 'small' }, 'All giveaways')],
          el('div', { class: 'row wrap center' },
            g.status === 'open' ? [control(g.id, 'stop', 'Stop'), control(g.id, 'draw', 'Draw', 'primary')] : control(g.id, 'start', 'Start', 'primary'),
            control(g.id, 'reroll', 'Reroll'), control(g.id, 'cancel', 'Cancel', 'ghost danger'), control(g.id, 'reset', 'Reset', 'ghost'),
            el('button', { class: 'small ghost danger', onclick: guard(async () => { if (!confirm(`Delete "${g.name}" and its command?`)) return; await del(`/v1/giveaways/${g.id}`); invalidate('commands'); go('giveaways'); }) }, 'Delete')),
          el('div', { class: 'row wrap center', style: 'margin-top:10px' },
            el('span', { class: 'small' }, 'Simulate an entry as'), simName, simRole,
            el('button', { class: 'small', onclick: guard(async () => {
              const text = g.commands[0] || g.keyword;
              if (!text) { toast('This giveaway has no entry command or keyword', true); return; }
              if (g.status !== 'open') { toast('Start the giveaway first', true); return; }
              if (g.entryCost > 0) await api('PUT', `/v1/points/twitch/sim-${simName.value.toLowerCase()}`, { points: g.entryCost * Math.max(1, g.maxTickets), userName: simName.value, add: true });
              await simulate(text, simName.value, simRole.value);
              setTimeout(load, 600);
            }) }, 'Send entry')),
          el('h4', {}, `Entries (${view.entrants} people, ${view.entries} tickets)`), searchBox,
          el('div', { class: 'scroll' }, el('table', {}, el('thead', {}, el('tr', {}, ['Viewer', 'Tickets', 'Paid', 'Entered', ''].map(h => el('th', {}, h)))), tbody)),
          el('h4', {}, 'Winners'),
          runs.size ? [...runs.entries()].sort(([a], [b]) => b - a).map(([run, list]) => el('div', {},
            el('div', { class: 'muted small' }, run === g.run ? `This run (#${run})` : `Run #${run}`),
            el('ul', { class: 'plain' }, list.map(w => el('li', {}, `${w.userName} — ${w.status}${w.claimedAt ? ` at ${fmtDate(w.claimedAt)}` : ''} `,
              ['pending', 'won'].includes(w.status) ? el('button', { class: 'small ghost', onclick: guard(async () => { await post(`/v1/giveaways/${g.id}/winners/${w.id}/claim`); load(); }) }, 'Mark claimed') : null)))))
            : el('p', { class: 'muted' }, 'No winners yet.')));
  }

  fill(root,
    pageHead('Raffles & Giveaways', 'Prepare giveaways once and run them whenever you like. Each one gets its own entry command, only switched on while it is open.',
      el('button', { class: 'primary', onclick: () => go('giveaways/new') }, 'New giveaway'), el('a', { href: '#/help/giveaways' }, 'How it works')),
    listNode, detail);

  await load();
  if (selected === 'new') fill(detail, card('New giveaway', null, editor(blank(), true)));

  const off = onEvent((ev) => {
    if (ev.source === 'Custom' && String(ev.data?.eventName || '').startsWith('giveaway.')) load();
  });
  const tick = setInterval(() => {
    for (const c of listNode.querySelectorAll('.countdown[data-ends]')) {
      const ends = Number(c.dataset.ends);
      if (ends) c.textContent = fmtDuration((ends - Date.now()) / 1000);
    }
  }, 1000);
  return { dispose() { off(); clearInterval(tick); } };
}
