import { el, fill, guard, input, field, card, toggle, openDialog, copy, toast, go } from './dom.js';
import { get, post, put } from './api.js';

export async function workflowCatalog() {
  const root = el('div', { class: 'grid-cards' });
  const load = async () => {
    const s = await get('/v1/quicksetup/status');
    const state = (configured, enabled, ready) => !ready ? 'Needs attention' : enabled ? 'Enabled' : configured ? 'Ready' : 'Not configured';
    const managed = (id, title, enabled, ready) => card(title, null,
      el('span', { class: 'badge' }, state(s[`${id}Configured`], enabled, ready)),
      s[`${id}Configured`] ? toggle(enabled, async on => { await post(`/v1/quicksetup/${id}`, { enabled: on, bonus: s.first.bonus, replace: false }); await load(); }, `Enable ${title}`) : null,
      el('p', { class: 'muted small' }, id === 'first' ? 'One winner per live stream, optional points, and !pinned spotlight. Existing first-chatter ranking is preserved on conversion.' : 'Confirmed live Twitch clips. Defaults: 60s global and 120s per-user cooldown.'),
      el('p', { class: 'small' }, ready ? 'Twitch prerequisites ready.' : 'Link Twitch' + (id === 'clip' ? ' with clips:edit.' : '.')),
      el('button', { onclick: guard(async () => {
        const preview = await get(`/v1/quicksetup/${id}/preview`);
        const bonus = input(s.first.bonus, { type: 'number', min: 0, max: 1000000 });
        const replace = el('input', { type: 'checkbox' });
        const on = el('input', { type: 'checkbox', checked: enabled || !s[`${id}Configured`] });
        openDialog(`Set up ${title}`, el('div', { class: 'stack' },
          el('label', { class: 'inline' }, on, 'Enable workflow'),
          id === 'first' ? field('Winner bonus (0 disables)', bonus) : null,
          el('ul', {}, preview.commands.map(c => el('li', {}, `${c.name}: ${c.commandId ? c.customized ? 'Replace custom/legacy action' : 'Update managed workflow' : 'Create command and action'}`, c.actionId ? el('a', { href: `#/actions/${c.actionId}` }, ' · Open action') : null))),
          el('label', { class: 'inline' }, replace, 'Replace existing workflow (required for custom or legacy actions)')),
          { okText: 'Apply setup', onOk: async () => { await post(`/v1/quicksetup/${id}`, { enabled: on.checked, bonus: Number(bonus.value), replace: replace.checked }); toast('Workflow saved'); await load(); } });
      }) }, 'Preview / configure'));
    fill(root,
      card('Loyalty Points', null, el('span', { class: 'badge' }, s.loyalty.enabled ? 'Enabled' : 'Ready'),
        el('p', { class: 'muted small' }, 'Currency, chat rewards, estimated chat presence and event bonuses. Existing settings are preserved.'),
        toggle(s.loyalty.enabled, async enabled => { await put('/v1/loyalty/settings', { ...s.loyalty, enabled }); load(); }),
        el('button', { onclick: () => {
          const currency=input(s.loyalty.currencyName), chat=input(s.loyalty.chatPoints,{type:'number',min:0}), cooldown=input(s.loyalty.chatCooldownSeconds,{type:'number',min:0}), watch=input(s.loyalty.watchPoints,{type:'number',min:0}), every=input(s.loyalty.watchIntervalMinutes,{type:'number',min:1});
          openDialog('Loyalty Quick Setup', el('div',{class:'stack'},field('Currency name',currency),field('Points per chat message',chat),field('Chat reward cooldown (seconds)',cooldown),field('Points per estimated chat-presence interval',watch),field('Interval (minutes)',every),el('a',{href:'#/loyalty'},'Advanced bonuses and role multipliers →')),
            {okText:'Save points setup',onOk:async()=>{await put('/v1/loyalty/settings',{...s.loyalty,currencyName:currency.value,chatPoints:Number(chat.value),chatCooldownSeconds:Number(cooldown.value),watchPoints:Number(watch.value),watchIntervalMinutes:Number(every.value)});await load();}});
        } }, 'Configure points')),
      managed('first', '!first & !pinned', s.first.enabled, s.linked), managed('clip', '!clip', s.clipEnabled, s.linked && s.clipsScope),
      card('Sery_Bot protection', null, el('span', { class: 'badge' }, s.seryCompleted ? 'Setup marked complete' : 'Not configured'),
        el('p', { class: 'muted small' }, 'Assisted setup with the official Sery_Bot service. Completion is owner-reported, not verified protection.'),
        el('button', { onclick: () => openDialog('Add Sery_Bot', el('div', { class: 'stack' },
          el('a', { href: 'https://docs.sery.bot/how-to-add/', target: '_blank', rel: 'noopener noreferrer' }, 'Read the official setup guide ↗'),
          el('button', { type: 'button', onclick: () => copy('/mod sery_bot') }, '1. Copy /mod sery_bot for your Twitch chat'),
          el('a', { href: 'https://sery.bot/auth', target: '_blank', rel: 'noopener noreferrer' }, '2. Authorize Sery_Bot for your channel ↗'),
          el('a', { href: 'https://www.twitch.tv/sery_bot', target: '_blank', rel: 'noopener noreferrer' }, '3. Open Sery_Bot chat and type !join ↗'),
          el('p', {}, 'CherBot’s own spam filter is configured separately. Opening these links does not enable protection.')),
          { okText: 'Mark setup complete', onOk: async () => { await put('/v1/quicksetup/sery', { completed: true }); load(); } }) }, 'Add Sery_Bot'),
        el('a', { href: '#/modules' }, 'CherBot spam filter →')),
      card('Song Requests', null, el('p', { class: 'muted small' }, 'Choose YouTube, Apple Music or Spotify links; moderate requests and play YouTube.'), el('a', { href: '#/songs' }, 'Configure Song Requests →')),
      ...[['Giveaways', 'giveaways'], ['Alerts & spam filter', 'modules'], ['Sharing', 'sharing'], ['Get AI Help', 'aihelp']].map(([title, page]) => card(title, null, el('a', { href: `#/${page}` }, `Open ${title} →`))));
  };
  await load(); return root;
}
