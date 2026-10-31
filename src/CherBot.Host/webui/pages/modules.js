// Modules: the on/off overview, plus editors for Chat Alerts, Emote Pyramid and Spam Filter.

import { el, fill, guard, toast, card, pageHead, toggle, go, fmtDate, debounce, select } from '../lib/dom.js';
import { get, post, put, simulate, lists } from '../lib/api.js';
import { formGrid, formField, messageEditor } from '../lib/forms.js';

const ALERT_TOKENS = [
  ['user', 'Who did it'], ['amount', 'Bits, gifts, viewers or months'], ['months', 'Total sub months'], ['streak', 'Streak months'], ['tier', 'Sub tier'],
  ['viewers', 'Raiders / viewers'], ['gifts', 'Gifted subs'], ['bits', 'Bits'], ['recipient', 'Gift recipient'], ['reward', 'Reward title'],
  ['cost', 'Reward cost'], ['input', 'Reward text'], ['message', 'Their message'], ['length', 'Ad length (s)'], ['total', 'Gifter\'s total'],
].map(([name, desc]) => ({ name, desc }));
const ALERT_EVENT_IDS = { follow: 101, sub: 103, resub: 104, giftSub: 105, giftBomb: 106, cheer: 102, raid: 107, firstWords: 120, reward: 112, streamOnline: 154, streamOffline: 155, shoutoutReceived: 156, adStarted: 139 };
const COLORS = [['primary', 'Channel colour'], ['blue', 'Blue'], ['green', 'Green'], ['orange', 'Orange'], ['purple', 'Purple']];
const ACTIONS = [['delete', 'Delete the message'], ['warn', 'Delete and warn in chat'], ['timeout', 'Time out'], ['log', 'Only block commands and points']];
const EXEMPT = [['', 'Module default'], ['subscriber', 'Subscribers and up'], ['vip', 'VIPs and up'], ['moderator', 'Moderators'], ['none', 'Nobody (except you and the bot)']];
const FILTERS = [
  ['links', 'Links', [{ key: 'allowedDomains', label: 'Allowed domains (one per line)', type: 'chips', hint: 'Subdomains are allowed too' }, { key: 'permitSeconds', label: '!permit lasts (s)', type: 'number', min: 5 }]],
  ['caps', 'Caps', [{ key: 'minLength', label: 'Only messages with at least N letters', type: 'number', min: 1 }, { key: 'maxPercent', label: 'Max % capitals', type: 'number', min: 1, max: 100 }]],
  ['symbols', 'Symbols', [{ key: 'minLength', label: 'Only messages with at least N characters', type: 'number', min: 1 }, { key: 'maxPercent', label: 'Max % symbols', type: 'number', min: 1, max: 100 }]],
  ['repeats', 'Repeated characters', [{ key: 'maxRepeat', label: 'Most identical characters in a row', type: 'number', min: 2 }]],
  ['length', 'Message length', [{ key: 'maxLength', label: 'Max characters', type: 'number', min: 20 }]],
  ['emotes', 'Emote spam', [{ key: 'maxEmotes', label: 'Max emotes per message', type: 'number', min: 1 }]],
  ['words', 'Banned words', []],
  ['duplicates', 'Duplicate messages', [{ key: 'windowSeconds', label: 'Same message within (s)', type: 'number', min: 1 }]],
  ['zalgo', 'Zalgo text', [{ key: 'maxCombining', label: 'Max combining marks', type: 'number', min: 1 }]],
];

export async function render(root, route) {
  const grid = el('div', { class: 'grid-cards' });
  const editorNode = el('div');
  const openId = route.rest[0];

  const loadGrid = guard(async () => {
    const r = await get('/v1/modules');
    fill(grid, r.modules.map(m => el('div', { class: `mod-card${m.enabled ? ' on' : ''}` },
      el('h3', {}, m.name, toggle(m.enabled, async (on) => { await post(`/v1/modules/${m.id}/enabled`, { enabled: on }); toast(`${m.name} ${on ? 'on' : 'off'}`); loadGrid(); }, `Enable ${m.name}`)),
      el('p', { class: 'muted small' }, m.description),
      el('p', { class: 'small' }, m.summary),
      el('div', { class: 'row' }, el('button', { class: 'small', onclick: () => go(m.page || `modules/${m.id}`) }, m.page ? 'Open page' : 'Edit')))));
  });

  fill(root, pageHead('Modules', 'Ready-made features you switch on and tune. Each card shows whether it is on and what it is doing.'), grid, editorNode);
  await loadGrid();

  const editors = { 'chat-alerts': chatAlerts, 'emote-pyramid': pyramid, 'spam-filter': spamFilter };
  if (openId && editors[openId]) {
    await editors[openId](editorNode, loadGrid);
    editorNode.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
}

function saveButton(id, settings, onSaved) {
  const btn = el('button', { onclick: guard(async () => { await put(`/v1/modules/${id}/settings`, settings); btn.classList.remove('primary'); toast('Saved'); onSaved?.(); }) }, 'Save');
  return { btn, mark: () => btn.classList.add('primary') };
}

// ── chat alerts ─────────────────────────────────────────────────────────────
async function chatAlerts(node, reloadGrid) {
  const [settings, kinds] = await Promise.all([get('/v1/modules/chat-alerts/settings'), get('/v1/modules/chat-alerts/kinds')]);
  const { btn, mark } = saveButton('chat-alerts', settings, reloadGrid);
  const rewards = await lists.rewards().catch(() => []);
  const rows = kinds.kinds.map(k => {
    const a = settings.alerts[k.key] ||= { ...kinds.defaults[k.key] };
    const out = el('div', { class: 'preview hidden' });
    return el('details', { class: 'section' },
      el('summary', {}, toggle(a.enabled, (on) => { a.enabled = on; mark(); }, `Enable ${k.label}`), el('strong', {}, k.label),
        el('span', { class: 'muted small' }, (a.messages || [])[0] || '(no message)')),
      el('div', { class: 'body stack' },
        messageEditor(a, 'messages', { label: 'Messages (one per line; one is picked at random)', multi: true, tokens: ALERT_TOKENS, sample: { ...k.sample, amount: k.sample[k.amountArg] }, defaultValue: kinds.defaults[k.key]?.messages, onChange: mark, eventTypes: [ALERT_EVENT_IDS[k.key]].filter(Boolean) }),
        formGrid(a, [
          k.amountArg ? { key: 'minAmount', label: `Only when ${k.amountArg} is at least`, type: 'number', min: 0, hint: '0 = always' } : null,
          { key: 'mode', label: 'Send as', type: 'select', options: [['chat', 'Chat message'], ['reply', 'Reply (when there is a message)'], ['announcement', 'Announcement']] },
          { key: 'color', label: 'Announcement colour', type: 'select', options: COLORS },
          { key: 'useBot', label: 'Use the bot account', type: 'bool' },
          { key: 'cooldownSeconds', label: 'Cooldown for this alert (s)', type: 'number', min: 0 },
          { key: 'userCooldownSeconds', label: 'Per-user cooldown (s)', type: 'number', min: 0 },
          k.key === 'reward' ? { key: 'rewardIds', label: 'Only these reward ids (one per line; empty = all)', type: 'chips', hint: rewards.length ? rewards.map(r => `${r.title}: ${r.id}`).join(' · ') : 'Link Twitch to list rewards' } : null,
        ].filter(Boolean), mark),
        el('div', { class: 'row' },
          el('button', { class: 'small', onclick: guard(async () => { const r = await post('/v1/modules/chat-alerts/test', { alert: k.key, send: false }); fill(out, r.text || '(silent)'); out.classList.remove('hidden'); }) }, 'Preview with sample data'),
          el('button', { class: 'small', onclick: guard(async () => { await put('/v1/modules/chat-alerts/settings', settings); const r = await post('/v1/modules/chat-alerts/test', { alert: k.key, send: true }); toast(`Sent: ${r.text}`); }) }, 'Save & send test to chat')),
        out));
  });
  fill(node, card('Chat alerts', [btn],
    el('p', { class: 'muted small' }, 'Thank supporters in chat. Gift subs that belong to a gift bomb are folded into the one gift-bomb message. Turn the module on with its switch above.'),
    rows));
}

// ── emote pyramid ───────────────────────────────────────────────────────────
async function pyramid(node, reloadGrid) {
  const s = await get('/v1/modules/emote-pyramid/settings');
  const { btn, mark } = saveButton('emote-pyramid', s, reloadGrid);
  const tokens = [['user', 'Builder'], ['emote', 'The emote'], ['width', 'Pyramid width'], ['breaker', 'Who broke it'], ['points', 'Points paid'], ['currency', 'Currency name']].map(([name, desc]) => ({ name, desc }));
  const emote = el('input', { value: 'Kappa', style: 'width:120px', 'aria-label': 'Test emote' });
  fill(node, card('Emote pyramids', [btn],
    formGrid(s, [
      { key: 'minWidth', label: 'Minimum width', type: 'number', min: 2 },
      { key: 'maxWidth', label: 'Maximum width (0 = none)', type: 'number', min: 0 },
      { key: 'emotesOnly', label: 'Emotes only (otherwise any repeated word)', type: 'bool' },
      { key: 'anyUserCanContinue', label: 'Anyone can continue a pyramid', type: 'bool' },
      { key: 'rewardBase', label: 'Reward (points)', type: 'number', min: 0 },
      { key: 'rewardPerWidth', label: '+ points per width level', type: 'number', min: 0 },
      { key: 'blockMode', label: 'Block mode', type: 'select', options: [['off', 'Off (celebrate pyramids)'], ['delete', 'Delete pyramid rows'], ['timeout', 'Time out builders']] },
      { key: 'blockFromWidth', label: 'Block from width', type: 'number', min: 2 },
      { key: 'blockTimeoutSeconds', label: 'Timeout length (s)', type: 'number', min: 1 },
    ], mark),
    el('div', { class: 'grid-2', style: 'margin-top:10px' },
      messageEditor(s, 'successMessage', { label: 'Success message', tokens, sample: { user: 'Builder', emote: 'Kappa', width: 3, points: 25, currency: 'points' }, onChange: mark }),
      messageEditor(s, 'brokenMessage', { label: 'Broken message (empty = silent)', tokens, sample: { user: 'Builder', emote: 'Kappa', width: 3, breaker: 'Rude' }, onChange: mark })),
    el('div', { class: 'row center', style: 'margin-top:10px' }, el('span', { class: 'small' }, 'Test with'), emote,
      el('button', { class: 'small', onclick: guard(async () => {
        await put('/v1/modules/emote-pyramid/settings', s);
        const w = Math.max(3, s.minWidth || 3);
        const rows = [...Array(w).keys()].map(i => i + 1).concat([...Array(w - 1).keys()].map(i => w - 1 - i));
        for (const n of rows) await simulate(Array(n).fill(emote.value).join(' '), 'pyramidtester', 'viewer', [emote.value]);
        toast(`Sent a ${w}-wide pyramid through the simulator${s.enabled ? '' : ' (the module is off, so nothing will happen)'}`);
      }) }, 'Run a scripted pyramid'))));
}

// ── spam filter ─────────────────────────────────────────────────────────────
async function spamFilter(node, reloadGrid) {
  const s = await get('/v1/modules/spam-filter/settings');
  const { btn, mark } = saveButton('spam-filter', s, reloadGrid);
  const ladder = el('div');
  const drawLadder = () => fill(ladder,
    s.ladder.map((step, i) => el('div', { class: 'row center', style: 'margin-bottom:4px' },
      el('span', { class: 'small', style: 'width:70px' }, `Strike ${i + 1}${i === s.ladder.length - 1 ? '+' : ''}`),
      formField(step, { key: 'action', label: '', type: 'select', options: ACTIONS.filter(([a]) => a !== 'log') }, mark),
      formField(step, { key: 'timeoutSeconds', label: '', type: 'number', min: 1 }, mark), el('span', { class: 'hint' }, 'seconds (timeouts)'),
      el('button', { class: 'small ghost danger', onclick: () => { s.ladder.splice(i, 1); mark(); drawLadder(); } }, '×'))),
    el('button', { class: 'small', onclick: () => { s.ladder.push({ action: 'timeout', timeoutSeconds: 600 }); mark(); drawLadder(); } }, '+ Add step'));
  drawLadder();

  const wordsArea = el('textarea', { rows: 5, placeholder: 'one per line: word, phrase, wild*card or /regex/' });
  wordsArea.value = s.words.words.join('\n');
  const wordsCheck = el('div', { class: 'small' });
  const validateWords = () => {
    s.words.words = wordsArea.value.split('\n').map(x => x.trim()).filter(Boolean);
    const bad = s.words.words.filter(w => w.length > 2 && w.startsWith('/') && w.endsWith('/')).filter(w => { try { new RegExp(w.slice(1, -1)); return false; } catch { return true; } });
    fill(wordsCheck, bad.length ? el('span', { class: 'warn-text' }, `Invalid regex: ${bad.join(', ')}`) : el('span', { class: 'muted' }, `${s.words.words.length} entries`));
    mark();
  };
  wordsArea.addEventListener('input', validateWords);
  validateWords();
  btn.classList.remove('primary');

  const sections = FILTERS.map(([key, label, extra]) => {
    const f = s[key];
    return el('details', { class: 'section' },
      el('summary', {}, toggle(f.enabled, (on) => { f.enabled = on; mark(); }, `Enable ${label}`), el('strong', {}, label),
        el('span', { class: 'muted small' }, `${ACTIONS.find(a => a[0] === f.action)?.[1] || f.action}`)),
      el('div', { class: 'body stack' },
        formGrid(f, [
          ...extra,
          { key: 'action', label: 'Punishment', type: 'select', options: ACTIONS },
          { key: 'timeoutSeconds', label: 'Timeout length (s)', type: 'number', min: 1 },
          { key: 'reason', label: 'Reason (shown in the warning and the timeout)', full: true },
          { key: 'exemptRole', label: 'Who is exempt', type: 'select', options: EXEMPT },
        ], mark),
        key === 'words' ? el('label', {}, 'Banned words', wordsArea, wordsCheck) : null));
  });

  // test box
  const testMsg = el('input', { placeholder: 'Type a message to test, e.g. CHECK OUT my-site.com', 'aria-label': 'Message to test' });
  const testRole = select([['viewer', 'Viewer'], ['subscriber', 'Subscriber'], ['vip', 'VIP'], ['moderator', 'Moderator']], 'viewer');
  const testEmotes = el('input', { placeholder: 'Emotes in it (optional)', style: 'width:180px' });
  const testOut = el('div', { class: 'small' });
  const runTest = guard(async () => {
    await put('/v1/modules/spam-filter/settings', s);
    btn.classList.remove('primary');
    const r = await post('/v1/modules/spam-filter/test', { message: testMsg.value, role: testRole.value, emotes: testEmotes.value.split(/[,\s]+/).filter(Boolean) });
    fill(testOut, r.blocked
      ? el('span', { class: 'warn-text' }, `Blocked by "${r.filter}" → ${ACTIONS.find(a => a[0] === r.action)?.[1] || r.action}${r.reason ? ` (${r.reason})` : ''}${s.escalation ? '; with escalation on, the ladder decides the punishment' : ''}`)
      : el('span', { class: 'ok-text' }, 'Allowed.'));
  });
  testMsg.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') runTest(); });

  // moderation log
  const logBody = el('tbody');
  const logSearch = el('input', { type: 'search', placeholder: 'Filter by user or filter…', 'aria-label': 'Filter the moderation log' });
  let log = [];
  const drawLog = () => {
    const q = logSearch.value.toLowerCase();
    fill(logBody, log.filter(l => !q || `${l.userName} ${l.filter} ${l.action}`.toLowerCase().includes(q)).map(l => el('tr', {},
      el('td', { class: 'small' }, fmtDate(l.timestamp)), el('td', {}, l.userName), el('td', {}, l.filter), el('td', { class: 'mono' }, l.excerpt),
      el('td', { class: l.success ? 'status-ok' : 'status-error', title: l.detail || '' }, l.action, l.success ? '' : ' (failed)'))));
  };
  const loadLog = guard(async () => { log = (await get('/v1/modules/spam-filter/log?limit=300')).log; drawLog(); });
  logSearch.addEventListener('input', debounce(drawLog, 200));

  fill(node,
    card('Spam filter', [btn],
      el('p', { class: 'muted small' }, 'Runs before commands, points and modules see a message. Blocked messages still appear in the Monitor feed with filtered = true. Deleting needs the moderator:manage:chat_messages scope (relink the broadcaster on Config if it is missing); the bot account is used when it is a moderator.'),
      el('h4', {}, 'Exemptions and warning'),
      formGrid(s, [
        { key: 'exemptRole', label: 'Exempt by default', type: 'select', options: EXEMPT.slice(1) },
        { key: 'permittedUsers', label: 'Always allowed users (logins, one per line)', type: 'chips' },
        { key: 'permittedGroups', label: 'Always allowed user groups', type: 'chips' },
        { key: 'warnMessage', label: 'Warning message ({user}, {reason}, {filter})' },
      ], mark),
      el('h4', {}, 'Filters'), sections,
      el('h4', {}, 'Escalation'),
      formGrid(s, [
        { key: 'escalation', label: 'Escalate repeat offenders (replaces each filter\'s own punishment)', type: 'bool', full: true },
        { key: 'strikeDecayMinutes', label: 'Strikes reset after (minutes)', type: 'number', min: 1 },
      ], mark),
      ladder),
    el('div', { class: 'grid-2' },
      card('Test a message', null, el('div', { class: 'stack' }, testMsg, el('div', { class: 'row' }, testRole, testEmotes, el('button', { class: 'primary', onclick: runTest }, 'Test')), testOut,
        el('p', { class: 'muted small' }, 'Testing saves your settings first and never punishes anyone.'))),
      card('Moderation log', [logSearch, el('button', { class: 'small ghost', onclick: loadLog }, 'Refresh')],
        el('div', { class: 'scroll' }, el('table', {}, el('thead', {}, el('tr', {}, ['Time', 'User', 'Filter', 'Message', 'Action'].map(h => el('th', {}, h)))), logBody)))));
  loadLog();
}
