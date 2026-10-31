// Quick Setup: wizards that create ordinary commands, actions and timers, then open them in the builder.
// Also the scene-shortcut table and the switches for the seeded !scene / !scenes commands.

import { el, fill, guard, toast, card, pageHead, go, toggle, uuid, select } from '../lib/dom.js';
import { get, post, put, del, lists, invalidate, loadCatalog, catalog, actionForCommand } from '../lib/api.js';
import { defaultsFor } from '../meta/fields.js';

const GROUP = 'Quick Setup';
const ROLES = [['', 'Everyone'], ['subscriber', 'Subscribers'], ['vip', 'VIPs'], ['moderator', 'Moderators'], ['broadcaster', 'Only you']];

/** A step with catalog defaults plus the given fields. */
function step(type, fields = {}) {
  return { id: uuid(), type, enabled: true, weight: 0, ...defaultsFor(type, catalog.sub.get(type)?.fields || []), ...fields };
}
const say = (text) => step(10, { text, useBot: true, fallback: true });
const trigger = (type, fields = {}) => ({ id: uuid(), type, enabled: true, ...fields });

async function createCommand(name, text, { minRole = '', globalCooldown = 0, userCooldown = 5, group = GROUP } = {}) {
  const c = await post('/v1/commands', { name, command: text || name, enabled: true, mode: 0, location: 0, sources: 1, globalCooldown, userCooldown, group, grantType: 0, permittedGroups: [], minRole });
  invalidate('commands');
  return c;
}

async function createAction(name, triggers, steps, extra = {}) {
  steps.forEach((s, i) => { s.index = i; });
  const a = await post('/v1/actions', { name, group: extra.group || GROUP, enabled: true, triggers, subActions: steps, ...extra });
  invalidate('actions');
  return a;
}

async function commandAction(cmdName, steps, opts = {}) {
  const c = await createCommand(cmdName, opts.text, opts);
  return createAction(opts.actionName || cmdName, [trigger(401, { commandId: c.id })], steps, opts.actionExtra);
}

const done = (a, what) => { toast(`${what} created`); go(`actions/${a.id}`); };
const req = (v, what) => { if (!String(v ?? '').trim()) throw new Error(`${what} is required`); return v.trim(); };

export async function render(root) {
  await loadCatalog();
  const scenes = await lists.scenes();
  const sceneOptions = scenes.length ? scenes.map(s => [s, s]) : [['', 'OBS isn\'t connected — type a name']];

  const wizards = el('div', { class: 'grid-3' });
  const field = (label, control, hint) => el('label', {}, label, control, hint ? el('span', { class: 'hint' }, hint) : null);
  const txt = (value = '', attrs = {}) => { const i = el('input', attrs); i.value = value; return i; };
  const area = (value = '') => { const t = el('textarea', { rows: 2 }); t.value = value; return t; };
  const sceneInput = () => scenes.length ? select(sceneOptions, scenes[0]) : txt('', { placeholder: 'Scene name' });
  const wizard = (title, blurb, fields, create) => el('div', { class: 'mod-card' },
    el('h3', {}, title), el('p', { class: 'muted small' }, blurb), el('div', { class: 'stack' }, fields),
    el('div', { class: 'row' }, el('button', { class: 'primary', onclick: guard(create) }, 'Create')));

  // chat command → reply
  const c1 = { cmd: txt('!hello'), reply: area('Hello %user%, welcome to the stream!'), cd: txt('5', { type: 'number' }) };
  // command → scene
  const c2 = { cmd: txt('!brb'), scene: sceneInput(), role: select(ROLES, 'moderator'), reply: txt('Switching to %sceneName%') };
  // reward → effect
  const rewards = await lists.rewards();
  const c3 = {
    reward: rewards.length ? select(rewards.map(r => [r.id, `${r.title} (${r.cost})`])) : txt('', { placeholder: 'Reward id (link Twitch to pick from a list)' }),
    effect: select([['scene', 'Switch scene'], ['flash', 'Flash a source'], ['sound', 'Play a sound'], ['tts', 'Read the text aloud (Speaker.bot)']], 'flash'),
    scene: sceneInput(), source: txt('', { placeholder: 'Source name (flash)' }), ms: txt('5000', { type: 'number' }), file: txt('', { placeholder: 'C:\\sounds\\airhorn.mp3' }),
  };
  // timer message
  const c4 = { name: txt('Socials'), every: txt('900', { type: 'number' }), lines: txt('5', { type: 'number' }), msg: area('Follow me everywhere: example.com/socials') };
  // alerts
  const c5 = {
    event: select([[101, 'Follow'], [103, 'Sub'], [104, 'Resub'], [105, 'Gift sub'], [106, 'Gift bomb'], [102, 'Cheer'], [107, 'Raid']], 101),
    msg: area('Thanks for the follow, %user%!'), file: txt('', { placeholder: 'Optional sound file' }), scene: sceneInput(), source: txt('', { placeholder: 'Optional OBS source to pulse' }),
  };
  const alertDefaults = { 101: 'Thanks for the follow, %user%!', 103: '%user% just subscribed! Thank you!', 104: '%user% resubscribed for %cumulativeMonths% months!', 105: '%user% gifted a sub to %recipientUser%!', 106: '%user% is gifting %gifts% subs!', 102: '%user% cheered %bits% bits!', 107: 'Welcome raiders from %user% (%viewers% viewers)!' };
  c5.event.addEventListener('change', () => { c5.msg.value = alertDefaults[c5.event.value]; });
  // shoutout, lurk, counter, quotes
  const c6 = { cmd: txt('!so'), msg: area('Go check out %targetUser% at https://twitch.tv/%targetUserName% — they were last playing %game%!') };
  const c7 = { cmd: txt('!lurk'), msg: area('%user% is lurking. Enjoy the stream!') };
  const c8 = { name: txt('deaths'), show: txt('!deaths'), add: txt('!death+'), msg: txt('Deaths so far: ~deaths~') };

  fill(wizards,
    wizard('Chat command → reply', 'A command that answers in chat.', [field('Command', c1.cmd), field('Reply', c1.reply, '%user%, %input0%, %rawInput%… use the {x} button in the builder for more'), field('Per-user cooldown (s)', c1.cd)], async () => {
      const a = await commandAction(req(c1.cmd.value, 'Command'), [say(req(c1.reply.value, 'Reply'))], { userCooldown: Number(c1.cd.value) });
      done(a, 'Command');
    }),
    wizard('Command → OBS scene', 'A command that switches OBS to a scene.', [field('Command', c2.cmd), field('Scene', c2.scene), field('Who can use it', c2.role), field('Reply (optional)', c2.reply)], async () => {
      const scene = req(c2.scene.value, 'Scene');
      const steps = [step(34, { sceneName: scene })];
      if (c2.reply.value.trim()) steps.push(say(c2.reply.value.replace('%sceneName%', scene)));
      const a = await commandAction(req(c2.cmd.value, 'Command'), steps, { minRole: c2.role.value, globalCooldown: 3, actionExtra: { cherbotKind: 'sceneShortcut' } });
      done(a, 'Scene command');
    }),
    wizard('Reward → effect', 'When a channel point reward is redeemed: switch scene, flash a source, play a sound or read the text aloud.',
      [field('Reward', c3.reward), field('Effect', c3.effect), field('Scene', c3.scene, 'For scene switches and flashes'), field('Source', c3.source), field('Flash length (ms)', c3.ms), field('Sound file', c3.file, 'A path on the engine machine')], async () => {
        const rewardId = req(c3.reward.value, 'Reward');
        const e = c3.effect.value;
        const steps = e === 'scene' ? [step(34, { sceneName: req(c3.scene.value, 'Scene') })]
          : e === 'flash' ? [step(30, { sceneName: c3.scene.value, sourceName: req(c3.source.value, 'Source'), state: 0 }), step(1002, { value: String(Number(c3.ms.value) || 5000) }), step(30, { sceneName: c3.scene.value, sourceName: c3.source.value, state: 1 })]
            : e === 'sound' ? [step(1, { soundFile: req(c3.file.value, 'Sound file'), volume: 1 })]
              : [step(602, { alias: '', message: '%user% says %rawInput%', badWordFilter: true })];
        const name = rewards.find(r => r.id === rewardId)?.title || 'Reward';
        const a = await createAction(`Reward: ${name}`, [trigger(112, { rewardId })], steps);
        done(a, 'Reward action');
      }),
    wizard('Timer message', 'Posts a message on a timer, only when chat is active.', [field('Name', c4.name), field('Every (seconds)', c4.every), field('Only after N chat lines', c4.lines), field('Message', c4.msg)], async () => {
      const t = await post('/v1/timers', { name: req(c4.name.value, 'Name'), enabled: true, repeat: true, interval: Number(c4.every.value) || 900, randomInterval: false, upperInterval: 0, lines: Number(c4.lines.value) || 0 });
      invalidate('timers');
      const a = await createAction(`Timer: ${c4.name.value}`, [trigger(701, { timerId: t.id })], [say(req(c4.msg.value, 'Message'))]);
      done(a, 'Timer');
    }),
    wizard('Alert', 'Thank followers, subs, cheerers or raiders with a message, an optional sound and an optional OBS source pulse. (For chat-only alerts the Chat Alerts module is simpler.)',
      [field('Event', c5.event), field('Chat message', c5.msg), field('Sound (optional)', c5.file), field('Scene of the source', c5.scene), field('Source to show for 5 s (optional)', c5.source)], async () => {
        const type = Number(c5.event.value);
        const steps = [say(req(c5.msg.value, 'Message'))];
        if (c5.file.value.trim()) steps.push(step(1, { soundFile: c5.file.value.trim(), volume: 1 }));
        if (c5.source.value.trim()) steps.push(step(30, { sceneName: c5.scene.value, sourceName: c5.source.value.trim(), state: 0 }), step(1002, { value: '5000' }), step(30, { sceneName: c5.scene.value, sourceName: c5.source.value.trim(), state: 1 }));
        const label = c5.event.options[c5.event.selectedIndex].text;
        const a = await createAction(`Alert: ${label}`, [trigger(type)], steps);
        done(a, 'Alert');
      }),
    wizard('Shoutout', 'Moderators type !so @user: CherBot sends a Twitch shoutout and a message about them.', [field('Command', c6.cmd), field('Message', c6.msg)], async () => {
      const a = await commandAction(req(c6.cmd.value, 'Command'), [step(50, { userLogin: '%input0%' }), step(29, { userLogin: '%targetUserName%' }), say(req(c6.msg.value, 'Message'))], { minRole: 'moderator', userCooldown: 0, globalCooldown: 5 });
      done(a, 'Shoutout');
    }),
    wizard('Lurk', 'A friendly !lurk reply.', [field('Command', c7.cmd), field('Reply', c7.msg)], async () => {
      const a = await commandAction(req(c7.cmd.value, 'Command'), [say(req(c7.msg.value, 'Reply'))], { userCooldown: 60 });
      done(a, 'Lurk command');
    }),
    wizard('Counter', 'A counter such as deaths: one command shows it, a moderator command adds one.', [field('Counter name (global)', c8.name), field('Show command', c8.show), field('Add command (moderators)', c8.add), field('Message', c8.msg, 'The counter is ~name~')], async () => {
      const name = req(c8.name.value, 'Counter name');
      const msg = c8.msg.value.replaceAll('~deaths~', `~${name}~`);
      await commandAction(req(c8.show.value, 'Show command'), [say(msg)], { userCooldown: 10, globalCooldown: 3, actionName: `${c8.show.value} (show ${name})` });
      const a = await commandAction(req(c8.add.value, 'Add command'), [step(122, { destination: 0, source: 1, variableName: name, value: '1', persisted: true }), say(msg)],
        { minRole: 'moderator', userCooldown: 0, globalCooldown: 2, actionName: `${c8.add.value} (add to ${name})` });
      done(a, 'Counter commands');
    }),
    wizard('Quotes pack', '!quote shows a random quote (or !quote 12), !addquote saves one (moderators).', [], async () => {
      const show = await commandAction('!quote', [
        { ...step(120, { input: '%input0%', operation: 9, value: '' }), subActions: [
          { type: 99901, id: uuid(), enabled: true, random: false, subActions: [step(1000, { quoteType: 0 })] },
          { type: 99902, id: uuid(), enabled: true, random: false, subActions: [step(1000, { quoteType: 1, quoteId: '%input0%' })] }] },
        say('Quote #%quoteId%: "%quote%" (%quoteGame%, %quoteTime%)'),
      ], { userCooldown: 10, globalCooldown: 3 });
      await commandAction('!addquote', [step(1037, { quoteVariable: 'rawInput' }), say('Added quote #%quoteId%.')], { minRole: 'moderator', userCooldown: 0 });
      done(show, 'Quote commands');
    }));

  // scene shortcuts
  const rows = [{ cmd: '!brb', scene: scenes.find(s => /brb|break/i.test(s)) || scenes[0] || '', role: 'moderator', cd: 3 }, { cmd: '!game', scene: scenes.find(s => /game|play/i.test(s)) || scenes[1] || '', role: 'moderator', cd: 3 }];
  const shortcutBody = el('tbody');
  const drawRows = () => fill(shortcutBody, rows.map((r, i) => {
    const cmd = txt(r.cmd); cmd.addEventListener('input', () => { r.cmd = cmd.value; });
    const scene = scenes.length ? select(sceneOptions, r.scene) : txt(r.scene, { placeholder: 'Scene name' }); scene.addEventListener('change', () => { r.scene = scene.value; }); scene.addEventListener('input', () => { r.scene = scene.value; });
    const role = select(ROLES, r.role); role.addEventListener('change', () => { r.role = role.value; });
    const cd = txt(r.cd, { type: 'number', min: 0 }); cd.addEventListener('input', () => { r.cd = Number(cd.value); });
    return el('tr', {}, el('td', {}, cmd), el('td', {}, scene), el('td', {}, role), el('td', {}, cd),
      el('td', { class: 'actions' }, el('button', { class: 'small ghost danger', onclick: () => { rows.splice(i, 1); drawRows(); } }, '×')));
  }));
  drawRows();
  const existing = el('div');
  const loadExisting = guard(async () => {
    invalidate('actions');
    const actions = (await lists.actions()).filter(a => a.cherbotKind === 'sceneShortcut');
    const cmds = await lists.commands();
    fill(existing, actions.length ? el('table', {}, el('tbody', {}, actions.map(a => {
      const t = (a.triggers || []).find(x => x.type === 401);
      const c = cmds.find(x => x.id === t?.commandId);
      const sceneStep = (a.subActions || []).find(s => s.type === 34);
      return el('tr', {}, el('td', {}, c?.name || a.name), el('td', {}, sceneStep?.sceneName || '?'), el('td', {}, c ? toggle(c.enabled, async (on) => { await put(`/v1/commands/${c.id}`, { ...c, enabled: on }); invalidate('commands'); }) : ''),
        el('td', { class: 'actions' }, el('a', { href: `#/actions/${a.id}` }, 'Edit'), ' ',
          el('button', { class: 'small ghost danger', onclick: guard(async () => { if (!confirm(`Delete ${c?.name || a.name}?`)) return; if (c) await del(`/v1/commands/${c.id}`); await del(`/v1/actions/${a.id}`); loadExisting(); }) }, 'Delete')));
    }))) : el('p', { class: 'muted small' }, 'No scene shortcuts yet.'));
  });

  // seeded !scene / !scenes
  const seeded = el('div');
  const loadSeeded = guard(async () => {
    invalidate('commands'); invalidate('actions');
    const cmds = await lists.commands();
    const parts = [];
    for (const name of ['!scene', '!scenes']) {
      const c = cmds.find(x => x.name === name && x.group === 'CherBot Scenes');
      if (!c) { parts.push(el('p', { class: 'muted small' }, `${name} was deleted.`)); continue; }
      const a = await actionForCommand(c.id);
      const s = a?.subActions?.find(x => x.type === 50020 || x.type === 50021);
      const allowed = el('div', { class: 'row wrap' }, scenes.map(sc => {
        const box = el('input', { type: 'checkbox' });
        box.checked = (s?.allowedScenes || []).includes(sc);
        box.addEventListener('change', guard(async () => {
          s.allowedScenes = scenes.filter((x, i) => allowed.querySelectorAll('input')[i].checked);
          await put(`/v1/actions/${a.id}`, a);
          invalidate('actions');
          toast(`${name}: ${s.allowedScenes.length ? s.allowedScenes.join(', ') : 'every scene'}`);
        }));
        return el('label', { class: 'inline' }, box, sc);
      }));
      parts.push(el('div', { class: 'stack', style: 'margin-bottom:10px' },
        el('div', { class: 'row center' }, toggle(c.enabled, async (on) => { await put(`/v1/commands/${c.id}`, { ...c, enabled: on }); invalidate('commands'); toast(`${name} ${on ? 'on' : 'off'}`); }, `Enable ${name}`),
          el('strong', {}, name), el('span', { class: 'muted small' }, name === '!scene' ? 'moderators: !scene <name> (exact, then prefix, then contains)' : 'lists the scenes'),
          a ? el('a', { href: `#/actions/${a.id}`, class: 'small' }, 'Edit action') : null),
        s && scenes.length ? el('div', {}, el('span', { class: 'hint' }, 'Allowed scenes (none ticked = every scene)'), allowed) : null));
    }
    fill(seeded, parts);
  });

  fill(root,
    pageHead('Quick Setup', 'Wizards that build ordinary commands and actions for you. Everything they create opens in the builder, ready to tweak.'),
    el('div', { class: 'grid-2' },
      card('Scene shortcuts', [el('button', { class: 'small', onclick: () => { rows.push({ cmd: '!', scene: scenes[0] || '', role: 'moderator', cd: 3 }); drawRows(); } }, '+ Row')],
        el('p', { class: 'muted small' }, 'Commands such as !brb or !game that switch OBS to one scene. Create several at once.'),
        el('table', {}, el('thead', {}, el('tr', {}, ['Command', 'Scene', 'Who', 'Cooldown (s)', ''].map(h => el('th', {}, h)))), shortcutBody),
        el('div', { class: 'row', style: 'margin-top:8px' }, el('button', { class: 'primary', onclick: guard(async () => {
          let n = 0;
          for (const r of rows.filter(r => r.cmd.trim().length > 1 && r.scene)) {
            await commandAction(r.cmd.trim(), [step(34, { sceneName: r.scene })], { minRole: r.role, globalCooldown: r.cd, userCooldown: 0, group: 'Scene Shortcuts', actionExtra: { cherbotKind: 'sceneShortcut', group: 'Scene Shortcuts' } });
            n++;
          }
          toast(`Created ${n} scene shortcut${n === 1 ? '' : 's'}`);
          loadExisting();
        }) }, 'Create shortcuts')),
        el('h4', {}, 'Existing shortcuts'), existing),
      card('Chat scene commands', null, el('p', { class: 'muted small' }, 'Built-in !scene and !scenes commands (created switched off).'), seeded)),
    el('h3', { style: 'margin:8px 0' }, 'Wizards'),
    wizards);
  loadExisting();
  loadSeeded();
}
