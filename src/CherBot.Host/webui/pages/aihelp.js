import { el, fill, input, select, field, card, pageHead, copy } from '../lib/dom.js';
import { buildAiPrompt } from '../lib/aiprompt.js';

export async function render(root) {
  const version=(await (await fetch('/version')).json()).version;
  const feature=el('textarea',{rows:4,placeholder:'Describe the command, alert, timer, reward or workflow you want.'});
  const os=select(['Linux','Windows'], 'Linux');
  const arrangement=select(['Local engine','Remote engine on my LAN/VPN','Windows desktop connected to a remote engine'],'Local engine');
  const shell=select(['Bash','PowerShell','Windows cmd'],'Bash');
  const names=el('textarea',{rows:3,placeholder:'Optional exact OBS scene/source names, reward IDs and other names. Enter only details you want to share.'});
  const prompt=el('textarea',{class:'code',rows:18,readonly:true,'aria-label':'Prompt for external AI chat'});
  const update=()=>{prompt.value=buildAiPrompt({feature:feature.value,version,os:os.value,arrangement:arrangement.value,shell:shell.value,names:names.value});};
  for(const control of [feature,os,arrangement,shell,names])control.addEventListener('input',update);
  fill(root,pageHead('Get AI Help','Take CherBot’s reference and a tailored prompt to an external AI chat.'),
    card('1. Choose an AI chat',null,el('div',{class:'row wrap'},...[['ChatGPT','https://chatgpt.com/'],['DeepSeek Chat','https://chat.deepseek.com/'],['Gemini','https://gemini.google.com/'],['Arena','https://arena.ai/']].map(([name,href])=>el('a',{href,target:'_blank',rel:'noopener noreferrer'},`${name} ↗`))),
      el('p',{class:'muted small'},'These services offer chat options with varying free limits, availability and attachment support. CherBot does not connect to them or require their API keys.')),
    card(`2. Download the reference for CherBot ${version}`,null,
      el('a',{href:'/help/docs/cherbot-ai-reference.txt',download:'cherbot-ai-reference.txt'},'Download AI reference'), ' · ',
      el('a',{href:'/help/docs/cherbot-ai-prompt.txt',download:'cherbot-ai-prompt.txt'},'Download reusable prompt'),
      el('p',{},'Attach the reference in your chosen chat. If attachments are unavailable, paste the relevant sections with the compatibility and variable rules.')),
    card('3. Describe your feature',null,field('What should CherBot do?',feature),el('div',{class:'grid-3'},field('Engine OS',os),field('Connection',arrangement),field('Shell',shell)),field('Optional integration names',names),
      el('button',{onclick:()=>copy(prompt.value)},'Copy prompt'),prompt),
    card('4. Apply and troubleshoot',null,el('p',{},'Ask the AI for CherBot CLI commands and complete JSON files. Keep your token in a local file, review changes, preview feature packs, then apply them to the running engine.'),
      el('p',{},'If something fails, return the CherBot version, smallest relevant error or run trace, and relevant configuration excerpt. Remove credentials and private viewer information first.'),
      el('a',{href:'#/sharing'},'Preview a feature pack →')));
  update();
}
