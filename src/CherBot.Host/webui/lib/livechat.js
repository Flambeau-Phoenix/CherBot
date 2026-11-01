import { el, fill, guard, input, card, fmtTime } from './dom.js';
import { get, onEvent } from './api.js';

export async function liveChat() {
  const feed = el('div', { class: 'live-chat', role: 'log', 'aria-label': 'Live chat', 'aria-live': 'off' });
  const search = input('', { type: 'search', placeholder: 'Search chat or @mentions', 'aria-label': 'Search live chat' });
  const winner = el('p', { class: 'muted' });
  const open = el('a', { target: '_blank', rel: 'noopener noreferrer', class: 'hidden' }, 'Open Twitch Chat ↗');
  let messages = [], paused = false, disposed = false;
  const draw = () => {
    const q = search.value.toLowerCase();
    fill(feed, messages.filter(m => `${m.userName} ${m.text}`.toLowerCase().includes(q)).slice(-100).map(m =>
      el('div', { class: `chat-line${m.filtered ? ' filtered' : ''}` },
        el('time', { class: 'muted small', datetime: m.timestamp }, fmtTime(m.timestamp)), ' ',
        el('a', { href: `#/users/${encodeURIComponent(m.userId)}` }, m.userName || m.login), ': ',
        ...String(m.text).split(/(@[a-zA-Z0-9_]+)/g).map(t => t.startsWith('@') ? el('mark', {}, t) : t),
        m.filtered ? el('span', { class: 'badge' }, 'Filtered') : null)));
    if (!paused) feed.scrollTop = feed.scrollHeight;
  };
  search.addEventListener('input', draw);
  const pause = el('button', { 'aria-pressed': 'false', onclick: () => { paused = !paused; pause.textContent = paused ? 'Resume feed' : 'Pause feed'; pause.setAttribute('aria-pressed', String(paused)); if (!paused) draw(); } }, 'Pause feed');
  const load = guard(async () => {
    const data = await get('/v1/live');
    if (disposed) return;
    const known = new Map([...data.messages, ...messages].map(m => [m.id, m]));
    messages = [...known.values()].sort((a,b) => a.timestamp.localeCompare(b.timestamp)).slice(-500);
    const s = data.session;
    winner.textContent = s?.live ? `Live since ${new Date(s.startedAt).toLocaleString()} · Spotlight: ${s.winnerName || 'Waiting for !first'}` : 'Stream offline · !first opens with the next stream';
    if (data.reconciliationError) winner.textContent += ' · Stream status needs reconnection';
    if (!paused) draw();
  });
  const off = onEvent(ev => {
    if (ev.source === 'Twitch' && ev.type === 'ChatMessage') {
      const d = ev.data;
      if (messages.some(m => m.id === d.msgId)) return;
      messages.push({ id: d.msgId || `event-${ev.seq}`, userId: d.userId, userName: d.user, login: d.userName, text: d.message, timestamp: ev.ts, filtered: d.filtered });
      messages = messages.slice(-500); if (!paused) draw();
    }
    if (['StreamOnline', 'StreamOffline'].includes(ev.type)) { messages = []; load(); }
  });
  await load();
  get('/v1/twitch/channel').then(channel => {
    if (disposed) return;
    const login = channel.login || channel.broadcasterLogin;
    if (login) { open.href = `https://www.twitch.tv/popout/${encodeURIComponent(login)}/chat?popout=`; open.classList.remove('hidden'); }
  }).catch(() => {});
  const timer = setInterval(load, 15000);
  return { node: card('Live chat', [pause, open], winner, search, feed,
    el('p', { class: 'muted small' }, 'Recent chat activity only. ', el('a', { href: '#/users' }, 'Browse users →'))),
    dispose() { disposed = true; off(); clearInterval(timer); } };
}
