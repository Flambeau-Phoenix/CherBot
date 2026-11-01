// Deliberately independent of lib/api.js: player capability grants no management API access.
const token=new URLSearchParams(location.hash.slice(1)).get('token')||'';
history.replaceState(null,'',location.pathname);
const status=document.querySelector('#status'),start=document.querySelector('#start'),external=document.querySelector('#external');
const clientId=crypto.randomUUID?crypto.randomUUID(): '10000000-1000-4000-8000-100000000000'.replace(/[018]/g,c=>(c^crypto.getRandomValues(new Uint8Array(1))[0]&15>>c/4).toString(16));
let lease=null,current=null,player=null,started=false,polling=false,lastSuccess=0,playerReady=false,reporting=false,sdkPromise=null;
async function api(path,body){
  const controller=new AbortController();const timeout=setTimeout(()=>controller.abort(),3500);
  try{
    const response=await fetch(`/song-player/${path}`,{method:body===undefined?'GET':'POST',headers:{'X-Song-Token':token,'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:controller.signal});
    if(!response.ok)throw new Error(response.status===401?'Player link revoked. Ask the owner for a new link.':(await response.json()).error||`Player HTTP ${response.status}`);
    return response.status===204?null:await response.json();
  }finally{clearTimeout(timeout);}
}
function stop(message){player?.stopVideo?.();current=null;lease=null;started=false;status.textContent=message;start.disabled=false;start.textContent='Reconnect player';}
async function result(failed,error){
  if(!current||reporting||!lease)return;reporting=true;
  try{await api('result',{leaseId:lease,requestId:current.id,failed,error});current=null;player?.stopVideo?.();await poll();}
  catch(e){stop(e.message);}finally{reporting=false;}
}
function ensureSdk(){
  sdkPromise??=new Promise((resolve,reject)=>{
    const timeout=setTimeout(()=>reject(new Error('YouTube player could not load. Check network access.')),15000);
    window.onYouTubeIframeAPIReady=()=>{clearTimeout(timeout);resolve();};
    const script=document.createElement('script');script.src='https://www.youtube.com/iframe_api';script.onerror=()=>{clearTimeout(timeout);reject(new Error('YouTube player failed to load.'));};document.head.append(script);
  });return sdkPromise;
}
async function show(request){
  external.replaceChildren();
  if(!request){player?.stopVideo?.();status.textContent='Connected · waiting for an approved request';return;}
  if(request.provider!=='youtube'){player?.stopVideo?.();status.textContent='Waiting for owner to play/complete this external request';const link=document.createElement('a');link.href=request.url;link.target='_blank';link.rel='noopener noreferrer';link.textContent='Open request in provider ↗';external.append(link);return;}
  status.textContent='Playing YouTube request';
  await ensureSdk();
  if(!started||!lease||current?.id!==request.id)return;
  if(!player){
    player=new window.YT.Player('player',{height:'390',width:'100%',videoId:request.trackId,playerVars:{origin:location.origin,playsinline:1,autoplay:1},events:{
      onReady:()=>{playerReady=true;if(started&&current?.provider==='youtube')player.loadVideoById(current.trackId);},
      onStateChange:event=>{if(event.data===window.YT.PlayerState.ENDED)result(false);},
      onError:event=>result(true,`YouTube error ${event.data}: embedding, availability or browser identity restriction`),
      onAutoplayBlocked:()=>{status.textContent='Autoplay blocked. Press Play in the YouTube player (use Interact in OBS).';}
    }});
  }else if(playerReady)player.loadVideoById(request.trackId);
}
async function poll(){
  if(!started||polling)return;polling=true;
  try{
    const data=await api(`state?leaseId=${encodeURIComponent(lease)}`);lastSuccess=Date.now();
    if(data.current?.id!==current?.id){current=data.current;await show(current);}
    else if(!current)status.textContent='Connected · waiting for an approved request';
  }catch(e){stop(e.message);}finally{polling=false;}
}
start.addEventListener('click',async()=>{
  if(!token){status.textContent='Missing restricted player link. Create one on Song Requests.';return;}
  start.disabled=true;
  try{const data=await api('lease',{clientId});lease=data.leaseId;started=true;lastSuccess=Date.now();await poll();}
  catch(e){stop(e.message);}
});
setInterval(poll,5000);
setInterval(()=>{if(started&&Date.now()-lastSuccess>18000)stop('Connection lost. Playback stopped before the controller lease expires.');},500);
document.addEventListener('visibilitychange',()=>{if(started)poll();});
window.addEventListener('pagehide',()=>player?.stopVideo?.());
