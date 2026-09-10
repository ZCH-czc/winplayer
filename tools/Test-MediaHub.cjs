// Optional browser regression harness. Uses Playwright from NODE_PATH; no account or personal media.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const root = process.env.AURALIS_UI_ROOT ? path.resolve(process.env.AURALIS_UI_ROOT) : path.resolve(__dirname, '../Auralis/wwwroot');
const output = path.resolve(__dirname, '../artifacts/media-hub-tests');
const mime = { '.html':'text/html', '.css':'text/css', '.js':'text/javascript', '.svg':'image/svg+xml', '.jpg':'image/jpeg', '.png':'image/png' };
async function until(test, message) { const deadline=Date.now()+4000; while(!test()) { if(Date.now()>deadline) throw Error(message); await new Promise(r=>setTimeout(r,20)); } }
async function checkSurface(page, options = false) {
  await page.locator('.media-hub-dialog').evaluate(n=>Promise.allSettled(n.getAnimations().map(a=>a.finished)));
  const geometry = await page.locator('.media-hub-dialog').evaluate(dialog => {
    const style=getComputedStyle(dialog), header=dialog.querySelector('header'), body=dialog.querySelector('.media-dialog-body');
    const close=dialog.querySelector('[data-media-action="close"]');
    const rect=dialog.getBoundingClientRect();
    return { gutter:style.scrollbarGutter, overflow:style.overflowY, radius:style.borderRadius,
      headerWidth:header.getBoundingClientRect().width, innerWidth:dialog.clientWidth,
      headerBackground:getComputedStyle(header).backgroundColor,
      bodyOverflow:body.scrollWidth-body.clientWidth, bottom:rect.bottom, viewport:innerHeight,
      closeWidth:close.clientWidth, closeHeight:close.clientHeight, closeSvg:!!close.querySelector('svg'),
      switches:[...dialog.querySelectorAll('.media-option')].map(row=>({
        font:parseFloat(getComputedStyle(row.querySelector('small')).fontSize),
        margin:getComputedStyle(row.querySelector('input')).margin,
        right:row.getBoundingClientRect().right-row.querySelector('input').getBoundingClientRect().right
      })) };
  });
  assert.equal(geometry.gutter,'auto','shell must not reserve a phantom scrollbar');
  assert.equal(geometry.overflow,'hidden','only dialog body scrolls');
  assert.equal(geometry.radius,'8px','one shared popup radius');
  assert(Math.abs(geometry.headerWidth-geometry.innerWidth)<1,'header spans the full panel without a right stripe');
  assert.equal(geometry.headerBackground,'rgba(0, 0, 0, 0)','header uses the shell material');
  assert(geometry.bodyOverflow<=1,'body has no horizontal clipping');
  assert(geometry.bottom<=geometry.viewport,'dialog fits the viewport');
  assert(geometry.closeWidth>=38&&geometry.closeHeight>=38&&geometry.closeSvg,'40px SVG close control');
  if(options) for(const control of geometry.switches) {
    assert(control.font>=12,'secondary text remains readable');
    assert.equal(control.margin,'0px','browser checkbox margins cannot displace the switch');
    assert(Math.abs(control.right)<1,'switch aligns with the row edge');
  }
}
async function checkDanmaku(page, reduced) {
  await page.evaluate(() => {
    const base=performance.now();
    window.testMediaTime=()=>.05+(performance.now()-base)/1000;
    const beat=()=>window.Auralis.setPlaybackState({id:'part2',isPlaying:true,currentTime:window.testMediaTime(),duration:60});
    beat(); window.testMediaTimer=setInterval(beat,100);
  });
  await page.waitForFunction(()=>document.querySelectorAll('#danmakuLayer span').length>=2);
  if(reduced) assert(await page.locator('#danmakuLayer span').first().evaluate(n=>n.classList.contains('danmaku-static')),'reduced motion uses static text');
  await page.evaluate(()=>{
    clearInterval(window.testMediaTimer);
    window.testPausedTime=window.testMediaTime()+.08;
    window.Auralis.setPlaybackState({id:'part2',isPlaying:false,currentTime:window.testPausedTime,duration:60});
  });
  const frozen=await page.locator('#danmakuLayer').innerHTML();
  await page.waitForTimeout(350);
  assert.equal(await page.locator('#danmakuLayer').innerHTML(),frozen,'pause freezes danmaku lanes and positions');
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'part2',isPlaying:true,currentTime:window.testPausedTime,duration:60}));
  await page.waitForTimeout(1000);
  const stalled=await page.locator('#danmakuLayer').innerHTML();
  await page.waitForTimeout(250);
  assert.equal(await page.locator('#danmakuLayer').innerHTML(),stalled,'missing native progress freezes danmaku');
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'part2',isPlaying:true,currentTime:8.8,duration:60}));
  await page.waitForFunction(()=>document.querySelector('#danmakuLayer').childElementCount===0);
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'part2',isPlaying:true,currentTime:0,duration:60}));
  await page.waitForTimeout(100);
  assert.equal(await page.locator('#danmakuLayer span').count(),0,'backward seek discards old lanes');
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'part2',isPlaying:false,currentTime:0,duration:60}));
}
const server = http.createServer(async (req,res) => {
  try {
    const name = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const file = path.resolve(root, '.' + (name === '/' ? '/index.html' : name));
    if (!file.startsWith(root + path.sep)) throw Error('outside root');
    res.setHeader('Content-Type', mime[path.extname(file)] || 'application/octet-stream'); res.end(await fs.readFile(file));
  } catch { res.writeHead(404); res.end(); }
});
const online = (handle, providerId='bili', title='现场测试') => ({ id:handle, handle, kind:'online', providerId, title, artist:'测试歌手', album:'测试专辑', durationSeconds:90, isPlayable:true, hasMusicVideo:true });
const lists = [{ id:'list1', name:'跨平台收藏', entries:[
  { entryId:'local1', track:{ id:'ui-test-1', title:'本地测试', artist:'测试歌手', album:'测试专辑', durationSeconds:180 } },
  { entryId:'bili1', track:{...online('saved-bili'),hasMusicVideo:false} },
  { entryId:'yt1', track:online('saved-ytm','ytm','YouTube 测试') }
] }];
(async () => {
  await fs.mkdir(output, {recursive:true});
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  // Procedural fixture: this test must also run against the public build, which does not
  // distribute the private album artwork. An opaque frame exercises the same canvas path.
  const framePage = await browser.newPage({viewport:{width:320,height:180}});
  await framePage.setContent('<body style="margin:0;background:#245567"><svg width="320" height="180"><rect x="40" y="40" width="240" height="100" fill="#80cbd2"/></svg></body>');
  const testFrame = await framePage.screenshot();
  await framePage.close();
  try {
    for (const theme of ['light','dark']) for (const scale of [1,1.5,2]) {
      const context = await browser.newContext({ viewport:scale===2?{width:720,height:480}:{width:1280,height:820}, deviceScaleFactor:scale, reducedMotion:scale===1.5?'reduce':'no-preference' });
      const page = await context.newPage(); const errors = [], messages = [];
      const openVideo = async () => {
        await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
        if(theme==='dark') { await page.locator('#largeCover').focus(); await page.keyboard.press('Enter'); }
        else await page.locator('#largeCover').click();
      };
      let fixtures=structuredClone(lists), failNextComments=false, holdVideo=false, holdMutation=false, holdComments=false, holdPlayback=false;
      page.on('pageerror', e => errors.push(e.message));
      await page.route('**/*', route => route.request().url().startsWith(origin) ? route.continue() : route.abort());
      await page.route('https://video.auralis.local/**', route => route.fulfill({
        contentType:'image/png', headers:{'Access-Control-Allow-Origin':'*'},
        body:testFrame
      }));
      await page.route('https://platform-art.auralis.local/saved-*', route=>route.fulfill({contentType:'image/png',body:testFrame}));
      await page.exposeFunction('testBridge', async m => {
        messages.push(m);
        if (m.action === 'playPlatformResult' && !holdPlayback) await page.evaluate(payload => window.Auralis.setPlatformPlaybackResult(payload),{handle:m.handle,success:true,item:fixtures.flatMap(p=>p.entries.map(e=>e.track)).find(t=>t.handle===m.handle)});
        if (m.action === 'platformSearchTracks') await page.evaluate(m => window.Auralis.setPlatformSearchResult({...m,items:[{id:'loading-test',handle:'loading-test',title:'加载状态测试',artist:'测试',album:'测试',durationSeconds:90,providerId:m.providerId,isPlayable:true,hasMusicVideo:true}],totalCount:1}),m);
        if (m.action === 'requestSavedPlaylists') await page.evaluate(items => window.Auralis.setSavedPlaylists({items,action:'requestSavedPlaylists'}),fixtures);
        if (m.action === 'hydrateSavedTrack') {
          const track=fixtures.flatMap(p=>p.entries.map(e=>e.track)).find(t=>t.handle===m.handle);
          if(track) Object.assign(track,{coverUrl:`https://platform-art.auralis.local/${m.handle}`,hasMusicVideo:true});
          await page.evaluate(payload=>window.Auralis.setSavedTrackDetails(payload),{handle:m.handle,item:track});
        }
        if (m.action === 'createSavedPlaylist') {
          if(holdMutation) return;
          fixtures.push({id:'created',name:m.name,entries:[]});
          await page.evaluate(({items,requestId})=>window.Auralis.setSavedPlaylists({items,requestId,action:'createSavedPlaylist'}),{items:fixtures,requestId:m.requestId});
        }
        if (m.action === 'requestPlatformExtras') {
          if (m.kind === 'comments' && holdComments) return;
          const error=m.kind==='comments'&&failNextComments ? {message:'测试网络暂时不可用'} : null;
          if(error) failNextComments=false;
          await page.evaluate(({m,error}) => window.Auralis.setPlatformExtras({...m,error,nextPageHandle:m.kind==='comments'&&!m.pageHandle?'comments-next':null,items:m.kind==='parts' ? [1,2].map(n=>({id:`part${n}`,handle:`part${n}`,kind:'online',providerId:'bili',title:`P${n} · 片段`,artist:'测试',durationSeconds:60,hasMusicVideo:true})) : m.kind==='comments' ? Array.from({length:20},(_,i)=>({author:`测试作者 ${i}`,text:'<img src=x onerror=alert(1)> 纯文本评论',likeCount:3})) : Array.from({length:50},(_,i)=>({seconds:1+i*.2,text:`测试弹幕 ${i}`}))}), {m,error});
        }
        if (m.action === 'setEmbeddedVideo' && (!holdVideo||!m.enabled)) await page.evaluate(m => window.Auralis.setEmbeddedVideoState({...m}),m);
      });
      await page.addInitScript(({record}) => { localStorage.setItem('auralis:platform-search-provider','bili'); localStorage.setItem('auralis:player-style',record?'record':'immersive'); window.chrome = {webview:{postMessage:m => window.testBridge(m)}}; },{record:theme==='dark'});
      await page.goto(`${origin}/?ui-test=1&ui-test-theme=${theme}`);
      await page.evaluate(() => window.Auralis.setPlatformConfiguration({providers:[{
        id:'bili',name:'Bilibili fixture',capabilities:['TrackSearch','PlaylistBrowse','Authentication','NativeLogin','MediaExtras','Comments','VideoResolution'],
        authentication:{status:'signedin',accountDisplayName:'Fixture'},playlists:[]
      }]}));
      await page.waitForSelector('#pageContent .page-view');
      await page.locator('#splashScreen').waitFor({state:'hidden'}).catch(()=>{});
      // One shared four-state model: clicking either surface updates both icon paths and labels.
      for (const mode of ['all','one','shuffle','off']) {
        await page.locator('#shuffleButton').click();
        assert.equal(await page.locator('#shuffleButton').getAttribute('data-playback-mode'),mode);
        assert.equal(await page.locator('#overlayRepeatButton').getAttribute('data-playback-mode'),mode);
        assert.equal(await page.locator('#shuffleButton svg').innerHTML(),await page.locator('#overlayRepeatButton svg').innerHTML());
      }
      await page.locator('[data-page="onlinePlaylists"]').click();
      await page.waitForSelector('.online-provider-filter');
      await page.waitForTimeout(450);
      assert.equal(await page.locator('.online-provider-filter').first().evaluate(n=>getComputedStyle(n).borderRadius),'6px');
      await page.screenshot({path:path.join(output,`${theme}-${scale}-online.png`)});
      await page.locator('[data-page="savedPlaylists"]').click();
      await page.waitForSelector('.saved-track-row');
      assert.equal(await page.locator('.saved-track-row').count(),3);
      await page.waitForFunction(()=>document.querySelector('.saved-track-play[data-index="1"] .queue-cover').style.backgroundImage.includes('platform-art.auralis.local/saved-bili'));
      assert(messages.some(m=>m.action==='hydrateSavedTrack'&&m.handle==='saved-bili'),'visible saved entry restores metadata');
      await page.locator('[data-media-action="new"]').click();
      await page.locator('#savedPlaylistName').fill('输入中的歌单名称');
      await page.evaluate(items=>window.Auralis.setSavedPlaylists({items,action:'requestSavedPlaylists'}),fixtures);
      assert.equal(await page.locator('#savedPlaylistName').inputValue(),'输入中的歌单名称');
      assert.equal(await page.evaluate(()=>document.activeElement.id),'savedPlaylistName');
      await checkSurface(page);
      await page.screenshot({path:path.join(output,`${theme}-${scale}-create-playlist.png`)});
      await page.keyboard.press('Enter');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      assert.equal(await page.locator('.saved-playlist-tabs .active').getAttribute('data-id'),'created');
      await page.locator('[data-media-action="select"][data-id="list1"]').click();
      holdMutation=true;
      await page.locator('[data-media-action="new"]').click();
      await page.locator('#savedPlaylistName').fill('旧请求');
      await page.keyboard.press('Enter');
      await until(()=>messages.filter(m=>m.action==='createSavedPlaylist').length>=2,'old create request');
      const oldMutation=messages.filter(m=>m.action==='createSavedPlaylist').at(-1);
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      await page.locator('[data-media-action="new"]').click();
      await page.locator('#savedPlaylistName').fill('保留新表单');
      await page.keyboard.press('Enter');
      await until(()=>messages.filter(m=>m.action==='createSavedPlaylist').length>=3,'replacement create request');
      const newMutation=messages.filter(m=>m.action==='createSavedPlaylist').at(-1);
      await page.evaluate(({m,items})=>window.Auralis.setSavedPlaylists({...m,items}),{m:oldMutation,items:fixtures});
      assert(await page.locator('.media-hub-dialog').isVisible(),'old write success cannot close new form');
      assert(await page.locator('#newSavedPlaylistForm button').isDisabled(),'new request remains pending');
      await page.evaluate(m=>window.Auralis.setSavedPlaylists({...m,error:{message:'模拟写入失败'}}),newMutation);
      assert.equal(await page.locator('#savedPlaylistName').inputValue(),'保留新表单');
      assert(await page.locator('#newSavedPlaylistForm button').isEnabled(),'matching write failure enables retry');
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      holdMutation=false;
      await page.screenshot({path:path.join(output,`${theme}-${scale}-saved.png`)});
      await page.locator('[data-media-action="saved-play"][data-index="1"]').click();
      await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='现场测试');
      await page.waitForFunction(()=>document.querySelector('#miniCover').style.backgroundImage.includes('platform-art.auralis.local/saved-bili'));
      await page.evaluate(item=>{
        window.Auralis.setPlaybackState({id:'saved-bili',isPlaying:false,currentTime:12,duration:90});
        window.Auralis.setLyrics({trackId:'saved-bili',source:'test',lines:[{timeSeconds:0,text:'收藏曲目的歌词必须保留'}]});
        window.savedLyricsMarkup=document.querySelector('#lyricsViewport').innerHTML;
        window.Auralis.setSavedTrackDetails({handle:'saved-bili',item});
      },fixtures[0].entries[1].track);
      assert.equal(await page.locator('#currentTime').textContent(),'0:12','late cover preserves playback progress');
      assert(await page.evaluate(()=>document.querySelector('#lyricsViewport').innerHTML===window.savedLyricsMarkup),'late cover preserves lyric DOM');
      await page.locator('#nextButton').click();
      await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='YouTube 测试');
      await page.locator('[data-media-action="saved-play"][data-index="1"]').click();
      await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='现场测试');
      // Actual fullscreen open control; the native bridge remains an isolated mock.
      await page.locator('#miniCover').click();
      await page.waitForSelector('#nowPlayingOverlay.open');
      await page.locator('#overlayRepeatButton').click();
      assert.equal(await page.locator('#shuffleButton').getAttribute('data-playback-mode'),'all');
      await page.locator('[data-media-action="parts"]').click();
      await checkSurface(page);
      await page.screenshot({path:path.join(output,`${theme}-${scale}-parts.png`)});
      await page.waitForSelector('[data-media-action="part"][data-index="1"]');
      await page.locator('[data-media-action="part"][data-index="1"]').click();
      await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='P2 · 片段');
      assert(messages.some(m=>m.action==='playPlatformResult'&&m.handle==='part2'));
      await page.locator('[data-media-action="comments"]').click();
      await page.waitForSelector('.media-comments article');
      assert.equal(await page.locator('.media-comments img').count(),0,'untrusted HTML comment remains literal');
      await page.locator('.media-hub-dialog').evaluate(n=>Promise.allSettled(n.getAnimations().map(a=>a.finished)));
      const drawer=await page.locator('.media-hub-dialog').boundingBox();
      await checkSurface(page);
      const contextTop=await page.locator('.media-dialog-context').evaluate(n=>n.getBoundingClientRect().top);
      await page.screenshot({path:path.join(output,`${theme}-${scale}-comments-list.png`)});
      assert(Math.abs(drawer.x+drawer.width-(page.viewportSize().width-12))<2,'comments dock to right edge');
      assert.equal(await page.locator('.media-hub-dialog').evaluate(n=>getComputedStyle(n,'::backdrop').backdropFilter),'none','drawer does not blur the whole player');
      await page.evaluate(()=>{window.firstCommentNode=document.querySelector('.media-comments article');});
      failNextComments=true;
      await page.locator('.media-dialog-body').evaluate(n=>{n.scrollTop=n.scrollHeight;});
      await page.waitForSelector('.media-inline-error');
      assert.equal(await page.locator('.media-dialog-context').evaluate(n=>n.getBoundingClientRect().top),contextTop,'track context remains outside the scroller');
      assert.equal(await page.locator('.media-comments article').count(),20);
      await page.locator('[data-media-action="retry-extras"]').scrollIntoViewIfNeeded();
      const scrollBefore=await page.locator('.media-dialog-body').evaluate(n=>n.scrollTop);
      await page.locator('[data-media-action="retry-extras"]').click();
      await page.waitForFunction(()=>document.querySelectorAll('.media-comments article').length===40);
      const scrollAfter=await page.locator('.media-dialog-body').evaluate(n=>n.scrollTop);
      assert(Math.abs(scrollAfter-scrollBefore)<80,`pagination keeps scroll: ${scrollBefore} → ${scrollAfter}`);
      assert(await page.evaluate(()=>window.firstCommentNode===document.querySelector('.media-comments article')),'append preserves already-read nodes');
      // Only app-owned avatar URLs become image elements; missing/broken images fall back.
      holdComments=true;
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      await page.locator('[data-media-action="comments"]').click();
      const avatarRequest=messages.filter(m=>m.action==='requestPlatformExtras'&&m.kind==='comments').at(-1);
      await page.route('https://platform-art.auralis.local/avatar-test', route=>route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><rect width="40" height="40" fill="#569fc9"/></svg>'}));
      await page.evaluate(m=>window.Auralis.setPlatformExtras({...m,items:[{author:'有头像',text:'头像测试',avatarUrl:'https://platform-art.auralis.local/avatar-test'},{author:'缺头像',text:'兜底',avatarUrl:'https://untrusted.invalid/avatar.png'}]}),avatarRequest);
      await page.waitForFunction(()=>document.querySelector('.comment-avatar img')?.naturalWidth>0);
      assert.equal(await page.locator('.comment-avatar img').count(),1,'remote unproxied avatar is rejected');
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      await page.locator('[data-media-action="comments"]').click();
      const emoteRequest=messages.filter(m=>m.action==='requestPlatformExtras'&&m.kind==='comments').at(-1);
      await page.evaluate(m=>window.Auralis.setPlatformExtras({...m,items:[{author:'表情测试',text:'你好[打call]<script>不是代码</script>',emotes:[{text:'[打call]',url:'https://platform-art.auralis.local/avatar-test'},{text:'代码',url:'https://evil.invalid/a.png'}]}]}),emoteRequest);
      assert.equal(await page.locator('.comment-emote').count(),scale===1.5?0:1,'emote uses proxy, reduced motion retains text');
      assert((await page.locator('.comment-content').textContent()).includes('<script>不是代码</script>'),'comment text remains escaped');
      await page.locator('.media-hub-dialog').evaluate(n=>Promise.allSettled(n.getAnimations().map(a=>a.finished)));
      await page.screenshot({path:path.join(output,`${theme}-${scale}-comments.png`)});
      await page.keyboard.press('Escape');
      holdComments=false;
      await page.locator('[data-media-action="options"]').click();
      await page.locator('#coverVideoOption').check();
      await page.locator('#danmakuOption').check();
      await page.waitForTimeout(250); // Capture settled switch geometry, not the in-flight thumb.
      await checkSurface(page,true);
      await page.screenshot({path:path.join(output,`${theme}-${scale}-options.png`)});
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      await checkDanmaku(page,scale===1.5);
      await openVideo();
      await page.waitForFunction(()=>!document.querySelector('#embeddedVideoPage').hidden);
      await page.waitForFunction(()=>document.querySelector('#embeddedVideoStatus').textContent==='P2 · 片段');
      await until(()=>messages.some(m=>m.action==='setEmbeddedVideoBounds'&&m.visible&&m.width>40),'video bounds settle');
      await page.waitForFunction(()=>!document.querySelector('#embeddedVideoSurface').classList.contains('awaiting-picture'));
      assert(await page.locator('[data-media-action="video-retry"]').isHidden(),'successful video does not show a retry button');
      const settled=messages.filter(m=>m.action==='setEmbeddedVideoBounds'&&m.visible).at(-1);
      const box=await page.locator('#embeddedVideoSurface').boundingBox();
      assert(box.width>40 && box.height>40,'composited video has usable responsive geometry');
      await page.locator('[data-media-action="options"]').click();
      assert.equal(messages.filter(m=>m.action==='setEmbeddedVideoBounds').at(-1)?.visible,true,'modal preserves composited video');
      assert(await page.locator('#embeddedVideoSurface canvas').isVisible(),'video canvas remains beneath modal blur');
      assert(await page.locator('#embeddedVideoSurface #danmakuLayer').count()===1,'video owns the synchronized danmaku layer');
      assert.equal(await page.locator('#embeddedVideoSurface canvas').evaluate(n=>n.getContext('2d').getImageData(0,0,1,1).data[3]),255,'decoded image remains beneath modal');
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      await page.locator('[data-media-action="audio"]').click();
      await page.waitForFunction(()=>document.querySelector('#embeddedVideoPage').hidden);
      // A late native success must not reopen video after the user returned to audio.
      holdVideo=true;
      await openVideo();
      const late=messages.filter(m=>m.action==='setEmbeddedVideo'&&m.enabled).at(-1);
      await page.locator('[data-media-action="audio"]').click();
      await page.evaluate(m=>window.Auralis.setEmbeddedVideoState({...m}),late);
      await page.locator('#embeddedVideoPage').waitFor({state:'hidden'});
      assert(await page.locator('#embeddedVideoPage').isHidden());
      holdVideo=false;
      // End of P2 advances to P1 while fullscreen stays open, retiring old video/comments.
      await openVideo();
      await page.waitForFunction(()=>document.querySelector('#embeddedVideoStatus').textContent==='P2 · 片段');
      await page.locator('[data-media-action="comments"]').click();
      await page.waitForSelector('.media-comments article');
      const oldVideo=messages.filter(m=>m.action==='setEmbeddedVideo'&&m.enabled).at(-1);
      const oldComments=messages.filter(m=>m.action==='requestPlatformExtras'&&m.kind==='comments').at(-1);
      await page.evaluate(()=>window.Auralis.nativeEnded('part2'));
      await page.waitForFunction(()=>document.querySelector('#stageTitle').textContent==='P1 · 片段');
      await page.waitForFunction(()=>document.querySelector('.media-context').textContent==='P1 · 片段');
      assert(await page.locator('#embeddedVideoPage').isHidden(),'next audio cannot retain previous video');
      assert(await page.locator('#nowPlayingOverlay').evaluate(n=>n.classList.contains('open')),'auto next keeps fullscreen');
      await page.evaluate(({v,c})=>{window.Auralis.setEmbeddedVideoState(v);window.Auralis.setPlatformExtras({...c,items:[{author:'OLD',text:'stale comment'}]});window.Auralis.nativeEnded('part2');window.Auralis.setLyrics({trackId:'part2',lines:[{text:'OLD LYRICS'}]});}, {v:oldVideo,c:oldComments});
      assert.equal(await page.locator('#stageTitle').textContent(),'P1 · 片段');
      assert(!(await page.locator('.media-comments').textContent()).includes('stale comment'));
      assert(!(await page.locator('#lyricsScroll').textContent()).includes('OLD LYRICS'));
      assert(await page.locator('#embeddedVideoPage').isHidden());
      await page.keyboard.press('Escape');
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      // A response during dismissal cannot reverse the closing animation.
      holdComments=true;
      await page.locator('[data-media-action="comments"]').click();
      await until(()=>messages.filter(m=>m.action==='requestPlatformExtras'&&m.kind==='comments').at(-1)?.handle==='part1','pending comment for P1');
      const closingRequest=messages.filter(m=>m.action==='requestPlatformExtras'&&m.kind==='comments').at(-1);
      await page.keyboard.press('Escape');
      await page.evaluate(m=>window.Auralis.setPlatformExtras({...m,items:[{author:'LATE',text:'dismissal response'}]}),closingRequest);
      await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      holdComments=false;
      // Geometric centers remain stable through play/pause and both fullscreen layouts.
      for (let i=0;i<4;i++) {
        await page.locator('#overlayPlayButton').click();
        const offset=await page.locator('#overlayPlayButton').evaluate(b=>{const a=b.getBoundingClientRect(),g=b.querySelector('.play-glyph').getBoundingClientRect();return Math.abs((a.y+a.height/2)-(g.y+g.height/2));});
        assert(offset<1,'play glyph vertical center');
      }
      assert.deepEqual(await page.locator('#nowPlayingOverlay').evaluate(n=>[n.scrollLeft,n.scrollTop]),[0,0],'focus never pans fullscreen stage');
      for (const selector of ['.immersive-toolbar','.immersive-player','#mediaTools','#overlayPlayButton']) {
        const rect=await page.locator(selector).boundingBox(), size=page.viewportSize();
        assert(rect.x>=-1&&rect.y>=-1&&rect.x+rect.width<=size.width+1&&rect.y+rect.height<=size.height+1,`${selector} stays within viewport`);
      }
      await page.waitForTimeout(450); // Play/pause halo and glyph finish before visual baseline.
      const toolsBox=await page.locator('#mediaTools').boundingBox();
      const visual=await page.locator('.record-visual').boundingBox();
      assert(visual.y>=toolsBox.y+toolsBox.height+4,'media toolbar leaves room for the cover/record');
      await page.screenshot({path:path.join(output,`${theme}-${scale}-fullscreen.png`)});
      holdComments=true;
      await page.locator('[data-media-action="comments"]').click();
      const retiredComments=messages.findLast(m=>m.action==='requestPlatformExtras'&&m.kind==='comments');
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
      assert(await page.locator('.media-hub-dialog').isHidden(),'removed comment capability closes its open panel');
      assert.equal(await page.locator('#largeCover').getAttribute('role'),'img','saved video flag does not invent missing plugin capability');
      const retiredStart=messages.length;
      await page.locator('#largeCover').evaluate(n=>n.click());
      await page.evaluate(m=>window.Auralis.setPlatformExtras({...m,items:[{author:'Late',text:'Retired response'}],nextPageHandle:'retired-next'}),retiredComments);
      await page.waitForTimeout(80);
      assert(!messages.slice(retiredStart).some(m=>(m.action==='setEmbeddedVideo'&&m.enabled)||m.action==='requestPlatformExtras'),'retired capabilities issue no new media requests');
      assert(await page.locator('.media-hub-dialog').isHidden(),'late comment reply cannot revive retired panel');
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Fixture',capabilities:['TrackSearch','MediaExtras','Comments','VideoResolution']}]}));
      holdComments=false;
      await page.locator('[data-media-action="comments"]').click();
      await page.locator('.media-comments article').first().waitFor();
      await page.keyboard.press('Escape');await page.locator('.media-hub-dialog').waitFor({state:'hidden'});
      // Video capability, not a hard-coded provider pair, selects the shared fullscreen engine.
      holdVideo=true;
      await openVideo();
      const retiredVideo=messages.findLast(m=>m.action==='setEmbeddedVideo'&&m.enabled);
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Fixture',configured:false,capabilities:['MediaExtras','Comments','VideoResolution']}]}));
      await page.locator('#embeddedVideoPage').waitFor({state:'hidden'});
      await page.evaluate(m=>window.Auralis.setEmbeddedVideoState(m),retiredVideo);
      assert(await page.locator('#embeddedVideoPage').isHidden(),'late video preparation cannot revive unconfigured capability');
      assert.equal(await page.locator('#largeCover').getAttribute('role'),'img');
      holdVideo=false;
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Fixture',capabilities:['TrackSearch','MediaExtras','Comments','VideoResolution']}]}));
      await openVideo();
      await page.waitForFunction(()=>!document.querySelector('#embeddedVideoPage').classList.contains('is-loading'));
      const activeVideoStart=messages.length;
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
      await page.waitForTimeout(80);
      assert(await page.locator('#embeddedVideoPage').isVisible(),'capability retirement preserves an already active video lease');
      assert(!messages.slice(activeVideoStart).some(m=>m.action==='pausePlayback'||(m.action==='setEmbeddedVideo'&&!m.enabled)),'retirement does not stop active playback');
      await page.locator('[data-media-action="audio"]').click();await page.locator('#embeddedVideoPage').waitFor({state:'hidden'});
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Fixture',capabilities:['TrackSearch','MediaExtras','Comments','VideoResolution']}]}));
      if (scale === 1) for (const provider of ['ytm', 'qq']) {
        await page.evaluate(id=>window.Auralis.setPlatformConfiguration({providers:[{id,name:'Declared video fixture',capabilities:['TrackSearch','VideoResolution']}]}),provider);
        await page.locator('#closeNowPlayingButton').click();
        await page.locator('[data-page="savedPlaylists"]').click();
        fixtures[0].entries[2].track = online(`video-${provider}`, provider, `${provider} 视频测试`);
        await page.evaluate(items => window.Auralis.setSavedPlaylists({items,action:'requestSavedPlaylists'}), fixtures);
        await page.locator('[data-media-action="select"][data-id="list1"]').click();
        await page.locator('[data-media-action="saved-play"][data-index="2"]').click();
        await page.waitForFunction(title=>document.querySelector('#miniTitle').textContent===title, `${provider} 视频测试`);
        await page.locator('#miniCover').click();
        await openVideo();
        await page.waitForFunction(title=>document.querySelector('#embeddedVideoStatus').textContent===title, `${provider} 视频测试`);
        assert(await page.locator('#embeddedVideoPage').isVisible(), `${provider} uses embedded video`);
        await page.evaluate(()=>{
          document.querySelector('[data-media-action="audio"]').click();
          document.querySelector('#largeCover').click();
        });
        await page.waitForTimeout(220);
        assert(await page.locator('#embeddedVideoPage').isVisible(), 'rapid reopen cancels old exit animation');
        await page.locator('[data-media-action="audio"]').click();
        await page.locator('#embeddedVideoPage').waitFor({state:'hidden'});
      }
      if(scale===2) {
        // Additional narrow English/high-contrast checks in the same isolated context.
        await page.emulateMedia({forcedColors:'active'});
        await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
        await page.locator('[data-media-action="options"]').click();
        await page.waitForFunction(()=>document.querySelector('#mediaDialogTitle').textContent==='Playback extras');
        await page.locator('.media-hub-dialog').evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).map(a=>a.finished)));
        assert.equal(await page.locator('#coverVideoOption').getAttribute('role'),'switch');
        const modal=await page.locator('.media-hub-dialog').boundingBox();
        assert(modal.x>=0&&modal.x+modal.width<=720,'English dialog fits minimum width');
        await page.screenshot({path:path.join(output,`${theme}-${scale}-contrast-en.png`)});
        await page.keyboard.press('Escape');
      }
      if (scale === 1) {
        // Empty local/saved libraries remain usable without any connected online account.
        await page.locator('#closeNowPlayingButton').click();
        await page.evaluate(() => { window.Auralis.receiveLibrary([]); window.Auralis.setPlatformConfiguration({}); });
        await page.locator('[data-page="songs"]').click();
        assert.equal(await page.locator('#pageContent .track-row').count(), 0);
        await page.locator('[data-page="savedPlaylists"]').click();
        await page.evaluate(() => window.Auralis.setSavedPlaylists({items:[],action:'requestSavedPlaylists'}));
        await page.waitForSelector('.saved-playlists-page .media-empty');
        await page.locator('[data-page="songs"]').click();
        await page.evaluate(() => window.Auralis.receiveLibrary([{id:'local-only',title:'离线回归',artist:'本地歌手',album:'本地专辑',durationSeconds:90}]));
        await page.locator('[data-action="play-all"]').click();
        await page.waitForFunction(() => document.querySelector('#miniTitle').textContent === '离线回归');
        assert(await page.locator('#mediaTools').isHidden());
      }
      if(await page.locator('#nowPlayingOverlay').evaluate(n=>n.classList.contains('open'))) await page.locator('#closeNowPlayingButton').click();
      // Re-enable the fixture provider after the plugin-missing regression above.
      await page.evaluate(() => window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Bilibili fixture',capabilities:['TrackSearch','PlaylistBrowse','Authentication','NativeLogin','MediaExtras','Comments','VideoResolution'],authentication:{status:'signedin',accountDisplayName:'Fixture'},playlists:[]}]}));
      await page.locator('#sidebarSearchButton').click();
      await page.locator('#searchInput').fill('加载状态测试');
      await page.waitForSelector('[data-platform-play-handle="loading-test"]');
      holdPlayback=true;
      await page.locator('[data-platform-play-row="loading-test"]').focus();
      await page.keyboard.press('Enter');
      await page.mouse.move(5,5);
      const busy=page.locator('[data-platform-play-row="loading-test"]');
      assert.equal(await busy.getAttribute('aria-busy'),'true');
      assert(await busy.locator('.platform-row-spinner').isVisible(),'pending spinner remains visible without hover');
      assert.equal(await busy.locator('.row-play').evaluate(n=>getComputedStyle(n).opacity),'1');
      assert.equal(await busy.locator('.platform-row-spinner').evaluate(n=>getComputedStyle(n).width),'22px');
      assert.match(await busy.locator('.title-stack span').textContent(),/准备音频|Preparing audio/);
      assert(await busy.locator('.title-stack span').isVisible(),'preparing status text is actually visible');
      await page.screenshot({path:path.join(output,`${theme}-${scale}-loading.png`)});
      assert.equal(await page.locator('[data-media-action="track-comments"]').count(),1,'declared comments appear on search rows');
      assert.equal(await page.locator('.platform-mv-button').count(),1,'declared video plus track metadata exposes video');
      await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'bili',name:'Search-only fixture',capabilities:['TrackSearch']}]}));
      assert.equal(await page.locator('[data-media-action="track-comments"]').count(),0,'search-only provider has no default comments');
      assert.equal(await page.locator('.platform-mv-button').count(),0,'saved video metadata cannot bypass current capability');
      assert.deepEqual(errors,[],`${theme} ${scale} page errors`);
      await context.close(); console.log(`PASS media UI ${theme} ${scale*100}%`);
    }
  } finally { await browser.close(); server.close(); }
})().catch(e=>{ console.error(e); server.close(); process.exitCode=1; });
