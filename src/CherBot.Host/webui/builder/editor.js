// The visual action builder: header, triggers with typed filters, a tree of step cards (If/Switch/While/
// Group as nested lanes), a searchable step palette, validation, and Save / Test / Run. An Advanced
// JSON tab edits the same draft.

import { el, fill, guard, toast, openDialog, closeDialog, parseJson, checkbox, copy, uuid, fmtVal } from '../lib/dom.js';
import { api, get, post, put, del, catalog, loadCatalog, subName, eventLabel, lists, invalidate } from '../lib/api.js';
import { resolveAll, defaultsFor, describe, containerOf, overrideFor } from '../meta/fields.js';
import { eventVariables } from '../meta/variables.js';
import { renderField } from './fields.js';
import { closeVarHelper } from './varhelper.js';

const IF = 120, SWITCH = 127, WHILE = 128, GROUP = 99900, THEN = 99901, ELSE = 99902, CASE = 99903, DEFAULT = 99904;
const LANES = new Set([THEN, ELSE, CASE, DEFAULT]);
const REQUIRED_PICKERS = new Set(['action', 'command', 'timer', 'csharp', 'customCommand', 'subAction', 'trigger']);
const clone = (x) => JSON.parse(JSON.stringify(x));

/** Gives every step an id, wraps plain If children in a Then lane, and sorts by index. */
function prepare(list) {
  (list || []).sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  for (const s of list || []) {
    s.id ||= uuid();
    if (s.enabled === undefined) s.enabled = true;
    if (s.type === IF) {
      s.subActions ||= [];
      const plain = s.subActions.filter(c => c.type !== THEN && c.type !== ELSE);
      if (plain.length) s.subActions = [{ type: THEN, id: uuid(), enabled: true, random: false, subActions: plain }, ...s.subActions.filter(c => c.type === THEN || c.type === ELSE)];
      if (!s.subActions.some(c => c.type === THEN)) s.subActions.unshift({ type: THEN, id: uuid(), enabled: true, random: false, subActions: [] });
      if (!s.subActions.some(c => c.type === ELSE)) s.subActions.push({ type: ELSE, id: uuid(), enabled: true, random: false, subActions: [] });
    }
    if (containerOf(s.type) || s.type === SWITCH) s.subActions ||= [];
    prepare(s.subActions);
  }
  return list;
}

/** Writes index and parentId the way Streamer.bot stores them. */
function finalize(list, parentId = null) {
  (list || []).forEach((s, i) => {
    s.index = i;
    if (parentId) s.parentId = parentId; else delete s.parentId;
    if (s.subActions) finalize(s.subActions, s.id);
  });
  return list;
}

function newStep(type) {
  const info = catalog.sub.get(type);
  const s = { id: uuid(), type, enabled: true, weight: 0, ...defaultsFor(type, info?.fields || []) };
  if (type === IF) s.subActions = [{ type: THEN, id: uuid(), enabled: true, random: false, subActions: [] }, { type: ELSE, id: uuid(), enabled: true, random: false, subActions: [] }];
  else if (type === SWITCH) s.subActions = [{ type: CASE, id: uuid(), enabled: true, values: [''], caseSensitive: false, random: false, subActions: [] }, { type: DEFAULT, id: uuid(), enabled: true, random: false, subActions: [] }];
  else if (containerOf(type) === 'list') s.subActions = [];
  return s;
}

function summary(s) {
  const skip = new Set(['id', 'index', 'type', 'weight', 'parentId', 'enabled', 'subActions', 'byteCode', 'random', 'useBot', 'fallback', 'autoType', 'connectionId', 'code']);
  return Object.entries(s).filter(([k, v]) => !skip.has(k) && v !== null && v !== '' && v !== false && !(Array.isArray(v) && !v.length) && !(typeof v === 'object' && !Array.isArray(v) && !Object.keys(v).length))
    .map(([k, v]) => `${k}=${fmtVal(v)}`).join('  ').slice(0, 140);
}

/** Example arguments for a trigger type (from meta/variables.js), used when no real event has been seen. */
export function exampleArgs(typeId) {
  return Object.fromEntries((eventVariables[typeId] || []).filter(v => v.example !== undefined && v.example !== '').map(v => [v.name, v.example]));
}

/**
 * Renders the editor into `root`. opts: { onSaved(action), onDeleted(), onClose() }
 * Returns { isDirty() }.
 */
export async function renderEditor(root, action, opts = {}) {
  await loadCatalog();
  const draft = clone(action);
  draft.triggers ||= [];
  draft.subActions = prepare(draft.subActions || []);
  let dirty = !action.id;
  let tab = 'builder';
  const collapsed = new Set();
  const countSteps = (list) => (list || []).reduce((n, s) => n + 1 + countSteps(s.subActions), 0);
  if (countSteps(draft.subActions) > 8) (function collapseAll(list) { for (const s of list || []) { if (!LANES.has(s.type)) collapsed.add(s.id); collapseAll(s.subActions); } })(draft.subActions);

  const markDirty = () => { dirty = true; saveBtn.classList.add('primary'); status.textContent = 'Unsaved changes'; };
  const status = el('span', { class: 'muted small' }, action.id ? '' : 'New action — not saved yet');
  const saveBtn = el('button', { class: action.id ? '' : 'primary', onclick: guard(() => save(false)) }, 'Save');
  const body = el('div');
  const validationBox = el('div');

  // ── header ──
  const actionsList = await lists.actions().catch(() => []);
  const queues = await lists.queues().catch(() => []);
  const groupList = `dl-groups-${uuid()}`;
  const name = el('input', { value: draft.name || '', 'aria-label': 'Action name' });
  name.addEventListener('input', () => { draft.name = name.value; markDirty(); });
  const group = el('input', { value: draft.group || '', list: groupList, 'aria-label': 'Group' });
  group.addEventListener('input', () => { draft.group = group.value; markDirty(); });
  const queue = el('select', { 'aria-label': 'Queue' }, el('option', { value: '' }, 'Default queue'), queues.map(q => el('option', { value: q.id }, `${q.name}${q.blocking ? ' (blocking)' : ''}`)));
  queue.value = draft.queue || '';
  queue.addEventListener('change', () => { draft.queue = queue.value || null; markDirty(); });
  const mode = el('div', { class: 'seg', role: 'group', 'aria-label': 'Run mode' });
  const drawMode = () => fill(mode, [['seq', 'Sequential'], ['random', 'Random (one step)'], ['concurrent', 'Concurrent']].map(([k, l]) => el('button', {
    type: 'button', class: (k === 'random' && draft.randomAction) || (k === 'concurrent' && draft.concurrent) || (k === 'seq' && !draft.randomAction && !draft.concurrent) ? 'on' : '',
    onclick: () => { draft.randomAction = k === 'random'; draft.concurrent = k === 'concurrent'; drawMode(); markDirty(); drawBody(); },
  }, l)));
  drawMode();

  const head = el('div', {},
    el('div', { class: 'builder-head' },
      el('label', {}, 'Name', name),
      el('label', {}, 'Group', group, el('datalist', { id: groupList }, [...new Set(actionsList.map(a => a.group).filter(Boolean))].map(g => el('option', { value: g })))),
      el('label', {}, 'Queue', queue)),
    el('div', { class: 'row wrap center' },
      checkbox('Enabled', draft.enabled !== false, (v) => { draft.enabled = v; markDirty(); }),
      checkbox('Always run (even when the queue is paused)', draft.alwaysRun, (v) => { draft.alwaysRun = v; markDirty(); }),
      checkbox('Exclude from history', draft.excludeFromHistory, (v) => { draft.excludeFromHistory = v; markDirty(); }),
      el('span', { class: 'grow' }), mode));

  const toolbar = el('div', { class: 'toolbar', style: 'margin-top:10px' },
    saveBtn,
    el('button', { onclick: guard(() => save(true)) }, 'Save & Test'),
    el('button', { onclick: guard(run), title: 'Run the saved action with no arguments' }, 'Run'),
    el('button', { class: 'ghost', onclick: guard(duplicate), disabled: !action.id }, 'Duplicate'),
    el('button', { class: 'ghost', onclick: guard(exportAction), disabled: !action.id }, 'Export'),
    el('button', { class: 'ghost danger', onclick: guard(remove), disabled: !action.id }, 'Delete'),
    el('span', { class: 'grow' }), status);

  const tabs = el('div', { class: 'builder-tabs', role: 'tablist' });
  const drawTabs = () => fill(tabs, [['builder', 'Builder'], ['json', 'Advanced JSON']].map(([k, l]) => el('button', {
    type: 'button', role: 'tab', class: tab === k ? 'on' : '', 'aria-selected': String(tab === k),
    onclick: () => switchTab(k),
  }, l)));
  drawTabs();

  let jsonArea = null;
  function switchTab(k) {
    if (k === tab) return;
    if (tab === 'json') {
      try {
        const parsed = parseJson(jsonArea.value);
        Object.keys(draft).forEach(key => delete draft[key]);
        Object.assign(draft, parsed);
        draft.triggers ||= [];
        draft.subActions = prepare(draft.subActions || []);
        name.value = draft.name || ''; group.value = draft.group || ''; queue.value = draft.queue || '';
        drawMode();
      } catch (e) { toast(e.message, true); return; }
    }
    tab = k;
    drawTabs();
    drawBody();
  }

  fill(root, head, toolbar, validationBox, tabs, body);

  // ── body ──
  function drawBody() {
    closeVarHelper();
    if (tab === 'json') {
      finalize(draft.subActions);
      jsonArea = el('textarea', { class: 'code', spellcheck: 'false', style: 'min-height:480px' }, JSON.stringify(draft, null, 2));
      jsonArea.addEventListener('input', markDirty);
      fill(body, el('p', { class: 'muted small' }, 'Shape follows Streamer.bot actions.json. Switch back to Builder to keep editing visually; Save works from either tab.'), jsonArea);
      return;
    }
    fill(body, triggersSection(), stepsSection());
  }

  // ── triggers ──
  function triggersSection() {
    const list = el('div');
    const draw = () => fill(list, draft.triggers.length ? draft.triggers.map((t, i) => triggerCard(t, i, draw)) : el('p', { class: 'muted' }, 'No triggers yet. Without one, run the action from a command, a button, the API or another action.'));
    draw();
    return el('section', { class: 'card' },
      el('div', { class: 'card-head' }, el('h3', {}, 'Triggers'), el('button', { class: 'small', onclick: () => triggerPalette((type) => { draft.triggers.push({ id: uuid(), type, enabled: true }); markDirty(); draw(); }) }, '+ Add trigger')),
      list);
  }

  function triggerCard(t, i, redraw) {
    const info = catalog.events.get(t.type);
    const fields = resolveAll('trigger', t.type, info?.fields || []);
    const ov = overrideFor('trigger', t.type);
    const enabled = el('input', { type: 'checkbox', class: 'switch', title: 'Enabled', 'aria-label': 'Trigger enabled' });
    enabled.checked = t.enabled !== false;
    enabled.addEventListener('change', () => { t.enabled = enabled.checked; markDirty(); });
    return el('div', { class: 'trigger-card' },
      el('div', { class: 'row center' }, enabled, el('strong', { class: 'grow' }, eventLabel(t.type)),
        info && !info.emitted ? el('span', { class: 'chip warn', title: 'CherBot never raises this event; kept for Streamer.bot compatibility' }, 'not raised by CherBot') : null,
        el('span', { class: 'muted small mono' }, `#${t.type}`),
        el('button', { class: 'small ghost danger', 'aria-label': 'Remove trigger', onclick: () => { draft.triggers.splice(i, 1); markDirty(); redraw(); } }, 'Remove')),
      ov.desc ? el('p', { class: 'muted small', style: 'margin:4px 0' }, ov.desc) : null,
      fields.length ? el('div', { class: 'form-grid', style: 'margin-top:6px' }, fields.map(f => renderField(f, t, { action: draft, onChange: markDirty, onCreate: createCommand, refresh: redraw }))) : null);
  }

  // ── steps ──
  function stepsSection() {
    const wrap = el('div');
    const draw = () => fill(wrap, stepList(draft.subActions, null, draw), addButton(draft.subActions, draw));
    draw();
    return el('section', { class: 'card' },
      el('div', { class: 'card-head' }, el('h3', {}, 'Sub-actions'),
        el('div', { class: 'row' },
          el('button', { class: 'small ghost', onclick: () => { (function all(l) { for (const s of l || []) { collapsed.delete(s.id); all(s.subActions); } })(draft.subActions); draw(); } }, 'Expand all'),
          el('button', { class: 'small ghost', onclick: () => { (function all(l) { for (const s of l || []) { if (!LANES.has(s.type)) collapsed.add(s.id); all(s.subActions); } })(draft.subActions); draw(); } }, 'Collapse all'))),
      draft.randomAction ? el('p', { class: 'muted small' }, 'Random mode: one top-level step runs per trigger, chosen by weight.') : null,
      wrap);
  }

  function addButton(list, redraw, label = '+ Add step') {
    return el('button', { class: 'small add-step', onclick: () => stepPalette((type) => { const s = newStep(type); list.push(s); markDirty(); redraw(); }) }, label);
  }

  let dragging = null;
  function stepList(list, parent, redraw) {
    const showWeight = parent ? !!parent.random : !!draft.randomAction;
    return el('ol', { class: 'steps' }, list.map((s, i) => stepCard(s, i, list, redraw, showWeight)));
  }

  function stepCard(s, i, list, redraw, showWeight) {
    const info = catalog.sub.get(s.type);
    const supported = info?.supported || LANES.has(s.type) || s.type === IF || s.type === SWITCH;
    const isCollapsed = collapsed.has(s.id);
    const sum = el('span', { class: 'summary' }, isCollapsed ? summary(s) : '');
    const onChange = (field) => {
      markDirty();
      sum.textContent = collapsed.has(s.id) ? summary(s) : '';
      if (['sceneName', 'sourceName', 'actionId', 'random'].includes(field)) drawStepBody();
    };
    const enabled = el('input', { type: 'checkbox', class: 'switch', title: 'Enabled', 'aria-label': `Enable ${subName(s.type)}` });
    enabled.checked = s.enabled !== false;
    enabled.addEventListener('change', () => { s.enabled = enabled.checked; card.classList.toggle('off', !s.enabled); markDirty(); });
    const weight = showWeight ? el('input', { type: 'number', min: 0, step: 'any', value: s.weight ?? 0, style: 'width:70px', title: 'Weight (random)', 'aria-label': 'Weight' }) : null;
    weight?.addEventListener('input', () => { s.weight = Number(weight.value); markDirty(); });
    const move = (delta) => { const j = i + delta; if (j < 0 || j >= list.length) return; [list[i], list[j]] = [list[j], list[i]]; markDirty(); redraw(); };
    const bodyNode = el('div', { class: 'step-body' });
    const title = s.type === GROUP && s.name ? `Group: ${s.name}` : subName(s.type);

    const card = el('li', { class: `step${s.enabled === false ? ' off' : ''}${supported ? '' : ' unsupported'}`, dataset: { id: s.id } },
      el('div', { class: 'step-head' },
        el('span', { class: 'grip', draggable: 'true', title: 'Drag to reorder', 'aria-hidden': 'true' }, '⠿'),
        enabled,
        el('button', { class: 'icon ghost', 'aria-expanded': String(!isCollapsed), title: isCollapsed ? 'Expand' : 'Collapse', onclick: () => { isCollapsed ? collapsed.delete(s.id) : collapsed.add(s.id); redraw(); } }, isCollapsed ? '▸' : '▾'),
        el('span', { class: 'title' }, title, sum),
        supported ? null : el('span', { class: 'chip warn' }, 'not on this host'),
        weight,
        el('button', { class: 'icon ghost', title: 'Move up', 'aria-label': 'Move up', disabled: i === 0, onclick: () => move(-1) }, '↑'),
        el('button', { class: 'icon ghost', title: 'Move down', 'aria-label': 'Move down', disabled: i === list.length - 1, onclick: () => move(1) }, '↓'),
        el('button', { class: 'icon ghost', title: 'Duplicate', 'aria-label': 'Duplicate step', onclick: () => { const c = clone(s); (function reid(x) { x.id = uuid(); (x.subActions || []).forEach(reid); })(c); list.splice(i + 1, 0, c); markDirty(); redraw(); } }, '⧉'),
        el('button', { class: 'icon ghost danger', title: 'Delete', 'aria-label': 'Delete step', onclick: () => { if (s.subActions?.length && !confirm('Delete this step and everything inside it?')) return; list.splice(i, 1); markDirty(); redraw(); } }, '✕')),
      isCollapsed ? null : bodyNode);

    // drag & drop within the same list
    const grip = card.querySelector('.grip');
    grip.addEventListener('dragstart', (ev) => { dragging = { list, index: i }; ev.dataTransfer.effectAllowed = 'move'; ev.dataTransfer.setData('text/plain', s.id); });
    card.addEventListener('dragover', (ev) => { if (dragging?.list === list && dragging.index !== i) { ev.preventDefault(); card.classList.add('drag-over'); } });
    card.addEventListener('dragleave', () => card.classList.remove('drag-over'));
    card.addEventListener('drop', (ev) => {
      card.classList.remove('drag-over');
      if (dragging?.list !== list) return;
      ev.preventDefault();
      const [item] = list.splice(dragging.index, 1);
      list.splice(i, 0, item);
      dragging = null;
      markDirty();
      redraw();
    });

    function drawStepBody() {
      const fields = resolveAll('sub', s.type, info?.fields || []).filter(f => !(s.type === GROUP && f.name === 'random'));
      const ctx = { action: draft, stepId: s.id, onChange, onCreate: createCommand, refresh: drawStepBody };
      fill(bodyNode,
        describe(s.type) ? el('p', { class: 'desc muted small' }, describe(s.type)) : null,
        supported ? null : el('p', { class: 'warn-text small' }, 'CherBot can\'t run this step; it is kept so the action stays compatible with Streamer.bot.'),
        fields.length ? el('div', { class: 'form-grid' }, fields.map(f => renderField(f, s, ctx))) : null,
        s.type === GROUP ? el('div', { class: 'row', style: 'margin-top:6px' }, checkbox('Random: run one step by weight', s.random, (v) => { s.random = v; markDirty(); drawStepBody(); })) : null,
        lanes(s, drawStepBody));
    }
    if (!isCollapsed) drawStepBody();
    return card;
  }

  /** Nested lists for container steps. */
  function lanes(s, redraw) {
    if (s.type === IF) {
      return [THEN, ELSE].map(type => {
        const lane = s.subActions.find(c => c.type === type);
        return laneBox(type === THEN ? 'Then (condition true)' : 'Else (condition false)', lane, redraw);
      });
    }
    if (s.type === SWITCH) {
      const cases = s.subActions.filter(c => c.type === CASE);
      const def = s.subActions.find(c => c.type === DEFAULT);
      return [
        cases.map((c, i) => laneBox(null, c, redraw, el('span', { class: 'row center grow' }, 'Case',
          renderField({ name: 'values', label: '', widget: 'chips', vars: true }, c, { action: draft, onChange: markDirty }),
          checkbox('Case sensitive', c.caseSensitive, (v) => { c.caseSensitive = v; markDirty(); }),
          el('button', { class: 'small ghost danger', onclick: () => { s.subActions.splice(s.subActions.indexOf(c), 1); markDirty(); redraw(); } }, 'Remove case')))),
        el('button', { class: 'small', style: 'margin-top:6px', onclick: () => { s.subActions.splice(s.subActions.indexOf(def) >= 0 ? s.subActions.indexOf(def) : s.subActions.length, 0, { type: CASE, id: uuid(), enabled: true, values: [''], caseSensitive: false, random: false, subActions: [] }); markDirty(); redraw(); } }, '+ Add case'),
        def ? laneBox('Default (no case matched)', def, redraw)
          : el('button', { class: 'small ghost', onclick: () => { s.subActions.push({ type: DEFAULT, id: uuid(), enabled: true, random: false, subActions: [] }); markDirty(); redraw(); } }, '+ Add default'),
      ];
    }
    if (containerOf(s.type) === 'list') {
      s.subActions ||= [];
      return el('div', { class: 'lane' }, el('div', { class: 'lane-head' }, s.type === WHILE ? 'Repeat while true' : 'Steps'),
        stepList(s.subActions, s, redraw), addButton(s.subActions, redraw));
    }
    return null;
  }

  function laneBox(title, lane, redraw, headExtra) {
    if (!lane) return null;
    lane.subActions ||= [];
    return el('div', { class: 'lane' },
      el('div', { class: 'lane-head' }, title ? el('span', {}, title) : null, headExtra || el('span', { class: 'grow' }),
        checkbox('Random', lane.random, (v) => { lane.random = v; markDirty(); redraw(); })),
      stepList(lane.subActions, lane, redraw), addButton(lane.subActions, redraw));
  }

  // ── palettes ──
  function stepPalette(onPick) {
    const search = el('input', { type: 'search', placeholder: 'Search steps (e.g. scene, message, delay)…', 'aria-label': 'Search steps' });
    const showAll = el('input', { type: 'checkbox' });
    const listNode = el('div', { class: 'palette' });
    const all = [...catalog.sub.values()].filter(s => s.id < 99900 || s.id === GROUP || s.id >= 99998);
    const draw = () => {
      const q = search.value.toLowerCase();
      const items = all.filter(s => (showAll.checked || s.supported) && (!q || `${s.display} ${s.name} ${s.category} ${describe(s.id)}`.toLowerCase().includes(q)));
      const cats = new Map();
      for (const s of items) {
        const c = s.id === GROUP ? 'Core / Logic' : (s.category || 'Other');
        if (!cats.has(c)) cats.set(c, []);
        cats.get(c).push(s);
      }
      fill(listNode, [...cats.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([c, list]) => el('div', { class: 'cat' },
        el('div', { class: 'cat-title' }, c),
        list.sort((a, b) => (a.display || a.name).localeCompare(b.display || b.name)).map(s => el('button', {
          type: 'button', class: 'opt', onclick: () => { closeDialog(); onPick(s.id); },
        }, el('strong', {}, s.display || s.name), el('span', { class: 'd' }, describe(s.id) || (s.supported ? '' : 'not on this host')))))),
        items.length ? null : el('p', { class: 'muted' }, 'No matching steps.'));
    };
    search.addEventListener('input', draw);
    showAll.addEventListener('change', draw);
    openDialog('Add a step', el('div', { class: 'stack' }, el('div', { class: 'row center' }, el('div', { class: 'grow' }, search), el('label', { class: 'inline' }, showAll, 'Show steps CherBot can\'t run')), listNode), { hideOk: true });
    draw();
    search.focus();
  }

  function triggerPalette(onPick) {
    const search = el('input', { type: 'search', placeholder: 'Search triggers (e.g. follow, command, scene)…', 'aria-label': 'Search triggers' });
    const showAll = el('input', { type: 'checkbox' });
    const listNode = el('div', { class: 'palette' });
    const draw = () => {
      const q = search.value.toLowerCase();
      const items = [...catalog.events.values()].filter(e => (showAll.checked || e.emitted) && (!q || `${e.display} ${e.name} ${e.category} ${overrideFor('trigger', e.id).desc || ''}`.toLowerCase().includes(q)));
      const cats = new Map();
      for (const e of items) { const c = e.category || e.source; if (!cats.has(c)) cats.set(c, []); cats.get(c).push(e); }
      fill(listNode, [...cats.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([c, list]) => el('div', { class: 'cat' },
        el('div', { class: 'cat-title' }, c),
        list.map(e => el('button', { type: 'button', class: 'opt', onclick: () => { closeDialog(); onPick(e.id); } },
          el('strong', {}, e.display), el('span', { class: 'd' }, overrideFor('trigger', e.id).desc || (e.emitted ? '' : 'never raised by CherBot')))))));
    };
    search.addEventListener('input', draw);
    showAll.addEventListener('change', draw);
    openDialog('Add a trigger', el('div', { class: 'stack' }, el('div', { class: 'row center' }, el('div', { class: 'grow' }, search), el('label', { class: 'inline' }, showAll, 'Show all (including events CherBot never raises)')), listNode), { hideOk: true });
    draw();
    search.focus();
  }

  function createCommand(kind, done) {
    const cmdName = el('input', { value: '!' });
    const text = el('input', { value: '!' });
    openDialog('New command', el('div', { class: 'stack' },
      el('label', {}, 'Name', cmdName), el('label', {}, 'Command text (one per line for aliases)', text),
      el('p', { class: 'muted small' }, 'Created enabled with a 5 second user cooldown. Fine-tune it on the Commands page.')), {
      okText: 'Create',
      onOk: async () => {
        const c = await post('/v1/commands', { name: cmdName.value, command: text.value || cmdName.value, enabled: true, mode: 0, location: 0, sources: 1, globalCooldown: 0, userCooldown: 5, group: draft.group || '', grantType: 0, permittedGroups: [] });
        invalidate('commands');
        done(c.id);
      },
    });
  }

  // ── validation, save, test, run ──
  async function validate() {
    const problems = [];
    if (!draft.name?.trim()) problems.push('The action needs a name.');
    const ids = { action: new Set(actionsList.map(a => a.id)), command: new Set((await lists.commands()).map(c => c.id)), timer: new Set((await lists.timers()).map(t => t.id)) };
    const checkObj = (kind, obj, label) => {
      const info = kind === 'trigger' ? catalog.events.get(obj.type) : catalog.sub.get(obj.type);
      for (const f of resolveAll(kind, obj.type, info?.fields || [])) {
        const v = obj[f.name];
        if (f.widget === 'picker' && REQUIRED_PICKERS.has(f.picker) && (!v || v === '00000000-0000-0000-0000-000000000000') && kind === 'sub') problems.push(`${label}: choose a ${f.label.toLowerCase()}.`);
        if (f.widget === 'picker' && ids[f.picker] && v && v !== '00000000-0000-0000-0000-000000000000' && !ids[f.picker].has(v) && !(f.picker === 'action' && v === draft.id)) problems.push(`${label}: the ${f.label.toLowerCase()} no longer exists.`);
      }
      if ((obj.type === IF || obj.type === WHILE) && Number(obj.operation) === 3 && obj.value && !String(obj.value).includes('%')) {
        try { new RegExp(obj.value); } catch (e) { problems.push(`${label}: the regex doesn't compile (${e.message}).`); }
      }
    };
    draft.triggers.forEach(t => checkObj('trigger', t, `Trigger "${eventLabel(t.type)}"`));
    (function walk(list) { for (const s of list || []) { if (!LANES.has(s.type)) checkObj('sub', s, `Step "${subName(s.type)}"`); walk(s.subActions); } })(draft.subActions);
    return problems;
  }

  async function save(thenTest) {
    if (tab === 'json') {
      try { const parsed = parseJson(jsonArea.value); Object.keys(draft).forEach(k => delete draft[k]); Object.assign(draft, parsed); draft.subActions = prepare(draft.subActions || []); } catch (e) { toast(e.message, true); return; }
    }
    const problems = await validate();
    if (problems.length && !validationBox.dataset.override) {
      fill(validationBox, el('div', { class: 'validation' }, el('strong', {}, 'Check these before saving:'), el('ul', { class: 'plain' }, problems.map(p => el('li', {}, p))),
        el('button', { class: 'small', onclick: () => { validationBox.dataset.override = '1'; save(thenTest); } }, 'Save anyway')));
      return;
    }
    delete validationBox.dataset.override;
    fill(validationBox);
    const body = clone(draft);
    finalize(body.subActions);
    const saved = body.id ? await put(`/v1/actions/${body.id}`, body) : await post('/v1/actions', body);
    invalidate('actions');
    draft.id = saved.id;
    dirty = false;
    saveBtn.classList.remove('primary');
    status.textContent = `Saved ${new Date().toLocaleTimeString()}`;
    toast('Saved');
    opts.onSaved?.(saved);
    if (thenTest) await test(saved);
  }

  async function test(saved) {
    const first = draft.triggers.find(t => t.enabled !== false) || draft.triggers[0];
    const r = await post(`/v1/actions/${saved.id}/test`, { sampleArgs: first ? exampleArgs(first.type) : {} });
    showRunResult(r.result, `Test (${r.sample === 'none' ? 'no sample data' : r.sample})`);
  }

  async function run() {
    if (!draft.id) { toast('Save the action first', true); return; }
    const r = await post(`/v1/actions/${draft.id}/run`, { args: {}, wait: true });
    showRunResult(r, 'Run');
  }

  async function duplicate() {
    const copyAction = await post(`/v1/actions/${draft.id}/duplicate`);
    invalidate('actions');
    toast(`Created "${copyAction.name}" (disabled)`);
    location.hash = `#/actions/${copyAction.id}`;
  }

  async function exportAction() {
    const r = await post('/v1/export', { actions: [draft.id], name: draft.name });
    showExport(r.export);
  }

  async function remove() {
    if (!confirm(`Delete "${draft.name}"?`)) return;
    await del(`/v1/actions/${draft.id}`);
    invalidate('actions');
    opts.onDeleted?.();
  }

  drawBody();
  return { isDirty: () => dirty };
}

export function showRunResult(r, title = 'Run') {
  openDialog(`${title}: ${r.actionName} — ${r.status}`, el('div', {},
    el('h4', {}, 'Trace'),
    el('ol', { class: 'log' }, (r.trace || []).map(t => el('li', {},
      el('span', { class: `status-${t.status === 'ok' ? 'ok' : t.status === 'error' ? 'error' : 'break'}` }, t.status), ' ',
      `${t.name} (#${t.type}) ${t.ms} ms`, t.error ? el('div', { class: 'error' }, t.error) : null))),
    el('h4', {}, 'Arguments at the end'),
    el('pre', { class: 'mono' }, JSON.stringify(r.args, null, 2))), { hideOk: true, wide: true });
}

export function showExport(text) {
  const area = el('textarea', { class: 'code', readonly: true, style: 'min-height:160px' }, text);
  openDialog('Export string', el('div', {}, el('p', { class: 'muted small' }, 'Paste into CherBot (or Streamer.bot) Import.'), area,
    el('button', { onclick: () => copy(text) }, 'Copy')), { hideOk: true });
}
