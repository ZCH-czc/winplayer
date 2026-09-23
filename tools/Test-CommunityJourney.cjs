'use strict';
// Production UI, declared v8 creator pages, isolated synthetic bridge. No account/network use.
const {chromium}=require('playwright'),assert=require('node:assert/strict');
const fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/community-journey-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error('path');
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets/auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
  res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const community=n=>'community-'+n.toString(16).padStart(32,'0'),nav=n=>'page-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const provider={id:'sample.journey',name:'Community example',configured:true,pageRevision:1,
      capabilities:['TrackSearch','CreatorSearch','Pages','Comments','CommentReplies'],
      pages:[{id:'creator',label:'作者主页',labelEn:'Creator',placement:'creator',presentation:'page',documentVersion:8,acceptsCreatorContext:true}]};
    const post=n=>({handle:community(100+n),author:'Demonstration musician',title:'Activity '+n,
      text:('A long original paragraph for reading and scroll restoration.\n\n').repeat(30),
      actions:[],open:{label:'Read activity',handle:nav(n)},discussionHandle:community(200+n),commentCount:1});
    const documentFor=m=>m.navigationHandle?{version:8,title:'Activity details',layout:'detail',cards:[{...post(1),open:null}],actions:[]}:
      {version:8,title:'Creator profile',layout:'feed',cards:[post(1),post(2)],actions:[]};
    let failCreators=true,holdCreators=false,heldCreators,holdPage=false,heldPage;
    await page.exposeFunction('journeyBridge',async m=>{
      messages.push(m);
      if(m.action==='platformSearchTracks')return page.evaluate(x=>window.Auralis.setPlatformSearchResult(x),{...m,items:[]});
      if(m.action==='searchPlatformCreators'){
        if(holdCreators){heldCreators=m;return;}
        const error=failCreators?{message:'Synthetic creator search failed'}:null;failCreators=false;
        return page.evaluate(x=>window.Auralis.setCreatorSearchResult(x),{...m,error,items:error||m.query==='No matches'?[]:Array.from({length:8},(_,i)=>({handle:community(i+1),displayName:'Musician '+(i+1),description:'Original sample profile'}))});
      }
      if(m.action==='readPluginPage'){
        if(holdPage){heldPage=m;return;}
        return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,page:documentFor(m)});
      }
      if(m.action==='requestPlatformExtras')return page.evaluate(x=>window.Auralis.setPlatformExtras(x),{...m,items:[{
        handle:community(999),author:'Reader',text:m.kind==='replies'?'Reply to the original comment':'Comment on the selected activity',publishedAt:'2026-09-20T01:00:00Z',replyCount:m.kind==='replies'?0:1}]});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:platform-search-provider','sample.journey');localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.journeyBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('#sidebarSearchButton').click();await page.locator('#searchInput').fill('Musician');
    const search=page.locator('#creatorSearchSection'),retry=search.getByRole('button',{name:'Retry',exact:true});
    await retry.waitFor();holdCreators=true;await retry.focus();await page.keyboard.press('Enter');
    assert(await search.locator('.continuation-button').evaluate(n=>n===document.activeElement&&n.getAttribute('aria-disabled')==='true'),'search pending retains the same button and disables duplicate requests');
    await page.evaluate(m=>window.Auralis.setCreatorSearchResult({...m,error:{message:'Synthetic creator search failed again'}}),heldCreators);
    assert(await retry.evaluate(n=>n===document.activeElement),'failed retry restores its button');
    holdCreators=false;await page.keyboard.press('Enter');await search.locator('.creator-search-card').first().waitFor();
    await page.locator('button[data-category="users"]').click();
    const chosen=page.locator('[data-creator-result="'+community(7)+'"]');await chosen.scrollIntoViewIfNeeded();await chosen.focus();
    const searchScroll=await page.locator('#pageContent').evaluate(n=>n.scrollTop);
    const searches=messages.filter(m=>m.action==='searchPlatformCreators').length;
    await page.keyboard.press('Enter');const route=page.locator('.plugin-page-route');
    await route.getByRole('heading',{name:'Creator profile',exact:true}).waitFor();
    assert(messages.some(m=>m.action==='readPluginPage'&&m.handle===community(7)),'user result enters declared creator page with the exact handle');
    const comments=route.locator('[data-inline-discussion="main"]').first();
    await comments.click();const discussion=route.locator('.plugin-detail-discussion');
    await discussion.locator('[data-reply]').waitFor();assert.equal(await page.locator('dialog[open]').count(),0);
    await discussion.locator('[data-reply]').click();await discussion.getByRole('heading',{name:'Comment details',exact:true}).waitFor();
    await page.keyboard.press('Escape');await discussion.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    // A failed refresh of a long detail keeps both reading content and recovery in view.
    await route.evaluate(n=>{n.parentElement.scrollTop=n.parentElement.scrollHeight;});
    const back=route.locator('[data-plugin-back]');
    assert(await back.evaluate(n=>{const r=n.getBoundingClientRect();return r.top>=0&&r.bottom<innerHeight-70;}),'long detail keeps Back reachable');
    holdPage=true;await route.locator('[data-plugin-refresh]').click();
    await page.evaluate(m=>window.Auralis.setPluginPage({...m,error:{message:'Synthetic detail refresh failed'}}),heldPage);
    const feedback=route.locator('.plugin-page-status'),recover=feedback.getByRole('button',{name:'Retry',exact:true});
    await recover.waitFor();
    assert(await feedback.evaluate(n=>{const r=n.getBoundingClientRect(),bar=document.querySelector('#playerBar').getBoundingClientRect();return r.top>=0&&r.bottom<=bar.top;}),'navigation recovery is visible above transport even at the end of a long post');
    assert.equal(await route.locator('.plugin-page-card').count(),1,'failed navigation preserves the post');
    await recover.focus();await page.keyboard.press('Enter');
    assert(await feedback.locator('.continuation-button').evaluate(n=>n===document.activeElement&&n.getAttribute('aria-disabled')==='true'),'navigation pending retains its focused control');
    await page.evaluate(m=>window.Auralis.setPluginPage({...m,error:{message:'Synthetic detail refresh failed'}}),heldPage);
    assert(await recover.evaluate(n=>n===document.activeElement));
    await page.screenshot({path:path.join(out,`${theme}-${scale}-recovery.png`)});
    holdPage=false;await page.keyboard.press('Enter');await route.getByRole('heading',{name:'Activity details',exact:true}).waitFor();
    holdPage=true;await route.locator('[data-plugin-refresh]').click();const oldRefresh=heldPage;
    await page.keyboard.press('Escape');const backRead=heldPage;
    assert.notEqual(backRead.requestId,oldRefresh.requestId,'Back cancels a pending refresh and reads its own target');
    await page.evaluate(m=>window.Auralis.setPluginPage({...m,error:{message:'Stale refresh error'}}),oldRefresh);
    assert.equal(await route.getByText('Stale refresh error',{exact:true}).count(),0);
    holdPage=false;await page.evaluate(x=>window.Auralis.setPluginPage(x),{...backRead,page:documentFor(backRead)});
    await route.getByRole('heading',{name:'Creator profile',exact:true}).waitFor();
    assert(await route.locator('[data-inline-discussion="main"]').first().evaluate(n=>n===document.activeElement),'detail Back restores card action');
    await page.keyboard.press('Escape');await route.waitFor({state:'hidden'});
    await page.waitForFunction(h=>document.activeElement?.dataset.creatorResult===h,community(7));
    assert.equal(await page.locator('button[data-category="users"]').getAttribute('aria-pressed'),'true');
    assert.equal(await page.locator('#searchInput').inputValue(),'Musician');
    assert.equal(messages.filter(m=>m.action==='searchPlatformCreators').length,searches,'round trip reuses creator search cache');
    assert(Math.abs(await page.locator('#pageContent').evaluate(n=>n.scrollTop)-searchScroll)<2,'round trip restores search scroll');
    await page.locator('#searchInput').fill('No matches');
    await search.getByText('No matching creators',{exact:true}).waitFor();
    assert.equal(await search.locator('.creator-search-card').count(),0,'empty query result removes old creators');
    assert(!messages.some(m=>['playPlatformResult','loadTrack','pausePlayback','manageOnlineAccount'].includes(m.action)),'reading never changes playback or account state');
    assert.deepEqual(errors,[]);await context.close();console.log(`PASS community journey ${theme} ${scale}: search retry/focus, users -> v8 creator -> detail -> replies -> users, long-page recovery, no playback`);
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
