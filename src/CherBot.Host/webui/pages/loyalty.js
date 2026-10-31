// Loyalty & Games: point settings, the leaderboard with inline edits, and one card per chat game.

import { el, fill, guard, toast, card, pageHead, toggle, debounce, go } from '../lib/dom.js';
import { get, post, put, api, simulate, invalidate, actionForCommand } from '../lib/api.js';
import { formGrid } from '../lib/forms.js';

const GAMES = [
  { key: 'gamble', title: 'Gamble', commands: ['!gamble'], tryIt: '!gamble 100', blurb: 'Roll 1–100; win at or under the win chance.',
    fields: [{ key: 'winChancePercent', label: 'Win chance %', type: 'number', min: 1, max: 99 }, { key: 'payoutMultiplier', label: 'Payout ×', type: 'number', step: 0.1, hint: '2 = bet back plus the same again' }] },
  { key: 'slots', title: 'Slots', commands: ['!slots'], tryIt: '!slots 100', blurb: 'Three reels; pairs, triples and a jackpot symbol pay out.',
    fields: [{ key: 'symbols', label: 'Symbols (one per line)', type: 'chips', full: true }, { key: 'twoOfAKindMultiplier', label: 'Pair ×', type: 'number', step: 0.1 },
      { key: 'threeOfAKindMultiplier', label: 'Triple ×', type: 'number', step: 0.1 }, { key: 'jackpotSymbol', label: 'Jackpot symbol' }, { key: 'jackpotMultiplier', label: 'Jackpot ×', type: 'number', step: 1 }] },
  { key: 'coinflip', title: 'Coinflip', commands: ['!coinflip'], tryIt: '!coinflip heads 100', blurb: 'Call heads or tails.',
    fields: [{ key: 'payoutMultiplier', label: 'Payout ×', type: 'number', step: 0.1 }] },
  { key: 'duel', title: 'Duel', commands: ['!duel', '!accept', '!decline'], tryIt: '!duel @someone 100', blurb: 'Challenge another viewer; the winner takes the bet.',
    fields: [{ key: 'timeoutSeconds', label: 'Answer within (s)', type: 'number', min: 10 }] },
  { key: 'heist', title: 'Heist', commands: ['!heist'], tryIt: '!heist 100', blurb: 'Viewers join a crew; each survivor is paid out, the rest lose their bet.',
    fields: [{ key: 'joinSeconds', label: 'Join window (s)', type: 'number', min: 5 }, { key: 'minPlayers', label: 'Min players', type: 'number', min: 1 },
      { key: 'baseSuccessPercent', label: 'Base success %', type: 'number' }, { key: 'successPerPlayerPercent', label: '+% per extra player', type: 'number' },
      { key: 'maxSuccessPercent', label: 'Max success %', type: 'number' }, { key: 'payoutMultiplier', label: 'Payout ×', type: 'number', step: 0.1 }] },
  { key: 'roulette', title: 'Roulette', commands: ['!roulette'], tryIt: '!roulette 100 red', blurb: 'Bet on red/black/green/even/odd/low/high or a number.',
    fields: [{ key: 'evenMoneyMultiplier', label: 'Even-money ×', type: 'number', step: 0.1 }, { key: 'greenMultiplier', label: 'Green ×', type: 'number' }, { key: 'numberMultiplier', label: 'Number ×', type: 'number' }] },
];
const BET_FIELDS = [{ key: 'minBet', label: 'Min bet', type: 'number', min: 0 }, { key: 'maxBet', label: 'Max bet (0 = none)', type: 'number', min: 0 }, { key: 'cooldownSeconds', label: 'Cooldown per user (s)', type: 'number', min: 0 }];

export async function render(root) {
  const settingsNode = el('div');
  const boardNode = el('tbody');
  const gamesNode = el('div', { class: 'grid-3' });
  const search = el('input', { type: 'search', placeholder: 'Search viewers…', 'aria-label': 'Search viewers' });
  let loyalty, games, commands, currency = 'points';

  const loadSettings = guard(async () => {
    const r = await get('/v1/loyalty/settings');
    loyalty = r.settings;
    currency = loyalty.currencyName || 'points';
    let dirty = false;
    const save = el('button', { class: 'primary', onclick: guard(async () => { await put('/v1/loyalty/settings', loyalty); dirty = false; save.textContent = 'Saved'; toast('Loyalty settings saved'); currency = loyalty.currencyName || 'points'; loadBoard(); }) }, 'Save');
    const mark = () => { if (!dirty) { dirty = true; save.textContent = 'Save changes'; } };
    fill(settingsNode,
      el('div', { class: 'row center wrap', style: 'margin-bottom:8px' },
        el('label', { class: 'inline' }, toggle(loyalty.enabled, async (on) => { loyalty.enabled = on; await put('/v1/loyalty/settings', loyalty); toast(on ? 'Points on' : 'Points off'); }), 'Points enabled'),
        r.live === true ? el('span', { class: 'chip ok' }, 'Live') : r.live === false ? el('span', { class: 'chip' }, 'Offline') : null),
      el('h4', {}, 'Currency and chat'),
      formGrid(loyalty, [
        { key: 'currencyName', label: 'Currency name', placeholder: 'points' },
        { key: 'chatPoints', label: 'Per chat message', type: 'number', min: 0 },
        { key: 'chatCooldownSeconds', label: 'Chat cooldown (s)', type: 'number', min: 0, hint: '0 = every message' },
        { key: 'firstWordPoints', label: 'First message of the stream', type: 'number', min: 0 },
      ], mark),
      el('h4', {}, 'Watch time'),
      formGrid(loyalty, [
        { key: 'watchEnabled', label: 'Pay viewers for watching', type: 'bool' },
        { key: 'watchOnlyWhenLive', label: 'Only while live', type: 'bool' },
        { key: 'watchIntervalMinutes', label: 'Every (minutes)', type: 'number', min: 1 },
        { key: 'watchPoints', label: 'Points each time', type: 'number', min: 0 },
        { key: 'watchActiveMinutes', label: 'Only chatters active in the last N minutes', type: 'number', min: 0, hint: '0 = everyone in chat (needs the chatters scope)' },
      ], mark),
      el('h4', {}, 'Role multipliers'),
      formGrid(loyalty, [
        { key: 'subscriberMultiplier', label: 'Subscribers ×', type: 'number', step: 0.1, min: 0 },
        { key: 'vipMultiplier', label: 'VIPs ×', type: 'number', step: 0.1, min: 0 },
        { key: 'moderatorMultiplier', label: 'Moderators ×', type: 'number', step: 0.1, min: 0 },
      ], mark),
      el('h4', {}, 'Bonuses'),
      formGrid(loyalty, [
        { key: 'followBonus', label: 'Follow', type: 'number', min: 0 },
        { key: 'subBonus', label: 'Sub', type: 'number', min: 0 },
        { key: 'resubBonus', label: 'Resub', type: 'number', min: 0 },
        { key: 'giftSubBonusPerSub', label: 'Per gifted sub (to the gifter)', type: 'number', min: 0 },
        { key: 'cheerBonusPer100Bits', label: 'Per 100 bits', type: 'number', min: 0 },
        { key: 'raidBonus', label: 'Raid (to the raider)', type: 'number', min: 0 },
      ], mark),
      el('div', { class: 'row', style: 'margin-top:10px' }, save));
  });

  const loadBoard = guard(async () => {
    const r = await get(`/v1/points?limit=50${search.value ? `&search=${encodeURIComponent(search.value)}` : ''}`);
    fill(boardNode, r.points.length ? r.points.map((p, i) => {
      const amount = el('input', { type: 'number', value: p.points, style: 'width:110px', 'aria-label': `Points for ${p.userName}` });
      const setPoints = guard(async () => {
        const value = Number(amount.value);
        await put(`/v1/points/${p.platform}/${encodeURIComponent(p.userId)}`, { points: value, userName: p.userName, add: false });
        toast(`${p.userName}: ${value} ${currency}`);
        loadBoard();
      });
      return el('tr', {},
        el('td', {}, search.value ? '' : String(i + 1)), el('td', {}, p.userName || p.userId), el('td', {}, amount),
        el('td', { class: 'actions' },
          el('button', { class: 'small', onclick: setPoints }, 'Set'), ' ',
          el('button', { class: 'small ghost', onclick: () => { amount.value = Number(amount.value) + 100; } }, '+100'), ' ',
          el('button', { class: 'small ghost', onclick: () => { amount.value = Math.max(0, Number(amount.value) - 100); } }, '−100')));
    }) : el('tr', {}, el('td', { colspan: 4, class: 'muted' }, search.value ? 'Nobody matches.' : `Nobody has any ${currency} yet.`)));
  });
  search.addEventListener('input', debounce(loadBoard, 300));

  const giveAmount = el('input', { type: 'number', value: 100, style: 'width:100px', 'aria-label': 'Amount to give' });

  const loadGames = guard(async () => {
    [games, commands] = await Promise.all([get('/v1/games/settings'), get('/v1/commands').then(r => r.commands)]);
    fill(gamesNode, GAMES.map(gameCard));
  });

  function gameCard(g) {
    const s = games[g.key];
    const cmds = g.commands.map(n => commands.find(c => c.name === n)).filter(Boolean);
    const on = cmds.length > 0 && cmds.every(c => c.enabled);
    const result = el('p', { class: 'small muted' });
    let dirty = false;
    const save = el('button', { class: 'small', onclick: guard(async () => { await put('/v1/games/settings', games); dirty = false; save.classList.remove('primary'); toast(`${g.title} saved`); }) }, 'Save');
    const mark = () => { if (!dirty) { dirty = true; save.classList.add('primary'); } };
    const main = cmds[0];
    const cmdText = el('input', { value: main ? main.command.split('\n').join(', ') : '', 'aria-label': `${g.title} command text`, title: 'Aliases separated by commas' });
    return el('div', { class: `mod-card${on ? ' on' : ''}` },
      el('h3', {}, g.title, toggle(on, async (v) => {
        for (const c of cmds) await put(`/v1/commands/${c.id}`, { ...c, enabled: v });
        invalidate('commands');
        toast(`${g.title} ${v ? 'enabled' : 'disabled'}`);
        loadGames();
      }, `Enable ${g.title}`)),
      el('p', { class: 'muted small' }, g.blurb),
      main ? el('div', { class: 'row' },
        el('label', { class: 'grow' }, 'Command (aliases with commas)', cmdText),
        el('button', { class: 'small', onclick: guard(async () => {
          await put(`/v1/commands/${main.id}`, { ...main, command: cmdText.value.split(',').map(x => x.trim()).filter(Boolean).join('\n') });
          invalidate('commands');
          toast('Command updated');
        }) }, 'Update')) : el('p', { class: 'warn-text small' }, 'The seeded command was deleted.'),
      el('details', { class: 'section' }, el('summary', {}, 'Odds, payouts and limits'),
        el('div', { class: 'body' }, formGrid(s, [...g.fields, ...BET_FIELDS], mark), el('div', { class: 'row', style: 'margin-top:8px' }, save))),
      el('div', { class: 'row center' },
        el('button', { class: 'small', onclick: guard(async () => {
          if (!on) { toast(`Enable ${g.title} first`, true); return; }
          await api('PUT', '/v1/points/twitch/sim-tester', { points: 1000, userName: 'tester', add: false });
          await simulate(g.tryIt, 'tester', 'viewer');
          result.textContent = `Sent "${g.tryIt}" as tester (topped up to 1000 ${currency}). The reply goes to chat and the Monitor feed.`;
        }) }, 'Try it'),
        main ? el('button', { class: 'small ghost', onclick: guard(async () => {
          const a = await actionForCommand(main.id);
          a ? go(`actions/${a.id}`) : toast('No action uses this command', true);
        }) }, 'Edit in builder') : null),
      result);
  }

  const stateNode = el('div', { class: 'small muted' });
  const loadState = guard(async () => {
    const st = await get('/v1/games/state');
    const parts = [];
    if (st.heist?.active) parts.push(`Heist forming: ${st.heist.crew.length} in the crew`);
    else if (st.heist?.cooldownUntil && new Date(st.heist.cooldownUntil) > new Date()) parts.push(`Heist cooldown until ${new Date(st.heist.cooldownUntil).toLocaleTimeString()}`);
    for (const d of st.duels || []) parts.push(`Duel: ${d.challenger} → ${d.target} for ${d.bet}`);
    stateNode.textContent = parts.join(' · ') || 'No games in progress.';
  });

  fill(root,
    pageHead('Loyalty & Games', 'Points viewers earn for chatting, watching and supporting, and the chat games they spend them on.'),
    el('div', { class: 'grid-2' },
      card('Loyalty settings', null, settingsNode),
      card('Leaderboard', [search, el('button', { class: 'small ghost', onclick: loadBoard }, 'Refresh')],
        el('div', { class: 'scroll' }, el('table', {}, el('thead', {}, el('tr', {}, ['#', 'Viewer', 'Points', ''].map(h => el('th', {}, h)))), boardNode)),
        el('div', { class: 'row wrap center', style: 'margin-top:10px' },
          el('span', { class: 'small' }, 'Give everyone in chat'), giveAmount,
          el('button', { class: 'small', onclick: guard(async () => { const r = await post('/v1/points/give-active', { amount: Number(giveAmount.value) }); toast(`Paid ${r.paid} viewers`); loadBoard(); }) }, 'Give'),
          el('button', { class: 'small ghost', onclick: guard(async () => { const r = await post('/v1/points/watch-now'); toast(`Watch time paid to ${r.paid} viewers`); loadBoard(); }) }, 'Pay watch time now'),
          el('span', { class: 'grow' }),
          el('button', { class: 'small ghost danger', onclick: guard(async () => {
            if (prompt(`Type RESET to delete every viewer's ${currency}. This can't be undone.`) !== 'RESET') return;
            const r = await api('DELETE', '/v1/points/twitch');
            toast(`Reset ${r.removed} viewers`);
            loadBoard();
          }) }, 'Reset everyone')))),
    el('div', { class: 'page-head', style: 'margin-top:8px' }, el('div', {}, el('h2', {}, 'Games'), el('p', { class: 'muted' }, 'Each game is a normal command plus action (group "CherBot Games"), created switched off.')), stateNode),
    gamesNode);

  loadSettings(); loadBoard(); loadGames(); loadState();
  const t = setInterval(loadState, 5000);
  return { dispose() { clearInterval(t); } };
}
