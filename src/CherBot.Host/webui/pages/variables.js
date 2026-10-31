// Variables: globals (persisted and temporary), the user-globals browser, and a searchable variable reference.

import { el, fill, guard, toast, card, pageHead, fmtVal, fmtDate, openDialog, select, input, copy, debounce } from '../lib/dom.js';
import { get, put, api, loadCatalog, catalog, invalidate } from '../lib/api.js';
import { eventVariables, alwaysAvailable, formats } from '../meta/variables.js';
import { overrides } from '../meta/overrides.js';

const typeOf = (v) => v === null || v === undefined ? 'text' : typeof v === 'number' ? 'number' : typeof v === 'boolean' ? 'bool' : typeof v === 'object' ? 'json' : 'text';
const show = (v) => typeof v === 'string' ? v : JSON.stringify(v);

/** Typed value editor: returns { node, read() }. */
function valueEditor(value) {
  const kind = select([['text', 'Text'], ['number', 'Number'], ['bool', 'True / false'], ['json', 'JSON (list or object)']], typeOf(value));
  const text = el('textarea', { rows: 3 });
  text.value = value === undefined || value === null ? '' : show(value);
  const read = () => {
    const raw = text.value;
    switch (kind.value) {
      case 'number': { const n = Number(raw); if (raw.trim() === '' || isNaN(n)) throw new Error('Enter a number'); return n; }
      case 'bool': return /^(true|1|yes|on)$/i.test(raw.trim());
      case 'json': try { return JSON.parse(raw); } catch (e) { throw new Error(`Invalid JSON: ${e.message}`); }
      default: return raw;
    }
  };
  return { node: el('div', { class: 'stack' }, el('label', {}, 'Type', kind), el('label', {}, 'Value', text)), read };
}

export async function render(root) {
  await loadCatalog();
  const globalsBody = el('tbody');
  const temp = el('input', { type: 'checkbox' });
  const userBody = el('tbody');
  const platform = select([['twitch', 'Twitch']], 'twitch');
  const userName = input('', { placeholder: 'Variable name (exact)' });
  const userId = input('', { placeholder: 'User id (optional)' });
  const userTemp = el('input', { type: 'checkbox' });
  const refSearch = el('input', { type: 'search', placeholder: 'Search variables (e.g. user, reward, scene)…', 'aria-label': 'Search variable reference' });
  const refNode = el('div');

  const loadGlobals = guard(async () => {
    const persisted = !temp.checked;
    const r = await get(`/v1/globals?persisted=${persisted}`);
    fill(globalsBody, r.globals.length ? r.globals.map(g => el('tr', {},
      el('td', { class: 'mono' }, g.name), el('td', { class: 'mono' }, fmtVal(g.value)), el('td', { class: 'muted small' }, typeOf(g.value)), el('td', { class: 'small' }, fmtDate(g.lastWrite)),
      el('td', { class: 'actions' },
        el('button', { class: 'small', onclick: () => editGlobal(g.name, g.value, persisted) }, 'Edit'), ' ',
        el('button', { class: 'small ghost', title: 'Copy ~name~', onclick: () => copy(`~${g.name}~`) }, 'Copy'), ' ',
        el('button', { class: 'small ghost danger', onclick: guard(async () => { if (!confirm(`Delete ${g.name}?`)) return; await api('DELETE', `/v1/globals/${encodeURIComponent(g.name)}?persisted=${persisted}`); invalidate('globals'); loadGlobals(); }) }, 'Delete'))))
      : el('tr', {}, el('td', { colspan: 5, class: 'muted' }, `No ${persisted ? 'persisted' : 'temporary'} globals.`)));
  });

  function editGlobal(name, value, persisted) {
    const nameInput = input(name || '', { placeholder: 'e.g. deaths' });
    const editor = valueEditor(value ?? '');
    const keep = el('input', { type: 'checkbox' });
    keep.checked = persisted;
    openDialog(name ? `Edit ${name}` : 'New global', el('div', { class: 'stack' },
      el('label', {}, 'Name', nameInput), editor.node, el('label', { class: 'inline' }, keep, 'Persisted (survives restarts)'),
      el('p', { class: 'muted small' }, 'Use it in any text field as ~name~, or read it with a Global (Get) step.')), {
      okText: 'Save',
      onOk: async () => {
        if (!nameInput.value.trim()) throw new Error('Name is required');
        await put(`/v1/globals/${encodeURIComponent(nameInput.value.trim())}`, { value: editor.read(), persisted: keep.checked });
        invalidate('globals');
        toast('Saved');
        loadGlobals();
      },
    });
  }

  const loadUsers = guard(async () => {
    const q = new URLSearchParams({ persisted: String(!userTemp.checked) });
    if (userName.value.trim()) q.set('name', userName.value.trim());
    if (userId.value.trim()) q.set('userId', userId.value.trim());
    const r = await get(`/v1/users/${platform.value}/globals?${q}`);
    fill(userBody, r.variables.length ? r.variables.slice(0, 500).map(v => el('tr', {},
      el('td', {}, v.userName || v.userLogin || v.userId, el('div', { class: 'muted small' }, v.userId)),
      el('td', { class: 'mono' }, v.name), el('td', { class: 'mono' }, fmtVal(v.value)), el('td', { class: 'small' }, fmtDate(v.lastWrite)),
      el('td', { class: 'actions' },
        el('button', { class: 'small', onclick: () => editUser(v) }, 'Edit'), ' ',
        el('button', { class: 'small ghost danger', onclick: guard(async () => {
          if (!confirm(`Delete ${v.name} for ${v.userName || v.userId}?`)) return;
          await api('DELETE', `/v1/users/${v.platform}/${encodeURIComponent(v.userId)}/globals/${encodeURIComponent(v.name)}?persisted=${v.persisted}`);
          loadUsers();
        }) }, 'Delete'))))
      : el('tr', {}, el('td', { colspan: 5, class: 'muted' }, 'No user globals match.')));
  });

  function editUser(v) {
    const editor = valueEditor(v.value);
    openDialog(`${v.name} for ${v.userName || v.userId}`, editor.node, {
      okText: 'Save',
      onOk: async () => {
        await put(`/v1/users/${v.platform}/${encodeURIComponent(v.userId)}/globals/${encodeURIComponent(v.name)}`,
          { value: editor.read(), persisted: v.persisted, userLogin: v.userLogin, userName: v.userName });
        toast('Saved');
        loadUsers();
      },
    });
  }

  // reference
  const reference = [];
  for (const v of alwaysAvailable) reference.push({ group: 'Always available', text: `%${v.name}%`, desc: v.desc });
  for (const [id, vars] of Object.entries(eventVariables)) {
    const ev = catalog.events.get(Number(id));
    if (!ev) continue;
    for (const v of vars) reference.push({ group: `Trigger: ${ev.source} ${ev.display}`, text: `%${v.name}%`, desc: `${v.desc}${v.example ? ` — e.g. ${v.example}` : ''}` });
  }
  for (const [id, o] of Object.entries(overrides)) {
    if (!o.outputs?.length) continue;
    const s = catalog.sub.get(Number(id));
    for (const out of o.outputs) reference.push({ group: `Step: ${s?.display || s?.name || id}`, text: `%${out}%`, desc: 'set by this step' });
  }
  for (const f of formats) reference.push({ group: 'Formats', text: f.name, desc: `${f.desc}${f.example ? ` — ${f.example}` : ''}` });
  const drawRef = () => {
    const q = refSearch.value.toLowerCase();
    const items = reference.filter(r => !q || r.text.toLowerCase().includes(q) || r.desc.toLowerCase().includes(q) || r.group.toLowerCase().includes(q));
    const groups = new Map();
    for (const r of items.slice(0, 600)) { if (!groups.has(r.group)) groups.set(r.group, []); groups.get(r.group).push(r); }
    fill(refNode, groups.size ? [...groups.entries()].map(([g, list]) => el('details', { class: 'section', open: q ? true : null },
      el('summary', {}, g, el('span', { class: 'muted small' }, `(${list.length})`)),
      el('div', { class: 'body' }, el('table', {}, el('tbody', {}, list.map(r => el('tr', {},
        el('td', { class: 'mono', style: 'width:40%' }, r.text), el('td', { class: 'small' }, r.desc),
        el('td', { class: 'actions' }, el('button', { class: 'small ghost', onclick: () => copy(r.text) }, 'Copy')))))))))
      : el('p', { class: 'muted' }, 'Nothing matches.'));
  };
  refSearch.addEventListener('input', debounce(drawRef, 200));

  temp.addEventListener('change', loadGlobals);
  fill(root,
    pageHead('Variables', 'Globals keep values between runs (~name~ in text). Arguments (%name%) exist only during one run.'),
    el('div', { class: 'grid-2' },
      card('Globals', [el('label', { class: 'inline' }, temp, 'Show temporary'), el('button', { class: 'small primary', onclick: () => editGlobal('', '', !temp.checked) }, 'New global')],
        el('div', { class: 'scroll' }, el('table', {}, el('thead', {}, el('tr', {}, ['Name', 'Value', 'Type', 'Last write', ''].map(h => el('th', {}, h)))), globalsBody))),
      card('User globals', null,
        el('form', { class: 'row wrap', onsubmit: (ev) => { ev.preventDefault(); loadUsers(); } },
          el('label', {}, 'Platform', platform), el('label', { class: 'grow' }, 'Name', userName), el('label', {}, 'User id', userId),
          el('label', { class: 'inline' }, userTemp, 'Temporary'), el('button', { type: 'submit' }, 'Search')),
        el('div', { class: 'scroll', style: 'margin-top:8px' }, el('table', {}, el('thead', {}, el('tr', {}, ['User', 'Name', 'Value', 'Last write', ''].map(h => el('th', {}, h)))), userBody)))),
    card('Variable reference', [refSearch], el('p', { class: 'muted small' }, 'Every argument CherBot knows about, by trigger and by step. In the builder, the {x} button next to a text field shows the ones that apply to that spot.'), refNode));

  loadGlobals();
  loadUsers();
  drawRef();
}
