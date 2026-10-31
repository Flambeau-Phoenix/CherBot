// Usage: node compat-test.mjs <port> <actionId> [password]
// Exits 0 when every check passes; prints one JSON line per check.
import { StreamerbotClient } from '@streamerbot/client';

const [port, actionId, password] = process.argv.slice(2);
const results = [];
const check = (name, ok, detail) => {
  results.push({ name, ok: !!ok, detail });
  console.log(JSON.stringify({ name, ok: !!ok, detail }));
};

const client = new StreamerbotClient({
  host: '127.0.0.1',
  port: Number(port),
  endpoint: '/',
  password: password || undefined,
  immediate: false,
  autoReconnect: false,
  logLevel: 'none',
  subscribe: {},
});

try {
  await client.connect(5000);
  // connect() resolves on socket open; the Hello handshake completes asynchronously.
  for (let i = 0; i < 50 && !client.version; i++) await new Promise((r) => setTimeout(r, 100));
  check('connect', client.version && client.info, client.info);
  if (password) check('authenticated', client.authenticated, null);

  const info = await client.getInfo();
  check('GetInfo', info.status === 'ok' && info.info?.instanceId, info.info);

  const events = await client.getEvents();
  check('GetEvents', events.status === 'ok' && Array.isArray(events.events?.Twitch), Object.keys(events.events ?? {}).length);

  const actions = await client.getActions();
  check('GetActions', actions.status === 'ok' && actions.actions.some((a) => a.id === actionId), actions.count);

  const globals = await client.getGlobals(true);
  check('GetGlobals', globals.status === 'ok' && globals.variables?.compatGlobal?.value === 'hello', globals.variables);

  const commands = await client.getCommands();
  check('GetCommands', commands.status === 'ok' && commands.count >= 1, commands.commands?.[0]);

  // Event subscription via .on(), then an action run that raises a Custom event.
  let customSeen = null;
  await client.on('Custom.Event', (payload) => {
    if (payload?.data?.eventName === 'compat-ping') customSeen = payload.data;
  });
  const done = await client.doAction(actionId, { from: 'compat' });
  check('DoAction', done.status === 'ok', null);
  for (let i = 0; i < 50 && !customSeen; i++) await new Promise((r) => setTimeout(r, 100));
  check('Subscribe + Custom.Event', customSeen?.args?.from === 'compat', customSeen);

  // Round-trip result via sbClientResponse (customEventResponse).
  const withResponse = await client.doAction({ id: actionId }, { from: 'round-trip' }, { customEventResponse: true });
  check('DoAction customEventResponse', withResponse.customEventResponseArgs?.echo === 'round-trip', withResponse.customEventResponseArgs);

  const byName = await client.doAction({ name: 'does not exist' }).catch((e) => ({ status: 'error', error: String(e) }));
  check('DoAction unknown action errors', byName.status === 'error', byName);
} catch (e) {
  check('fatal', false, String(e?.stack ?? e));
} finally {
  await client.disconnect().catch(() => {});
}

const failed = results.filter((r) => !r.ok);
process.exit(failed.length ? 1 : 0);
