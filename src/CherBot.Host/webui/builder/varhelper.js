// The {x} variable helper: a popover listing trigger variables, values set earlier in the action,
// always-available arguments, persisted globals and format examples. Clicking inserts at the cursor.

import { el, fill, insertAtCursor, fmtVal } from '../lib/dom.js';
import { get, lists, cached, eventLabel, subName } from '../lib/api.js';
import { eventVariables, alwaysAvailable, formats } from '../meta/variables.js';
import { outputsOf } from '../meta/fields.js';

let open = null;

export function closeVarHelper() {
  open?.remove();
  open = null;
  document.removeEventListener('mousedown', outside, true);
  document.removeEventListener('keydown', escape, true);
}
const outside = (ev) => { if (open && !open.contains(ev.target) && !ev.target.closest?.('.var-btn')) closeVarHelper(); };
const escape = (ev) => { if (ev.key === 'Escape') { closeVarHelper(); ev.stopPropagation(); } };

const sample = (typeId) => cached(`sample.${typeId}`, async () => {
  try { return (await get(`/v1/events/sample?typeId=${typeId}`)).data || {}; } catch { return null; }
}, 30000);

/** Variables set by steps before `stepId` (outputs plus variableName-style fields). */
export function earlierVariables(action, stepId) {
  const out = [];
  let done = false;
  const walk = (list) => {
    for (const s of list || []) {
      if (done) return;
      if (s.id === stepId) { done = true; return; }
      const by = `set by ${subName(s.type)}`;
      for (const o of outputsOf(s.type)) out.push({ name: o, desc: by });
      for (const key of ['variableName', 'destinationVariable', 'saveToVariable', 'outputVariable']) {
        const val = s[key];
        if (typeof val !== 'string' || !val || val.includes('%')) continue;
        if (s.type === 122) continue; // Global (Set) writes a global, not an argument
        if (s.type === 121 && key === 'variableName' && s.destinationVariable) continue;
        out.push({ name: val, desc: by });
      }
      walk(s.subActions);
    }
  };
  walk(action?.subActions);
  return [...new Map(out.filter(o => o.name).map(o => [o.name, o])).values()];
}

/**
 * Opens the helper next to `anchor`, inserting into `target`.
 * ctx: { action, stepId, mode: 'args' | 'tokens', tokens: [{name, desc}] }
 */
export function openVarHelper(anchor, target, ctx = {}) {
  if (open) { closeVarHelper(); return; }
  const search = el('input', { type: 'search', placeholder: 'Search variables…', 'aria-label': 'Search variables' });
  const listNode = el('div');
  const pop = el('div', { class: 'popover', role: 'dialog', 'aria-label': 'Insert a variable' }, search, listNode);
  document.getElementById('popover-root').append(pop);
  open = pop;
  const r = anchor.getBoundingClientRect();
  const place = () => {
    const h = pop.offsetHeight;
    const below = r.bottom + 4;
    pop.style.top = `${below + h > window.innerHeight - 8 ? Math.max(8, r.top - h - 4) : below}px`;
    pop.style.left = `${Math.max(8, Math.min(r.right - 380, window.innerWidth - 390))}px`;
  };
  setTimeout(() => {
    document.addEventListener('mousedown', outside, true);
    document.addEventListener('keydown', escape, true);
  });
  search.focus();

  const item = (v) => ({ insert: `%${v.name}%`, label: `%${v.name}%`, desc: v.example ? `${v.desc} — e.g. ${String(v.example).slice(0, 40)}` : v.desc });
  const groups = [];
  if (ctx.tokens?.length) groups.push({ title: 'Placeholders', items: ctx.tokens.map(t => ({ insert: `{${t.name}}`, label: `{${t.name}}`, desc: t.desc })) });

  const pending = [];
  if (ctx.mode !== 'tokens-only') {
    const triggerTypes = [...new Set((ctx.action?.triggers || []).map(t => t.type)), ...(ctx.eventTypes || [])];
    for (const typeId of triggerTypes) {
      const known = eventVariables[typeId] || [];
      const group = { title: `Trigger: ${eventLabel(typeId)}`, items: known.map(item) };
      groups.push(group);
      pending.push(sample(typeId).then(live => {
        if (!live) return;
        const names = new Map(known.map(v => [v.name, { ...v, example: v.name in live ? fmtVal(live[v.name]) : v.example }]));
        for (const [k, val] of Object.entries(live)) if (!names.has(k) && k !== 'args') names.set(k, { name: k, desc: 'from the latest real event', example: fmtVal(val) });
        group.title += ' (live sample)';
        group.items = [...names.values()].map(item);
      }));
    }
    if (ctx.action) {
      if (!triggerTypes.length) groups.push({ title: 'Trigger', items: [], note: 'Add a trigger to see its variables.' });
      const earlier = earlierVariables(ctx.action, ctx.stepId);
      groups.push({ title: 'Set earlier in this action', items: earlier.map(v => ({ insert: `%${v.name}%`, label: `%${v.name}%`, desc: v.desc })), note: earlier.length ? null : 'Nothing yet.' });
    }
    groups.push({ title: 'Always available', items: alwaysAvailable.map(v => ({ insert: `%${v.name}%`, label: `%${v.name}%`, desc: v.desc })) });
    const globalsGroup = { title: 'Persisted globals', items: [], note: 'Loading…' };
    groups.push(globalsGroup);
    pending.push(lists.globals().catch(() => []).then(globals => {
      globalsGroup.items = globals.map(g => ({ insert: `~${g.name}~`, label: `~${g.name}~`, desc: fmtVal(g.value).slice(0, 40) }));
      globalsGroup.note = globals.length ? null : 'No globals yet.';
    }));
    groups.push({ title: 'Formats', items: formats.map(f => ({ insert: f.name, label: f.name, desc: `${f.desc}${f.example ? ` — ${f.example}` : ''}` })) });
  }

  const draw = () => {
    const q = search.value.toLowerCase();
    fill(listNode, groups.map(g => {
      const items = g.items.filter(i => !q || i.label.toLowerCase().includes(q) || (i.desc || '').toLowerCase().includes(q));
      if (q && !items.length) return null;
      return [el('div', { class: 'grp' }, g.title),
        items.length ? items.map(i => el('button', {
          type: 'button', class: 'var', title: i.desc || '',
          onclick: () => { insertAtCursor(target, i.insert); closeVarHelper(); },
        }, el('code', {}, i.label), el('span', { class: 'd' }, i.desc || ''))) : el('div', { class: 'muted small', style: 'padding:2px 6px' }, g.note || '')];
    }));
  };
  search.addEventListener('input', draw);
  search.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter') { ev.preventDefault(); listNode.querySelector('.var')?.click(); }
  });
  draw();
  place();
  for (const p of pending) p.then(() => { if (open === pop) { draw(); place(); } });
}

/** A small "{x}" button that opens the helper for `target`. ctxFn returns the current context. */
export function varButton(target, ctxFn = () => ({})) {
  return el('button', {
    type: 'button', class: 'var-btn small', title: 'Insert a variable', 'aria-label': 'Insert a variable',
    onclick: (ev) => { ev.preventDefault(); openVarHelper(ev.currentTarget, target, ctxFn()); },
  }, '{x}');
}
