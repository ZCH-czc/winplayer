/* Self-hosted, owner-authorized media only. No platform endpoints or credentials. */
(() => {
  'use strict';
  const audio=document.getElementById('song-audio'),controls=document.getElementById('song-controls');
  const play=document.getElementById('song-play'),seek=document.getElementById('song-seek'),volume=document.getElementById('song-volume');
  const output=document.getElementById('song-time'),status=document.getElementById('song-status');
  let enabled=false,pending=false,ticker=null;
  audio.volume=.45;
  const clock=t=>`${Math.floor(t/60)}:${String(Math.floor(t%60)).padStart(2,'0')}`;
  const snapshot=()=>({currentTime:Number.isFinite(audio.currentTime)?audio.currentTime:0,duration:Number.isFinite(audio.duration)?audio.duration:241,playing:enabled&&!audio.paused&&!audio.ended&&!audio.error});
  window.AuralisFeaturedAudio=Object.freeze({snapshot});
  function update(){
    const state=snapshot();
    play.textContent=pending?'正在加载…':state.playing?'暂停音乐':'播放《大海带不走》';
    play.setAttribute('aria-pressed',String(state.playing));
    play.setAttribute('aria-busy',String(pending));
    seek.max=String(state.duration);seek.value=String(state.currentTime);seek.disabled=audio.readyState<1;
    seek.setAttribute('aria-valuetext',`${clock(state.currentTime)} / ${clock(state.duration)}`);
    output.textContent=`${clock(state.currentTime)} / ${clock(state.duration)}`;
    window.dispatchEvent(new Event('auralis-audio-change'));
  }
  function stopTicker(){clearInterval(ticker);ticker=null;}
  play.addEventListener('click',async()=>{
    if(!enabled)return;
    if(!audio.paused||pending){pending=false;audio.pause();update();return;}
    pending=true;status.textContent='正在准备音乐…';update();
    if(!audio.getAttribute('src'))audio.src='assets/dahai/audio.mp3';
    if(audio.error)audio.load();
    try{await audio.play();if(!enabled||document.hidden)audio.pause();}
    catch(error){if(error.name!=='AbortError')status.textContent='音频未能播放，请点击重试。';}
    finally{pending=false;update();}
  });
  audio.addEventListener('playing',()=>{
    pending=false;status.textContent='MP3 · 128 kbps · LRC 同步；其他布局仍以截图轮播展示。';
    stopTicker();ticker=setInterval(update,200);update();
  });
  audio.addEventListener('pause',()=>{pending=false;stopTicker();update();});
  audio.addEventListener('waiting',()=>{status.textContent='正在缓冲音乐…';});
  audio.addEventListener('error',()=>{pending=false;stopTicker();status.textContent='音频未能加载，请点击重试。';update();});
  for(const event of ['timeupdate','loadedmetadata','seeked','ended'])audio.addEventListener(event,update);
  seek.addEventListener('input',()=>{if(audio.readyState>=1){audio.currentTime=Math.max(0,Math.min(Number(seek.value),audio.duration));update();}});
  volume.addEventListener('input',()=>{audio.volume=Number(volume.value);});
  function language(){enabled=document.documentElement.lang==='zh-CN';controls.hidden=!enabled;if(!enabled)audio.pause();update();}
  window.addEventListener('auralis-language-change',language);
  document.addEventListener('visibilitychange',()=>{if(document.hidden)audio.pause();});
  window.addEventListener('pagehide',()=>{audio.pause();stopTicker();});
  language();
})();
