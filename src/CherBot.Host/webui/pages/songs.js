import { el, fill, guard, input, field, card, pageHead, copy, toast } from '../lib/dom.js';
import { get, post, put, del, onEvent } from '../lib/api.js';

export async function render(root) {
  let settings,requests=[],disposed=false,dirty=false;
  const form=el('div',{class:'stack'}),queue=el('div',{class:'table-scroll'}),status=el('p',{role:'status'}),playerLink=input('',{readonly:true,'aria-label':'Restricted song player URL'});
  const load=guard(async()=>{
    const data=await get('/v1/songs');if(disposed)return;requests=data.requests;
    status.textContent=data.playerConnected?'Player connected · one controller active':'Player offline · open the player on the streaming PC';
    if(!dirty){settings={...data.settings};drawForm();}
    const active=requests.filter(r=>['pending','approved','playing'].includes(r.status));
    const act=(r,operation,label)=>el('button',{class:'small',onclick:guard(async()=>{await post(`/v1/songs/${r.id}/${operation}`);load();})},label);
    fill(queue,requests.length?el('table',{},el('thead',{},el('tr',{},...['Status','Provider / track','Requested by','Controls'].map(t=>el('th',{},t)))),el('tbody',{},[...active,...requests.filter(r=>!active.includes(r)).reverse().slice(0,25)].map(r=>el('tr',{},
      el('td',{},r.status,r.error?el('p',{class:'error small'},r.error):null),
      el('td',{},r.provider,' · ',el('a',{href:r.url,target:'_blank',rel:'noopener noreferrer'},r.provider==='youtube'?r.trackId:'Open in provider ↗')),
      el('td',{},el('a',{href:`#/users/${encodeURIComponent(r.userId)}`},r.userName||r.userId)),
      el('td',{},el('div',{class:'row wrap'},
        r.status==='pending'?[act(r,'approve','Approve'),act(r,'reject','Reject')]:null,
        r.status==='approved'?act(r,'reject','Reject'):null,r.status==='playing'?[act(r,'complete','Complete'),act(r,'skip','Skip')]:null,
        active.includes(r)?el('button',{class:'small',disabled:active.indexOf(r)===0,onclick:guard(async()=>{
          const ids=active.map(x=>x.id),i=ids.indexOf(r.id);[ids[i-1],ids[i]]=[ids[i],ids[i-1]];await post('/v1/songs/reorder',{ids});load();
        })},'Move up'):null,
        el('button',{class:'small ghost danger',onclick:guard(async()=>{await del(`/v1/songs/${r.id}`);load();})},'Remove'))))))) : el('p',{class:'muted'},'No requests yet. Enable providers, then viewers can use !sr <track URL>.'));
  });
  function drawForm(){
    const bool=(key,label)=>{const control=el('input',{type:'checkbox',checked:settings[key]});control.addEventListener('change',()=>{settings[key]=control.checked;dirty=true;});return el('label',{class:'inline'},control,label);};
    const number=(key,label,min,max)=>{const control=input(settings[key],{type:'number',min,max});control.addEventListener('input',()=>{settings[key]=Number(control.value);dirty=true;});return field(label,control);};
    fill(form,bool('enabled','Enable Song Requests'),el('div',{class:'row wrap'},bool('youTube','YouTube (browser player)'),bool('appleMusic','Apple Music (external links)'),bool('spotify','Spotify (external links)')),
      bool('approvalRequired','Require owner/moderator approval'),bool('onlyWhileLive','Accept only while live'),bool('intakePaused','Pause new requests'),
      el('div',{class:'grid-3'},number('cooldownSeconds','Cooldown per user (seconds)',0,3600),number('perUserLimit','Outstanding requests per user',1,100),number('queueLimit','Queue limit',1,1000)),
      el('button',{class:'primary',onclick:guard(async()=>{await put('/v1/songs/settings',settings);dirty=false;toast('Song settings saved');load();})},'Save settings'));
  }
  fill(root,pageHead('Song Requests','A moderated queue for YouTube, Apple Music and Spotify links.'),
    card('Quick Setup',null,form,el('p',{class:'muted small'},'Use !sr or !songrequest with a track/video link. Moderators can use !songqueue and !songskip. Requests are free. Spotify and Apple Music open in their own apps; CherBot plays YouTube through its official player.')),
    card('Player on the streaming PC',null,status,
      el('p',{},'Open a dedicated browser player on the streaming PC, or use its URL in an OBS browser source. Press Start player or Interact in OBS if autoplay is blocked. Test the connection before going live. Keep the standard YouTube player visible.'),
      el('button',{onclick:guard(async()=>{const r=await post('/v1/songs/player-token');playerLink.value=`${location.origin}/song-player.html#token=${encodeURIComponent(r.token)}`;toast('New player link created. Previous links are revoked; an existing lease may take 20 seconds to expire.');})},'Create / rotate restricted player link'),
      playerLink,el('div',{class:'row'},el('button',{onclick:()=>{if(playerLink.value)copy(playerLink.value);}},'Copy player URL'),el('button',{onclick:()=>{if(playerLink.value)window.open(playerLink.value,'_blank','noopener,noreferrer');}},'Open player / test'))),
    card('Queue',null,el('div',{class:'row'},el('button',{onclick:guard(async()=>{await post('/v1/songs/advance');load();})},'Start next approved request'),el('button',{onclick:guard(async()=>{if(confirm('Clear every song request?')){await del('/v1/songs');load();}})},'Clear queue')),queue));
  await load();const off=onEvent(ev=>{if(ev.source==='CherBot'&&ev.type==='SongQueueChanged')load();});const tick=setInterval(load,10000);
  return{isDirty:()=>dirty,dispose(){disposed=true;off();clearInterval(tick);}};
}
