// A still, live-rendered settings preview; no clock, native host or media playback.
(() => {
  'use strict';
  const api=window.Auralis;
  if(!api)return;
  const allowed=['blue','teal','violet','coral','amber'];
  let language='en';
  api.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'});
  api.receiveLibrary([{id:'custom-demo',title:'Quiet Geometry',artist:'Auralis',album:'Quiet Geometry',fileName:'demo.flac',extension:'.flac',size:24000000,durationSeconds:204,dateAdded:'2026-09-01T00:00:00Z',coverUrl:new URL('assets/public-preview.svg',location.href).href}]);
  api.setPlatformConfiguration({providers:[]});api.playLocalTrack('custom-demo');
  api.setPlaybackState({id:'custom-demo',isPlaying:false,currentTime:46,duration:204});
  // This frame boots with reduced motion: both native UI route transitions commit synchronously.
  document.querySelector('[data-page="settings"]').click();
  document.querySelector('[data-settings-section="fullscreen"]').click();
  const apply=accent=>{
    const button=document.createElement('button');button.hidden=true;button.dataset.accent=accent;
    document.getElementById('pageContent').append(button);button.click();button.remove();
    if(language==='zh-CN'&&window.AuralisDemoSong){
      const song=window.AuralisDemoSong,preview=document.querySelector('[data-fullscreen-preview]');
      preview.querySelector('figcaption strong').textContent=song.title;
      preview.querySelector('figcaption small').textContent=`${song.artist} · 设置预览，不播放音频`;
      preview.querySelector('.fullscreen-preview-track strong').textContent=song.title;
      preview.querySelector('.fullscreen-preview-track span').textContent=song.artist;
      preview.querySelectorAll('.fullscreen-preview-lyrics > *').forEach((el,i)=>el.textContent=song.lines.filter(line=>line.timeSeconds>=43)[i].text);
      preview.querySelector('.fullscreen-preview-cover').style.backgroundImage=`url("${new URL('../assets/dahai/cover.jpg',location.href).href}")`;
      preview.querySelector('.fullscreen-preview-cover').style.backgroundSize='cover';
    }
  };
  window.addEventListener('message',event=>{
    if(event.source!==parent||event.origin!==location.origin)return;
    const m=event.data;
    if(m?.type==='auralis-customize-ping'){parent.postMessage({type:'auralis-customize-ready'},location.origin);return;}
    if(m?.type!=='auralis-customize-state'||!allowed.includes(m.accent)||!['en','zh-CN'].includes(m.language))return;
    if(language!==m.language){language=m.language;const locale=language==='zh-CN'?'zh-CN':'en-US';api.setUiLanguageState({preference:locale,resolvedLanguage:locale});}
    apply(m.accent);
    parent.postMessage({type:'auralis-customize-applied',accent:m.accent,language},location.origin);
  });
  parent.postMessage({type:'auralis-customize-ready'},location.origin);
})();
