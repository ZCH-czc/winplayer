'use strict';
// Actual published/source UI, isolated Edge, synthetic plugin documents. No accounts or external HTTP.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/plugin-media-pages-ui');
const server=http.createServer(async(req,res)=>{
  try{
    const name=new URL(req.url,'http://localhost').pathname;
    let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
    if(!file.startsWith(root+path.sep))throw Error();
    if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
    res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
    res.end(await fs.readFile(file));
  }catch{res.statusCode=404;res.end();}
});

(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];let fail=false;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const provider={id:'independent.media',name:'Independent studio',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','StreamResolution'],
      pages:[{id:'catalogue',label:'作品目录',labelEn:'Catalogue',placement:'global',presentation:'page',documentVersion:6}]};
    const media=(key,title,availability='available')=>({providerId:provider.id,handle:'track-'+key.repeat(24),id:'track-'+key.repeat(24),
      kind:'online',sourceName:provider.name,title,artist:'Original demonstration artist',album:'Independent release',
      durationSeconds:204,availability,isPlayable:availability!=='unavailable',hasMusicVideo:false,coverUrl:null});
    const first=media('a','Quiet Geometry'),second=media('b','Evening Colors','previewonly'),blocked=media('c','Unavailable work','unavailable');
    const doc={title:'Catalogue',description:'Plugin-owned works. Browse freely; play or queue only when you choose.',layout:'cards',
      append:false,collectionHandle:'catalogue',actions:[],cards:[
        {handle:'card-a',title:'Quiet Geometry',text:'An original demonstration. Media metadata stays separate from stream resolution.',media:first},
        {handle:'card-b',title:'Evening Colors',text:'A preview-only work keeps its availability when queued.',media:second},
        {handle:'card-c',title:'Unavailable work',text:'The platform controls access. Unavailable entries cannot start playback.',media:blocked},
        {handle:'card-note',title:'Release notes',text:'A plain card has no playback controls.'}]};
    await page.exposeFunction('mediaPageBridge',async m=>{
      messages.push(m);
      if(m.action==='readPluginGlobalPage')return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:doc});
      if(m.action==='playPlatformResult'){
        if(fail){fail=false;return page.evaluate(x=>window.Auralis.setPlatformPlaybackResult(x),{handle:m.handle,error:{message:'Synthetic playback failure'}});}
        return page.evaluate(x=>window.Auralis.setPlatformPlaybackResult(x),{handle:m.handle,success:true,item:m.handle===first.handle?first:second});
      }
    });
    await page.addInitScript(theme=>{localStorage.setItem('auralis:language','en-US');localStorage.setItem('auralis:theme',theme);window.chrome={webview:{postMessage:m=>window.mediaPageBridge(m)}};},theme);
    await page.goto(origin+(scale===2?'/':'/?ui-test=1&ui-test-theme='+theme));
    if(scale===2)await page.evaluate(()=>window.Auralis.receiveLibrary([]));
    await page.locator('#app:not(.is-loading)').waitFor();
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden());
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    const start=messages.length,nav=page.locator('[data-plugin-navigation="independent.media:catalogue"]'),route=page.locator('.plugin-page-route');
    await nav.click();await route.locator('[data-page-media="play"]').first().waitFor();
    const plays=()=>messages.filter(m=>m.action==='playPlatformResult'),prefetches=()=>messages.filter(m=>m.action==='prefetchPlatformTrack'&&m.handle);
    assert.equal(plays().length,0);assert(!messages.slice(start).some(m=>m.action==='prefetchPlatformTrack'&&m.handle),'page read does not prefetch works');
    const play=route.locator('[data-page-media="play"]'),enqueue=route.locator('[data-page-media="enqueue"]');
    assert.equal(await play.count(),3);assert(await play.nth(2).isDisabled());assert(await enqueue.nth(2).isDisabled());
    await enqueue.first().focus();await page.keyboard.press('Enter');await enqueue.first().click();
    assert.equal(plays().length,0,'enqueue never plays');
    if(scale===2){
      await page.evaluate(()=>window.Auralis.mediaCommand('playPause'));
      await page.waitForFunction(()=>document.querySelector('#miniTitle')?.textContent==='Quiet Geometry');
      assert.equal(plays().at(-1)?.handle,first.handle,'empty queue transport plays queued media');
    }else{
      fail=true;await play.first().click();
      await page.getByText('Synthetic playback failure',{exact:true}).waitFor();
      assert(await route.isVisible(),'failure preserves page');
      await play.first().focus();await page.keyboard.press('Enter');
    }
    await page.waitForFunction(()=>document.querySelector('#playerBar')?.textContent.includes('Quiet Geometry'));
    const count=plays().length;
    await page.evaluate(h=>window.Auralis.setPlaybackState({id:h,isPlaying:true,currentTime:3,duration:204}),first.handle);
    await enqueue.nth(1).click();await enqueue.nth(1).click();
    assert.equal(plays().length,count,'queueing second never interrupts the first');
    await page.waitForTimeout(80);
    assert.equal(prefetches().at(-1)?.handle,second.handle,'only actual next work reaches shared prefetch planner');
    await page.locator('#queueButton').click();await page.locator('#queuePanel.open').waitFor();
    assert.equal(await page.locator('#queuePanel').getByText('Evening Colors',{exact:true}).count(),1,'queue identity deduplicates');
    await page.locator('#closeQueueButton').click();
    await page.locator('#queuePanel').waitFor({state:'hidden'});
    await page.waitForFunction(()=>!document.querySelector('.toast'));
    await route.evaluate(n=>n.parentElement.scrollTop=0);
    assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'media cards fit viewport');
    await page.screenshot({path:path.join(out,theme+'-'+scale+'.png')});
    await page.locator('#nowPlayingButton').click();await page.locator('#nowPlayingOverlay.open').waitFor();
    assert.equal(await page.locator('#stageTitle').textContent(),'Quiet Geometry');
    await page.evaluate(()=>window.Auralis.setFullscreenState(true));
    await page.evaluate(h=>window.Auralis.nativeEnded(h),first.handle);
    await page.waitForFunction(()=>document.querySelector('#stageTitle').textContent==='Evening Colors');
    assert.equal(plays().at(-1).handle,second.handle,'auto advance uses queued media and updates fullscreen metadata');
    await page.evaluate(()=>window.Auralis.setFullscreenState(false));
    await page.locator('#closeNowPlayingButton').click();await page.locator('#nowPlayingOverlay').waitFor({state:'hidden'});
    assert(await route.isVisible(),'leaving fullscreen preserves plugin page');
    if(scale===1){
      await page.setViewportSize({width:1920,height:1080});await page.evaluate(()=>window.Auralis.setWindowState(true));
      assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1),'maximized card layout stays within content');
      await page.setViewportSize({width:1280,height:820});await page.evaluate(()=>window.Auralis.setWindowState(false));
    }
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:2}]}),provider);
    await route.waitFor({state:'hidden'});
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));assert(await page.locator('#pluginNavigation').isHidden());
    assert.deepEqual(errors,[]);await context.close();
    console.log('PASS page media '+theme+' '+scale+': inert browse/enqueue, keyboard, unavailable/preview, failed play, mixed queue, actual next prefetch, revocation.');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
