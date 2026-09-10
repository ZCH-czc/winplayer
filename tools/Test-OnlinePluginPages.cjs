// Isolated, synthetic provider data; no real login, plugin import or playback.
const {chromium}=require('playwright');
const fs=require('node:fs/promises'), path=require('node:path'), http=require('node:http'), assert=require('node:assert/strict');
const root=path.resolve(process.env.AURALIS_UI_ROOT || 'Auralis/wwwroot');
async function checkBrandAndSearchLoading(page, messages, theme, scale) {
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'ui-test-1',isPlaying:false,currentTime:42,duration:180,
    audioInformation:{codec:'FLAC',bitrateKbps:800,sampleRateHz:96000,bitsPerSample:24,channels:2,isLossless:true,isAverageBitrate:true}}));
  const qualities=page.locator('[data-stream-quality]');
  assert.equal(await qualities.count(),2);
  for(const badge of await qualities.all()) {
    assert.equal(await badge.textContent(),'FLAC · ≈ 800 kbps');
    assert.match(await badge.getAttribute('title'),/96 kHz · 24 bit · 2 ch/);
  }
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'stale-track',audioInformation:{codec:'WRONG',bitrateKbps:1}}));
  assert.equal(await qualities.first().textContent(),'FLAC · ≈ 800 kbps','old track metadata cannot replace current quality');
  await page.evaluate(()=>window.Auralis.setPlaybackState({id:'ui-test-1',isPlaying:false,currentTime:42,duration:180,audioInformation:null}));
  assert.equal(await qualities.first().getAttribute('hidden'),'','missing measurements do not retain old FLAC bitrate');
  await page.locator('[data-page="settings"]').click();
  await page.locator('.settings-home-about img.about-mark').waitFor();
  await page.waitForFunction(()=>[...document.querySelectorAll('img.about-mark,img.splash-logo')].every(n=>n.complete&&n.naturalWidth===1024));
  await page.evaluate(()=>Promise.all(document.getAnimations().filter(a=>a.effect?.getComputedTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{}))));
  assert.equal(await page.locator('.about-mark i').count(),0);
  const mark=await page.locator('.about-mark').evaluate(n=>({width:n.getBoundingClientRect().width,height:n.getBoundingClientRect().height,background:getComputedStyle(n).backgroundColor}));
  assert.equal(mark.width,44);assert.equal(mark.height,44);assert.equal(mark.background,'rgba(0, 0, 0, 0)');
  await page.locator('.settings-home-about').scrollIntoViewIfNeeded();
  await fs.mkdir('artifacts/brand-search-ui',{recursive:true});
  await page.screenshot({path:`artifacts/brand-search-ui/brand-${theme}-${scale}.png`});
  await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'fixture.search.loading',name:'Slow search',capabilities:['TrackSearch'],configured:true}]}));
  await page.locator('#sidebarSearchButton').click();
  const immediate=await page.evaluate(()=>{
    const input=document.querySelector('#searchInput');input.value='New query';input.dispatchEvent(new Event('input',{bubbles:true}));
    return document.querySelector('.platform-search-section').textContent;
  });
  assert.match(immediate,/正在搜索/);assert.doesNotMatch(immediate,/没有匹配歌曲/);
  await page.waitForFunction(()=>document.querySelector('.platform-search-section .is-loading'));
  await page.waitForTimeout(350);
  const request=messages.findLast(m=>m.action==='platformSearchTracks'&&m.providerId==='fixture.search.loading');assert(request);
  await page.screenshot({path:`artifacts/brand-search-ui/loading-${theme}-${scale}.png`});
  await page.evaluate(r=>window.Auralis.setPlatformSearchResult({...r,items:[]}),request);
  assert.match(await page.locator('.platform-search-section').innerText(),/没有匹配歌曲/,'empty state requires an actual completed response');
  await page.locator('#clearSearchButton').click();
}
async function checkLyricsCapabilityUi(page, messages) {
  const original=await page.evaluate(()=>window.AuralisI18n.preference);
  await page.evaluate(()=>window.AuralisI18n.setPreference('zh-CN'));
  await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{
    id:'fixture.lyrics.any-id',name:'Independent lyrics',capabilities:['LyricsLookup'],playlists:[]
  }]}));
  await page.locator('#nowPlayingButton').click();
  await page.locator('#lyricsSourceSwitcher [data-lyrics-source="online"]').waitFor({state:'visible'});
  await page.locator('#lyricsSourceSwitcher [data-lyrics-source="online"]').focus();
  await page.waitForFunction(()=>document.activeElement?.getAttribute('data-lyrics-source')==='online');
  await page.keyboard.press('Enter');
  try { await page.waitForFunction(()=>document.querySelector('.lyrics-status span')?.textContent.includes('已启用的歌词插件'),null,{timeout:4000}); }
  catch(error) {
    console.error('Lyrics fixture state',await page.evaluate(()=>({language:window.AuralisI18n.language,
      status:[...document.querySelectorAll('.lyrics-status')].map(n=>n.textContent),
      source:document.querySelector('#lyricsSourceSwitcher')?.outerHTML,
      overlay:document.querySelector('#nowPlayingOverlay')?.className,
      active:document.activeElement?.outerHTML})),messages.filter(m=>m.action==='requestLyrics'));
    throw error;
  }
  const lookup=messages.findLast(m=>m.action==='requestLyrics');
  assert(lookup?.allowOnline&&lookup.sourceSelection==='online');
  assert(!('providerId' in lookup),'WebView requests capability without hard-coded routing');
  await page.evaluate(()=>window.AuralisI18n.setPreference('en-US'));
  await page.waitForFunction(()=>document.querySelector('.lyrics-status span')?.textContent.includes('enabled lyrics plugins'));
  assert.equal(await page.evaluate(()=>window.AuralisI18n.t('无法读取 Example Service 个人歌单。')),'Could not load Example Service personal playlists.');
  assert.equal(await page.evaluate(()=>window.AuralisI18n.t('无法读取 Example Service 云歌单')),'Could not load Example Service cloud playlists');
  await page.evaluate(preference=>{
    window.AuralisI18n.setPreference(preference);
    window.Auralis.setLyrics({trackId:'ui-test-1',source:'Independent <img src=x> lyrics · cached',selectedSource:'online',
      isSynced:true,lines:[{timeSeconds:0,text:'Independent fixture line'}]});
  },original);
  assert.equal(await page.locator('#lyricsSourceBadge').textContent(),'Independent <img src=x> lyrics · cached','Provider/cache source is displayed verbatim, not rewritten by platform identity');
  assert.equal(await page.locator('#lyricsSourceBadge img').count(),0,'Source remains text, never HTML');
  // The existing active-line transform deliberately scales/translates the full-width line box
  // under overflow:hidden. Measure its rendered text, not that intentionally oversized box.
  const lyricGeometry=await page.locator('#lyricsViewport').evaluate(n=>{
    const bounds=n.getBoundingClientRect(),line=n.querySelector('.lyric-line');
    const text=document.createTreeWalker(line,NodeFilter.SHOW_TEXT).nextNode();
    const range=document.createRange();range.selectNodeContents(text);const glyphs=range.getBoundingClientRect();
    return {left:bounds.left,right:bounds.right,textLeft:glyphs.left,textRight:glyphs.right};
  });
  assert(lyricGeometry.textLeft>=lyricGeometry.left-1&&lyricGeometry.textRight<=lyricGeometry.right+1,JSON.stringify(lyricGeometry));
  await page.locator('#closeNowPlayingButton').click();
  await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
}
const server=http.createServer(async(req,res)=>{try{
 let file=path.resolve(root,'.'+new URL(req.url,'http://localhost').pathname.replace(/\/$/,'/index.html'));
 if(!file.startsWith(root+path.sep)) throw Error();
 // Source-mode server mirrors the single MSBuild-linked brand asset. Published
 // tests never fall back, so a missing packaged image still fails acceptance.
 if(!process.env.AURALIS_UI_ROOT && file===path.join(root,'assets','auralis-icon.png')) file=path.resolve('Auralis/Assets/AuralisIcon.png');
 res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(file));
}catch{res.writeHead(404);res.end();}});
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r)); const origin=`http://127.0.0.1:${server.address().port}`;
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try { for(const theme of ['light','dark']) for(const scale of [1,1.5,2]) {
  const context=await browser.newContext({viewport:scale===2?{width:720,height:600}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1?'no-preference':'reduce'});
  const page=await context.newPage(), messages=[], errors=[];
  page.on('pageerror',e=>errors.push(e.message));await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
  await page.exposeFunction('bridge',m=>messages.push(m));await page.addInitScript(()=>{window.chrome={webview:{postMessage:m=>window.bridge(m)}};});
  await page.goto(`${origin}/?ui-test=1&ui-test-theme=${theme}`);
  const config=providers=>page.evaluate(providers=>window.Auralis.setPlatformConfiguration({providers}),providers);
  await checkBrandAndSearchLoading(page,messages,theme,scale);
  assert.deepEqual(await page.evaluate(()=>Object.keys(window.Auralis).filter(k=>/Qq|YouTubeMusic|NeteaseMusic|Bilibili/.test(k))),[], 'no platform-specific Web callbacks');
  assert.equal(await page.locator('#qqCloudSection,#neteaseMusicSection,#youtubeMusicSection').count(),0,'legacy platform DOM removed, not just hidden');
  await config([]);await page.locator('[data-page="onlinePlaylists"]').click();
  await page.locator('[data-action="manage-platform-plugins"]').waitFor();
  assert.equal(await page.locator('[data-online-provider]').count(),0,'core-only has no built-in accounts');
  assert.equal(await page.locator('[data-online-provider-filter]').count(),0);
  await checkLyricsCapabilityUi(page,messages);
  const fixture={id:'fixture.new',name:'New Plugin <safe>',capabilities:['TrackSearch','PlaylistBrowse','PlaylistDetails','Authentication','NativeLogin','VideoResolution'],authentication:{status:'signedin',accountDisplayName:'Fixture user'},playlists:[{handle:'fixture-list',title:'Fixture collection',trackCount:1}]};
  await config([fixture]);assert.equal(await page.locator('[data-online-provider]').count(),1);
  assert.equal(await page.locator('[data-online-provider-filter]').count(),2);
  assert.equal(await page.locator('.online-account-copy strong img').count(),0);
  await page.locator('[data-online-playlist-handle="fixture-list"]').click();await page.waitForTimeout(200);
  const request=messages.findLast(m=>m.action==='requestOnlineCollection');assert.equal(request.providerId,'fixture.new');
  await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Fixture details'},tracks:[{handle:'fixture-track',title:'Fixture song',providerId:r.providerId,durationSeconds:120}]}}),request);
  await page.getByRole('heading',{name:'Fixture details'}).waitFor();
  assert.equal(await page.locator('[data-media-action="track-comments"]').count(),0,'collection rows do not invent an undeclared comment capability');
  await page.locator('[data-platform-play-row="fixture-track"]').hover();
  await page.locator('[data-platform-play-handle="fixture-track"]').click();await page.waitForTimeout(80);
  assert(messages.some(m=>m.action==='playPlatformResult'&&m.handle==='fixture-track'),'generic collection routes playback');
  await config([]);await page.locator('[data-action="manage-platform-plugins"]').waitFor();
  assert.equal(await page.getByRole('heading',{name:'Fixture details'}).count(),0,'removed provider immediately clears its open detail');
  assert.equal(await page.locator('[data-online-provider]').count(),0,'disabled/absent provider disappears');
  await config([{...fixture,authentication:{status:'signedout'},playlists:[]}]);
  await page.locator('[data-action="generic-platform-login"]').click();await page.waitForTimeout(40);
  assert(messages.some(m=>m.action==='manageOnlineAccount'&&m.providerId==='fixture.new'&&m.operation==='login'));
  await config([fixture]);await page.locator('[data-page="onlinePlaylists"]').click();await page.waitForTimeout(350);
  await page.locator('[data-online-playlist-handle="fixture-list"]').click();await page.waitForTimeout(250);
  const expiredRequest=messages.findLast(m=>m.action==='requestOnlineCollection');
  await config([{...fixture,authentication:{status:'expired'},playlists:[]}]);
  await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Late private detail'},tracks:[]}}),expiredRequest);
  assert.equal(await page.getByRole('heading',{name:'Late private detail'}).count(),0,'account expiry rejects late details');
  const publicSource={...fixture,id:'fixture.public',name:'Public Source',authentication:null,capabilities:['TrackSearch','PlaylistBrowse','PlaylistDetails'],playlists:[{handle:'public-list',title:'Public collection',trackCount:1}]};
  await config([publicSource]);await page.locator('[data-page="onlinePlaylists"]').click();await page.waitForTimeout(350);
  assert.equal(await page.locator('[data-online-provider]').count(),0,'public source does not invent accounts');
  assert.equal(await page.locator('[data-action="generic-platform-login"]').count(),0);
  assert.equal(await page.locator('[data-online-playlist-handle="public-list"]').count(),1,'public collection shown without fake signed-in state');
  await page.locator('[data-public-provider] [data-action="generic-platform-refresh"]').click();await page.waitForTimeout(60);
  assert(messages.some(m=>m.action==='manageOnlineAccount'&&m.providerId==='fixture.public'&&m.operation==='refresh'));
  await fs.mkdir('artifacts/public-collections-ui',{recursive:true});
  await page.screenshot({path:`artifacts/public-collections-ui/${theme}-${scale}.png`});
  assert(await page.locator('#pageContent').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'public page fits viewport');
  await page.locator('[data-online-playlist-handle="public-list"]').click();await page.waitForTimeout(250);
  const publicRequest=messages.findLast(m=>m.action==='requestOnlineCollection');
  await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,error:{code:'NetworkUnavailable',message:'Synthetic error'}}),publicRequest);
  assert.equal(await page.locator('[data-action="generic-platform-login"]').count(),0,'public error does not demand login');
  await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Stale public detail'},tracks:[]}}),publicRequest);
  assert.equal(await page.getByRole('heading',{name:'Stale public detail'}).count(),0,'completed request cannot replay');
  for (const source of [publicSource,fixture]) {
    const companion={...publicSource,id:'fixture.other',name:'Other source',playlists:[{handle:'other-list',title:'Other collection',trackCount:1}]};
    await config([source,companion]);await page.locator('[data-page="onlinePlaylists"]').click();
    await page.locator('[data-online-provider-filter="all"]').click();
    const oldHandle=source.playlists[0].handle;
    await page.locator(`[data-online-playlist-handle="${oldHandle}"]`).click();
    await page.waitForTimeout(100);
    const failed=messages.findLast(m=>m.action==='requestOnlineCollection');
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,error:{code:'NotFound',message:'Synthetic collection handle expired'}}),failed);
    const recovery=page.locator('.online-collection-page [data-action="generic-platform-refresh"]');
    await page.evaluate(()=>window.AuralisI18n.setPreference('en-US'));
    assert.equal(await recovery.innerText(),'Refresh and return to playlists');
    await page.evaluate(()=>window.AuralisI18n.setPreference('zh-CN'));
    await page.evaluate(()=>Promise.all(document.getAnimations().filter(a=>a.effect?.getComputedTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{}))));
    await page.screenshot({path:`artifacts/public-collections-ui/recovery-${source.id}-${theme}-${scale}.png`});
    await recovery.focus();const beforeRecovery=messages.length;await page.keyboard.press('Enter');
    await page.locator('.online-playlists-page').waitFor({timeout:2000});
    assert.equal(await page.locator('.online-playlists-page').getAttribute('data-provider'),source.id,'recovery returns to the affected source');
    assert.equal(await page.locator(`[data-online-playlist-handle="${oldHandle}"]`).count(),0,'expired handles are not offered again while refreshing');
    assert(await page.locator(`[data-online-provider-filter="${source.id}"]`).evaluate(n=>n===document.activeElement),'keyboard focus follows recovery to the source filter');
    await page.waitForTimeout(50);
    assert.equal(messages.slice(beforeRecovery).filter(m=>m.action==='manageOnlineAccount'&&m.operation==='refresh'&&m.providerId===source.id).length,1);
    assert(!messages.slice(beforeRecovery).some(m=>m.action==='requestOnlineCollection'||m.operation==='login'||m.operation==='signout'),'recovery does not replay handles or change authentication');
    await page.locator('[data-online-provider-filter="all"]').click();
    assert.equal(await page.locator('[data-online-playlist-handle="other-list"]').count(),1,'recovery does not discard another source collections');
    await page.locator(`[data-online-provider-filter="${source.id}"]`).click();
    const freshHandle=source.id+'-fresh-list';
    await config([{...source,playlists:[{handle:freshHandle,title:'Fresh collection',trackCount:1}]},companion]);
    await page.locator(`[data-online-playlist-handle="${freshHandle}"]`).waitFor();
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Late failed detail'},tracks:[]}}),failed);
    assert.equal(await page.getByRole('heading',{name:'Late failed detail'}).count(),0,'late detail cannot pull recovery back into stale content');
    await page.locator(`[data-online-playlist-handle="${freshHandle}"]`).click();await page.waitForTimeout(80);
    const fresh=messages.findLast(m=>m.action==='requestOnlineCollection');assert.equal(fresh.handle,freshHandle);
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Recovered collection'},tracks:[]}}),fresh);
    await page.getByRole('heading',{name:'Recovered collection'}).waitFor();
  }
  await config([{...publicSource,configured:false}]);await page.locator('[data-page="onlinePlaylists"]').click();await page.waitForTimeout(350);
  assert.equal(await page.locator('[data-online-playlist-handle]').count(),0,'missing required setting hides old collections');
  const pendingSources=Array.from({length:4},(_,i)=>({...publicSource,id:`fixture.setup.${i}`,name:`Setup <safe> ${i}`,configured:false,
    settings:[{key:'server',kind:'endpoint',label:'Server address',description:'Synthetic endpoint',required:true,value:''}],playlists:[]}));
  await config([fixture,...pendingSources]);
  await page.locator('[data-online-provider-filter="all"]').click();
  const publicGroup=page.locator('#onlinePublicSources');
  assert.equal(await publicGroup.count(),1,'multiple public sources share one compact group');
  assert.equal(await publicGroup.evaluate(n=>n.open),false,'setup rows do not push existing collections below the fold');
  await fs.mkdir('artifacts/public-collections-ui',{recursive:true});
  await page.evaluate(()=>Promise.all(document.getAnimations().filter(a=>a.effect?.getComputedTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{}))));
  await page.screenshot({path:`artifacts/public-collections-ui/setup-collapsed-${theme}-${scale}.png`});
  assert.equal(await page.locator('[data-online-playlist-handle="fixture-list"]').count(),1,'ready collections remain available');
  assert.equal(await publicGroup.locator('[data-action="generic-platform-refresh"]').count(),0,'unconfigured sources do not offer futile refresh');
  await publicGroup.locator('summary').focus();await page.keyboard.press('Enter');
  assert.equal(await publicGroup.evaluate(n=>n.open),true,'public sources support keyboard expansion');
  assert.equal(await publicGroup.locator('strong img').count(),0,'declared source names remain escaped');
  await config([fixture,...pendingSources]);
  assert.equal(await publicGroup.evaluate(n=>n.open),true,'configuration refresh preserves disclosure state');
  assert(await publicGroup.locator('summary').evaluate(n=>n===document.activeElement),'configuration refresh preserves summary keyboard focus');
  await fs.mkdir('artifacts/public-collections-ui',{recursive:true});
  await page.screenshot({path:`artifacts/public-collections-ui/setup-${theme}-${scale}.png`});
  assert(await publicGroup.evaluate(n=>n.scrollWidth<=n.clientWidth+1),'group and controls fit narrow windows');
  const setupStart=messages.length;
  await publicGroup.locator('[data-action="configure-online-source"]').first().click();
  await page.locator('[data-provider-setting-input="server"]').first().waitFor({state:'visible'});
  assert(!messages.slice(setupStart).some(m=>m.action==='manageOnlineAccount'),'settings navigation does not trigger account or refresh requests');
  await page.locator('[data-page="onlinePlaylists"]').click();
  await page.locator('[data-online-provider-filter="fixture.setup.0"]').click();
  assert.equal(await page.locator('#onlinePublicSources').count(),0,'one selected source has a direct status row');
  assert.equal(await page.locator('[data-public-provider]').count(),1);
  assert.equal(await page.locator('[data-action="configure-online-source"]').count(),1);
  await config([fixture,{...pendingSources[0],configured:true,playlists:[{handle:'new-ready',title:'Configured collection',trackCount:1}]},...pendingSources.slice(1)]);
  assert.equal(await page.locator('[data-action="configure-online-source"]').count(),0,'setup action follows live configuration');
  assert.equal(await page.locator('[data-public-provider] [data-action="generic-platform-refresh"]').count(),1);
  assert.equal(await page.locator('[data-online-playlist-handle="new-ready"]').count(),1);
  await page.locator('[data-online-provider-filter="all"]').click();
  await publicGroup.locator('summary').focus();await page.keyboard.press('Space');
  assert.equal(await publicGroup.evaluate(n=>n.open),true,'Space expands without triggering global playback');
  await page.evaluate(()=>window.AuralisI18n.setPreference('en-US'));
  assert.match(await publicGroup.locator('summary').innerText(),/Public collection sources/);
  await page.evaluate(()=>window.AuralisI18n.setPreference('zh-CN'));
  await config([fixture]);
  assert.equal(await page.locator('#onlinePublicSources').count(),0,'removed plugins remove the source group');
  await config([{...publicSource,configured:false,settings:[]}]);
  assert.equal(await page.locator('[data-public-provider] [data-action="manage-platform-plugins"]').count(),1,'missing declarations route to plugin management instead of an empty settings page');
  // Existing package IDs must work through exactly the same generic bridge as an unknown plugin.
  if (scale === 1) for (const id of ['tx','wy','ytm','bili']) {
    const platform={...fixture,id,name:`Declared ${id}`,playlists:[{handle:`${id}-list`,title:`${id} collection`,trackCount:2}]};
    await config([platform]);await page.locator('[data-page="onlinePlaylists"]').click();await page.waitForTimeout(300);
    await page.locator(`[data-online-provider-filter="${id}"]`).click();await page.waitForTimeout(300);
    await page.locator(`[data-online-provider="${id}"] [data-action="generic-platform-signout"]`).click();await page.waitForTimeout(40);
    assert(messages.some(m=>m.action==='manageOnlineAccount'&&m.providerId===id&&m.operation==='signout'));
    await page.locator(`[data-online-playlist-handle="${id}-list"]`).click();await page.waitForTimeout(200);
    const r=messages.findLast(m=>m.action==='requestOnlineCollection');assert.equal(r.providerId,id);
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,providerId:'other',detail:{playlist:{handle:r.handle,title:'Wrong provider'},tracks:[]}}),r);
    assert.equal(await page.getByRole('heading',{name:'Wrong provider'}).count(),0);
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:'wrong-handle',title:'Wrong handle'},tracks:[]}}),r);
    assert.equal(await page.getByRole('heading',{name:'Wrong handle'}).count(),0);
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'搜索',owner:'设置'},tracks:[1,2].map(i=>({handle:r.providerId+'-track-'+i,title:'Generic song '+i,providerId:r.providerId,durationSeconds:120,hasMusicVideo:true}))}}),r);
    await page.getByRole('heading',{name:'搜索',exact:true}).waitFor();
    assert.equal(await page.locator('.online-collection-header p').textContent(),'设置 · 2 首歌曲');
    assert.equal(await page.locator('.platform-mv-button').first().getAttribute('title'),'全屏播放器内播放视频');
    await page.locator(`[data-platform-play-row="${id}-track-1"]`).hover();
    await page.locator(`[data-platform-play-handle="${id}-track-1"]`).click();await page.waitForTimeout(40);
    assert(messages.some(m=>m.action==='playPlatformResult'&&m.handle===`${id}-track-1`));
    await page.evaluate(handle=>window.Auralis.setPlatformPlaybackResult({handle,success:false,error:{code:'NetworkUnavailable',message:'Synthetic playback error'}}),`${id}-track-1`);
    await page.locator('[data-page-link="onlinePlaylists"]').click();await page.waitForTimeout(250);
    await page.locator(`[data-online-playlist-handle="${id}-list"]`).click();await page.waitForTimeout(200);
    const failure=messages.findLast(m=>m.action==='requestOnlineCollection');
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,error:{code:'AuthenticationRequired',message:'Synthetic expired session'}}),failure);
    await page.locator('[data-action="generic-platform-login"]').waitFor();
    await page.evaluate(r=>window.Auralis.setOnlineCollection({...r,detail:{playlist:{handle:r.handle,title:'Replayed private detail'},tracks:[]}}),failure);
    assert.equal(await page.getByRole('heading',{name:'Replayed private detail'}).count(),0);
    await page.locator('[data-page-link="onlinePlaylists"]').click();await page.waitForTimeout(200);
    assert.equal(await page.locator('[data-online-playlist-handle]').count(),0,'detail authentication failure clears the unified account collections');
  }
  assert(!messages.some(m=>/^(open|signOut|request)(Qq|YouTubeMusic|NeteaseMusic|Bilibili)/.test(m.action)),'no legacy bridge requests');
  assert.deepEqual(errors,[]);
  console.log(`PASS dynamic online plugins ${theme} ${scale*100}%: absent, arbitrary provider, login/detail/playback, expiry, public collections/refresh/errors, settings and stale responses.`);
  await context.close();
 }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
