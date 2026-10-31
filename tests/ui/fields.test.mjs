// UI coverage test: every field of every supported step, and every filter on emitted events, must resolve to a
// real form widget (not the raw JSON fallback), and every emitted event needs a variables entry.
// Run with: node --test tests/ui

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { resolveField, parseFields, WIDGETS, defaultsFor } from '../../src/CherBot.Host/webui/meta/fields.js';
import { overrides } from '../../src/CherBot.Host/webui/meta/overrides.js';
import { eventVariables, alwaysAvailable } from '../../src/CherBot.Host/webui/meta/variables.js';

const here = dirname(fileURLToPath(import.meta.url));
const load = (name) => JSON.parse(readFileSync(join(here, 'fixtures', name), 'utf8').replace(/^\uFEFF/, ''));
const subActions = load('cherbot-catalog-subactions.json').subActions;
const events = load('cherbot-catalog-events.json').events;

test('every field of every supported step resolves to a real widget', () => {
  const missing = [];
  for (const s of subActions.filter(s => s.supported)) {
    for (const { name, type } of parseFields(s.fields)) {
      const w = resolveField('sub', s.id, name, type);
      assert.ok(WIDGETS.has(w.widget), `${s.id} ${name}: unknown widget ${w.widget}`);
      if (w.widget === 'json') missing.push(`${s.id} ${s.display}: ${name}:${type}`);
      if (w.widget === 'select') assert.ok(Array.isArray(w.options) && w.options.length > 0, `${s.id} ${name}: select without options`);
      if (w.widget === 'picker') assert.ok(typeof w.picker === 'string', `${s.id} ${name}: picker without a source`);
    }
  }
  assert.deepEqual(missing, [], `fields falling back to raw JSON:\n${missing.join('\n')}`);
});

test('every filter on emitted events resolves to a real widget', () => {
  const missing = [];
  for (const e of events.filter(e => e.emitted)) {
    for (const { name, type } of parseFields(e.fields)) {
      const w = resolveField('trigger', e.id, name, type);
      if (w.widget === 'json') missing.push(`${e.id} ${e.name}: ${name}:${type}`);
    }
  }
  assert.deepEqual(missing, [], `trigger filters falling back to raw JSON:\n${missing.join('\n')}`);
});

test('every emitted event has a variables entry', () => {
  const missing = events.filter(e => e.emitted && !Array.isArray(eventVariables[e.id])).map(e => `${e.id} ${e.name}`);
  assert.deepEqual(missing, []);
  assert.ok(alwaysAvailable.some(v => v.name === 'actionName'));
});

test('every supported step has a description and usable defaults', () => {
  const undescribed = subActions.filter(s => s.supported && !overrides[s.id]?.desc && ![124, 125, 126, 129, 130].includes(s.id) && s.id < 99900)
    .map(s => `${s.id} ${s.display}`);
  assert.deepEqual(undescribed, []);
  for (const s of subActions.filter(s => s.supported)) {
    const d = defaultsFor(s.id, s.fields);
    for (const [k, v] of Object.entries(d)) assert.notEqual(v, undefined, `${s.id} default ${k}`);
  }
});

test('select options of state fields match the handlers', () => {
  assert.deepEqual(resolveField('sub', 30, 'state', 'int').options.map(o => o[0]), [0, 1, 2]);
  assert.equal(resolveField('sub', 1012, 'key', 'x').widget, 'key');
  assert.equal(resolveField('sub', 99999, 'byteCode', 'byte[]').widget, 'code');
  assert.equal(resolveField('sub', 1011, 'groupName', 'string').picker, 'actionGroup');
  assert.equal(resolveField('sub', 211, 'groupName', 'string').picker, 'rewardGroup');
});
