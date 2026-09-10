/* A presentation only: no playback engine, accounts, analytics, or external runtime dependencies. */
(() => {
  'use strict';
  const zh = {
    skip:'跳转到内容',navExperience:'播放器体验',navComponents:'模块化设计',
    eyebrow:'以 FLUENT 为形，为你的音乐而生。',headline1:'你的音乐。',headline2:'恰如其分的归属。',
    intro1:'少一点界面，多一点感受。',intro2:'一个围绕聆听而设计的 Windows 播放器。',
    github:'在 GitHub 探索',meet:'认识你的播放器',platform:'Windows 10 / 11 · 开源 · 本地优先',
    chapter1:'深夜唱片',chapter2:'切入晨光',chapter3:'封面主场',chapter4:'深色沉浸',chapter5:'下一首',chapter6:'歌词随心',demoNote:'深浅色实时切换 · 其余布局与面板使用截图轮播',
    experienceEyebrow:'熟悉的感觉，全新的视角。',experienceTitle1:'各就其位。',experienceTitle2:'让音乐自在发生。',experienceIntro:'把你的收藏整理好，让正在播放的音乐始终触手可及。',
    libraryCaption:'清晰的曲库，同一个持续的播放会话。',libraryTitle:'属于你的音乐收藏。',libraryBody:'按歌曲、专辑和艺术家浏览。收藏喜欢的音乐，重温最近播放，再把心仪曲目整理成自己的歌单。',
    sessionTitle:'不打断这一刻。',sessionBody:'底部播放栏和右侧队列让下一首触手可及。自由浏览页面，同一个播放会话始终伴随。',
    windowsTitle:'自然融入 Windows。',windowsBody:'熟悉的导航、浅色与深色主题、媒体按键、系统媒体控制和可选托盘行为。一个真正属于桌面的播放器。',
    listenEyebrow:'少一些纷扰，多一些沉浸。',listenTitle1:'让世界，',listenTitle2:'暂时退后一点。',listenBody:'展开专辑封面的沉浸视图，或让唱片与歌词相伴。两种布局，同一首音乐，多一点聆听的空间。',
    lyricsNote:'歌词动效为示意。本地 LRC 依据文件中的时间信息同步；TXT 保持可读，不虚构时间戳。',qualityNote:'数值仅作演示，不是音质实测。',
    detail1Title:'看见声音的细节。',detail1Body:'展示可用的码率、采样率、位深和声道，而不只是一个“无损”标签。',detail2Title:'让歌词陪在身边。',detail2Body:'本地歌词、可调整的时间偏移，以及独立的桌面歌词显示。',detail3Title:'始终由你掌控。',detail3Body:'曲库与展开播放器共享播放模式、音量和进度。',
    customEyebrow:'你的播放器，你的习惯。',customTitle1:'换一种心情。',customTitle2:'仍然是你的样子。',customBody:'选择布局、强调色和背景，调整歌词的呈现方式。先看看效果，再回到音乐。',customNote:'浅色、深色与跟随系统主题；完整、减少与跟随系统的动效。原生窗口材质取决于 Windows 支持。',customCaption:'预览一种氛围，不打断当前曲目。',
    componentEyebrow:'协作有序，边界清晰。',componentTitle1:'一个播放器。',componentTitle2:'更多生长空间。',componentIntro:'富有表现力的播放页面，由职责清晰的组件支撑。默认播放后端已内置，加入你的音乐即可开始。',
    component1Title:'界面与体验',component1Body:'曲库、播放页面、队列和交互由应用统一管理。',component2Title:'播放组件',component2Body:'专用组件负责媒体会话，默认实现随播放器一起提供。',component3Title:'传输组件',component3Body:'媒体访问使用独立契约，与界面及播放会话分开。',componentNote:'可选组件需要兼容的安装包、明确的信任授权和重启。组件在进程内运行，并非安全沙箱。组件化不代表可以任意替换播放页布局。',
    closingEyebrow:'给你的音乐，多留一点空间。',closingTitle:'音乐，自在这里。',closingBody:'探索 Auralis，阅读使用文档，或一起参与它的下一步。',guide:'阅读使用指南',releaseNote:'当前提供源码预览。此仓库尚未提供具有公开受信任签名的 Windows 安装程序。',footer:'为你的聆听方式而设计。',license:'开源 · MIT 许可 ↗',
    pauseMotion:'暂停动画',resumeMotion:'继续动画',reducedMotion:'已遵循减少动态效果',
  };
  const en = Object.fromEntries([...document.querySelectorAll('[data-i18n]')].map(el=>[el.dataset.i18n,el.textContent]));
  Object.assign(en,{resumeMotion:'Resume animation',reducedMotion:'Reduced motion respected'});
  const motionButton=document.getElementById('motion'),languageButton=document.getElementById('language');
  const chapters=[...document.querySelectorAll('[data-chapter]')];
  const frame=document.getElementById('player-demo'),stage=document.getElementById('player-stage');
  let demoReady=false;
  const reduce=window.matchMedia('(prefers-reduced-motion: reduce)');
  const lyrics=[...document.querySelectorAll('[data-lyric]')];
  let language='en',paused=false,scene=0,heroVisible=true,lyricsVisible=false,sceneTimer=null,lyricTimer=null,lyric=1;
  const words=()=>language==='zh-CN'?zh:en;
  const running=()=>!paused&&!reduce.matches&&!document.hidden;
  function syncDemo(){if(demoReady)frame.contentWindow.postMessage({type:'auralis-demo-state',scene:Math.min(scene,1),language,running:scene<2&&running()&&heroVisible,audio:window.AuralisFeaturedAudio?.snapshot()},window.location.origin);}
  window.addEventListener('auralis-audio-change',syncDemo);
  function syncPicture(){
    stage.dataset.presentation=scene<2?'live':'image';
    const locale=language==='zh-CN'?'zh-CN':'en-US';
    window.AuralisGallery.show(document.getElementById('scene-gallery'),`assets/gallery/scene-${scene}-${locale}.png`,words()[`chapter${scene+1}`]);
  }
  function resizeDemo(){frame.style.transform=`scale(${stage.clientWidth/1280})`;}
  window.addEventListener('message',event=>{if(event.source===frame.contentWindow&&event.origin===window.location.origin&&event.data?.type==='auralis-demo-ready'){demoReady=true;resizeDemo();stage.classList.add('demo-ready');syncDemo();}});
  frame.addEventListener('load',()=>frame.contentWindow.postMessage({type:'auralis-demo-ping'},window.location.origin));
  if('ResizeObserver' in window)new ResizeObserver(resizeDemo).observe(stage);
  else window.addEventListener('resize',resizeDemo);
  resizeDemo();
  function updateMotionButton(){
    motionButton.disabled=reduce.matches;
    motionButton.setAttribute('aria-pressed',String(paused||reduce.matches));
    motionButton.querySelector('span').textContent=words()[reduce.matches?'reducedMotion':paused?'resumeMotion':'pauseMotion'];
  }
  function setScene(next){
    scene=next;
    chapters.forEach((el,i)=>{el.classList.toggle('active',i===scene);el.setAttribute('aria-pressed',String(i===scene));});
    syncPicture();syncDemo();
  }
  function scheduleScenes(){
    clearTimeout(sceneTimer);sceneTimer=null;
    document.body.dataset.story=running()&&heroVisible?'playing':'paused';
    syncDemo();
    if(running()&&heroVisible)sceneTimer=setTimeout(()=>{setScene((scene+1)%chapters.length);scheduleScenes();},6500);
  }
  function scheduleLyrics(){
    clearTimeout(lyricTimer);lyricTimer=null;
    if(running()&&lyricsVisible)lyricTimer=setTimeout(()=>{
      lyric=(lyric+1)%lyrics.length;
      lyrics.forEach((el,i)=>el.classList.toggle('current',i===lyric));
      scheduleLyrics();
    },3200);
  }
  function updateMotion(){
    document.body.dataset.motion=running()?'playing':'paused';
    if(reduce.matches)document.querySelectorAll('.reveal').forEach(el=>el.classList.add('is-visible'));
    updateMotionButton();scheduleScenes();scheduleLyrics();
  }
  function setLanguage(next){
    language=next;document.documentElement.lang=next;
    document.querySelectorAll('[data-i18n]').forEach(el=>{const value=words()[el.dataset.i18n];if(value!==undefined)el.textContent=value;});
    document.querySelectorAll('[data-shot]').forEach(img=>{img.src=language==='zh-CN'&&img.dataset.shot==='player'?'assets/gallery/scene-0-zh-CN.png':`assets/${img.dataset.shot}-${language==='zh-CN'?'zh-CN':'en-US'}.png`;});
    const alt=language==='zh-CN'?{player:'Auralis 深色唱片与歌词播放页面。',library:'Auralis 本地曲库与持续可用的底部播放栏。',customize:'Auralis 全屏播放器设置及外观预览。'}:{player:'Auralis record-and-lyrics playback page, in the dark theme.',library:'Auralis local song library with its persistent playback bar.',customize:'Auralis player customization with a visual preview.'};
    document.querySelectorAll('[data-shot]').forEach(img=>img.alt=alt[img.dataset.shot]);
    languageButton.textContent=language==='zh-CN'?'EN':'中文';languageButton.lang=language==='zh-CN'?'en':'zh-CN';
    languageButton.setAttribute('aria-label',language==='zh-CN'?'Switch to English':'切换为中文');
    document.getElementById('guide-link').href=`https://github.com/ZCH-czc/winplayer/blob/main/docs/USER_GUIDE.${language==='zh-CN'?'zh-CN':'en-US'}.md`;
    document.title=language==='zh-CN'?'Auralis — 你的音乐，恰如其分的归属。':'Auralis — Your music. Beautifully at home.';
    document.querySelector('meta[name="description"]').content=language==='zh-CN'?'Auralis：以 Fluent 为灵感的 Windows 音乐播放器，本地曲库、沉浸式播放、同步歌词与模块化播放组件。':'Meet Auralis: a Fluent-inspired Windows music player with a local library, immersive playback pages, synchronized lyrics and modular playback.';
    updateMotionButton();syncPicture();syncDemo();
    window.dispatchEvent(new Event('auralis-language-change'));
  }
  languageButton.addEventListener('click',()=>setLanguage(language==='en'?'zh-CN':'en'));
  motionButton.addEventListener('click',()=>{paused=!paused;updateMotion();});
  chapters.forEach(button=>button.addEventListener('click',()=>{setScene(Number(button.dataset.chapter));scheduleScenes();}));
  reduce.addEventListener('change',updateMotion);
  document.addEventListener('visibilitychange',updateMotion);
  // Reveal once. Animation loops also sleep outside the viewport; no hidden-page RAF loops.
  if('IntersectionObserver' in window){
    document.body.classList.add('js-ready');
    const revealObserver=new IntersectionObserver(entries=>{entries.forEach(entry=>{if(entry.isIntersecting){entry.target.classList.add('is-visible');revealObserver.unobserve(entry.target);}});},{threshold:.08});
    document.querySelectorAll('.reveal').forEach(el=>revealObserver.observe(el));
    const visibilityObserver=new IntersectionObserver(entries=>{entries.forEach(entry=>{
      entry.target.dataset.offscreen=String(!entry.isIntersecting);
      if(entry.target.id==='showcase'){heroVisible=entry.isIntersecting;scheduleScenes();}
      if(entry.target.classList.contains('listening')){lyricsVisible=entry.isIntersecting;scheduleLyrics();}
    });},{threshold:0});
    document.querySelectorAll('#showcase,.listening,.customize,.components,.closing,.hero-copy').forEach(el=>visibilityObserver.observe(el));
  }else{lyricsVisible=true;}
  window.addEventListener('pagehide',()=>{clearTimeout(sceneTimer);clearTimeout(lyricTimer);if(demoReady)frame.contentWindow.postMessage({type:'auralis-demo-state',scene,language,running:false},window.location.origin);});
  window.addEventListener('pageshow',updateMotion);
  syncPicture();updateMotion();
})();
