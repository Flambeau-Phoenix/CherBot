// API client, live event stream and shared caches. Every request carries the API token.

const TOKEN_KEY = 'cherbot.token';

(function readTokenFromFragment() {
  const m = location.hash.match(/token=([^&]+)/);
  if (m) {
    localStorage.setItem(TOKEN_KEY, decodeURIComponent(m[1]));
    history.replaceState(null, '', location.pathname + location.search + '#/dashboard');
  }
})();

export const token = () => localStorage.getItem(TOKEN_KEY) || '';
export const setToken = (t) => localStorage.setItem(TOKEN_KEY, t);
export const forgetToken = () => localStorage.removeItem(TOKEN_KEY);

export class ApiError extends Error {
  constructor(status, message, data) { super(message); this.status = status; this.data = data; }
}

let onUnauthorized = () => {};
export const setUnauthorizedHandler = (fn) => { onUnauthorized = fn; };

export async function api(method, path, body, { raw = false, allowError = false } = {}) {
  const opts = { method, headers: { Authorization: `Bearer ${token()}` } };
  if (body !== undefined) {
    opts.headers['Content-Type'] = raw ? 'text/plain' : 'application/json';
    opts.body = raw ? body : JSON.stringify(body);
  }
  const res = await fetch(path, opts);
  if (res.status === 401) { onUnauthorized('The token was rejected.'); throw new ApiError(401, 'unauthorized'); }
  const text = await res.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!res.ok && !allowError) throw new ApiError(res.status, (data && (data.error || data.message || data.title)) || res.statusText, data);
  return allowError ? { ok: res.ok, status: res.status, data } : data;
}

export const get = (p) => api('GET', p);
export const post = (p, b) => api('POST', p, b);
export const put = (p, b) => api('PUT', p, b);
export const del = (p) => api('DELETE', p);

// ── live events (one EventSource shared by every page) ──────────────────────
const listeners = new Set();
let source = null;
export function onEvent(fn) {
  listeners.add(fn);
  if (!source) {
    source = new EventSource(`/v1/events?token=${encodeURIComponent(token())}`);
    source.onmessage = (msg) => {
      let ev;
      try { ev = JSON.parse(msg.data); } catch { return; }
      for (const l of [...listeners]) { try { l(ev); } catch (e) { console.error(e); } }
    };
  }
  return () => listeners.delete(fn);
}

// ── catalogs ────────────────────────────────────────────────────────────────
export const catalog = { sub: new Map(), events: new Map(), loaded: false };
let catalogPromise = null;
export function loadCatalog() {
  catalogPromise ??= Promise.all([get('/v1/catalog/subactions'), get('/v1/catalog/events')]).then(([subs, events]) => {
    subs.subActions.forEach(s => catalog.sub.set(s.id, s));
    events.events.forEach(e => catalog.events.set(e.id, e));
    catalog.loaded = true;
    return catalog;
  });
  return catalogPromise;
}
export const subName = (t) => catalog.sub.get(t)?.display || catalog.sub.get(t)?.name || `Type ${t}`;
export const eventLabel = (t) => catalog.events.get(t) ? `${catalog.events.get(t).source}: ${catalog.events.get(t).display}` : `Event ${t}`;

// ── short-lived caches for pickers ──────────────────────────────────────────
const cache = new Map();
export function cached(key, loader, ttlMs = 15000) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttlMs) return hit.promise;
  const promise = loader().catch(e => { cache.delete(key); throw e; });
  cache.set(key, { at: Date.now(), promise });
  return promise;
}
export const invalidate = (prefix) => { for (const k of [...cache.keys()]) if (k.startsWith(prefix)) cache.delete(k); };

export const lists = {
  actions: () => cached('actions', async () => (await get('/v1/actions')).actions),
  commands: () => cached('commands', async () => (await get('/v1/commands')).commands),
  timers: () => cached('timers', async () => (await get('/v1/timers')).timers.map(t => t.timer)),
  queues: () => cached('queues', async () => (await get('/v1/queues')).definitions),
  obsConnections: () => cached('obs.connections', async () => (await get('/v1/obs/connections')).connections),
  scenes: () => cached('obs.scenes', async () => { try { return (await get('/v1/obs/scenes')).scenes; } catch { return []; } }, 5000),
  sceneItems: (scene) => cached(`obs.items.${scene}`, async () => { try { return (await get(`/v1/obs/scene-items?scene=${encodeURIComponent(scene)}`)).items; } catch { return []; } }, 5000),
  filters: (src) => cached(`obs.filters.${src}`, async () => { try { return (await get(`/v1/obs/filters?source=${encodeURIComponent(src)}`)).filters; } catch { return []; } }, 5000),
  inputs: () => cached('obs.inputs', async () => { try { return (await get('/v1/obs/inputs')).inputs; } catch { return []; } }, 5000),
  rewards: () => cached('twitch.rewards', async () => { try { return (await get('/v1/twitch/rewards/list')).rewards; } catch { return []; } }, 30000),
  viewers: () => cached('viewers', async () => { try { return (await get('/v1/viewers')).viewers; } catch { return []; } }, 5000),
  globals: () => cached('globals', async () => (await get('/v1/globals?persisted=true')).globals, 5000),
  userGroups: () => cached('groups', async () => {
    try { return [...new Set((await get('/v1/groups')).members.map(m => m.group))].sort(); } catch { return []; }
  }, 10000),
  giveaways: () => cached('giveaways', async () => { try { return (await get('/v1/giveaways')).giveaways.map(g => g.giveaway); } catch { return []; } }, 5000),
  customCommands: () => cached('customCommands', async () => { try { return (await get('/v1/custom-commands')).commands.map(c => c.command); } catch { return []; } }, 5000),
};

// ── UI settings (pins, dashboard layout), stored on the engine ──────────────
let uiSettings = null;
export async function getUiSettings() {
  uiSettings ??= await get('/v1/ui/settings').catch(() => ({}));
  return uiSettings;
}
export async function saveUiSettings(patch) {
  const current = await getUiSettings();
  uiSettings = { ...current, ...patch };
  await put('/v1/ui/settings', uiSettings);
  return uiSettings;
}

/** The action whose Command Triggered trigger points at the command, if any. */
export async function actionForCommand(commandId) {
  const actions = await lists.actions();
  return actions.find(a => (a.triggers || []).some(t => t.type === 401 && t.commandId === commandId)) || null;
}

/** Sends a chat message through the simulator (never reaches Twitch; replies do). */
export const simulate = (message, user = 'tester', role = 'viewer', emotes) =>
  post('/v1/chat/simulate', { message, user, role, emotes });
