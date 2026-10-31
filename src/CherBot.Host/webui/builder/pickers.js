// Data sources for the builder's smart pickers. Each returns { options: [[value, label]], free, empty }.
// "free" pickers are text boxes with suggestions (names, logins); the rest are dropdowns of ids.

import { lists, subName, loadCatalog } from '../lib/api.js';

const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

const uniq = (xs) => [...new Set(xs.filter(Boolean))].sort((a, b) => a.localeCompare(b));

/** ctx: { values (the step's current fields), action (the action being edited) } */
export async function pickerOptions(picker, ctx = {}) {
  const v = ctx.values || {};
  switch (picker) {
    case 'action': {
      const actions = await lists.actions();
      return { options: actions.map(a => [a.id, `${a.group ? `${a.group} / ` : ''}${a.name}`]), empty: '— choose an action —' };
    }
    case 'command': {
      const cmds = await lists.commands();
      return { options: cmds.map(c => [c.id, `${c.name}${c.group ? ` (${c.group})` : ''}${c.enabled ? '' : ' · off'}`]), empty: '— choose a command —', create: 'command' };
    }
    case 'timer': return { options: (await lists.timers()).map(t => [t.id, t.name]), empty: '— choose a timer —' };
    case 'queue': return { options: (await lists.queues()).map(q => [q.id, q.name]), empty: 'All queues / default' };
    case 'obsConnection': return { options: (await lists.obsConnections()).map(c => [c.id, `${c.name} (${c.host}:${c.port})`]), empty: 'First connected OBS' };
    case 'reward': {
      const rewards = await lists.rewards();
      return { options: rewards.map(r => [r.id, `${r.title} (${r.cost})${r.manageable ? '' : ' · not editable by CherBot'}`]), empty: rewards.length ? '— choose a reward —' : '— link Twitch to list rewards —', free: true };
    }
    case 'giveaway': return { options: (await lists.giveaways()).map(g => [g.id, `${g.name} (${g.status})`]), empty: 'The open giveaway' };
    case 'customCommand': return { options: (await lists.customCommands()).map(c => [c.id, c.name]), empty: '— choose a custom command —' };
    case 'csharp': {
      const actions = await lists.actions();
      await loadCatalog();
      const opts = [];
      const walk = (a, list) => (list || []).forEach(s => {
        if (s.type === 99999) opts.push([s.id, `${a.name} / ${s.name || 'Execute C# Code'}`]);
        walk(a, s.subActions);
      });
      for (const a of actions) walk(a, a.subActions);
      if (ctx.action) walk(ctx.action, ctx.action.subActions);
      return { options: dedupe(opts), empty: '— choose a C# step —' };
    }
    case 'subAction': {
      const action = await actionFor(v.actionId, ctx);
      const opts = [];
      await loadCatalog();
      const walk = (list, depth) => (list || []).forEach(s => {
        opts.push([s.id, `${'· '.repeat(depth)}${subName(s.type)}${s.name ? ` "${s.name}"` : ''}`]);
        walk(s.subActions, depth + 1);
      });
      walk(action?.subActions, 0);
      return { options: opts, empty: action ? '— choose a step —' : '— choose the action first —' };
    }
    case 'trigger': {
      const action = await actionFor(v.actionId, ctx);
      const { eventLabel } = await import('../lib/api.js');
      return { options: (action?.triggers || []).map(t => [t.id, eventLabel(t.type)]), empty: action ? '— choose a trigger —' : '— choose the action first —' };
    }
    case 'scene': return { options: (await lists.scenes()).map(s => [s, s]), free: true, empty: '' };
    case 'source': {
      const scene = v.sceneName;
      const items = scene && !scene.includes('%') ? await lists.sceneItems(scene) : (await lists.inputs()).map(i => ({ name: i.name }));
      return { options: uniq(items.map(i => i.name)).map(n => [n, n]), free: true };
    }
    case 'filter': {
      const src = v.sourceName || v.sceneName;
      const filters = src && !src.includes('%') ? await lists.filters(src) : [];
      return { options: filters.map(f => [f.name, f.name]), free: true };
    }
    case 'obsGroup': {
      const scene = v.sceneName;
      const items = scene && !scene.includes('%') ? await lists.sceneItems(scene) : [];
      return { options: items.filter(i => i.isGroup).map(i => [i.name, i.name]), free: true };
    }
    case 'viewer': return { options: (await lists.viewers()).map(x => [x.login, `${x.display} (${x.role})`]), free: true };
    case 'global': return { options: (await lists.globals()).map(g => [g.name, g.name]), free: true };
    case 'actionGroup': return { options: uniq((await lists.actions()).map(a => a.group)).map(g => [g, g]), free: true };
    case 'commandGroup': return { options: uniq((await lists.commands()).map(c => c.group)).map(g => [g, g]), free: true };
    case 'rewardGroup': return { options: [], free: true };
    default: return { options: [], free: true };
  }
}

const dedupe = (opts) => [...new Map(opts.map(o => [o[0], o])).values()];

async function actionFor(id, ctx) {
  if (!id || id === EMPTY_GUID) return ctx.action || null;
  if (ctx.action?.id === id) return ctx.action;
  return (await lists.actions()).find(a => a.id === id) || null;
}

export const isIdPicker = (picker) => !['scene', 'source', 'filter', 'obsGroup', 'viewer', 'global', 'actionGroup', 'commandGroup', 'rewardGroup', 'reward'].includes(picker);
