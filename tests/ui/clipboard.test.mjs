import { test } from 'node:test';
import assert from 'node:assert/strict';
import { copy, toast } from '../../src/CherBot.Host/webui/lib/dom.js';

function browser(t, clipboard) {
  const messages = [], prompts = [];
  const toast = { classList: { toggle() {}, add() {}, remove() {} }, set textContent(v) { messages.push(v); } };
  for (const [name, value] of Object.entries({
    document: { querySelector: () => toast },
    navigator: { clipboard },
    window: { prompt: (message, text) => prompts.push(text) },
  })) {
    const original = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { value, configurable: true });
    t.after(() => original ? Object.defineProperty(globalThis, name, original) : delete globalThis[name]);
  }
  t.after(() => clearTimeout(toast.timer));
  return { messages, prompts };
}

test('copy reports success only after the clipboard write finishes', async t => {
  let finish;
  const state = browser(t, { writeText: () => new Promise(resolve => { finish = resolve; }) });
  const pending = copy('hello');
  assert.deepEqual(state.messages, []);
  finish();
  await pending;
  assert.deepEqual(state.messages, ['Copied']);
});

test('copy offers the original text when clipboard access is unavailable', async t => {
  const state = browser(t, undefined);
  await copy('~deaths~');
  assert.deepEqual(state.prompts, ['~deaths~']);
  assert.ok(!state.messages.includes('Copied'));
});

test('copy offers manual copying when clipboard permission is rejected', async t => {
  const state = browser(t, { writeText: async () => { throw new Error('Permission denied'); } });
  await copy('export text');
  assert.deepEqual(state.prompts, ['export text']);
  assert.ok(!state.messages.includes('Copied'));
});
