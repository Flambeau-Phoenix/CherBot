// A small markdown renderer for the help pages. Builds DOM nodes directly (no HTML strings), so
// help text can never inject markup. Supports headings, paragraphs, lists, fenced code, tables,
// block quotes, rules, and inline code / bold / italic / links.

import { el } from './dom.js';

/** Turns heading text into an anchor id. */
export const slug = (text) => text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

/** Renders inline markdown to nodes. linkMap rewrites hrefs (e.g. other .md files to help routes). */
export function inline(text, linkMap = (h) => h) {
  const out = [];
  const re = /(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*\s][^*]*\*)|(_[^_\s][^_]*_)|(\[[^\]]+\]\([^)\s]+\))/g;
  let last = 0;
  let m;
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const t = m[0];
    if (m[1]) out.push(el('code', {}, t.slice(1, -1)));
    else if (m[2]) out.push(el('strong', {}, inline(t.slice(2, -2), linkMap)));
    else if (m[3] || m[4]) out.push(el('em', {}, inline(t.slice(1, -1), linkMap)));
    else {
      const lm = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(t);
      const href = linkMap(lm[2]);
      const external = /^https?:/i.test(href);
      out.push(el('a', { href, target: external ? '_blank' : null, rel: external ? 'noopener' : null }, inline(lm[1], linkMap)));
    }
    last = m.index + t.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

/** Renders a markdown document. Returns { node, headings: [{ level, text, id }] }. */
export function render(md, { linkMap } = {}) {
  const lines = md.replace(/\r\n?/g, '\n').split('\n');
  const root = el('div', { class: 'md' });
  const headings = [];
  let i = 0;

  const isBlockStart = (l) => /^(#{1,6})\s/.test(l) || /^```/.test(l) || /^\s*([-*+]|\d+\.)\s/.test(l) || /^\|/.test(l) || /^>\s?/.test(l) || /^(-{3,}|\*{3,})\s*$/.test(l);

  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }

    let m;
    if ((m = /^(#{1,6})\s+(.*)$/.exec(line))) {
      const level = m[1].length;
      const id = slug(m[2]);
      headings.push({ level, text: m[2], id });
      root.append(el(`h${Math.min(level + 1, 6)}`, { id }, inline(m[2], linkMap)));
      i++;
      continue;
    }
    if (/^```/.test(line)) {
      const lang = line.slice(3).trim();
      const body = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) body.push(lines[i++]);
      i++;
      root.append(el('pre', { class: `code${lang ? ` lang-${lang}` : ''}` }, el('code', {}, body.join('\n'))));
      continue;
    }
    if (/^(-{3,}|\*{3,})\s*$/.test(line)) { root.append(el('hr')); i++; continue; }
    if (/^>\s?/.test(line)) {
      const body = [];
      while (i < lines.length && /^>\s?/.test(lines[i])) body.push(lines[i++].replace(/^>\s?/, ''));
      root.append(el('blockquote', {}, [...render(body.join('\n'), { linkMap }).node.childNodes]));
      continue;
    }
    if (/^\|/.test(line)) {
      const rows = [];
      while (i < lines.length && /^\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = (r) => r.replace(/^\||\|\s*$/g, '').split('|').map(c => c.trim());
      const head = cells(rows[0]);
      const body = rows.slice(/^\|[\s:|-]+\|?\s*$/.test(rows[1] || '') ? 2 : 1).map(cells);
      root.append(el('table', { class: 'md-table' },
        el('thead', {}, el('tr', {}, head.map(c => el('th', {}, inline(c, linkMap))))),
        el('tbody', {}, body.map(r => el('tr', {}, r.map(c => el('td', {}, inline(c, linkMap))))))));
      continue;
    }
    if (/^\s*([-*+]|\d+\.)\s/.test(line)) {
      root.append(list(lines, () => i, (v) => { i = v; }, linkMap));
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() && !isBlockStart(lines[i])) para.push(lines[i++].trim());
    root.append(el('p', {}, inline(para.join(' '), linkMap)));
  }
  return { node: root, headings };
}

/** Parses a (possibly nested) list starting at the current line. */
function list(lines, getI, setI, linkMap) {
  let i = getI();
  const indentOf = (l) => l.match(/^\s*/)[0].length;
  const baseIndent = indentOf(lines[i]);
  const ordered = /^\s*\d+\./.test(lines[i]);
  const node = el(ordered ? 'ol' : 'ul');
  let current = null;
  while (i < lines.length) {
    const l = lines[i];
    if (!l.trim()) {
      if (i + 1 < lines.length && /^\s*([-*+]|\d+\.)\s/.test(lines[i + 1]) && indentOf(lines[i + 1]) >= baseIndent) { i++; continue; }
      break;
    }
    const m = /^(\s*)([-*+]|\d+\.)\s+(.*)$/.exec(l);
    if (m && m[1].length === baseIndent) {
      current = el('li', {}, inline(m[3], linkMap));
      node.append(current);
      i++;
    } else if (m && m[1].length > baseIndent && current) {
      current.append(list(lines, () => i, (v) => { i = v; }, linkMap));
    } else if (!m && indentOf(l) > baseIndent && current) {
      current.append(' ', ...inline(l.trim(), linkMap));
      i++;
    } else break;
  }
  setI(i);
  return node;
}
