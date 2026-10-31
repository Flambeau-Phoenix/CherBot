// Commands: a Nightbot-style custom command builder, and every command with a link to its action.

import { el, fill, guard, toast, card, pageHead, toggle, go, openDialog, jsonEditor, parseJson, select, debounce } from '../lib/dom.js';
import { get, post, put, del, lists, invalidate, simulate } from '../lib/api.js';
import { formGrid, previewTokens } from '../lib/forms.js';
import { varButton } from '../builder/varhelper.js';

const TOKENS = [
  ['user', 'Who used the command'], ['touser', 'First word without @ (or the user)'], ['args', 'Everything after the command'],
  ['1', 'First word after the command'], ['2', 'Second word'], ['count', 'Times used (counts up)'], ['random:1-100', 'Random number'],
  ['pick:a|b|c', 'Random choice'], ['uptime', 'How long you\'ve been live'], ['game', 'Current category'], ['title', 'Stream title'],
  ['followage', 'How long the user has followed'], ['points', 'Their points'], ['currency', 'Points name'], ['channel', 'Your channel name'],
].map(([name, desc]) => ({ name, desc }));
const SAMPLE = { user: 'Viewer', touser: 'Friend', args: '@Friend hello', 1: '@Friend', 2: 'hello', count: 42, uptime: '1h 23m', game: 'Just Chatting', title: 'Chill stream', followage: '1 year, 2 months', points: 1500, currency: 'points', channel: 'You' };

const blank = () => ({ name: '!', aliases: [], enabled: true, responses: [''], lineDelayMs: 1000, replyMode: 'chat', color: 'primary', minRole: 'everyone', permittedGroups: [], globalCooldown: 5, userCooldown: 10, cost: 0, onlyWhileLive: false });

export async function render(root, route) {
  const view = route.query.get('view') === 'all' ? 'all' : 'custom';
  const tabs = el('div', { class: 'seg' },
    el('button', { class: view === 'custom' ? 'on' : '', onclick: () => go('commands') }, 'Custom commands'),
    el('button', { class: view === 'all' ? 'on' : '', onclick: () => go('commands?view=all') }, 'All commands'));
  const body = el('div');
  fill(root, pageHead('Commands', 'Chat commands. Custom commands are quick text replies; every command can also start any action.', tabs), body);
  if (view === 'all') await allCommands(body);
  else return await customCommands(body, route.rest[0]);
}

// ── custom commands ─────────────────────────────────────────────────────────
async function customCommands(root, selectedId) {
  const listNode = el('div', { class: 'list' });
  const detail = el('div', { class: 'detail' });
  const search = el('input', { type: 'search', placeholder: 'Search custom commands…', 'aria-label': 'Search custom commands' });
  let items = [];
  let dirty = false;

  const load = guard(async () => {
    items = (await get('/v1/custom-commands')).commands;
    drawList();
  });

  function drawList() {
    const q = search.value.toLowerCase();
    const shown = items.filter(v => !q || `${v.command.name} ${v.command.aliases.join(' ')} ${v.command.responses.join(' ')}`.toLowerCase().includes(q));
    fill(listNode, shown.length ? shown.map(v => {
      const c = v.command;
      return el('div', { class: `item${c.enabled ? '' : ' disabled'}${c.id === selectedId ? ' selected' : ''}`, tabindex: '0', role: 'button', onclick: () => go(`commands/${c.id}`), onkeydown: (ev) => { if (ev.key === 'Enter') go(`commands/${c.id}`); } },
        toggle(c.enabled, async (on) => { await put(`/v1/custom-commands/${c.id}`, { ...c, enabled: on }); c.enabled = on; invalidate('commands'); drawList(); }, `Enable ${c.name}`),
        el('span', { class: 'name' }, c.name), c.advanced ? el('span', { class: 'chip warn', title: 'Edited in the visual builder' }, 'builder') : null,
        el('span', { class: 'muted small' }, (c.responses[0] || '').slice(0, 30)));
    }) : el('p', { class: 'muted', style: 'padding:12px' }, items.length ? 'Nothing matches.' : 'No custom commands yet.'));
  }

  function editor(def, isNew) {
    const responsesNode = el('div', { class: 'stack' });
    const previewNode = el('div');
    const save = el('button', { class: isNew ? 'primary' : '', onclick: guard(async () => {
      def.responses = def.responses.filter(r => r.trim());
      const saved = isNew ? await post('/v1/custom-commands', def) : await put(`/v1/custom-commands/${def.id}`, def);
      invalidate('commands'); invalidate('actions'); invalidate('customCommands');
      toast('Saved');
      dirty = false;
      save.classList.remove('primary');
      if (isNew) go(`commands/${saved.command.id}`); else load();
    }) }, isNew ? 'Create' : 'Save');
    const mark = () => { dirty = true; save.classList.add('primary'); };

    const drawResponses = () => fill(responsesNode, def.responses.map((r, i) => {
      const area = el('textarea', { rows: 2, placeholder: 'Hello {user}! Several lines send several messages.' });
      area.value = r;
      const counter = el('div', { class: 'counter' });
      const prev = el('div', { class: 'preview' });
      const draw = () => {
        const lines = area.value.split('\n').filter(l => l.trim());
        fill(prev, lines.length ? lines.map(l => el('div', {}, previewTokens(l, SAMPLE))) : el('span', { class: 'muted' }, 'Empty'));
        const longest = Math.max(0, ...lines.map(l => previewTokens(l, SAMPLE).length));
        counter.textContent = `${longest}/500${lines.length > 1 ? ` · ${lines.length} messages` : ''}`;
        counter.classList.toggle('over', longest > 500);
      };
      area.addEventListener('input', () => { def.responses[i] = area.value; draw(); mark(); });
      draw();
      return el('div', { class: 'stack', style: 'gap:4px' },
        el('div', { class: 'row between center' }, el('span', { class: 'small' }, def.responses.length > 1 ? `Variant ${i + 1}` : 'Response'),
          el('div', { class: 'row' }, varButton(area, () => ({ tokens: TOKENS, mode: 'args', eventTypes: [401] })),
            def.responses.length > 1 ? el('button', { class: 'small ghost danger', onclick: () => { def.responses.splice(i, 1); mark(); drawResponses(); } }, 'Remove') : null)),
        area, counter, prev);
    }), el('button', { class: 'small', onclick: () => { def.responses.push(''); mark(); drawResponses(); } }, '+ Add a variant (picked at random)'));
    drawResponses();

    const aliases = el('input', { value: def.aliases.join(', '), placeholder: '!hi, !hey' });
    aliases.addEventListener('input', () => { def.aliases = aliases.value.split(',').map(a => a.trim()).filter(Boolean); mark(); });

    return el('div', { class: 'stack' },
      el('div', { class: 'row between center' }, el('h3', {}, isNew ? 'New custom command' : def.name),
        def.advanced ? el('span', { class: 'chip warn' }, 'Edited in the builder — the response here is used only by its Custom Command Response step') : null),
      formGrid(def, [{ key: 'name', label: 'Command', placeholder: '!hello' }], mark),
      el('label', {}, 'Aliases (comma separated)', aliases),
      responsesNode,
      el('details', { class: 'section', open: true }, el('summary', {}, 'Who, when and how'), el('div', { class: 'body' }, formGrid(def, [
        { key: 'replyMode', label: 'Send as', type: 'select', options: [['chat', 'Chat message'], ['reply', 'Reply to the user'], ['announcement', 'Announcement']] },
        { key: 'color', label: 'Announcement colour', type: 'select', options: [['primary', 'Channel colour'], ['blue', 'Blue'], ['green', 'Green'], ['orange', 'Orange'], ['purple', 'Purple']] },
        { key: 'minRole', label: 'Who can use it', type: 'select', options: [['everyone', 'Everyone'], ['subscriber', 'Subscribers and up'], ['vip', 'VIPs and up'], ['moderator', 'Moderators'], ['broadcaster', 'Only you']] },
        { key: 'permittedGroups', label: 'Or only these user groups', type: 'chips', rows: 2 },
        { key: 'globalCooldown', label: 'Cooldown (s)', type: 'number', min: 0 },
        { key: 'userCooldown', label: 'Per-user cooldown (s)', type: 'number', min: 0 },
        { key: 'cost', label: 'Cost (points)', type: 'number', min: 0 },
        { key: 'lineDelayMs', label: 'Delay between lines (ms)', type: 'number', min: 0 },
        { key: 'onlyWhileLive', label: 'Only while live', type: 'bool' },
        { key: 'enabled', label: 'Enabled', type: 'bool' },
      ], mark))),
      el('div', { class: 'row wrap' },
        save,
        el('button', { onclick: guard(async () => {
          const r = await post('/v1/custom-commands/preview', { definition: def, user: 'Viewer', input: '@Friend hello' });
          fill(previewNode, el('div', { class: 'preview' }, r.lines.length ? r.lines.map(l => el('div', {}, l)) : el('span', { class: 'muted' }, '(nothing would be sent)')),
            el('p', { class: 'hint' }, 'Rendered by the engine with sample data (Viewer typing "@Friend hello"); nothing was sent.'));
        }) }, 'Test'),
        isNew ? null : el('button', { onclick: guard(async () => { await simulate(`${def.name} @Friend hello`, 'tester', 'moderator'); toast('Sent through the chat simulator as tester (moderator)'); }) }, 'Try in chat'),
        isNew || !def.actionId ? null : el('button', { onclick: () => go(`actions/${def.actionId}`) }, 'Open in builder'),
        isNew ? null : el('button', { class: 'ghost danger', onclick: guard(async () => { if (!confirm(`Delete ${def.name}?`)) return; await del(`/v1/custom-commands/${def.id}`); dirty = false; invalidate('commands'); invalidate('actions'); go('commands'); }) }, 'Delete')),
      previewNode);
  }

  const importBox = el('textarea', { rows: 5, placeholder: '!discord Join us at discord.gg/...\n!socials Find me everywhere at ...' });
  const importOut = el('div', { class: 'small' });

  fill(root, el('div', { class: 'split' },
    el('div', {},
      el('div', { class: 'toolbar' }, search, el('button', { class: 'primary small', onclick: () => go('commands/new') }, 'New')),
      listNode,
      card('Bulk import', null, el('p', { class: 'muted small' }, 'One per line: the command, a space, then the response.'), importBox,
        el('div', { class: 'row', style: 'margin-top:6px' }, el('button', { class: 'small', onclick: guard(async () => {
          const r = await post('/v1/custom-commands/import', { text: importBox.value });
          fill(importOut, `Added ${r.added}.`, r.skipped.length ? el('div', { class: 'warn-text' }, `Skipped (not "!command response", or the name is taken): ${r.skipped.join(' · ')}`) : null);
          if (r.added) { importBox.value = ''; invalidate('commands'); load(); }
        }) }, 'Import')), importOut)),
    detail));
  search.addEventListener('input', debounce(drawList, 150));

  await load();
  if (selectedId === 'new') fill(detail, editor(blank(), true));
  else if (selectedId) {
    const v = items.find(x => x.command.id === selectedId);
    fill(detail, v ? editor(JSON.parse(JSON.stringify(v.command)), false) : el('p', { class: 'muted' }, 'That command no longer exists.'));
  } else {
    fill(detail, el('div', { class: 'stack' }, el('h3', {}, 'Custom commands'),
      el('p', { class: 'muted' }, 'Write a response, use {placeholders} like {user}, {touser}, {count} or {random:1-6}, and CherBot creates the command and its action for you. Open it in the builder any time to add sounds, OBS changes or anything else.'),
      el('div', {}, el('button', { class: 'primary', onclick: () => go('commands/new') }, 'New custom command'))));
  }
  return { isDirty: () => dirty };
}

// ── all commands ────────────────────────────────────────────────────────────
async function allCommands(root) {
  const tbody = el('tbody');
  const search = el('input', { type: 'search', placeholder: 'Search commands…', 'aria-label': 'Search commands' });
  const groupFilter = el('select', { 'aria-label': 'Group' });
  let commands = [], actions = [];

  const load = guard(async () => {
    invalidate('commands'); invalidate('actions');
    [commands, actions] = await Promise.all([lists.commands(), lists.actions()]);
    const groups = [...new Set(commands.map(c => c.group || ''))].sort();
    const keep = groupFilter.value;
    fill(groupFilter, el('option', { value: '*' }, 'All groups'), groups.map(g => el('option', { value: g }, g || 'No group')));
    groupFilter.value = groups.includes(keep) || keep === '*' ? keep : '*';
    draw();
  });

  const linked = (c) => actions.filter(a => (a.triggers || []).some(t => (t.type === 401 || t.type === 402) && t.commandId === c.id));

  function draw() {
    const q = search.value.toLowerCase();
    const g = groupFilter.value || '*';
    fill(tbody, commands.filter(c => (g === '*' || (c.group || '') === g) && (!q || `${c.name} ${c.command} ${c.group}`.toLowerCase().includes(q))).map(c => {
      const acts = linked(c);
      return el('tr', {},
        el('td', {}, toggle(c.enabled, async (on) => { await put(`/v1/commands/${c.id}`, { ...c, enabled: on }); c.enabled = on; invalidate('commands'); }, `Enable ${c.name}`)),
        el('td', {}, el('strong', {}, c.name), c.group ? el('div', { class: 'muted small' }, c.group) : null),
        el('td', { class: 'mono' }, c.command.split('\n').join(' · ')),
        el('td', { class: 'small' }, `${c.globalCooldown}s / ${c.userCooldown}s`, c.minRole ? el('div', { class: 'muted' }, `${c.minRole}+`) : null),
        el('td', {}, acts.length ? acts.map(a => el('div', {}, el('a', { href: `#/actions/${a.id}` }, a.name))) : el('span', { class: 'warn-text small' }, 'no action')),
        el('td', { class: 'actions' },
          el('button', { class: 'small', onclick: () => editCommand(c) }, 'Edit'), ' ',
          el('button', { class: 'small ghost danger', onclick: guard(async () => { if (!confirm(`Delete ${c.name}?`)) return; await del(`/v1/commands/${c.id}`); load(); }) }, 'Delete')));
    }));
  }

  function editCommand(c) {
    const d = JSON.parse(JSON.stringify(c));
    const text = el('textarea', { rows: 3 });
    text.value = d.command;
    text.addEventListener('input', () => { d.command = text.value; });
    const raw = jsonEditor(d, 240);
    let showRaw = false;
    const simple = el('div', { class: 'stack' },
      formGrid(d, [
        { key: 'name', label: 'Name' }, { key: 'group', label: 'Group' },
        { key: 'mode', label: 'Match', type: 'select', options: [[0, 'Text'], [1, 'Regex']] },
        { key: 'location', label: 'Where in the message', type: 'select', options: [[0, 'At the start'], [1, 'The whole message'], [2, 'Anywhere']] },
        { key: 'globalCooldown', label: 'Cooldown (s)', type: 'number', min: 0 }, { key: 'userCooldown', label: 'Per-user cooldown (s)', type: 'number', min: 0 },
        { key: 'minRole', label: 'Minimum role', type: 'select', options: [['', 'Everyone'], ['subscriber', 'Subscribers'], ['vip', 'VIPs'], ['moderator', 'Moderators'], ['broadcaster', 'Only you']] },
        { key: 'caseSensitive', label: 'Case sensitive', type: 'bool' }, { key: 'enabled', label: 'Enabled', type: 'bool' },
        { key: 'persistCounter', label: 'Keep the counter across restarts', type: 'bool' },
      ]),
      el('label', {}, 'Command text (one per line for aliases)', text));
    const holder = el('div', {}, simple);
    openDialog(c.id ? `Edit ${c.name}` : 'New command', el('div', { class: 'stack' },
      el('button', { type: 'button', class: 'small ghost', onclick: (ev) => { ev.preventDefault(); showRaw = !showRaw; if (showRaw) raw.value = JSON.stringify(d, null, 2); fill(holder, showRaw ? raw : simple); } }, 'Toggle JSON'),
      holder,
      el('p', { class: 'muted small' }, 'Link it to an action with a Command Triggered trigger (the builder\'s trigger picker lists every command).')), {
      okText: 'Save',
      onOk: async () => {
        const body = showRaw ? parseJson(raw.value) : d;
        body.id ? await put(`/v1/commands/${body.id}`, body) : await post('/v1/commands', body);
        invalidate('commands');
        toast('Saved');
        load();
      },
    });
  }

  search.addEventListener('input', debounce(draw, 150));
  groupFilter.addEventListener('change', draw);
  fill(root, card('All commands', [search, groupFilter,
    el('button', { class: 'small primary', onclick: () => editCommand({ name: '!hello', command: '!hello', enabled: true, mode: 0, location: 0, sources: 1, globalCooldown: 0, userCooldown: 5, group: '', grantType: 0, permittedGroups: [] }) }, 'New command')],
    el('table', {}, el('thead', {}, el('tr', {}, ['On', 'Name', 'Text', 'Cooldowns', 'Action', ''].map(h => el('th', {}, h)))), tbody)));
  await load();
}
