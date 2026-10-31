// Actions: the list (search, groups, enable toggles, pins) and the visual builder for the selected action.

import { el, fill, guard, toast, openDialog, pageHead, toggle, go } from '../lib/dom.js';
import { get, post, invalidate, getUiSettings, saveUiSettings, loadCatalog } from '../lib/api.js';
import { renderEditor, showExport } from '../builder/editor.js';

export async function render(root, route) {
  await loadCatalog();
  const listNode = el('div', { class: 'list', 'aria-label': 'Actions' });
  const detail = el('div', { class: 'detail' });
  const filter = el('input', { type: 'search', placeholder: 'Filter actions…', 'aria-label': 'Filter actions' });
  let actions = [];
  let editor = null;
  let pins = new Set((await getUiSettings()).pinned || []);
  const selectedId = route.rest[0] || null;

  const load = guard(async () => {
    actions = (await get('/v1/actions')).actions;
    drawList();
  });

  function drawList() {
    const q = filter.value.toLowerCase();
    const groups = new Map();
    for (const a of actions.filter(a => !q || a.name.toLowerCase().includes(q) || (a.group || '').toLowerCase().includes(q))) {
      const g = a.group || 'No group';
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push(a);
    }
    const nodes = [];
    for (const [g, list] of [...groups.entries()].sort(([a], [b]) => a.localeCompare(b))) {
      nodes.push(el('div', { class: 'group-head' }, `${g} (${list.length})`));
      for (const a of list) {
        const pinned = pins.has(a.id);
        nodes.push(el('div', {
          class: `item${a.enabled ? '' : ' disabled'}${a.id === route.rest[0] ? ' selected' : ''}`, tabindex: '0', role: 'button',
          onclick: () => open(a.id), onkeydown: (ev) => { if (ev.key === 'Enter') open(a.id); },
        },
        toggle(a.enabled, async (on) => { await post(`/v1/actions/${a.id}/enabled`, { enabled: on }); a.enabled = on; invalidate('actions'); drawList(); }, `Enable ${a.name}`),
        el('span', { class: 'name', title: a.name }, a.name),
        el('span', { class: 'muted small' }, String(a.subActions?.length ?? 0)),
        el('button', {
          class: `star${pinned ? ' on' : ''}`, title: pinned ? 'Unpin from the dashboard' : 'Pin to the dashboard', 'aria-label': pinned ? 'Unpin' : 'Pin',
          onclick: guard(async (ev) => {
            ev.stopPropagation();
            pinned ? pins.delete(a.id) : pins.add(a.id);
            await saveUiSettings({ pinned: [...pins] });
            drawList();
          }),
        }, pinned ? '★' : '☆')));
      }
    }
    fill(listNode, nodes.length ? nodes : el('p', { class: 'muted', style: 'padding:12px' }, 'No actions yet. Create one, use Quick Setup, or import from Streamer.bot.'));
  }

  function open(id) {
    go(`actions/${id}`);
  }

  async function showEditor(id) {
    if (!id) {
      fill(detail, el('div', { class: 'stack' },
        el('h3', {}, 'Pick an action, or start a new one'),
        el('p', { class: 'muted' }, 'Actions are a list of steps started by triggers (a command, a follow, a reward, a timer…). The builder shows every step as a form, and the {x} button next to text fields lists the variables you can use.'),
        el('div', { class: 'row' }, el('button', { class: 'primary', onclick: () => go('actions/new') }, 'New action'), el('a', { href: '#/quicksetup' }, 'Or use a Quick Setup wizard →'))));
      return;
    }
    let action;
    if (id === 'new') action = { name: 'New action', group: '', enabled: true, triggers: [], subActions: [] };
    else {
      try { action = await get(`/v1/actions/${id}`); } catch { fill(detail, el('p', { class: 'muted' }, 'That action no longer exists.')); return; }
    }
    editor = await renderEditor(detail, action, {
      onSaved: (saved) => { if (id === 'new') { history.replaceState(null, '', `#/actions/${saved.id}`); route.rest[0] = saved.id; } load(); },
      onDeleted: () => { editor = null; go('actions'); load(); },
    });
  }

  filter.addEventListener('input', drawList);
  fill(root,
    pageHead('Actions', 'What CherBot does. Each action has triggers (what starts it) and steps (what it does).',
      el('button', { class: 'primary', onclick: () => open('new') }, 'New action'),
      el('button', { onclick: importDialog }, 'Import…'),
      el('button', { onclick: guard(exportAll) }, 'Export all')),
    el('div', { class: 'split' },
      el('div', {}, el('div', { class: 'toolbar' }, filter, el('button', { class: 'ghost small', onclick: load }, 'Refresh')), listNode),
      detail));

  await load();
  await showEditor(selectedId);

  async function exportAll() {
    const [cmds, timers] = await Promise.all([get('/v1/commands'), get('/v1/timers')]);
    const r = await post('/v1/export', { actions: actions.map(a => a.id), commands: cmds.commands.map(c => c.id), timers: timers.timers.map(t => t.timer.id) });
    showExport(r.export);
  }

  function importDialog() {
    const input = el('textarea', { class: 'code', style: 'min-height:200px', placeholder: 'Paste a Streamer.bot export string, actions.json or commands.json…' });
    const file = el('input', { type: 'file', accept: '.json,.txt,.sb' });
    file.addEventListener('change', async () => { if (file.files[0]) input.value = await file.files[0].text(); });
    const overwrite = el('input', { type: 'checkbox', checked: true });
    const report = el('div');
    const runImport = async (dryRun) => {
      const { api } = await import('../lib/api.js');
      const r = await api('POST', `/v1/import?dryRun=${dryRun}&overwrite=${overwrite.checked}`, input.value, { raw: true });
      fill(report, renderImportReport(r));
      if (!dryRun) { toast(`Imported ${r.actions} actions, ${r.commands} commands`); invalidate('actions'); invalidate('commands'); await load(); }
      return true;
    };
    openDialog('Import', el('div', { class: 'stack' }, input, el('div', { class: 'row center' }, file,
      el('label', { class: 'inline' }, overwrite, 'Replace items with the same name'),
      el('button', { type: 'button', onclick: guard(async (ev) => { ev.preventDefault(); await runImport(true); }) }, 'Preview')), report),
    { okText: 'Import', onOk: () => runImport(false), wide: true });
  }

  return {
    isDirty: () => !!editor?.isDirty(),
  };
}

function renderImportReport(r) {
  const unsupported = new Map();
  for (const u of r.unsupported) {
    const key = `${u.kind}: ${u.typeName} (#${u.type})`;
    unsupported.set(key, (unsupported.get(key) || 0) + 1);
  }
  return el('div', {},
    el('p', {}, `${r.dryRun ? 'Preview' : 'Imported'} (${r.format}${r.sourceVersion ? `, from ${r.sourceVersion}` : ''}): ${r.actions} actions, ${r.commands} commands, ${r.queues} queues, ${r.timers} timers.`),
    r.items.length ? el('ul', { class: 'plain' }, r.items.map(i => el('li', {}, `${i.kind} "${i.name}": ${i.status}${i.note ? ` (${i.note})` : ''}`))) : null,
    unsupported.size ? el('div', {}, el('h4', {}, 'Kept but not runnable on this host'), el('ul', { class: 'plain' }, [...unsupported].map(([k, n]) => el('li', {}, `${k} × ${n}`)))) : null,
    r.paths.length ? el('div', {}, el('h4', {}, 'File paths'), el('ul', { class: 'plain' }, r.paths.map(p => el('li', { class: p.exists ? '' : 'error' }, `${p.action}: ${p.original} → ${p.mapped}${p.exists ? '' : ' (missing)'}`)))) : null,
    r.warnings.length ? el('div', {}, el('h4', {}, 'Warnings'), el('ul', { class: 'plain' }, r.warnings.map(w => el('li', {}, w)))) : null);
}
