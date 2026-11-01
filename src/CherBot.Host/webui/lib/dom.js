// DOM helpers shared by every page. Content is always built with el(); innerHTML is never used for data.

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

/** Builds DOM nodes; children may be strings (as text), nodes, arrays or null. */
export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') node.className = v;
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
    else if (k === 'dataset') Object.assign(node.dataset, v);
    else if (k === 'value' && 'value' in node) node.value = v;
    else if (k === 'checked') node.checked = !!v;
    else node.setAttribute(k, v === true ? '' : v);
  }
  append(node, children);
  return node;
}

function append(node, children) {
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

/** Replaces a node's children (null-safe). */
export function fill(node, ...children) {
  node.replaceChildren();
  append(node, children);
  return node;
}

/** Decorative, locally bundled Tabler SVG; the surrounding control supplies its accessible name. */
export function icon(name, className = '') {
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(name)) throw new Error('Invalid icon name');
  return el('span', { class: `svg-icon ${className}`.trim(), 'aria-hidden': 'true', style: `--icon-url: url("icons/${name}.svg")` });
}

export function toast(message, bad = false) {
  const t = $('#toast');
  t.textContent = message;
  t.classList.toggle('bad', bad);
  t.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.remove('show'), bad ? 6000 : 2500);
}

/** Wraps an async handler: errors become a red toast (401 is handled by the login screen). */
export const guard = (fn) => async (...a) => {
  try { return await fn(...a); } catch (e) { if (e?.status !== 401) toast(e?.message || String(e), true); }
};

export const fmtTime = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString(); };
export const fmtDate = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString(); };
export const fmtVal = (v) => (v === null || v === undefined) ? '' : (typeof v === 'object' ? JSON.stringify(v) : String(v));
export const fmtDuration = (seconds) => {
  if (seconds === null || seconds === undefined) return 'until stopped';
  const s = Math.max(0, Math.ceil(seconds));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
};

/** Opens the shared dialog. onOk may return true to keep it open; thrown errors show inside the dialog. */
export function openDialog(title, content, { okText = 'OK', onOk = null, hideOk = false, wide = false } = {}) {
  const dlg = $('#dialog');
  dlg.classList.toggle('wide', wide);
  $('#dialog-title').textContent = title;
  fill($('#dialog-content'), content);
  $('#dialog-error').textContent = '';
  const ok = $('#dialog-ok');
  ok.textContent = okText;
  ok.classList.toggle('hidden', hideOk);
  ok.onclick = async (ev) => {
    if (!onOk) return;
    ev.preventDefault();
    try {
      const keep = await onOk();
      if (keep !== true) dlg.close();
    } catch (e) {
      $('#dialog-error').textContent = e.message || String(e);
    }
  };
  dlg.showModal();
  return dlg;
}

export const closeDialog = () => $('#dialog').close();

export function jsonEditor(value, minHeight = 360) {
  return el('textarea', { class: 'code', spellcheck: 'false', 'aria-label': 'JSON', style: `min-height:${minHeight}px` }, JSON.stringify(value, null, 2));
}

export function parseJson(text) {
  try { return JSON.parse(text); } catch (e) { throw new Error(`Invalid JSON: ${e.message}`); }
}

/** A labelled form control: label text above the control, optional hint below. */
export function field(label, control, hint) {
  return el('label', {}, label, control, hint ? el('span', { class: 'hint' }, hint) : null);
}

export function checkbox(label, checked, onchange, attrs = {}) {
  const box = el('input', { type: 'checkbox', ...attrs });
  box.checked = !!checked;
  if (onchange) box.addEventListener('change', () => onchange(box.checked));
  return el('label', { class: 'inline' }, box, label);
}

/** A switch-style toggle bound to an async setter; reverts on failure. */
export function toggle(checked, onchange, label = 'Enabled') {
  const box = el('input', { type: 'checkbox', class: 'switch', 'aria-label': label, title: label });
  box.checked = !!checked;
  box.addEventListener('click', (ev) => ev.stopPropagation());
  box.addEventListener('change', async () => {
    try { await onchange(box.checked); } catch (e) { box.checked = !box.checked; toast(e.message || String(e), true); }
  });
  return box;
}

export function select(options, value, attrs = {}) {
  const s = el('select', attrs, options.map(o => {
    const [v, label] = Array.isArray(o) ? o : typeof o === 'object' ? [o.value, o.label] : [o, o];
    return el('option', { value: String(v) }, label);
  }));
  s.value = value === undefined || value === null ? '' : String(value);
  return s;
}

export function input(value, attrs = {}) {
  const i = el('input', attrs);
  i.value = value === undefined || value === null ? '' : String(value);
  return i;
}

export function num(value, attrs = {}) { return input(value, { type: 'number', ...attrs }); }

export function card(title, actions, ...body) {
  return el('section', { class: 'card' },
    el('div', { class: 'card-head' }, el('h3', {}, title), actions ? el('div', { class: 'row' }, actions) : null),
    body);
}

export function empty(text) { return el('p', { class: 'muted empty' }, text); }

export function debounce(fn, ms = 300) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

export async function copy(text) {
  try {
    if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
    await navigator.clipboard.writeText(text);
    toast('Copied');
  } catch {
    window.prompt('Automatic copy is unavailable. Copy this text:', text);
  }
}

/** Inserts text at the caret of an input or textarea and fires an input event. */
export function insertAtCursor(target, text) {
  const start = target.selectionStart ?? target.value.length;
  const end = target.selectionEnd ?? target.value.length;
  target.value = target.value.slice(0, start) + text + target.value.slice(end);
  target.selectionStart = target.selectionEnd = start + text.length;
  target.dispatchEvent(new Event('input', { bubbles: true }));
  target.focus();
}

/** Navigates to a page route such as "actions/<id>". */
export const go = (path) => { location.hash = `#/${path.replace(/^[#/]+/, '')}`; };

/** Page header with a title, a description and optional buttons. */
export function pageHead(title, description, ...actions) {
  return el('div', { class: 'page-head' },
    el('div', {}, el('h2', {}, title), description ? el('p', { class: 'muted' }, description) : null),
    actions.length ? el('div', { class: 'row wrap' }, actions) : null);
}

export const uuid = () => crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
  const r = Math.random() * 16 | 0;
  return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
});
