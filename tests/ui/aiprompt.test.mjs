import test from 'node:test';
import assert from 'node:assert/strict';
import { buildAiPrompt } from '../../src/CherBot.Host/webui/lib/aiprompt.js';

test('external AI prompt asks for supported online CLI commands and local credential files',()=>{
  const prompt=buildAiPrompt({feature:'Make !brb',version:'0.3.0',os:'Linux',arrangement:'Remote engine',shell:'Bash',names:'Scene: BRB'});
  for(const text of ['Make !brb','0.3.0','Bash','Scene: BRB','cherbot api','--body-file','--token-file','preview','removal instructions','Do not invent'])assert.ok(prompt.includes(text),text);
  assert.ok(!prompt.includes('install.bat'));
});
