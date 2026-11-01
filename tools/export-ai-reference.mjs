// Export public capabilities only. No private actions, configuration, or event samples.
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { alwaysAvailable, eventVariables, formats } from '../src/CherBot.Host/webui/meta/variables.js';
import { parseFields, resolveField, defaultsFor, overrideFor } from '../src/CherBot.Host/webui/meta/fields.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { values } = parseArgs({ options: {
  url: { type: 'string', default: 'http://127.0.0.1:8091' },
  'token-file': { type: 'string' },
  output: { type: 'string', default: resolve(root, 'docs/cherbot-ai-reference.txt') },
} });
if (!values['token-file']) throw new Error('Usage: node tools/export-ai-reference.mjs --url http://127.0.0.1:8091 --token-file <path> [--output <path>]');
const token = (await readFile(values['token-file'], 'utf8')).trim();
if (!token) throw new Error('Token file is empty.');
const read = path => readFile(resolve(root, path), 'utf8');
async function get(path) {
  const response = await fetch(new URL(path, values.url), { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw new Error(`Catalog request ${path}: HTTP ${response.status}`);
  return await response.json();
}
const [version, events, subs, intro, signatures, models, example, props] = await Promise.all([
  get('/version'), get('/v1/catalog/events'), get('/v1/catalog/subactions'),
  read('docs/ai-reference-intro.txt'), read('src/CherBot.Plugin.Interface/IInlineInvokeProxy.cs'),
  read('src/CherBot.Plugin.Interface/Models.cs'), read('docs/examples/hello-workflow.json'), read('Directory.Build.props'),
]);
const sourceVersion = props.match(/<Version>([^<]+)<\/Version>/)?.[1];
if (version.version !== sourceVersion) throw new Error(`Engine version ${version.version} differs from source ${sourceVersion}. Build and run this checkout first.`);
const supported = subs.subActions.filter(s => s.supported).sort((a, b) => a.id - b.id);
const emitted = events.events.filter(e => e.emitted).sort((a, b) => a.id - b.id);
if (!supported.length || !emitted.length) throw new Error('Engine returned empty capability catalogs.');

const lines = [
  `CHERBOT AI REFERENCE — VERSION ${sourceVersion}`,
  `Generated from this checkout and its engine catalogs: ${supported.length} supported steps; ${emitted.length} emitted events.`,
  'Regenerate with tools/export-ai-reference.mjs; do not edit the generated sections by hand.',
  'Companion files: cherbot-ai-prompt.txt and ai-guide.md. No private engine data is included.',
  '', intro.trim(), '',
];
function section(title) { lines.push('', title, '='.repeat(title.length), ''); }
function variables(items) {
  // Shared chat metadata can repeat rawInput; the trigger-specific definition wins.
  for (const v of new Map(items.map(v => [v.name, v])).values())
    lines.push(`  ${v.name}: ${v.desc}${v.example !== undefined && v.example !== '' ? ` | example ${JSON.stringify(v.example)}` : ''}`);
}
function fields(kind, item) {
  const all = parseFields(item.fields);
  if (!all.length) { lines.push('  Fields: none beyond the common record fields.'); return; }
  for (const f of all) {
    const spec = resolveField(kind, item.id, f.name, f.type);
    lines.push(`  ${f.name}: ${f.type}${spec.picker ? `; selects ${spec.picker}` : ''}${spec.options ? `; options ${JSON.stringify(spec.options)}` : ''}${spec.hint ? `; ${spec.hint}` : ''}`);
  }
}
section('RUN ARGUMENTS AND FORMATTING');
lines.push('Metadata below is available as applicable. triggerId/category require an actual trigger;',
  'isTest is supplied for test runs, not guaranteed for every run. Treat optional fields defensively.');
variables(alwaysAvailable);
lines.push('', 'Template format examples:');
variables(formats);

section('EMITTED TRIGGERS: IDS, FILTERS AND ARGUMENTS');
for (const event of emitted) {
  lines.push('', `[${event.id}] ${event.name} — ${event.source}.${event.type} — ${event.display}`);
  const description = overrideFor('trigger', event.id).desc;
  if (description) lines.push(description);
  lines.push('Filters:'); fields('trigger', event);
  lines.push('Arguments (in addition to applicable run metadata):');
  variables(eventVariables[event.id] || []);
  if (!eventVariables[event.id]?.length) lines.push('  No additional variables documented.');
}

section('SUPPORTED SUB-ACTIONS: IDS, FIELDS, OPTIONS AND OUTPUTS');
for (const step of supported) {
  const metadata = overrideFor('sub', step.id);
  lines.push('', `[${step.id}] ${step.name} — ${step.display} (${step.category})`);
  if (metadata.desc) lines.push(metadata.desc);
  if (metadata.container) lines.push(`  Container: ${metadata.container}`);
  fields('sub', step);
  lines.push(`  Builder defaults: ${JSON.stringify(defaultsFor(step.id, step.fields))}`);
  if (metadata.outputs?.length) lines.push(`  Output arguments: ${metadata.outputs.join(', ')}`);
}

section('COMPLETE SUPPORTED CPH INTERFACE (EXACT C# SIGNATURES)');
lines.push(signatures.trim(), '', 'Implementation notes: only the methods declared above are provided.');
section('CPH RETURN MODELS AND PLATFORM ENUM (C#)');
lines.push('Platform enum values are compatibility names, not a list of connected services.', models.trim());

section('READABLE IMPORT EXAMPLE: !hello -> GREETING -> CHAT');
lines.push('This is docs/examples/hello-workflow.json. Import Preview should report one command,',
  'one action, no unsupported entries, and no unresolved command links. Importing enables',
  '!hello for everyone. Running it sends real Twitch chat when an account is linked.', example.trim());

section('CLI SOURCE: EXACT SUBCOMMANDS AND FLAGS');
lines.push((await read('src/CherBot.Host/Program.cs')).trim());
lines.push('', (await read('src/CherBot.Host/OnlineCli.cs')).trim());
section('FEATURE PACK MODELS: EXACT JSON FIELDS');
lines.push((await read('src/CherBot.Host/Sharing/FeaturePack.cs')).trim());
section('SONG REQUEST SETTINGS AND QUEUE CONTRACT');
lines.push((await read('src/CherBot.Integrations/Songs/SongRequestService.cs')).split('/// <summary>A free request queue;')[0].trim());
section('API ROUTE INVENTORY (DISCOVERY; SEE BODY SHAPES ABOVE)');
const apiDir = 'src/CherBot.Host/Api';
for (const file of (await readdir(resolve(root, apiDir))).filter(f => f.endsWith('.cs')).sort()) {
  const source = await read(`${apiDir}/${file}`);
  const routes = [...source.matchAll(/\b(v1|app)\.Map(Get|Post|Put|Patch|Delete)\("([^"]+)"/g)];
  if (!routes.length) continue;
  lines.push(`\n${file}:`);
  for (const [, target, method, path] of routes) lines.push(`  ${method.toUpperCase()} ${target === 'v1' ? '/v1' : ''}${path}`);
}
lines.push('', 'END OF CHERBOT AI REFERENCE', '');
const output = lines.join('\n');
await writeFile(values.output, output, 'utf8');
console.log(`Wrote ${values.output} (${Buffer.byteLength(output)} bytes; ${supported.length} steps, ${emitted.length} events).`);
