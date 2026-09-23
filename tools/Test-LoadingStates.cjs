'use strict';
// Real Web UI, held synthetic requests, isolated Edge. No personal accounts or plugin execution.
const {chromium}=require('playwright'),assert=require('node:assert/strict');
const fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/loading-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error('path');
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const handle=n=>'community-'+n.toString(16).padStart(32,'0'),nav=n=>'page-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    await page.exposeFunction('loadingBridge',m=>messages.push(m));
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.loadingBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    assert.deepEqual(await page.evaluate(()=>[
      AuralisLoading.retryImageUrl('https://platform-art.auralis.local/avatar-abc',1),
      AuralisLoading.retryImageUrl('https://example.com/private',1),
      AuralisLoading.retryImageUrl('https://platform-art.auralis.local/avatar-abc?token=secret',1)
    ]),['https://platform-art.auralis.local/avatar-abc?retry=1','',''],
    'image retries preserve only an approved opaque host handle, never an upstream URL or signed query');
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),{
      id:'sample.loading',name:'Loading example',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','Comments','CommentReplies'],
      pages:[{id:'feed',label:'动态',labelEn:'Activities',placement:'global',presentation:'page',documentVersion:8}]
    });
    await page.locator('[data-plugin-navigation="sample.loading:feed"]').click();
    const route=page.locator('.plugin-page-route'),status=route.locator('.plugin-page-status');
    await status.locator('.loading-state.is-initial').waitFor();
    assert.equal(await route.getByText('Loading…',{exact:true}).count(),1,'no duplicate header/footer label');
    assert.equal(await route.locator('.media-dialog-body').getAttribute('aria-busy'),'true');
    const style=await status.locator('.loading-ring').evaluate(n=>({name:getComputedStyle(n).animationName,duration:getComputedStyle(n).animationDuration,opacity:getComputedStyle(n).opacity}));
    assert.equal(style.name,scale===1.5?'none':'loading-orbit, loading-arrive');
    if(scale!==1.5)assert(style.duration.startsWith('1.4s'));
    // Wait only for finite decoration/page entrances, never for the indeterminate orbit.
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    await page.screenshot({path:path.join(out,`${theme}-${scale}-initial.png`)});
    const last=()=>messages.filter(m=>m.action==='readPluginGlobalPage').at(-1);
    const card=n=>({handle:handle(n),author:'Demo creator',title:'Activity '+n,text:'Original sample paragraph. '.repeat(50),discussionHandle:handle(100+n),commentCount:2,actions:[]});
    const doc={version:8,title:'Creator activities',layout:'feed',cards:[card(1),card(2)],actions:[],collectionHandle:'collection-demo',next:{label:'More activities',handle:nav(1)}};
    const deliver=async(m,d)=>page.evaluate(p=>window.Auralis.setPluginPage(p),{...m,handle:null,page:d});
    await deliver(last(),doc);await route.locator('.plugin-page-card').first().waitFor();
    await page.evaluate(()=>window.originalContinuation=document.querySelector('.plugin-page-status .continuation-button'));
    // Auto paging or its explicit fallback retains the cards, never replaces them with skeletons.
    await status.scrollIntoViewIfNeeded();
    await page.waitForFunction(()=>document.querySelector('.plugin-page-status .loading-state'));
    assert.equal(await route.locator('.plugin-page-card').count(),2);
    assert.equal(await status.locator('.loading-placeholder').count(),0);
    assert(await page.evaluate(()=>window.originalContinuation===document.querySelector('.plugin-page-status .continuation-button')),'append does not replace the More control');
    assert(await status.locator('.continuation-button').evaluate(n=>n.getBoundingClientRect().height>=44),'quiet button retains its accessible hit area');
    await deliver(last(),{...doc,append:true,cards:[card(3)],next:null});
    assert.equal(await route.locator('.plugin-page-card').count(),3);
    // Refresh remains in the reading position while pending, on error, and after retry.
    await page.locator('#pageContent').evaluate(n=>n.scrollTop=200);
    await route.locator('[data-plugin-refresh]').evaluate(n=>n.click());const refresh=last();
    const before=await page.locator('#pageContent').evaluate(n=>n.scrollTop);
    assert.equal(await route.locator('.plugin-page-card').count(),3);
    assert.equal(await status.locator('.loading-placeholder').count(),0);
    await status.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    await page.screenshot({path:path.join(out,`${theme}-${scale}-refresh.png`)});
    await page.evaluate(m=>window.Auralis.setPluginPage({...m,error:{message:'Synthetic retryable failure'}}),refresh);
    assert.equal(await status.locator('.loading-ring').count(),0);
    await status.getByRole('button',{name:'Retry',exact:true}).click();
    await deliver(last(),{...doc,cards:[card(1),card(2),card(3)],next:null});
    assert(Math.abs(await page.locator('#pageContent').evaluate(n=>n.scrollTop)-before)<2,'refresh does not jump back to top');
    assert.equal(await route.locator('.plugin-page-card').evaluateAll(nodes=>nodes.flatMap(n=>n.getAnimations()).length),0,'refresh has no repeated entrance animation');
    // Motion policy covers legacy rings and the video pseudo-element too.
    await page.evaluate(()=>{
      const fixture=document.createElement('div');fixture.id='loading-fixture';
      fixture.innerHTML='<span class="platform-spinner"></span><div class="embedded-video-page is-loading"><div id="embeddedVideoSurface"></div></div>';
      document.body.append(fixture);
      document.documentElement.dataset.motion='reduced';
    });
    assert.equal(await page.locator('#loading-fixture .platform-spinner').evaluate(n=>getComputedStyle(n).animationName),'none');
    assert.equal(await page.locator('#loading-fixture #embeddedVideoSurface').evaluate(n=>getComputedStyle(n,'::after').animationName),'none');
    await page.evaluate(()=>{document.documentElement.dataset.motion='full';Object.defineProperty(document,'hidden',{configurable:true,get:()=>true});document.dispatchEvent(new Event('visibilitychange'));});
    assert.equal(await page.locator('#loading-fixture .platform-spinner').evaluate(n=>getComputedStyle(n).animationPlayState),'paused');
    await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));document.querySelector('#loading-fixture').remove();});
    // Cancelling an initial request removes the visible indicator; late responses cannot resurrect it.
    await page.keyboard.press('Escape');await route.waitFor({state:'hidden'});await page.locator('[data-plugin-navigation="sample.loading:feed"]').click();
    await status.locator('.loading-state').waitFor();const stale=last();await page.keyboard.press('Escape');
    await route.waitFor({state:'hidden'});await deliver(stale,doc);assert.equal(await page.locator('.loading-state:visible').count(),0);
    assert.equal(messages.some(m=>['playPlatformResult','setEmbeddedVideo'].includes(m.action)),false);
    assert.deepEqual(errors,[]);console.log(`PASS loading ${theme} ${scale}: first/append/refresh/retry/cancel, reduced/hidden, retained content and scroll`);
    await context.close();
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
