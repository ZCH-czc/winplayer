/* Actual UI with a silent presentation clock in both languages. */
(() => {
  'use strict';
  const api=window.Auralis, root=document.documentElement;
  if(!api)return;
  const scenes=[['dark','record'],['light','record'],['light','immersive'],['dark','immersive'],['dark','record','queue'],['light','record','lyrics']];
  const titles=['Quiet Geometry','First Light','Soft Horizons','Still Water','After the Rain','Paper Skies','A Small Journey','Evening Glow'];
  const lines=['Light settles on the windowsill','A quiet rhythm fills the room','Leave a little space for wonder','Let the moment find its melody','Every note a softer color','Every pause a place to stay','Carry this small song with you'];
  let running=false, timer=null, time=18, last=performance.now(), scene=-1, language='';
  const tracks=titles.map((title,i)=>({id:`demo-${i}`,title,artist:'Auralis',album:'Quiet Geometry',durationSeconds:204+i*13,fileName:`demo-${i}.flac`,extension:'.flac',sizeBytes:24000000,dateAdded:'2026-09-01T00:00:00Z',coverUrl:new URL('assets/public-preview.svg',location.href).href}));
  const quality={codec:'FLAC',bitrateKbps:941,sampleRateHz:48000,bitsPerSample:24,channels:2,isLossless:true,isAverageBitrate:true};
  const song=window.AuralisDemoSong;
  const chinese=()=>language==='zh-CN'&&!!song;
  const activeId=()=>chinese()?'dahai-demo':'demo-0';
  const click=id=>document.getElementById(id)?.click();
  function settle(){if(!running)for(const animation of document.getAnimations?.()||[]){if(Number.isFinite(animation.effect?.getTiming().iterations))try{animation.finish();}catch{}}}
  // This is a visual demonstration, not an audio playback session.
  function playback(){api.setPlaybackState({id:activeId(),isPlaying:running,currentTime:time,duration:chinese()?song.duration:204,audioInformation:chinese()?{codec:'MP3',bitrateKbps:128,sampleRateHz:44100,channels:2,bitsPerSample:null,isLossless:false,isAverageBitrate:false}:quality});settle();}
  function loop(){
    if(!running)return;
    const now=performance.now();time+=(now-last)/1000;last=now;
    if(time>=(chinese()?song.duration-10:194))time=6;
    playback();timer=setTimeout(loop,250);
  }
  function play(value){
    if(running===value)return;
    if(running)time+=(performance.now()-last)/1000;
    running=value;clearTimeout(timer);timer=null;last=performance.now();
    document.body.dataset.demoPaused=String(!running);playback();
    if(!running)document.querySelector('.vinyl-disc').getAnimations().forEach(animation=>animation.pause());
    if(running)timer=setTimeout(loop,250);
  }
  // Existing delegated setting controls invoke the real layout handler.
  const settings=document.createElement('div');settings.hidden=true;settings.dataset.setting='playerStyle';
  for(const style of ['record','immersive']){const button=document.createElement('button');button.dataset.value=style;settings.append(button);}
  function setScene(index){
    if(index===scene)return;
    scene=index;const [theme,style,panel]=scenes[index];
    if(document.getElementById('queuePanel').classList.contains('open'))click('closeQueueButton');
    document.getElementById('lyricsOptionsMenu').classList.remove('open');
    document.getElementById('lyricsOptionsMenu').setAttribute('aria-hidden','true');
    // Settings events are delegated on pageContent, which language changes may re-render.
    document.getElementById('pageContent').append(settings);
    settings.querySelector(`[data-value="${style}"]`).click();
    root.dataset.demoThemeChange='true';
    if(root.dataset.theme!==theme)document.querySelector('#fullscreenThemeSelector [data-day-night-toggle]').click();
    if(panel==='queue')click('overlayQueueButton');
    if(panel==='lyrics')click('lyricsOptionsButton');
  }
  api.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'});
  api.receiveLibrary(tracks);api.setPlatformConfiguration({providers:[]});api.playLocalTrack('demo-0');
  api.setLyrics({trackId:'demo-0',source:'Local LRC',selectedSource:'local',isSynced:true,instrumental:false,
    lines:Array.from({length:35},(_,i)=>({timeSeconds:i*6,text:lines[i%lines.length]}))});
  function selectLanguage(next){
    play(false);language=next;time=18;
    const locale=next==='zh-CN'?'zh-CN':'en-US';api.setUiLanguageState({preference:locale,resolvedLanguage:locale});
    const localized=chinese()?{...tracks[0],id:activeId(),title:song.title,artist:song.artist,album:song.album,durationSeconds:song.duration,extension:'.mp3',fileName:'dahai-demo.mp3',sizeBytes:3857975,coverUrl:new URL('../assets/dahai/cover.jpg',location.href).href}:tracks[0];
    api.receiveLibrary([localized,...tracks.slice(1)]);api.playLocalTrack(activeId());
    api.setLyrics({trackId:activeId(),source:'Local LRC',selectedSource:'local',isSynced:true,instrumental:false,lines:chinese()?song.lines:Array.from({length:35},(_,i)=>({timeSeconds:i*6,text:lines[i%lines.length]}))});
    playback();
  }
  playback();click('nowPlayingButton');document.body.dataset.demoPaused='true';setScene(0);
  window.addEventListener('message',event=>{
    if(event.source!==parent||event.origin!==location.origin)return;
    const m=event.data;
    if(m?.type==='auralis-demo-ping'){parent.postMessage({type:'auralis-demo-ready'},location.origin);return;}
    if(m?.type!=='auralis-demo-state'||!Number.isInteger(m.scene)||m.scene<0||m.scene>=scenes.length||typeof m.running!=='boolean'||!['en','zh-CN'].includes(m.language))return;
    if(language!==m.language)selectLanguage(m.language);
    play(m.running&&!document.hidden&&!matchMedia('(prefers-reduced-motion: reduce)').matches);setScene(m.scene);playback();
  });
  document.addEventListener('visibilitychange',()=>{if(document.hidden)play(false);});
  document.addEventListener('load',settle,true);
  window.addEventListener('pagehide',()=>play(false));
  parent.postMessage({type:'auralis-demo-ready'},location.origin);
})();
