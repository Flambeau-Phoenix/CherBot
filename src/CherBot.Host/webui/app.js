// CherBot web UI shell: sidebar navigation, hash routing, login and the header status.
// Plain ES modules, no build step. Pages live in pages/*.js and export render(root, params).

import { $, el, fill, toast, icon } from './lib/dom.js';
import { api, token, setToken, setUnauthorizedHandler } from './lib/api.js';

export const PAGES = [
  { id: 'dashboard', title: 'Live Admin', icon: 'layout-dashboard' },
  { id: 'users', title: 'Users', icon: 'users' },
  { id: 'actions', title: 'Actions', icon: 'bolt' },
  { id: 'commands', title: 'Commands', icon: 'terminal-2' },
  { id: 'modules', title: 'Modules', icon: 'puzzle' },
  { id: 'quicksetup', title: 'Quick Setup', icon: 'wand' },
  { id: 'loyalty', title: 'Loyalty & Games', icon: 'star' },
  { id: 'giveaways', title: 'Giveaways', icon: 'gift' },
  { id: 'songs', title: 'Song Requests', icon: 'music' },
  { id: 'sharing', title: 'Sharing', icon: 'arrows-exchange' },
  { id: 'aihelp', title: 'Get AI Help', icon: 'sparkles' },
  { id: 'history', title: 'Data & History', icon: 'history' },
  { id: 'variables', title: 'Variables', icon: 'variable' },
  { id: 'monitor', title: 'Monitor', icon: 'activity' },
  { id: 'config', title: 'Config', icon: 'settings' },
  { id: 'help', title: 'Help', icon: 'help-circle' },
];

let current = null;
let currentHash = location.hash;
let routeVersion = 0;

function renderNav(active) {
  fill($('#nav'), PAGES.map(p => el('a', {
    href: `#/${p.id}`, class: `nav-item${p.id === active ? ' active' : ''}`, 'aria-current': p.id === active ? 'page' : null,
  }, icon(p.icon, 'nav-icon'), el('span', {}, p.title))));
}

/** "#/actions/abc?x=1" → { page: 'actions', rest: ['abc'], query: URLSearchParams } */
export function parseRoute(hash = location.hash) {
  const [path, qs] = hash.replace(/^#\/?/, '').split('?');
  const [page, ...rest] = path.split('/').filter(Boolean).map(decodeURIComponent);
  return { page: PAGES.some(p => p.id === page) ? page : 'dashboard', rest, query: new URLSearchParams(qs || '') };
}

export const navigate = (path) => { location.hash = path.startsWith('#') ? path : `#/${path.replace(/^\//, '')}`; };

async function route(ev) {
  if (!token()) return;
  if (current?.isDirty?.() && !confirm('Discard unsaved changes?')) {
    history.replaceState(null, '', ev?.oldURL ? new URL(ev.oldURL).hash : currentHash);
    return;
  }
  const version = ++routeVersion;
  currentHash = location.hash;
  const r = parseRoute();
  renderNav(r.page);
  const root = $('#page');
  try { current?.dispose?.(); } catch { /* ignore */ }
  current = null;
  const page = PAGES.find(p => p.id === r.page);
  document.title = `${page.title} · CherBot`;
  fill(root, el('p', { class: 'muted' }, 'Loading…'));
  try {
    const mod = await import(`./pages/${r.page}.js`);
    if (version !== routeVersion) return;
    const view = document.createElement('div');
    view.className = `view view-${r.page}`;
    fill(root, view);
    const rendered = (await mod.render(view, r)) || null;
    if (version !== routeVersion) { rendered?.dispose?.(); return; }
    current = rendered;
  } catch (e) {
    if (version !== routeVersion) return;
    if (e?.status === 401) return;
    console.error(e);
    fill(root, el('div', { class: 'card' }, el('h3', {}, 'This page failed to load'), el('pre', { class: 'mono' }, String(e?.stack || e))));
  }
}

function showLogin(message = '') {
  $('#login').classList.remove('hidden');
  $('#page').classList.add('hidden');
  $('#login-error').textContent = message;
}
setUnauthorizedHandler(showLogin);

$('#login-form').addEventListener('submit', async (ev) => {
  ev.preventDefault();
  setToken($('#login-token').value.trim());
  try {
    await api('GET', '/v1/integrations');
    $('#login').classList.add('hidden');
    $('#page').classList.remove('hidden');
    start();
  } catch (e) {
    if (e?.status !== 401) $('#login-error').textContent = e.message;
  }
});

async function refreshBadges() {
  try {
    const h = await (await fetch('/health')).json();
    $('#version').textContent = `v${h.version}`;
    window.cherbotHealth = h;
    $('#engine-status').textContent = `Connected · ${h.instanceName || location.host}`;
    $('#engine-status').className = 'engine-status ok-text';
    $('#engine-address').textContent = location.host;
    fill($('#badges'), h.integrations.map(i => el('span', {
      class: `badge ${i.connected ? 'ok' : (i.state === 'disabled' || i.state === 'not configured') ? '' : 'warn'}`,
      title: i.detail || i.state,
    }, `${i.name}: ${i.state}`)));
  } catch {
    $('#engine-status').textContent = 'Engine offline · retrying…';
    $('#engine-status').className = 'engine-status warn-text';
    fill($('#badges'), el('span', { class: 'badge bad' }, 'engine unreachable'));
  }
}

let started = false;
function start() {
  if (started) { route(); return; }
  started = true;
  refreshBadges();
  setInterval(refreshBadges, 10000);
  window.addEventListener('hashchange', route);
  window.addEventListener('beforeunload', ev => {
    if (current?.isDirty?.()) { ev.preventDefault(); ev.returnValue = ''; }
  });
  if (!location.hash || location.hash === '#') history.replaceState(null, '', '#/dashboard');
  // Old tab names from the first UI.
  const legacy = { '#actions': '#/actions', '#monitor': '#/monitor', '#config': '#/config' };
  if (legacy[location.hash]) history.replaceState(null, '', legacy[location.hash]);
  route();
}

window.addEventListener('unhandledrejection', (e) => { if (e.reason?.status !== 401) toast(e.reason?.message || String(e.reason), true); });

if (!token()) showLogin();
else start();
