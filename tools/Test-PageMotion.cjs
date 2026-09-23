'use strict';
// Production UI and opaque synthetic page messages. No plugins, accounts or real media.
const {chromium}=require('playwright'),assert=require('node:assert/strict');
const fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/page-motion-ui');
const server=http.createServer(async(req,res)=>{try{
  const url=new URL(req.url,'http://localhost'),file=path.resolve(root,'.'+decodeURIComponent(url.pathname==='/'?'/index.html':url.pathname));
  if(!file.startsWith(root+path.sep))throw Error('path');
  const source=!process.env.AURALIS_UI_ROOT&&file.endsWith(path.join('assets','auralis-icon.png'))?path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png'):file;
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(source));
}catch{res.statusCode=404;res.end();}});
const nav=n=>'page-'+n.toString(16).padStart(32,'0'),id=n=>'community-'+n.toString(16).padStart(32,'0');
const doc=(tab=0,detail=false)=>({version:8,title:detail?'Reading details':'Morning studio',description:'An original demonstration of connected reading surfaces.',layout:detail?'detail':'feed',
  cards:[{handle:id(10+tab),title:detail?'The full story':['Studio journal','New recordings','About the studio'][tab],author:'Morning studio',text:'Music, small observations, and stories behind the songs.\nA calm reading surface that keeps the music within reach.',open:detail?null:{label:'Read story',handle:nav(10)},actions:[]}],
  actions:[],tabs:detail?[]:['Activity','Uploads','About'].map((label,i)=>({selected:i===tab,action:{label,handle:nav(i+1)}}))});
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const reduced=scale===1.5,context=await browser.newContext({viewport:scale===2?{width:680,height:640}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:reduced?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    await page.exposeFunction('motionBridge',m=>messages.push(m));
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.motionBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[{id:'sample.motion',name:'Motion sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages'],pages:[{id:'home',label:'创作主页',labelEn:'Creator space',placement:'global',presentation:'page',documentVersion:8}]}]}));
    const route=page.locator('.plugin-page-route'),panel=route.locator('#pluginTabPanel');
    const requests=()=>messages.filter(m=>m.action==='readPluginGlobalPage');
    const waitRequest=async count=>{for(let i=0;i<100&&requests().length<count;i++)await page.waitForTimeout(10);assert.equal(requests().length,count);return requests().at(-1);};
    const reply=async(req,data,error)=>page.evaluate(p=>window.Auralis.setPluginPage(p),{...req,handle:null,page:data,error});
    const settle=()=>route.evaluate(async n=>{for(let i=0;i<4;i++){const animations=n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity);if(!animations.length)return;await Promise.allSettled(animations.map(a=>a.finished));}});
    await page.locator('[data-plugin-navigation]').click();await reply(await waitRequest(1),doc());await panel.waitFor();await settle();
    const playback=()=>messages.filter(m=>/^(playPlatformResult|prefetchPlatformTrack|pausePlayback|resumePlayback|seekPlayback|setEmbeddedVideo)$/.test(m.action));
    const playbackBaseline=playback(); // The test harness/configuration cancels prefetch on startup.
    await panel.evaluate(n=>window.originalPanel=n);
    await route.getByRole('tab',{name:'Uploads',exact:true}).click();const uploadRequest=await waitRequest(2);
    assert(await panel.evaluate(n=>n===window.originalPanel&&!n.inert),'network waiting retains the readable, interactive old content');
    // Freeze animation clocks, not the app state, so both phases can be inspected reliably.
    await page.evaluate(p=>{window.Auralis.setPluginPage(p);const n=document.querySelector('#pluginTabPanel'),a=n.getAnimations().find(a=>a.effect.getKeyframes().some(f=>'transform' in f));if(a){a.pause();a.currentTime=50;}},{...uploadRequest,handle:null,page:doc(1)});
    if(!reduced){
      assert(await panel.evaluate(n=>n===window.originalPanel&&n.inert&&n.getAnimations().some(a=>a.effect.getTiming().duration===100)),'ready response exits the previous pane before replacing it');
      await page.screenshot({path:path.join(out,`${theme}-${scale}-leaving.png`)});
      await panel.evaluate(n=>n.getAnimations().find(a=>a.effect.getKeyframes().some(f=>'transform' in f)).finish());
    }
    await page.waitForFunction(()=>document.querySelector('[role="tab"][aria-selected="true"]')?.textContent==='Uploads');
    if(!reduced){
      assert(await panel.evaluate(n=>n.getAnimations().some(a=>a.effect.getTiming().duration===240)),'new pane enters on the shared timeline');
      assert(await route.locator('.plugin-tab-indicator').evaluate(n=>n.getAnimations().some(a=>a.effect.getTiming().duration===280)),'selection indicator travels instead of jumping');
    }
    await settle();assert(await panel.evaluate(n=>!n.inert&&getComputedStyle(n).opacity==='1'&&getComputedStyle(n).transform==='none'));
    // A delayed error must leave the selected section and all reading nodes intact.
    await panel.evaluate(n=>window.uploadPanel=n);await route.getByRole('tab',{name:'About',exact:true}).click();
    await reply(await waitRequest(3),null,{message:'Temporary read failure'});
    assert(await panel.evaluate(n=>n===window.uploadPanel&&!n.inert));
    assert.equal(await route.locator('[aria-selected="true"]').textContent(),'Uploads');
    // Return to a cached sibling still uses the two-phase transition; identity stays valid.
    await route.getByRole('tab',{name:'Activity',exact:true}).click();await reply(await waitRequest(4),doc());await settle();
    assert(await panel.evaluate(n=>n===window.originalPanel&&!n.inert),'cache restoration preserves original reading nodes');
    // Cancel a response during its exit, then reject both its late replay and its commit.
    await route.getByRole('tab',{name:'Uploads',exact:true}).click();const stale=await waitRequest(5);
    await page.evaluate(p=>{window.Auralis.setPluginPage(p);document.querySelectorAll('[role="tab"]')[2].click();},{...stale,handle:null,page:doc(1)});
    const latest=await waitRequest(6);await reply(stale,doc(1));await reply(latest,doc(2));await settle();
    assert.equal(await route.locator('[aria-selected="true"]').textContent(),'About');
    assert.equal(await route.locator('h3').textContent(),'About the studio');
    assert.equal(await route.locator('[inert]').count(),0);
    // Forward and Back use opposite directions, retain focus and do not issue playback commands.
    await route.getByRole('button',{name:'About the studio',exact:true}).click();await reply(await waitRequest(7),doc(2,true));await settle();
    assert.equal(await route.getAttribute('data-reading-layout'),'detail');
    await route.locator('[data-plugin-back]').click();await reply(await waitRequest(8),doc(2));await settle();
    assert.equal(await route.getAttribute('data-reading-layout'),'feed');
    assert(await route.getByRole('button',{name:'About the studio',exact:true}).evaluate(n=>n===document.activeElement),'Back restores entry focus');
    assert(await route.evaluate(n=>n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'motion leaves no horizontal overflow');
    await page.screenshot({path:path.join(out,`${theme}-${scale}-settled.png`)});
    if(!reduced){
      await route.getByRole('tab',{name:'Activity',exact:true}).click();const req=await waitRequest(9);
      await page.evaluate(p=>{window.Auralis.setPluginPage(p);document.querySelector('#pluginTabPanel').getAnimations().find(a=>a.effect.getKeyframes().some(f=>'transform' in f))?.pause();},{...req,handle:null,page:doc()});
      await page.emulateMedia({reducedMotion:'reduce'});
      await page.waitForFunction(()=>document.documentElement.dataset.motion==='reduced');
      await page.waitForFunction(()=>document.querySelector('[role="tab"][aria-selected="true"]')?.textContent==='Activity');
      assert.equal(await route.locator('[inert]').count(),0,'live reduced motion completes the ready transaction');
      assert.equal(await panel.evaluate(n=>n.getAnimations().filter(a=>a.effect.getKeyframes().some(f=>'transform' in f||'opacity' in f)).length),0,'reduced motion leaves no navigation animation (scrollbar colour feedback is unrelated)');
      await page.emulateMedia({reducedMotion:'no-preference'});
      await page.waitForFunction(()=>document.documentElement.dataset.motion==='full');
    }
    await route.getByRole('tab',{name:reduced?'Activity':'Uploads',exact:true}).click();const last=await waitRequest(reduced?9:10);
    assert.deepEqual(playback(),playbackBaseline,'reading motion never commands playback or changes prefetch');
    await page.evaluate(p=>{window.Auralis.setPluginPage(p);window.Auralis.setPlatformConfiguration({providers:[]});},{...last,handle:null,page:doc(reduced?0:1)});
    await page.waitForTimeout(150);assert.equal(await route.count(),0,'revoking the provider cancels the ready commit and navigation');
    const cleanup=await page.evaluate(()=>{
      // A detached test owner exercises cleanup without altering the production page contract.
      const box=document.createElement('div');document.body.append(box);let commits=0;
      const owner=window.AuralisPageMotion.create(()=>false);
      const start=()=>{const old=document.createElement('div');box.replaceChildren(old);owner.swap(old,()=>{commits++;const next=document.createElement('div');box.replaceChildren(next);return next;});return old;};
      if(document.documentElement.dataset.motion==='reduced'){box.remove();return null;}
      const cancelled=start();owner.cancel();const cancelClean=!cancelled.inert&&commits===0;
      start();Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));
      const hiddenClean=commits===1&&!box.firstElementChild.inert&&box.firstElementChild.getAnimations().length===0;
      delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));
      start();window.dispatchEvent(new Event('resize'));const resizeClean=commits===2&&!box.firstElementChild.inert;
      owner.cancel();box.remove();return {cancelClean,hiddenClean,resizeClean};
    });
    if(!reduced)assert.deepEqual(cleanup,{cancelClean:true,hiddenClean:true,resizeClean:true});
    assert.deepEqual(errors,[]);console.log(`PASS motion ${theme} ${scale}: staged exit/enter, tab indicator, cached Back, failure retention, rapid cancellation, reduced motion, revocation, no playback side effects`);
    await context.close();
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
