// Maps catalog fields ("name:type") to form widgets. Pure (no DOM), so the Node coverage test can load it.

import { overrides, triggerOverrides } from './overrides.js';

/** Fields the builder never shows as form controls (handled by the card itself, or internal). */
export const HIDDEN = new Set(['id', 'index', 'parentId', 'enabled', 'subActions', 'device', 'weight', 'group']);

/** String fields that get a multi-line box. */
export const LONG_TEXT = new Set(['text', 'message', 'raw', 'payload']);

/** Smart pickers chosen by field name alone. */
const PICKER_BY_NAME = {
  actionId: 'action',
  commandId: 'command',
  timerId: 'timer',
  actionQueueId: 'queue',
  connectionId: 'obsConnection',
  obsId: 'obsConnection',
  sceneName: 'scene',
  sourceName: 'source',
  filterName: 'filter',
  rewardId: 'reward',
  userLogin: 'viewer',
  executeCodeId: 'csharp',
  subActionId: 'subAction',
  triggerId: 'trigger',
  giveawayId: 'giveaway',
  customCommandId: 'customCommand',
};

/** groupName means a different kind of group depending on the step. */
const GROUP_PICKER = {
  1011: 'actionGroup', 1017: 'actionGroup',
  1010: 'commandGroup', 1013: 'commandGroup', 1015: 'commandGroup',
  211: 'rewardGroup', 212: 'rewardGroup',
  31: 'obsGroup', 32: 'obsGroup',
};

/** Every widget the builder can render. "json" is the raw fallback the coverage test guards against. */
export const WIDGETS = new Set(['hidden', 'checkbox', 'number', 'range', 'text', 'textarea', 'code', 'chips', 'kv', 'select', 'picker',
  'color', 'file', 'key', 'category', 'object', 'json']);

export function parseField(spec) {
  const i = spec.indexOf(':');
  return i < 0 ? { name: spec, type: 'string' } : { name: spec.slice(0, i), type: spec.slice(i + 1) };
}

export const parseFields = (list) => (list || []).map(parseField);

/** Overrides for a step or trigger type. */
export const overrideFor = (kind, typeId) => (kind === 'trigger' ? triggerOverrides : overrides)[typeId] || {};

/**
 * Resolves the widget for one field.
 * kind: "sub" (sub-action) or "trigger"; returns { widget, picker?, options?, hint?, label?, vars?, ... }.
 */
export function resolveField(kind, typeId, name, type) {
  const ov = overrideFor(kind, typeId).fields?.[name] || {};
  const base = { name, type, label: ov.label || labelFor(name), hint: ov.hint };
  if (HIDDEN.has(name) && !ov.widget) return { ...base, widget: 'hidden' };

  if (ov.options) return { ...base, ...ov, widget: ov.widget || 'select' };
  if (ov.widget) return { ...base, vars: ['text', 'textarea', 'chips', 'category'].includes(ov.widget), ...ov };

  const picker = ov.picker || (name === 'groupName' ? GROUP_PICKER[typeId] : null) || PICKER_BY_NAME[name];
  if (picker) return { ...base, ...ov, widget: 'picker', picker, vars: !/Id$|^connectionId$|^obsId$/.test(name) };

  const t = type.replace(/\?$/, '');
  if (t === 'bool') return { ...base, widget: 'checkbox' };
  if (t === 'int' || t === 'long') return { ...base, widget: 'number', step: 1 };
  if (t === 'float' || t === 'double' || t === 'decimal') return { ...base, widget: 'number', step: 'any' };
  if (t === 'List<string>' || t === 'string[]') return { ...base, widget: 'chips', vars: true };
  if (/^Dictionary</.test(t)) return { ...base, widget: 'kv', vars: true };
  if (t === 'object') return { ...base, widget: 'object', vars: true };
  if (t === 'Guid') return { ...base, widget: 'text', placeholder: '00000000-0000-0000-0000-000000000000' };
  if (t === 'string') {
    if (LONG_TEXT.has(name) || (name === 'value' && typeId === 1009)) return { ...base, widget: 'textarea', vars: true };
    return { ...base, widget: 'text', vars: true };
  }
  return { ...base, widget: 'json' };
}

/** All visible fields of a step or trigger, resolved, in catalog order. */
export function resolveAll(kind, typeId, fields) {
  return parseFields(fields).map(f => resolveField(kind, typeId, f.name, f.type)).filter(f => f.widget !== 'hidden');
}

export function labelFor(name) {
  const special = { userLogin: 'User', actionId: 'Action', commandId: 'Command', timerId: 'Timer', actionQueueId: 'Queue', connectionId: 'OBS connection',
    obsId: 'OBS connection', rewardId: 'Reward', executeCodeId: 'C# code step', subActionId: 'Step', triggerId: 'Trigger', autoType: 'Auto type',
    giveawayId: 'Giveaway', customCommandId: 'Custom command', useArge: 'Use arguments', runImmedately: 'Run immediately' };
  if (special[name]) return special[name];
  return name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());
}

/** Default field values for a new step: catalog type defaults, then overrides. */
export function defaultsFor(typeId, fields) {
  const out = {};
  for (const { name, type } of parseFields(fields)) {
    if (HIDDEN.has(name) && name !== 'enabled') continue;
    const t = type.replace(/\?$/, '');
    if (t === 'bool') out[name] = false;
    else if (t === 'int' || t === 'long' || t === 'float' || t === 'double') out[name] = 0;
    else if (t === 'List<string>') out[name] = [];
    else if (/^Dictionary</.test(t)) out[name] = {};
    else if (t === 'Guid') out[name] = '';
    else if (t === 'string' || t === 'object') out[name] = '';
  }
  return { ...out, ...(overrides[typeId]?.defaults || {}) };
}

export const describe = (typeId) => overrides[typeId]?.desc || '';
export const outputsOf = (typeId) => overrides[typeId]?.outputs || [];
export const containerOf = (typeId) => overrides[typeId]?.container || null;
