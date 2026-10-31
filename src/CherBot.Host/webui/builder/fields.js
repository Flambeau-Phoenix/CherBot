// Renders one form control for a resolved field (see meta/fields.js). Controls write straight into the
// edited object and call onChange(name) so the editor can refresh dependent pickers.

import { el, fill, debounce, uuid } from '../lib/dom.js';
import { get } from '../lib/api.js';
import { pickerOptions, isIdPicker } from './pickers.js';
import { varButton } from './varhelper.js';

const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/** Base64 (UTF-8) helpers for the C# code box. */
export const b64decode = (s) => { try { return new TextDecoder().decode(Uint8Array.from(atob(s || ''), c => c.charCodeAt(0))); } catch { return s || ''; } };
export const b64encode = (s) => { const bytes = new TextEncoder().encode(s); let bin = ''; bytes.forEach(b => { bin += String.fromCharCode(b); }); return btoa(bin); };

/**
 * desc: resolved field; obj: the step or trigger being edited; ctx: { action, stepId, onChange(name) }
 * Returns a <label> (or a wrapper) containing the control.
 */
export function renderField(desc, obj, ctx = {}) {
  const name = desc.name;
  const changed = () => ctx.onChange?.(name);
  const set = (v) => { obj[name] = v; changed(); };
  const varCtx = () => ({ action: ctx.action, stepId: ctx.stepId });
  const withVars = (control) => desc.vars === false ? control : el('div', { class: 'field-wrap' }, control, varButton(control, varCtx));
  const label = (control, extraClass = '') => el('label', { class: extraClass },
    desc.label, control, desc.hint ? el('span', { class: 'hint' }, desc.hint) : null);

  switch (desc.widget) {
    case 'checkbox': {
      const box = el('input', { type: 'checkbox' });
      box.checked = !!obj[name];
      box.addEventListener('change', () => set(box.checked));
      return el('label', { class: 'inline', title: desc.hint || '' }, box, desc.label);
    }
    case 'number': {
      const i = el('input', { type: 'number', step: desc.step ?? 'any', min: desc.min, max: desc.max });
      i.value = obj[name] ?? '';
      i.addEventListener('input', () => set(i.value === '' ? 0 : Number(i.value)));
      return label(i);
    }
    case 'range': {
      const r = el('input', { type: 'range', min: desc.min ?? 0, max: desc.max ?? 1, step: desc.step ?? 0.01 });
      const n = el('input', { type: 'number', min: desc.min ?? 0, max: desc.max ?? 1, step: desc.step ?? 0.01, style: 'width:80px' });
      r.value = n.value = obj[name] ?? desc.max ?? 1;
      r.addEventListener('input', () => { n.value = r.value; set(Number(r.value)); });
      n.addEventListener('input', () => { r.value = n.value; set(Number(n.value)); });
      return label(el('div', { class: 'row center' }, r, n));
    }
    case 'select': {
      const s = el('select', {}, desc.options.map(([v, l]) => el('option', { value: String(v) }, l)));
      const current = obj[name] ?? desc.options[0]?.[0];
      if (!desc.options.some(([v]) => String(v) === String(current)) && current !== undefined && current !== '') s.append(el('option', { value: String(current) }, `${current} (custom)`));
      s.value = String(current ?? '');
      if (obj[name] === undefined && desc.options.length) obj[name] = desc.options[0][0];
      s.addEventListener('change', () => {
        const opt = desc.options.find(([v]) => String(v) === s.value);
        set(opt ? opt[0] : (isNaN(Number(s.value)) ? s.value : Number(s.value)));
      });
      return label(s);
    }
    case 'textarea': case 'object': case 'text': case 'file': {
      const multi = desc.widget === 'textarea';
      const i = multi ? el('textarea', { rows: 3, placeholder: desc.placeholder || '' }) : el('input', { type: 'text', placeholder: desc.placeholder || '' });
      i.value = obj[name] === undefined || obj[name] === null ? '' : (typeof obj[name] === 'object' ? JSON.stringify(obj[name]) : String(obj[name]));
      i.addEventListener('input', () => set(i.value));
      return label(withVars(i), multi ? 'full' : '');
    }
    case 'code': {
      const area = el('textarea', { class: 'code', spellcheck: 'false', style: 'min-height:220px' });
      const usesCode = desc.base64 && typeof obj.code === 'string';
      area.value = desc.base64 ? (usesCode ? obj.code : b64decode(obj[name])) : (obj[name] ?? '');
      area.addEventListener('keydown', (ev) => {
        if (ev.key === 'Tab') { ev.preventDefault(); const p = area.selectionStart; area.value = area.value.slice(0, p) + '    ' + area.value.slice(area.selectionEnd); area.selectionStart = area.selectionEnd = p + 4; area.dispatchEvent(new Event('input')); }
      });
      area.addEventListener('input', () => {
        if (!desc.base64) return set(area.value);
        delete obj.code;
        set(b64encode(area.value));
      });
      return label(area, 'full');
    }
    case 'color': {
      const text = el('input', { type: 'text', placeholder: '#RRGGBB', style: 'width:110px' });
      const pick = el('input', { type: 'color' });
      text.value = obj[name] ?? '';
      if (/^#[0-9a-f]{6}$/i.test(text.value)) pick.value = text.value;
      pick.addEventListener('input', () => { text.value = pick.value; set(pick.value); });
      text.addEventListener('input', () => { if (/^#[0-9a-f]{6}$/i.test(text.value)) pick.value = text.value; set(text.value); });
      return label(el('div', { class: 'row center' }, pick, text));
    }
    case 'chips': return label(chipInput(obj, name, desc, ctx, changed), 'full');
    case 'kv': return label(kvEditor(obj, name, changed, varCtx), 'full');
    case 'key': return label(keyCapture(obj, name, desc, changed));
    case 'category': return label(categoryInput(obj, name, changed));
    case 'picker': return label(pickerControl(desc, obj, ctx, changed, withVars));
    default: {
      const area = el('textarea', { class: 'code', style: 'min-height:60px' }, JSON.stringify(obj[name] ?? null));
      area.addEventListener('input', () => { try { obj[name] = JSON.parse(area.value); changed(); area.classList.remove('bad'); } catch { area.classList.add('bad'); } });
      return label(area, 'full');
    }
  }
}

function pickerControl(desc, obj, ctx, changed, withVars) {
  const name = desc.name;
  const pickerCtx = { values: obj, action: ctx.action };
  if (!isIdPicker(desc.picker)) {
    const listId = `dl-${uuid()}`;
    const i = el('input', { type: 'text', list: listId, placeholder: desc.placeholder || '' });
    const dl = el('datalist', { id: listId });
    i.value = obj[name] ?? '';
    i.addEventListener('input', () => { obj[name] = i.value; changed(); });
    i.addEventListener('change', () => changed());
    pickerOptions(desc.picker, pickerCtx).then(({ options }) => fill(dl, options.map(([v, l]) => el('option', { value: v }, l === v ? null : l)))).catch(() => {});
    return el('div', {}, withVars(i), dl);
  }
  const s = el('select', {}, el('option', { value: '' }, 'Loading…'));
  const current = obj[name] && obj[name] !== EMPTY_GUID ? String(obj[name]) : '';
  s.addEventListener('change', () => {
    if (s.value === '__new_command') { s.value = current; ctx.onCreate?.('command', (id) => { obj[name] = id; changed(); ctx.refresh?.(); }); return; }
    obj[name] = s.value || (desc.type === 'Guid?' || desc.type === 'Guid' ? '' : ''); changed();
  });
  pickerOptions(desc.picker, pickerCtx).then(({ options, empty, create }) => {
    fill(s, el('option', { value: '' }, empty || '—'), options.map(([v, l]) => el('option', { value: String(v) }, l)),
      current && !options.some(([v]) => String(v) === current) ? el('option', { value: current }, `Unknown (${current.slice(0, 8)}…)`) : null,
      create === 'command' && ctx.onCreate ? el('option', { value: '__new_command' }, '+ New command…') : null);
    s.value = current;
  }).catch((e) => fill(s, el('option', { value: current }, `Couldn't load: ${e.message}`)));
  return s;
}

function chipInput(obj, name, desc, ctx, changed) {
  if (!Array.isArray(obj[name])) obj[name] = obj[name] ? [String(obj[name])] : [];
  const box = el('div', { class: 'chip-input' });
  const listId = `dl-${uuid()}`;
  const entry = el('input', { type: 'text', placeholder: 'Type and press Enter', list: desc.picker ? listId : null });
  const dl = desc.picker ? el('datalist', { id: listId }) : null;
  const draw = () => fill(box, obj[name].map((v, i) => el('span', { class: 'chip' }, v,
    el('button', { type: 'button', 'aria-label': `Remove ${v}`, onclick: () => { obj[name].splice(i, 1); changed(); draw(); } }, '×'))), entry);
  const add = () => {
    const v = entry.value.trim();
    if (!v) return;
    obj[name].push(v);
    entry.value = '';
    changed();
    draw();
    entry.focus();
  };
  entry.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter' || ev.key === ',') { ev.preventDefault(); add(); }
    else if (ev.key === 'Backspace' && !entry.value && obj[name].length) { obj[name].pop(); changed(); draw(); entry.focus(); }
  });
  entry.addEventListener('change', () => { if (desc.picker && entry.value) add(); });
  entry.addEventListener('blur', add);
  if (desc.picker) pickerOptions(desc.picker, { values: obj, action: ctx.action }).then(({ options }) => fill(dl, options.map(([v]) => el('option', { value: v })))).catch(() => {});
  draw();
  return el('div', {}, box, dl);
}

function kvEditor(obj, name, changed, varCtx) {
  if (!obj[name] || typeof obj[name] !== 'object' || Array.isArray(obj[name])) obj[name] = {};
  const wrap = el('div');
  const rows = Object.entries(obj[name]).map(([k, v]) => ({ k, v }));
  const write = () => { obj[name] = Object.fromEntries(rows.filter(r => r.k).map(r => [r.k, r.v])); changed(); };
  const draw = () => fill(wrap, rows.map((r, i) => {
    const k = el('input', { placeholder: 'Name', value: r.k });
    const v = el('input', { placeholder: 'Value', value: r.v });
    k.addEventListener('input', () => { r.k = k.value; write(); });
    v.addEventListener('input', () => { r.v = v.value; write(); });
    return el('div', { class: 'kv-row' }, k, v, varButton(v, varCtx), el('button', { type: 'button', class: 'small ghost', 'aria-label': 'Remove row', onclick: () => { rows.splice(i, 1); write(); draw(); } }, '×'));
  }), el('button', { type: 'button', class: 'small', onclick: () => { rows.push({ k: '', v: '' }); draw(); } }, '+ Add row'));
  draw();
  return wrap;
}

/** WinForms key names, which the Windows app's Keyboard Press understands. */
function winKey(ev) {
  const c = ev.code;
  if (/^Key[A-Z]$/.test(c)) return c.slice(3);
  if (/^Digit\d$/.test(c)) return `D${c.slice(5)}`;
  if (/^F\d{1,2}$/.test(c)) return c;
  if (/^Numpad\d$/.test(c)) return `NumPad${c.slice(6)}`;
  const map = { Space: 'Space', Enter: 'Enter', Escape: 'Escape', Tab: 'Tab', Backspace: 'Back', Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End',
    PageUp: 'PageUp', PageDown: 'PageDown', ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down', Pause: 'Pause', ScrollLock: 'Scroll',
    PrintScreen: 'PrintScreen', MediaPlayPause: 'MediaPlayPause', MediaTrackNext: 'MediaNextTrack', MediaTrackPrevious: 'MediaPreviousTrack', AudioVolumeMute: 'VolumeMute' };
  return map[c] || null;
}

function keyCapture(obj, name, desc, changed) {
  const show = el('input', { type: 'text', placeholder: desc.modifiers ? 'e.g. ControlKey+ShiftKey' : 'e.g. F13' });
  show.value = typeof obj[name] === 'string' ? obj[name] : (obj[name] === undefined || obj[name] === null ? '' : JSON.stringify(obj[name]));
  const btn = el('button', { type: 'button', class: 'small' }, 'Record');
  show.addEventListener('input', () => { obj[name] = show.value; changed(); });
  btn.addEventListener('click', () => {
    btn.textContent = 'Press a key…';
    btn.classList.add('active');
    const onKey = (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      const mods = [ev.ctrlKey && 'ControlKey', ev.shiftKey && 'ShiftKey', ev.altKey && 'Menu', ev.metaKey && 'LWin'].filter(Boolean);
      if (desc.modifiers) {
        if (!mods.length) return;
        obj[name] = mods.join('+');
      } else {
        const k = winKey(ev);
        if (!k) return;
        obj[name] = k;
      }
      show.value = obj[name];
      changed();
      window.removeEventListener('keydown', onKey, true);
      btn.textContent = 'Record';
      btn.classList.remove('active');
    };
    window.addEventListener('keydown', onKey, true);
  });
  return el('div', { class: 'row center' }, show, btn);
}

function categoryInput(obj, name, changed) {
  const listId = `dl-${uuid()}`;
  const i = el('input', { type: 'text', list: listId, placeholder: 'Start typing a category…' });
  const dl = el('datalist', { id: listId });
  i.value = obj[name] ?? '';
  const search = debounce(async () => {
    if (i.value.length < 2) return;
    try {
      const r = await get(`/v1/twitch/categories?query=${encodeURIComponent(i.value)}`);
      fill(dl, r.categories.map(c => el('option', { value: c.name })));
    } catch { /* not linked */ }
  }, 350);
  i.addEventListener('input', () => { obj[name] = i.value; changed(); search(); });
  return el('div', {}, i, dl);
}
