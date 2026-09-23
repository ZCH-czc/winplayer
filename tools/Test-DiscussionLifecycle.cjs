'use strict';
// Actual application renderer, isolated browser and synthetic bridge; no accounts or platform HTTP.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/shared-discussion-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error();
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
  res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const handle=n=>'community-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];let hold=false;
    const provider={id:'sample.discussion',name:'Discussion sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','Comments','CommentReplies','StreamResolution'],
      pages:[{id:'detail',label:'讨论示例',labelEn:'Discussion sample',placement:'global',presentation:'page',documentVersion:8}]};
    const track={kind:'online',id:'track-'+'a'.repeat(24),handle:'track-'+'a'.repeat(24),providerId:provider.id,title:'Original demonstration',artist:'Demo musician',durationSeconds:200,isPlayable:true,hasMusicVideo:false};
    const response=m=>({...m,items:Array.from({length:m.kind==='replies'?4:25},(_,i)=>({handle:handle((m.kind==='replies'?100:1)+i),author:'Reader '+i,text:'Original comment. <script>Plain text</script> '.repeat(3),replyCount:m.kind==='replies'?0:4,publishedAt:'2026-09-20T01:00:00Z'})),nextPageHandle:null});
    const reads=()=>messages.filter(m=>m.action==='requestPlatformExtras');
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    await page.exposeFunction('discussionBridge',async m=>{
      messages.push(m);
      if(m.action==='readPluginGlobalPage')return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:{version:8,title:'A discussion beside the music',layout:'detail',cards:[{handle:handle(1000),title:track.title,text:'An original demonstration reading surface.',media:track,discussionHandle:handle(500),commentCount:25}],actions:[]}});
      if(m.action==='playPlatformResult')return page.evaluate(x=>window.Auralis.setPlatformPlaybackResult(x),{handle:m.handle,success:true,item:track});
      if(m.action==='requestPlatformExtras'&&!hold)return page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(m));
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.discussionBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);
    await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('[data-plugin-navigation="sample.discussion:detail"]').click();
    const route=page.locator('.plugin-page-route'),inline=route.locator('.plugin-detail-discussion');
    await route.locator('[data-page-media="play"]').click();
    await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='Original demonstration');
    // Moving from a pending detail discussion to the current-track drawer transfers read ownership.
    hold=true;await route.locator('[data-inline-discussion="main"]').click();
    await inline.getByText('Loading…',{exact:true}).waitFor();const oldPage=reads().at(-1);
    await page.locator('#nowPlayingButton').click();
    await page.locator('#mediaTools [data-media-action="comments"]').click();
    const drawer=page.locator('dialog[aria-labelledby="mediaDialogTitle"]');
    await drawer.getByText('Loading…',{exact:true}).waitFor();const drawerRequest=reads().at(-1);
    const originalViewport=page.viewportSize();
    await page.setViewportSize({width:1800,height:900});
    await page.waitForFunction(()=>{
      const drawer=document.querySelector('dialog.is-comments');
      return drawer&&drawer.getBoundingClientRect().width>=650;
    });
    const wideDrawer=await drawer.evaluate(n=>({width:n.getBoundingClientRect().width,scrollWidth:n.scrollWidth,clientWidth:n.clientWidth,
      viewport:innerWidth,media:matchMedia('(min-width: 1500px)').matches,computed:getComputedStyle(n).width,
      maxWidth:getComputedStyle(n).maxWidth,style:n.getAttribute('style')}));
    assert(wideDrawer.width>=650&&wideDrawer.width<=720&&wideDrawer.scrollWidth<=wideDrawer.clientWidth+1,
      'shared comments use a wider but bounded drawer on spacious logical desktops: '+JSON.stringify(wideDrawer));
    await page.setViewportSize(originalViewport);
    assert.notEqual(drawerRequest.requestId,oldPage.requestId,'both instances use one monotonic allocator');
    assert.equal(drawerRequest.handle,track.handle,'drawer owns current media, not the post');
    assert.equal(await inline.locator('.discussion-list').count(),0,'old inline read is retired');
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(oldPage));
    assert.equal(await drawer.locator('.discussion-list > article').count(),0,'old page response cannot fill the drawer');
    hold=false;await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(drawerRequest));
    await drawer.locator('[data-reply]').nth(3).scrollIntoViewIfNeeded();
    const scroll=await drawer.locator('.discussion-scroll').evaluate(n=>n.scrollTop),playback=messages.filter(m=>['playPlatformResult','pausePlayback','resumePlayback','setEmbeddedVideo'].includes(m.action)).length;
    await drawer.locator('[data-reply]').nth(3).click();
    await drawer.getByRole('heading',{name:'Comment details',exact:true}).waitFor();
    await drawer.locator('.discussion-list > article').nth(3).waitFor();
    assert.equal(await drawer.locator('.discussion-root').count(),1);
    assert(await drawer.locator('#mediaDialogTitle').evaluate(n=>document.activeElement===n),'reply heading has focus');
    assert.equal(await drawer.locator('script').count(),0);
    await drawer.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).map(a=>a.finished)));
    const layout=await drawer.evaluate(n=>{const s=n.querySelector('.discussion-scroll'),r=s.getBoundingClientRect(),d=n.getBoundingClientRect();return{width:s.scrollWidth-s.clientWidth,top:r.top,bottom:r.bottom,drawerBottom:d.bottom,height:s.clientHeight};});
    assert(layout.width<=1&&layout.height>60&&layout.top>=0&&layout.bottom<=layout.drawerBottom,'drawer has one bounded, reachable scroller');
    if(scale===1.5)assert.equal(await drawer.evaluate(n=>n.getAnimations({subtree:true}).filter(a=>a.playState==='running').length),0);
    await page.screenshot({path:path.join(out,`${theme}-${scale}-replies.png`)});
    await page.keyboard.press('Escape');await drawer.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    assert(Math.abs(await drawer.locator('.discussion-scroll').evaluate(n=>n.scrollTop)-scroll)<2,'reply Back restores root scroll');
    assert(await drawer.locator('[data-reply]').nth(3).evaluate(n=>document.activeElement===n),'reply Back restores exact source control');
    assert.equal(messages.filter(m=>['playPlatformResult','pausePlayback','resumePlayback','setEmbeddedVideo'].includes(m.action)).length,playback,'discussion navigation never schedules audio or video');
    // Closing cancels at animation start; a pending reply cannot revive it or overwrite roots.
    hold=true;await drawer.locator('[data-reply]').nth(3).click();const lateReply=reads().at(-1);
    await page.keyboard.press('Escape');
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(lateReply));
    assert.equal(await drawer.locator('.discussion-root').count(),0);
    assert.equal(await drawer.locator('.discussion-list > article').count(),25);
    await page.keyboard.press('Escape');await drawer.waitFor({state:'hidden'});
    assert(await page.locator('#mediaTools [data-media-action="comments"]').evaluate(n=>document.activeElement===n),'close restores player toolbar focus');
    // Capability loss removes reply navigation, but keeps readable root comments.
    hold=false;await page.locator('#mediaTools [data-media-action="comments"]').click();await drawer.locator('[data-reply]').first().waitFor();
    hold=true;await drawer.locator('[data-reply]').first().click();const revokedReply=reads().at(-1);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,capabilities:p.capabilities.filter(c=>c!=='CommentReplies')}]}),provider);
    await drawer.getByRole('heading',{name:'Comments',exact:true}).waitFor();assert.equal(await drawer.locator('[data-reply]').count(),0);
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(revokedReply));assert.equal(await drawer.locator('.discussion-root').count(),0);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await drawer.locator('[data-reply]').first().waitFor();
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,capabilities:p.capabilities.filter(c=>c!=='CommentReplies')}]}),provider);
    assert.equal(await drawer.locator('[data-reply]').count(),0,'root view also loses revoked reply controls');
    // Account/settings/revision changes invalidate the drawer without stopping the current track.
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:2}]}),provider);
    await drawer.waitFor({state:'hidden'});assert.equal(await page.locator('#stageTitle').textContent(),track.title);
    hold=false;await page.locator('#mediaTools [data-media-action="comments"]').click();await drawer.locator('[data-reply]').first().waitFor();
    const beforeLanguage=reads().length;
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'zh-CN',resolvedLanguage:'zh-CN'}));
    await drawer.getByRole('heading',{name:'评论',exact:true}).waitFor();assert.equal(reads().length,beforeLanguage);
    hold=true;await drawer.locator('[data-reply]').first().click();const oldTrackReply=reads().at(-1);
    await page.evaluate(()=>window.Auralis.playLocalTrack('ui-test-1'));
    await drawer.waitFor({state:'hidden'});
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(oldTrackReply));
    assert(await drawer.isHidden(),'late reply from the previous track cannot reopen comments');
    assert(await page.locator('#nowPlayingOverlay.open').count()===1,'changing track preserves the player surface');
    // A list/post discussion is deliberately fixed to its subject, unlike the player drawer.
    await page.locator('#closeNowPlayingButton').click();
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('[data-plugin-navigation="sample.discussion:detail"]').click();
    hold=false;await route.locator('[data-inline-discussion="main"]').click();await inline.locator('[data-reply]').first().waitFor();
    // Simulate a rapid owner hand-off while a dialog close event is still queued.
    await route.locator('[data-page-media="play"]').click();await page.locator('#nowPlayingButton').click();
    hold=true;await page.locator('#mediaTools [data-media-action="comments"]').click();
    const oldDrawer=reads().at(-1);
    await page.evaluate(()=>{
      document.querySelector('#closeNowPlayingButton').click();
      document.querySelector('[data-inline-discussion="main"]').click();
    });
    await inline.getByText('Loading…',{exact:true}).waitFor();
    const newPage=reads().at(-1);assert.equal(newPage.handle,handle(500));
    await page.waitForTimeout(50);
    const tail=messages.slice(messages.findIndex(m=>m===newPage)+1);
    assert(!tail.some(m=>m.action==='cancelPlatformExtras'&&['comments','replies'].includes(m.kind)),'queued old close cannot cancel successor read');
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(oldDrawer));
    assert.equal(await inline.locator('.discussion-list > article').count(),0);
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),response(newPage));
    await inline.locator('[data-reply]').first().waitFor();
    assert.deepEqual(errors,[]);
    await context.close();console.log(`PASS shared discussion ${theme} ${scale}: ownership, replies, focus, revision, capability, track and stale guards`);
  }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
