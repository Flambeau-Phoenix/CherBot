// Help: how-to guides (webui/help/*.md), the two reference documents, generated step and event
// references, and search across all of them.

import { el, fill, pageHead, debounce } from '../lib/dom.js';
import { loadCatalog, catalog } from '../lib/api.js';
import { render as renderMd, slug } from '../lib/markdown.js';
import { overrides, triggerOverrides } from '../meta/overrides.js';
import { eventVariables } from '../meta/variables.js';
import { parseFields, labelFor } from '../meta/fields.js';

export const GUIDES = [
  ['Start here', [
    ['getting-started', 'Getting started'],
    ['first-command', 'Build your first command'],
    ['builder', 'The action builder'],
    ['variables', 'Variables and templates'],
  ]],
  ['How-to', [
    ['obs-scenes', 'Chat → OBS scenes'],
    ['rewards', 'Channel point rewards'],
    ['alerts', 'Alerts and sounds'],
    ['custom-commands', 'Custom commands'],
    ['quick-setup', 'Quick Setup wizards'],
    ['loyalty-games', 'Loyalty points and games'],
    ['giveaways', 'Running a raffle or giveaway'],
    ['modules', 'Modules: alerts, pyramids, spam filter'],
    ['queues', 'Queues'],
    ['csharp', 'C# scripts'],
    ['troubleshooting', 'Troubleshooting'],
  ]],
  ['Reference', [
    ['ref-steps', 'All steps (sub-actions)'],
    ['ref-events', 'All triggers and their variables'],
    ['docs/operating-guide', 'Operating guide'],
    ['docs/configuration-reference', 'Configuration reference'],
  ]],
];

const ALL = GUIDES.flatMap(([, items]) => items);
const titleOf = (id) => ALL.find(([k]) => k === id)?.[1] || id;
const cache = new Map();

async function fetchMd(id) {
  if (!cache.has(id)) cache.set(id, fetch(`help/${id}.md`).then(r => (r.ok ? r.text() : `# Not found\n\nThe page "${id}" is missing.`)));
  return cache.get(id);
}

/** help/foo.md links and ../docs links become help routes. */
const linkMap = (href) => {
  if (/^https?:|^#/.test(href)) return href;
  const m = /([\w-]+)\.md(#.*)?$/.exec(href);
  if (!m) return href;
  const id = /operating-guide|configuration-reference/.test(m[1]) ? `docs/${m[1]}` : m[1];
  return `#/help/${id}`;
};

function stepsReference() {
  const cats = new Map();
  for (const s of [...catalog.sub.values()].filter(s => s.supported && (s.id < 99900 || s.id >= 99998 || s.id === 99900))) {
    const c = s.category || 'Core / Logic';
    if (!cats.has(c)) cats.set(c, []);
    cats.get(c).push(s);
  }
  const lines = ['# All steps', '', 'Every step CherBot can run, by category. Fields are what the builder shows; outputs are arguments the step sets for later steps.', ''];
  for (const [c, list] of [...cats.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    lines.push(`## ${c}`, '');
    for (const s of list.sort((a, b) => (a.display || a.name).localeCompare(b.display || b.name))) {
      const o = overrides[s.id] || {};
      const fields = parseFields(s.fields).filter(f => !['subActions', 'device', 'weight', 'id', 'index', 'parentId', 'enabled'].includes(f.name)).map(f => o.fields?.[f.name]?.label || labelFor(f.name));
      lines.push(`### ${s.display || s.name}`, '', `${o.desc || ''} (type ${s.id})`, '');
      if (fields.length) lines.push(`- **Fields:** ${fields.join(', ')}`);
      if (o.outputs?.length) lines.push(`- **Sets:** ${o.outputs.map(x => `\`%${x}%\``).join(', ')}`);
      lines.push('');
    }
  }
  return lines.join('\n');
}

function eventsReference() {
  const lines = ['# All triggers', '', 'Triggers CherBot raises, with the arguments each one gives your action. Filters are the options on the trigger card.', ''];
  const bySource = new Map();
  for (const e of [...catalog.events.values()].filter(e => e.emitted)) {
    if (!bySource.has(e.source)) bySource.set(e.source, []);
    bySource.get(e.source).push(e);
  }
  for (const [src, list] of [...bySource.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    lines.push(`## ${src}`, '');
    for (const e of list) {
      const t = triggerOverrides[e.id] || {};
      lines.push(`### ${e.display}`, '', `${t.desc || ''} (type ${e.id})`, '');
      const filters = parseFields(e.fields).map(f => t.fields?.[f.name]?.label || labelFor(f.name));
      if (filters.length) lines.push(`- **Filters:** ${filters.join(', ')}`);
      const vars = eventVariables[e.id] || [];
      if (vars.length) {
        lines.push('', '| Variable | Meaning | Example |', '| --- | --- | --- |');
        for (const v of vars) lines.push(`| \`%${v.name}%\` | ${v.desc} | ${v.example ? `\`${String(v.example).replace(/\|/g, '/')}\`` : ''} |`);
      }
      lines.push('');
    }
  }
  return lines.join('\n');
}

async function source(id) {
  if (id === 'ref-steps') return stepsReference();
  if (id === 'ref-events') return eventsReference();
  return fetchMd(id);
}

export async function render(root, route) {
  await loadCatalog();
  const id = route.rest.join('/') || 'getting-started';
  const search = el('input', { type: 'search', placeholder: 'Search help…', 'aria-label': 'Search help' });
  const nav = el('nav', { class: 'help-nav card' });
  const content = el('article', { class: 'card' });

  const drawNav = () => fill(nav, search, GUIDES.map(([group, items]) => [
    el('div', { class: 'grp' }, group),
    items.map(([k, t]) => el('a', { href: `#/help/${k}`, class: k === id ? 'active' : '' }, t)),
  ]));
  drawNav();

  const showPage = async () => {
    const md = await source(id);
    const { node, headings } = renderMd(md, { linkMap });
    const toc = headings.filter(h => h.level === 2);
    fill(content, toc.length > 3 ? el('details', { class: 'section', style: 'margin-bottom:12px' }, el('summary', {}, 'On this page'),
      el('div', { class: 'body' }, toc.map(h => el('div', {}, el('a', { href: `#/help/${id}`, onclick: (ev) => { ev.preventDefault(); document.getElementById(h.id)?.scrollIntoView({ behavior: 'smooth' }); } }, h.text))))) : null, node);
  };

  const runSearch = debounce(async () => {
    const q = search.value.trim().toLowerCase();
    if (q.length < 2) { showPage(); return; }
    const results = [];
    const plain = (md) => md.replace(/\[([^\]]+)\]\([^)]+\)/g, '$1').replace(/^#+\s*/gm, '').replace(/[`*|]/g, ' ').replace(/^\s*[-:]{3,}\s*$/gm, '');
    for (const [k, t] of ALL) {
      const text = plain(await source(k));
      const lower = text.toLowerCase();
      let at = lower.indexOf(q);
      let hits = 0;
      const snippets = [];
      while (at >= 0 && hits < 3) {
        const start = Math.max(0, at - 70);
        snippets.push(text.slice(start, at + q.length + 90).replace(/\s+/g, ' '));
        hits++;
        at = lower.indexOf(q, at + q.length);
      }
      if (snippets.length) results.push({ k, t, snippets });
    }
    const mark = (s) => {
      const i = s.toLowerCase().indexOf(q);
      return i < 0 ? s : [s.slice(0, i), el('mark', {}, s.slice(i, i + q.length)), s.slice(i + q.length)];
    };
    fill(content, el('h2', {}, `Results for "${search.value}"`),
      results.length ? results.map(r => el('div', { style: 'margin-bottom:12px' }, el('a', { href: `#/help/${r.k}` }, el('strong', {}, r.t)),
        r.snippets.map(s => el('div', { class: 'muted small' }, '…', mark(s), '…')))) : el('p', { class: 'muted' }, 'Nothing found.'));
  }, 250);
  search.addEventListener('input', runSearch);

  fill(root, pageHead('Help', `${titleOf(id)}`), el('div', { class: 'help' }, nav, content));
  await showPage();
  window.scrollTo(0, 0);
}

export { slug };
