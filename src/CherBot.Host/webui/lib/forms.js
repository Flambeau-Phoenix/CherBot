// Declarative settings forms: a list of field specs bound to a plain object (mutated in place).

import { el, fill, uuid } from './dom.js';
import { varButton } from '../builder/varhelper.js';

/**
 * spec: { key, label, type: 'number'|'bool'|'text'|'textarea'|'select'|'chips'|'color', hint, options, step, min, max, full, tokens, placeholder }
 * onChange(key) runs after every edit.
 */
export function formField(obj, spec, onChange = () => {}) {
  const { key, label, type = 'text', hint } = spec;
  const set = (v) => { obj[key] = v; onChange(key); };
  const wrap = (control, cls = '') => el('label', { class: `${cls}${spec.full ? ' full' : ''}`.trim() }, label, control, hint ? el('span', { class: 'hint' }, hint) : null);
  switch (type) {
    case 'bool': {
      const box = el('input', { type: 'checkbox' });
      box.checked = !!obj[key];
      box.addEventListener('change', () => set(box.checked));
      return el('label', { class: `inline${spec.full ? ' full' : ''}`, title: hint || '' }, box, label);
    }
    case 'number': {
      const i = el('input', { type: 'number', step: spec.step ?? 1, min: spec.min, max: spec.max, placeholder: spec.placeholder || '' });
      i.value = obj[key] ?? '';
      i.addEventListener('input', () => set(i.value === '' ? 0 : Number(i.value)));
      return wrap(i);
    }
    case 'select': {
      const s = el('select', {}, spec.options.map(o => { const [v, l] = Array.isArray(o) ? o : [o, o]; return el('option', { value: String(v) }, l); }));
      s.value = String(obj[key] ?? '');
      s.addEventListener('change', () => {
        const opt = spec.options.find(o => String(Array.isArray(o) ? o[0] : o) === s.value);
        set(Array.isArray(opt) ? opt[0] : opt);
      });
      return wrap(s);
    }
    case 'chips': {
      const area = el('textarea', { rows: spec.rows || 3, placeholder: spec.placeholder || 'One per line' });
      area.value = (obj[key] || []).join('\n');
      area.addEventListener('input', () => set(area.value.split('\n').map(x => x.trim()).filter(Boolean)));
      return wrap(area);
    }
    case 'color': {
      const i = el('input', { type: 'color' });
      i.value = obj[key] || '#9146ff';
      i.addEventListener('input', () => set(i.value));
      return wrap(i);
    }
    case 'textarea': case 'text': default: {
      const i = type === 'textarea' ? el('textarea', { rows: spec.rows || 2, placeholder: spec.placeholder || '' }) : el('input', { type: 'text', placeholder: spec.placeholder || '' });
      i.value = obj[key] ?? '';
      i.addEventListener('input', () => set(i.value));
      if (spec.tokens) return wrap(el('div', { class: 'field-wrap' }, i, varButton(i, () => ({ tokens: spec.tokens, mode: spec.argsToo ? 'args' : 'tokens-only' }))));
      return wrap(i);
    }
  }
}

/** A grid of fields. */
export function formGrid(obj, specs, onChange) {
  return el('div', { class: 'form-grid' }, specs.map(s => formField(obj, s, onChange)));
}

/** Replaces {token} placeholders with sample values for previews. */
export function previewTokens(text, sample) {
  return String(text || '').replace(/\{([A-Za-z][A-Za-z0-9_]*)(?::([^}]*))?\}/g, (m, name, arg) => {
    if (name === 'random') { const [a, b] = String(arg || '1-100').split('-').map(Number); return String(Math.floor((a + b) / 2) || 50); }
    if (name === 'pick') return String(arg || '').split('|')[0];
    return sample[name] !== undefined ? String(sample[name]) : m;
  });
}

/** Message editor: variants (one per line or a single message), live preview, character counter and token helper. */
export function messageEditor(obj, key, { label, tokens = [], sample: sampleArg = {}, defaultValue, multi = false, onChange = () => {}, max = 500, argsToo = true, eventTypes = [] } = {}) {
  const area = el('textarea', { rows: multi ? 3 : 2 });
  const preview = el('div', { class: 'preview' });
  const counter = el('div', { class: 'counter' });
  const value = () => (multi ? (obj[key] || []).join('\n') : (obj[key] ?? ''));
  area.value = value();
  const draw = () => {
    const sample = typeof sampleArg === 'function' ? sampleArg() : sampleArg;
    const lines = multi ? (obj[key] || []) : [obj[key] || ''];
    const first = lines.find(l => l.trim()) || '';
    fill(preview, first ? previewTokens(first, sample) : el('span', { class: 'muted' }, 'Silent (empty)'));
    const longest = Math.max(0, ...lines.map(l => previewTokens(l, sample).length));
    counter.textContent = `${longest}/${max}${multi && lines.length > 1 ? ` · ${lines.length} variants (picked at random)` : ''}`;
    counter.classList.toggle('over', longest > max);
  };
  area.addEventListener('input', () => {
    obj[key] = multi ? area.value.split('\n').filter(l => l.trim()) : area.value;
    draw();
    onChange(key);
  });
  draw();
  const node = el('div', { class: 'stack', style: 'gap:4px' },
    el('div', { class: 'row between center' }, el('span', { class: 'small' }, label),
      el('div', { class: 'row center' },
        varButton(area, () => ({ tokens, mode: argsToo ? 'args' : 'tokens-only', eventTypes })),
        defaultValue !== undefined ? el('button', { type: 'button', class: 'small ghost', onclick: () => { obj[key] = multi ? [...defaultValue] : defaultValue; area.value = value(); draw(); onChange(key); } }, 'Reset') : null)),
    area, el('div', { class: 'row between' }, el('span', { class: 'hint' }, 'Preview'), counter), preview);
  node.refreshPreview = draw;
  return node;
}

export const formId = () => `f-${uuid()}`;
