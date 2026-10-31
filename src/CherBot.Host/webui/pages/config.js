// Config: Twitch and OBS connections, timers, queues and integrations. Commands, globals and points
// moved to their own pages (Commands, Variables, Loyalty & Games).

import { el, fill, guard, toast, fmtTime, openDialog, jsonEditor, parseJson, card, pageHead, input, num, toggle } from '../lib/dom.js';
import { api, get, post, put, del, forgetToken, invalidate } from '../lib/api.js';

export async function render(root) {
  const twitch = el('div');
  const obs = el('div');
  const timers = el('tbody');
  const queues = el('tbody');
  const integrations = el('div');
  const engine = el('span');

  const loadTwitch = guard(async () => {
    const s = await get('/v1/twitch/status');
    const x = s.extra || {};
    const scopes = x.broadcaster?.scopes || [];
    const missing = ['moderator:manage:chat_messages', 'moderator:read:followers', 'moderator:read:chatters'].filter(sc => x.broadcaster && !scopes.includes(sc));
    const account = (role, info) => el('div', { class: 'row center', style: 'margin:6px 0' },
      el('strong', { style: 'width:110px' }, role === 'broadcaster' ? 'Broadcaster' : 'Bot'),
      el('span', { class: 'grow' }, info ? `${info.displayName} (${info.login})` : el('span', { class: 'muted' }, 'not linked')),
      el('button', { class: 'small', onclick: () => linkTwitch(role) }, info ? 'Relink' : 'Link'),
      info ? el('button', { class: 'small ghost danger', onclick: guard(async () => { await del(`/v1/twitch/auth/${role}`); loadTwitch(); }) }, 'Unlink') : null);
    fill(twitch,
      el('p', {}, 'State: ', el('strong', {}, s.state), s.detail ? el('span', { class: 'error' }, ` — ${s.detail}`) : null),
      x.clientConfigured ? null : el('p', { class: 'error' }, 'Set CherBot:Twitch:ClientId (your Twitch app, registered as a Public client) in cherbot.json, then restart.'),
      account('broadcaster', x.broadcaster), account('bot', x.bot),
      missing.length ? el('p', { class: 'warn-text small' }, `Relink the broadcaster to grant: ${missing.join(', ')} (needed by the spam filter, followers-only giveaways and watch-time points).`) : null,
      x.subscriptions?.length ? el('details', {}, el('summary', {}, `EventSub subscriptions (${x.subscriptions.filter(s => s.status === 'enabled').length}/${x.subscriptions.length})`),
        el('ul', { class: 'plain' }, x.subscriptions.map(s => el('li', { class: s.status === 'enabled' ? '' : 'error' }, `${s.type} v${s.version}: ${s.status}${s.error ? ` — ${s.error}` : ''}`)))) : null);
  });

  async function linkTwitch(role) {
    try {
      const flow = await post(`/v1/twitch/auth/${role}/device`);
      const status = el('p', { class: 'muted' }, 'Waiting for you to approve…');
      openDialog(`Link Twitch ${role}`, el('div', {},
        el('p', {}, 'Open ', el('a', { href: flow.verificationUri, target: '_blank', rel: 'noopener' }, flow.verificationUri), ` and sign in as the ${role} account, then enter:`),
        el('p', { class: 'devicecode' }, flow.userCode), status), { hideOk: true });
      const poll = setInterval(guard(async () => {
        const s = await get(`/v1/twitch/auth/${role}`);
        if (!s.flow || s.flow.status === 'pending') return;
        clearInterval(poll);
        status.textContent = s.flow.status === 'complete' ? `Linked as ${s.flow.login}.` : `Login ${s.flow.status}: ${s.flow.error || ''}`;
        loadTwitch();
      }), 2000);
      document.querySelector('#dialog').addEventListener('close', () => clearInterval(poll), { once: true });
    } catch (e) { toast(e.message, true); }
  }

  const loadObs = guard(async () => {
    const r = await get('/v1/obs/connections');
    const states = new Map((r.status.extra?.connections || []).map(c => [c.id, c]));
    fill(obs, r.connections.length
      ? el('table', {}, el('tbody', {}, r.connections.map(c => el('tr', {},
        el('td', {}, c.name), el('td', { class: 'mono' }, `${c.host}:${c.port}`),
        el('td', { class: states.get(c.id)?.state === 'connected' ? 'status-ok' : 'status-break' }, states.get(c.id)?.state || 'idle',
          states.get(c.id)?.error ? el('div', { class: 'error small' }, states.get(c.id).error) : null),
        el('td', { class: 'actions' }, el('button', { class: 'small ghost danger', onclick: guard(async () => { await del(`/v1/obs/connections/${c.id}`); invalidate('obs'); loadObs(); }) }, 'Remove'))))))
      : el('p', { class: 'muted' }, 'No OBS connections. Enable the WebSocket server in OBS (Tools → WebSocket Server Settings).'));
  });

  const loadTimers = guard(async () => {
    const r = await get('/v1/timers');
    fill(timers, r.timers.map(({ timer: t, nextFire }) => el('tr', {},
      el('td', {}, t.name), el('td', {}, `${t.interval}s${t.randomInterval ? `–${t.upperInterval}s` : ''}${t.lines ? `, ${t.lines} lines` : ''}`),
      el('td', {}, nextFire ? fmtTime(nextFire) : '—'),
      el('td', {}, toggle(t.enabled, async (on) => { await put(`/v1/timers/${t.id}`, { ...t, enabled: on }); loadTimers(); })),
      el('td', { class: 'actions' },
        el('button', { class: 'small', onclick: () => editTimer(t) }, 'Edit'), ' ',
        el('button', { class: 'small', onclick: guard(async () => { await post(`/v1/timers/${t.id}/fire`); toast('Fired'); }) }, 'Fire'), ' ',
        el('button', { class: 'small ghost danger', onclick: guard(async () => { if (confirm(`Delete ${t.name}?`)) { await del(`/v1/timers/${t.id}`); loadTimers(); } }) }, 'Delete')))));
  });

  function editTimer(t) {
    const name = input(t.name), interval = num(t.interval, { min: 1 }), upper = num(t.upperInterval || 0, { min: 0 }), lines = num(t.lines || 0, { min: 0 });
    const random = el('input', { type: 'checkbox' }); random.checked = !!t.randomInterval;
    openDialog(t.id ? `Edit timer ${t.name}` : 'New timer', el('div', { class: 'stack' },
      el('div', { class: 'form-grid' }, el('label', {}, 'Name', name), el('label', {}, 'Every (seconds)', interval),
        el('label', { class: 'inline' }, random, 'Random interval'), el('label', {}, 'Up to (seconds)', upper),
        el('label', {}, 'Only after N chat lines (0 = always)', lines)),
      el('p', { class: 'muted small' }, 'Fire actions from a timer with a Timed Actions trigger (or the Quick Setup "Timer message" wizard).')), {
      okText: 'Save',
      onOk: async () => {
        const body = { ...t, name: name.value, interval: Number(interval.value), randomInterval: random.checked, upperInterval: Number(upper.value), lines: Number(lines.value) };
        t.id ? await put(`/v1/timers/${t.id}`, body) : await post('/v1/timers', body);
        invalidate('timers');
        loadTimers();
      },
    });
  }

  const loadQueues = guard(async () => {
    const r = await get('/v1/queues');
    fill(queues, r.definitions.map(q => el('tr', {},
      el('td', {}, q.name), el('td', {}, q.blocking ? 'Blocking' : 'Concurrent'),
      el('td', { class: 'actions' }, el('button', { class: 'small ghost danger', onclick: guard(async () => { await del(`/v1/queues/${q.id}`); invalidate('queues'); loadQueues(); }) }, 'Delete')))));
  });

  const loadIntegrations = guard(async () => {
    const r = await get('/v1/integrations');
    fill(integrations, el('table', {}, el('tbody', {}, r.integrations.map(i => el('tr', {},
      el('td', {}, i.name), el('td', { class: i.connected ? 'status-ok' : 'status-break' }, i.state), el('td', { class: 'muted small' }, i.detail || ''))))));
    const h = window.cherbotHealth || await (await fetch('/health')).json();
    engine.textContent = `${h.os} · data ${h.dataDir} · sound ${h.sound}`;
  });

  const obsName = input('OBS', { required: true }), obsHost = input('127.0.0.1', { required: true }), obsPort = num(4455, { required: true }), obsPassword = input('', { type: 'password', autocomplete: 'off' });
  const queueName = input('', { required: true }), queueBlocking = el('input', { type: 'checkbox', checked: true });

  fill(root,
    pageHead('Config', 'Connections, timers, queues and engine details. Commands live on the Commands page, globals on Variables, points on Loyalty & Games.'),
    el('div', { class: 'grid-2' },
      card('Twitch', [el('button', { class: 'small ghost', onclick: guard(async () => { await post('/v1/twitch/reconnect'); toast('Reconnecting'); setTimeout(loadTwitch, 2000); }) }, 'Reconnect')], twitch),
      card('OBS', null, obs, el('form', {
        class: 'row wrap', style: 'margin-top:8px', onsubmit: guard(async (ev) => {
          ev.preventDefault();
          await post('/v1/obs/connections', { name: obsName.value, host: obsHost.value, port: Number(obsPort.value), password: obsPassword.value || null });
          obsPassword.value = '';
          invalidate('obs');
          toast('Connection added');
          setTimeout(loadObs, 1500);
        }),
      }, el('label', {}, 'Name', obsName), el('label', {}, 'Host', obsHost), el('label', {}, 'Port', obsPort), el('label', {}, 'Password', obsPassword),
      el('button', { type: 'submit' }, 'Add connection')))),
    el('div', { class: 'grid-2' },
      card('Timers', [el('button', { class: 'small', onclick: () => editTimer({ name: 'Promo', enabled: true, repeat: true, interval: 900, randomInterval: false, upperInterval: 0, lines: 5 }) }, 'New timer')],
        el('table', {}, el('thead', {}, el('tr', {}, ['Name', 'Every', 'Next', 'On', ''].map(h => el('th', {}, h)))), timers)),
      card('Queues', null, el('table', {}, el('thead', {}, el('tr', {}, ['Name', 'Mode', ''].map(h => el('th', {}, h)))), queues),
        el('form', {
          class: 'row', style: 'margin-top:8px', onsubmit: guard(async (ev) => {
            ev.preventDefault();
            await post('/v1/queues', { name: queueName.value, blocking: queueBlocking.checked });
            queueName.value = '';
            invalidate('queues');
            loadQueues();
          }),
        }, el('label', { class: 'grow' }, 'Name', queueName), el('label', { class: 'inline' }, queueBlocking, 'Blocking'), el('button', { type: 'submit' }, 'Add queue')))),
    el('div', { class: 'grid-2' },
      card('Integrations', null, integrations, el('p', { class: 'muted small' }, 'Engine: ', engine)),
      card('This browser', null,
        el('p', { class: 'muted small' }, 'The API token is stored in this browser only.'),
        el('div', { class: 'row' },
          el('a', { href: '#/commands' }, 'Commands →'), el('a', { href: '#/variables' }, 'Variables →'), el('a', { href: '#/loyalty' }, 'Points →')),
        el('p', {}, el('button', { class: 'ghost small', onclick: () => { forgetToken(); location.reload(); } }, 'Forget token on this browser')))));

  loadTwitch(); loadObs(); loadTimers(); loadQueues(); loadIntegrations();
}
