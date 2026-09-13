'use strict';
// Actual renderer, original synthetic page data, isolated browser. No platform accounts.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/plugin-page-feeds-ui');
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
const handle=c=>'page-'+c.repeat(32),community=n=>'community-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>{
      const url=r.request().url();
      if(url.startsWith('https://platform-art.auralis.local/'))return r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#86aeba"/><circle cx="220" cy="130" r="90" fill="#dcd2ae"/></svg>'});
      return url.startsWith(origin)?r.continue():r.abort();
    });
    const track={id:'demo',handle:'demo',kind:'online',providerId:'sample.feed',title:'Original sample',artist:'Demo musician',durationSeconds:120,isPlayable:true};
    const provider={id:'sample.feed',name:'Independent feed',configured:true,capabilities:['TrackSearch','Pages','CreatorSearch','Comments','CommentReplies'],
      pages:[{id:'home',label:'动态',labelEn:'Activity',placement:'creator',presentation:'page',acceptsCreatorContext:true,documentVersion:2}]};
    const picture='https://platform-art.auralis.local/demo';
    const makeCard=n=>({handle:community(n+100),author:'Demo musician',avatar:picture,title:'Original post '+n,
      text:'Paragraph one.\n\n'+('Readable demonstration content. '.repeat(n%2?12:5))+' <script>not executable</script>',publishedAt:'2026-09-13T03:00:00Z',
      images:n===0?[picture,picture+'2',picture+'3']:[picture],actions:[],discussionHandle:community(n+200),commentCount:30});
    const first={title:'Demo musician',description:'An original read-only plugin page.\nActivity and pictures supplied by the plugin.',image:picture,
      layout:'cards',cards:[],actions:[],append:false,collectionHandle:'collection-demo',next:{label:'Activity',handle:handle('a')}};
    let failed=false,hold=false,last,attempts=0;
    await page.exposeFunction('feedBridge',async m=>{
      messages.push(m);
      if(m.action==='platformSearchTracks')return page.evaluate(x=>window.Auralis.setPlatformSearchResult(x),{...m,items:[track]});
      if(m.action==='searchPlatformCreators')return page.evaluate(x=>window.Auralis.setCreatorSearchResult(x),{...m,items:[{handle:community(1),displayName:'Precise creator',description:'Creator context'}]});
      if(m.action==='readPluginPage'){
        last=m;if(hold)return;
        if(m.navigationHandle===handle('b')&&!failed){failed=true;attempts++;return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,error:{message:'Temporary append error'}});}
        const doc=!m.navigationHandle?first:{...first,append:true,cards:m.navigationHandle===handle('a')?Array.from({length:6},(_,i)=>makeCard(i)):[makeCard(0),makeCard(6)],
          next:m.navigationHandle===handle('a')?{label:'More activity',handle:handle('b')}:null};
        return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,page:doc});
      }
      if(m.action==='requestPlatformExtras'){
        assert(['comments','replies'].includes(m.kind),'page must not invoke legacy creator/feed reads');
        const payload={...m,items:m.kind==='comments'?[{handle:community(900),author:'Reader',text:'A dated comment',publishedAt:'2026-09-12T01:00:00Z',replyCount:2,avatarUrl:picture}]:
          [{handle:community(m.pageHandle?902:901),author:'Reply author',text:'Reply '+(m.pageHandle?'two':'one'),publishedAt:'2026-09-12T02:00:00Z',replyToAuthor:'Reader'}],
          nextPageHandle:m.kind==='replies'&&!m.pageHandle?'reply-page':null};
        return page.evaluate(x=>window.Auralis.setPlatformExtras(x),payload);
      }
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:platform-search-provider','sample.feed');localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.feedBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.locator('#sidebarSearchButton').click();await page.locator('#searchInput').fill('Demo');
    const author=page.locator('[data-media-action="track-creator"]:visible');await author.click();
    const feed=page.locator('.plugin-page-route');
    try{await page.waitForFunction(()=>document.querySelectorAll('.plugin-page-route .plugin-page-card').length===6,{},{timeout:10000});}
    catch(e){console.log({errors,messages:messages.slice(-12),dom:await feed.evaluate(n=>n.outerHTML)});await page.screenshot({path:path.join(out,'failure.png')});throw e;}
    assert.equal(await page.locator('dialog[open]').count(),0,'plugin presentation is main page');
    assert(await feed.locator('.plugin-page-header').evaluate(n=>{
      const back=n.querySelector('button').getBoundingClientRect(),title=n.querySelector('h1').getBoundingClientRect();
      return title.left-back.right<=20&&title.left>=back.right;
    }),'title stays beside Back, not at the far right');
    assert.equal(await feed.locator('script').count(),0);
    await feed.evaluate(n=>Promise.allSettled(n.getAnimations().map(a=>a.finished)));
    assert(await feed.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'no horizontal overflow');
    assert.equal(await feed.locator('.plugin-page-cards').evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length),scale===2?1:2);
    await page.screenshot({path:path.join(out,theme+'-'+scale+'-feed.png')});
    await feed.locator('.plugin-image-button').first().click();const gallery=page.locator('.plugin-gallery');
    await gallery.waitFor({state:'visible'});await page.keyboard.press('ArrowRight');assert.match(await gallery.locator('header').textContent(),/2 \/ 3/);
    await page.keyboard.press('Escape');await gallery.waitFor({state:'hidden'});assert(await feed.isVisible());
    assert(await feed.locator('.plugin-image-button').first().evaluate(n=>n===document.activeElement),'image focus restored');
    await feed.getByRole('button',{name:'View comments · 30',exact:true}).first().click();
    const comments=page.locator('dialog[aria-labelledby="mediaDialogTitle"]');try{await comments.locator('.comment-meta time').waitFor({timeout:10000});}catch(e){console.log({errors,messages:messages.slice(-6),comments:await comments.evaluate(n=>n.outerHTML)});throw e;}
    await comments.locator('[data-media-action="replies"]').click();await comments.locator('.comment-reply').waitFor();
    await comments.locator('[data-media-action="more-replies"]').click();await page.waitForFunction(()=>document.querySelectorAll('.comment-reply').length===2);
    assert.equal(await comments.locator('.comment-reply time').count(),2);
    await page.keyboard.press('Escape');await comments.waitFor({state:'hidden'});
    await feed.evaluate(n=>{window.originalCard=n.querySelector('.plugin-page-card');n.parentElement.scrollTop=n.parentElement.scrollHeight;});
    await feed.getByText('Temporary append error',{exact:true}).waitFor();
    assert.equal(await feed.locator('.plugin-page-card').count(),6,'failed append keeps old cards');
    await page.waitForTimeout(250);assert.equal(attempts,1,'failed append stops automatic retry');
    await feed.getByRole('button',{name:'Retry',exact:true}).click();
    await page.waitForFunction(()=>document.querySelectorAll('.plugin-page-route .plugin-page-card').length===7);
    assert(await page.evaluate(()=>window.originalCard===document.querySelector('.plugin-page-card')),'append never replaces already read DOM');
    await page.keyboard.press('Escape');await feed.waitFor({state:'hidden'});
    await page.waitForFunction(()=>document.activeElement?.matches('[data-media-action="track-creator"]'));
    assert.equal(await page.locator('#searchInput').inputValue(),'Demo');
    await page.locator('[data-creator-result="'+community(1)+'"]').click();
    await feed.waitFor({state:'visible'});assert(messages.some(m=>m.action==='readPluginPage'&&m.handle===community(1)),'creator search uses Pages');
    await page.keyboard.press('Escape');await feed.waitFor({state:'hidden'});
    hold=true;await author.click();await feed.waitFor({state:'visible'});
    await page.waitForFunction(()=>document.querySelector('.plugin-page-route [aria-busy="true"]'));
    const stale={...last,page:first};
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:99}]}),provider);
    await feed.waitFor({state:'hidden'});await page.evaluate(x=>window.Auralis.setPluginPage(x),stale);
    assert(await feed.isHidden(),'revoked response cannot reopen page');
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert.equal(await page.locator('[data-media-action="track-creator"]:visible').count(),0);
    assert(!messages.some(m=>['playPlatformResult','manageOnlineAccount','loadTrack'].includes(m.action)),'browsing cannot start playback/auth');
    assert.deepEqual(errors,[]);
    await context.close();console.log('PASS page feeds '+theme+' '+scale+': main route, append/dedupe/retry, galleries, comments/replies, creator search, revision.');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
