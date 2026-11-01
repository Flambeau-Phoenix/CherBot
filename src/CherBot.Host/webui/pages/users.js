import { el, fill, guard, input, card, pageHead, fmtDate, openDialog, toast } from '../lib/dom.js';
import { get, put } from '../lib/api.js';

export async function render(root, route) {
  const search = input('', { type: 'search', placeholder: 'Search names or Twitch user IDs', 'aria-label': 'Search users' });
  const history = el('input', { type: 'checkbox', 'aria-label': 'Search retained history' });
  const list = el('div', { class: 'table-scroll' });
  let offset = 0, disposed = false, request = 0;
  const load = guard(async () => {
    const version = ++request;
    const data = await get(`/v1/users?search=${encodeURIComponent(search.value)}&offset=${offset}&limit=100&history=${history.checked}`);
    if (disposed || version !== request) return;
    fill(list, data.users.length ? el('table', {}, el('thead', {}, el('tr', {}, ...['User', 'Observed role', 'Last active', data.historical ? 'Retained messages' : 'Messages this session', 'Points', 'Rank'].map(h => el('th', {}, h)))),
      el('tbody', {}, data.users.map(u => el('tr', {},
        el('td', {}, el('button', { class: 'ghost', onclick: () => showUser(u.userId) }, u.userName || u.login || u.userId)),
        el('td', {}, u.role), el('td', {}, fmtDate(u.lastSeen)), el('td', {}, u.messages), el('td', {}, u.points ?? '—'), el('td', {}, u.rank || '—')))))
      : el('p', { class: 'muted' }, 'No matching recent chatters. Retained users appear only after extended history is enabled.'));
    next.disabled = data.users.length < 100; previous.disabled = offset === 0;
  });
  const showUser = guard(async id => {
    const data = await get(`/v1/users/${encodeURIComponent(id)}`);
    const balance = input(data.points, { type: 'number', 'aria-label': 'Points balance' });
    openDialog(data.user?.userName || `User ${id}`, el('div', { class: 'stack' },
      el('p', {}, `Twitch ID: ${id} · Rank: ${data.rank || 'unranked'}`),
      el('label', {}, 'Points', balance),
      el('button', { type: 'button', onclick: guard(async () => { await put(`/v1/points/twitch/${encodeURIComponent(id)}`, { points: Number(balance.value), userName: data.user?.userName || '', add: false }); toast('Points saved'); load(); }) }, 'Set points'),
      el('h3', {}, 'Recent messages'),
      el('div', { class: 'live-chat' }, data.messages.map(m => el('p', {}, el('time', { class: 'muted small' }, fmtDate(m.timestamp)), ' ', m.text))),
      el('a', { href: `#/history?userId=${encodeURIComponent(id)}`, onclick: () => document.querySelector('#dialog').close() }, data.historyEnabled ? 'Open retained history →' : 'Learn about optional history →')),
      { hideOk: true, wide: true });
  });
  const previous = el('button', { onclick: () => { offset = Math.max(0, offset - 100); load(); } }, 'Previous');
  const next = el('button', { onclick: () => { offset += 100; load(); } }, 'Next');
  let debounce;
  search.addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(() => { offset = 0; load(); }, 250); });
  history.addEventListener('change', () => { offset = 0; load(); });
  fill(root, pageHead('Users', 'Recent chatters and observed chat presence; this does not identify every video viewer.'),
    card('Find a user', null, search, el('label', { class: 'inline' }, history, 'Search retained history'), list, el('div', { class: 'row' }, previous, next)));
  await load();
  if (route.rest[0]) await showUser(route.rest[0]);
  const timer = setInterval(load, 15000);
  return { dispose() { disposed = true; clearInterval(timer); clearTimeout(debounce); } };
}
