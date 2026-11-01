// Dashboard: live status, OBS scene and source controls, pinned actions, quick chat, stream info,
// one-click tools, the active giveaway and a mini event feed.

import { el, fill, guard, toast, card, pageHead, fmtDuration, debounce, uuid, go, icon } from '../lib/dom.js';
import { get, post, api, onEvent, getUiSettings, lists, invalidate } from '../lib/api.js';
import { eventItem } from './monitor.js';
import { showRunResult } from '../builder/editor.js';
import { liveChat } from '../lib/livechat.js';

export async function render(root) {
  const chat = await liveChat();
  const strip = el('div', { class: 'strip' });
  const setup = el('section', { class: 'card hidden', 'aria-label': 'Getting started' });
  const scenesNode = el('div', { class: 'btn-grid' });
  const sourcesNode = el('div');
  const pinsNode = el('div', { class: 'btn-grid' });
  const giveawayNode = el('div');
  const feed = el('ol', { class: 'log', style: 'max-height:300px' });
  let currentScene = null;
  let channel = null;

  const statIcons = { Twitch: 'device-tv', Stream: 'broadcast', Uptime: 'clock', OBS: 'device-tv', Queues: 'list-details', 'Pending runs': 'player-play' };
  const stat = (k, v, cls = '') => el('div', { class: 'stat' }, el('div', { class: 'k' }, icon(statIcons[k]), k), el('div', { class: `v ${cls}` }, v));

  const loadStrip = guard(async () => {
    const [health, queues] = await Promise.all([fetch('/health').then(r => r.json()), get('/v1/queues')]);
    const tw = health.integrations.find(i => i.name === 'twitch');
    const obs = health.integrations.find(i => i.name === 'obs');
    const ready = !!tw?.connected;
    root.querySelectorAll('[data-needs-twitch]').forEach(control => {
      control.disabled = !ready;
      control.title = ready ? '' : 'Connect Twitch on Config to use this control.';
    });
    setup.classList.toggle('hidden', ready && !!obs?.connected);
    fill(setup, el('div', { class: 'card-head' }, el('h3', {}, 'Get your stream connected'), el('a', { href: '#/help/getting-started' }, 'Setup guide →')),
      el('ol', { class: 'setup-checklist' },
        ...[
          [ready, '1. Connect Twitch', '#/config', 'Set your Twitch app client ID in cherbot.json, restart, then link your accounts on Config.'],
          [!!obs?.connected, '2. Add OBS', '#/config', 'Enable OBS WebSocket, then add the streaming PC’s host, port and password.'],
          [false, '3. Create a command', '#/quicksetup', 'Use a Quick Setup wizard or write a text reply on Commands.'],
          [false, '4. Check the result', '#/monitor', 'Use Monitor to inspect events and runs. Simulated commands can send real replies.'],
        ].map(([done, title, href, description]) => el('li', {}, el('a', { href }, `${done ? '✓ ' : ''}${title}`), el('p', { class: 'muted small' }, description)))));
    channel = await get('/v1/twitch/channel').catch(() => null);
    const paused = queues.queues.filter(q => q.paused).length;
    const pending = queues.queues.reduce((n, q) => n + q.pending, 0);
    fill(strip,
      stat('Twitch', tw?.connected ? 'Connected' : (tw?.state || 'off'), tw?.connected ? 'ok-text' : 'warn-text'),
      stat('Stream', channel ? (channel.live ? `LIVE · ${channel.viewers} viewers` : 'Offline') : '—', channel?.live ? 'ok-text' : ''),
      stat('Uptime', channel?.live && channel.startedAt ? fmtDuration((Date.now() - new Date(channel.startedAt).getTime()) / 1000) : '—'),
      stat('OBS', obs?.connected ? (currentScene || 'Connected') : (obs?.state || 'off'), obs?.connected ? 'ok-text' : 'warn-text'),
      stat('Queues', paused ? `${paused} paused` : 'Running', paused ? 'warn-text' : 'ok-text'),
      stat('Pending runs', String(pending)));
    drawChannel();
  });

  const loadScenes = guard(async () => {
    let r;
    try { r = await get('/v1/obs/scenes'); } catch {
      fill(scenesNode, el('p', { class: 'muted' }, 'OBS isn\'t connected. ', el('a', { href: '#/config' }, 'Add a connection →')));
      fill(sourcesNode);
      return;
    }
    currentScene = r.current;
    fill(scenesNode, r.scenes.map(s => el('button', {
      class: `scene-btn${s === currentScene ? ' current' : ''}`,
      onclick: guard(async () => { await post('/v1/obs/scene', { sceneName: s }); currentScene = s; loadScenes(); }),
    }, s)));
    loadSources();
  });

  const loadSources = guard(async () => {
    if (!currentScene) return;
    const r = await get(`/v1/obs/scene-items?scene=${encodeURIComponent(currentScene)}`).catch(() => ({ items: [] }));
    fill(sourcesNode, el('h4', {}, `Sources in ${currentScene}`), r.items.length ? r.items.map(i => {
      const box = el('input', { type: 'checkbox', class: 'switch', 'aria-label': `Show ${i.name}` });
      box.checked = i.enabled;
      box.addEventListener('change', guard(async () => { await post('/v1/obs/scene-item', { sceneName: currentScene, sceneItemId: i.id, enabled: box.checked }); }));
      return el('div', { class: 'source-row' }, box, el('span', {}, i.name), i.isGroup ? el('span', { class: 'chip' }, 'group') : null);
    }) : el('p', { class: 'muted' }, 'No sources.'));
  });

  const loadPins = guard(async () => {
    const pins = (await getUiSettings()).pinned || [];
    const actions = await lists.actions();
    const pinned = pins.map(id => actions.find(a => a.id === id)).filter(Boolean);
    fill(pinsNode, pinned.length ? pinned.map(a => el('button', {
      title: `Run ${a.name}`, onclick: guard(async () => { const r = await post(`/v1/actions/${a.id}/run`, { args: {}, wait: true }); toast(`${a.name}: ${r.status}`, r.status === 'error'); if (r.status === 'error') showRunResult(r); }),
    }, a.name)) : el('p', { class: 'muted' }, 'Pin actions with the ☆ on the Actions page to get one-click buttons here.'));
  });

  const loadGiveaway = guard(async () => {
    const r = await get('/v1/giveaways').catch(() => ({ giveaways: [] }));
    const open = r.giveaways.find(g => g.giveaway.status === 'open');
    if (!open) {
      fill(giveawayNode, el('p', { class: 'muted' }, 'No giveaway is open. ', el('a', { href: '#/giveaways' }, 'Giveaways →')));
      return;
    }
    const g = open.giveaway;
    const ends = open.remainingSeconds === null ? null : Date.now() + open.remainingSeconds * 1000;
    const clock = el('div', { class: 'countdown' }, ends ? fmtDuration(open.remainingSeconds) : 'until stopped');
    giveawayNode.dataset.ends = ends || '';
    fill(giveawayNode,
      el('div', { class: 'row between center' }, el('strong', {}, g.name), clock),
      el('p', { class: 'muted small' }, `${open.entrants} entrants · ${open.entries} tickets · prize: ${g.prize || '—'}`),
      el('div', { class: 'row' },
        el('button', { onclick: guard(async () => { await api('POST', `/v1/giveaways/${g.id}/stop`); loadGiveaway(); }) }, 'Stop'),
        el('button', { class: 'primary', onclick: guard(async () => { const x = await api('POST', `/v1/giveaways/${g.id}/draw`, undefined, { allowError: true }); toast(x.data?.message || 'Drawn', !x.ok); loadGiveaway(); }) }, 'Draw'),
        el('a', { href: `#/giveaways/${g.id}` }, 'Open')));
  });

  // quick chat
  const chatText = el('textarea', { rows: 2, placeholder: 'Say something in chat…', 'aria-label': 'Chat message', 'data-needs-twitch': '', disabled: true });
  const asBot = el('select', { 'aria-label': 'Send as', 'data-needs-twitch': '', disabled: true }, el('option', { value: 'bot' }, 'as the bot'), el('option', { value: 'broadcaster' }, 'as the broadcaster'));
  const sendChat = guard(async () => {
    if (!chatText.value.trim()) return;
    const r = await post('/v1/twitch/chat', { message: chatText.value, useBot: asBot.value === 'bot' });
    if (r.sent) { chatText.value = ''; toast('Sent'); } else toast(`Not sent: ${r.error}`, true);
  });
  chatText.addEventListener('keydown', (ev) => { if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); sendChat(); } });

  // stream info
  const titleInput = el('input', { 'aria-label': 'Stream title', 'data-needs-twitch': '', disabled: true });
  const categoryList = `dl-${uuid()}`;
  const categoryInput = el('input', { list: categoryList, placeholder: 'Search categories…', 'aria-label': 'Category', 'data-needs-twitch': '', disabled: true });
  const categoryOptions = el('datalist', { id: categoryList });
  let categories = [];
  let channelDirty = false;
  titleInput.addEventListener('input', () => { channelDirty = true; });
  categoryInput.addEventListener('input', () => { channelDirty = true; });
  const searchCategories = debounce(async () => {
    if (categoryInput.value.length < 2) return;
    try {
      categories = (await get(`/v1/twitch/categories?query=${encodeURIComponent(categoryInput.value)}`)).categories;
      fill(categoryOptions, categories.map(c => el('option', { value: c.name })));
    } catch { /* not linked */ }
  }, 350);
  categoryInput.addEventListener('input', searchCategories);
  function drawChannel() {
    if (!channel || channelDirty) return;
    if (document.activeElement !== titleInput) titleInput.value = channel.title || '';
    if (document.activeElement !== categoryInput) categoryInput.value = channel.gameName || '';
  }
  const saveChannel = guard(async () => {
    const body = { title: titleInput.value };
    const cat = categories.find(c => c.name.toLowerCase() === categoryInput.value.toLowerCase());
    if (cat) body.gameId = cat.id;
    else if (categoryInput.value && categoryInput.value !== channel?.gameName) { toast('Pick the category from the suggestions', true); return; }
    await api('PATCH', '/v1/twitch/channel', body);
    channelDirty = false;
    toast('Stream info updated');
    loadStrip();
  });

  const tool = (label, fn, cls = '') => el('button', {
    class: cls, onclick: guard(fn),
    ...(/^(Stream marker|Clip|Ad |Shoutout)/.test(label) ? { 'data-needs-twitch': '', disabled: true } : {}),
  }, label);
  const tools = el('div', { class: 'btn-grid' },
    tool('Stream marker', async () => { const r = await post('/v1/twitch/marker', { description: 'CherBot marker' }); toast(`Marker at ${r.position ?? '?'}s`); }),
    tool('Clip', async () => { const r = await post('/v1/twitch/clip'); toast(r.url ? `Clip: ${r.url}` : 'Clip requested'); if (r.editUrl) window.open(r.editUrl, '_blank', 'noopener'); }),
    ...[30, 60, 90].map(n => tool(`Ad ${n}s`, async () => { if (confirm(`Run a ${n} second ad now?`)) { await post('/v1/twitch/commercial', { length: n }); toast('Ad started'); } })),
    tool('Shoutout…', async () => { const login = prompt('Shout out which channel? (login name)'); if (login) { await post('/v1/twitch/shoutout', { login: login.replace(/^@/, '') }); toast(`Shouted out ${login}`); } }),
    tool('Pause queues', async () => { await post('/v1/queues/all/pause'); toast('All queues paused'); loadStrip(); }),
    tool('Resume queues', async () => { await post('/v1/queues/all/resume'); toast('All queues resumed'); loadStrip(); }),
    tool('Reset first words', async () => { if (confirm('Reset the first-words list for a new stream?')) { await api('DELETE', '/v1/first-words'); toast('First words reset'); } }),
    tool('Reconnect Twitch', async () => { await post('/v1/twitch/reconnect'); toast('Reconnecting Twitch'); setTimeout(loadStrip, 2500); }),
    tool('Reconnect OBS', async () => { await post('/v1/obs/reconnect'); invalidate('obs'); toast('Reconnecting OBS'); setTimeout(() => { loadScenes(); loadStrip(); }, 2500); }));

  fill(root,
    pageHead('Live Admin', 'Live controls for your stream.', el('a', { href: '#/users' }, 'Users →'), el('a', { href: '#/monitor' }, 'Monitor →')),
    strip,
    setup,
    chat.node,
    el('div', { class: 'grid-2' },
      card('OBS scenes', [el('button', { class: 'small ghost', onclick: () => { invalidate('obs'); loadScenes(); } }, 'Refresh')], scenesNode, sourcesNode),
      el('div', {},
        card('Pinned actions', [el('button', { class: 'small ghost', onclick: () => go('actions') }, 'Manage')], pinsNode),
        card('Active giveaway', null, giveawayNode))),
    el('div', { class: 'grid-2' },
      card('Quick chat', null, el('p', { class: 'muted small' }, 'Requires a Twitch connection. ', el('a', { href: '#/config' }, 'Manage accounts →')),
        el('div', { class: 'stack' }, chatText, el('div', { class: 'row' }, asBot, el('button', { class: 'primary', onclick: sendChat, 'data-needs-twitch': '', disabled: true }, 'Send')))),
      card('Stream info', null, el('div', { class: 'stack' },
        el('label', {}, 'Title', titleInput), el('label', {}, 'Category', categoryInput, categoryOptions),
        el('div', { class: 'row' }, el('button', { class: 'primary', onclick: saveChannel, 'data-needs-twitch': '', disabled: true }, 'Update'),
          el('button', { class: 'ghost', onclick: () => { if (!channelDirty || confirm('Discard your stream info changes?')) { channelDirty = false; loadStrip(); } } }, 'Reload'))))),
    el('div', { class: 'grid-2' },
      card('Tools', null, tools),
      card('Recent events', null, feed)));

  loadStrip(); loadScenes(); loadPins(); loadGiveaway();
  const recent = await get('/v1/events/recent?limit=25').catch(() => ({ events: [] }));
  for (const ev of recent.events) feed.prepend(eventItem(ev));

  const off = onEvent((ev) => {
    if (ev.type === 'SceneChanged' && ev.source === 'Obs') { currentScene = ev.data.sceneName; loadScenes(); loadStrip(); }
    if (ev.type === 'Event' && ev.source === 'Custom' && String(ev.data?.eventName || '').startsWith('giveaway.')) loadGiveaway();
    if (['StreamOnline', 'StreamOffline', 'StreamUpdate'].includes(ev.type)) loadStrip();
    if (ev.source === 'Core' && /^Action/.test(ev.type)) return;
    feed.prepend(eventItem(ev));
    while (feed.children.length > 40) feed.lastChild.remove();
  });
  const tick = setInterval(() => {
    const ends = Number(giveawayNode.dataset.ends);
    const clock = giveawayNode.querySelector('.countdown');
    if (ends && clock) clock.textContent = fmtDuration((ends - Date.now()) / 1000);
  }, 1000);
  const refresh = setInterval(loadStrip, 15000);
  return { isDirty: () => channelDirty, dispose() { chat.dispose(); off(); clearInterval(tick); clearInterval(refresh); } };
}
