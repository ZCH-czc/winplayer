'use strict';
// Synthetic provider + actual Web assets. No account, plugin execution or external requests.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/page-updates-ui'),nav=n=>'page-'+n.toString(16).padStart(32,'0');
const server=http.createServer(async(req,res)=>{try{
  let file=path.resolve(root,'.'+new URL(req.url,'http://localhost').pathname.replace(/^\/$/,'/index.html'));
  if(!file.startsWith(root+path.sep))throw Error();
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets/auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
(async()=>{
 await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
 try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
  const context=await browser.newContext({viewport:scale===2?{width:640,height:680}:{width:1380,height:900},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
  const page=await context.newPage(),messages=[],errors=[];let revision='A',hold=false,held,fail=false;
  const picture='https://platform-art.auralis.local/demo-portrait';
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',r=>r.request().url()===picture?r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128"><rect width="128" height="128" fill="#7aa9ad"/><circle cx="64" cy="43" r="25" fill="#f3d5ac"/><path d="M20 128Q10 62 64 72Q118 62 108 128" fill="#456b9c"/></svg>'}):r.request().url().startsWith(origin)?r.continue():r.abort());
  const provider={id:'demo.feed',name:'Demonstration',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages'],pages:[{id:'hub',label:'动态',labelEn:'Activity',placement:'global',presentation:'page',documentVersion:9}]};
  const updates=()=>({check:{label:'Check',handle:nav(5)},reload:{label:'Show new activity',handle:nav(6)},fingerprint:revision.repeat(64),intervalSeconds:90});
  const doc=(m)=>{const selected=m.navigationHandle===nav(2),people=m.navigationHandle===nav(3),detail=m.navigationHandle===nav(7);
    if(m.navigationHandle===nav(5))return {version:9,title:'Activity',layout:'feed',cards:[],append:false,updates:updates()};
    return {version:9,title:detail?'Post details':'Activity',description:'Follow your favourite creators. Search for new people in the main search.',layout:detail?'detail':people?'cards':'feed',append:false,
      navigation:detail||people?[]:Array.from({length:16},(_,i)=>({action:{label:i?'Creator '+i:'All activity',handle:nav(i===1?2:i===0?1:20+i)},image:i?picture:null,selected:selected?i===1:i===0})),
      tabs:detail?[]:['All activity','Videos','My following'].map((label,i)=>({action:{label,handle:nav([1,4,3][i])},selected:people?i===2:i===0})),
      updates:detail||people?null:updates(),cards:Array.from({length:detail?1:people?9:4},(_,i)=>({handle:'community-'+(i+1).toString(16).padStart(32,'0'),author:selected?'Creator 1':'Demo musician',avatar:picture,title:(people?'Creator ':selected?'Selected activity ':revision==='B'?'New activity ':'A creative afternoon ')+(i+1),publishedAt:'2026-09-21T02:00:00Z',text:'A quiet space to share a story.\n\n'+('This demonstration uses original text and synthetic artwork. '.repeat(5)),images:people?[]:[picture],open:detail?null:{label:'Read post',handle:nav(7)},actions:[]}))};
  };
  await page.exposeFunction('updateBridge',async m=>{messages.push(m);if(m.action!=='readPluginGlobalPage')return;
    if(hold){held=m;return;}if(fail&&m.navigationHandle===nav(5)){fail=false;return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,error:{message:'Synthetic rate limit'}});}
    return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:doc(m)});
  });
  await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.updateBridge(m)}};});
  await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
  await page.clock.install({time:new Date('2026-09-21T00:00:00Z')});await page.clock.pauseAt(new Date('2026-09-21T00:00:01Z'));
  const tick=async(ms=500)=>{await page.clock.fastForward(ms);await page.evaluate(()=>document.getAnimations().filter(a=>a.effect?.getTiming().iterations!==Infinity).forEach(a=>a.finish()));};
  await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
  assert.equal(messages.filter(m=>m.action==='readPluginGlobalPage').length,0,'discovery never polls');
  const hasSideEffect=m=>m.action==='playPlatformResult'||m.action==='prefetchPlatformTrack'&&!!m.handle||m.action==='setEmbeddedVideo'&&m.enabled||m.action==='manageOnlineAccount';
  const sideEffectsBefore=messages.filter(hasSideEffect).length;
  const entry=page.locator('[data-plugin-navigation="demo.feed:hub"]'),route=page.locator('.plugin-page-route');
  await entry.click();await tick();await route.getByText('A creative afternoon 1',{exact:true}).waitFor();
  assert.equal(await page.locator('#pluginNavigation .nav-item').count(),1);
  assert.equal(await route.locator('.plugin-identity-filter').count(),16);
  assert(await route.locator('.plugin-identity-filter').evaluateAll(ns=>ns.every(n=>n.getBoundingClientRect().height>=40)));
  assert(await route.locator('.plugin-identity-rail').evaluate(n=>n.scrollWidth>n.clientWidth&&getComputedStyle(n).overflowX==='auto'));
  assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1),'rail must not overflow page');
  await route.locator('.plugin-identity-filter').first().focus();await page.keyboard.press('ArrowRight');
  assert.equal(await page.evaluate(()=>document.activeElement.title),'Creator 1');
  await page.keyboard.press('Enter');await tick();await route.getByText('Selected activity 1',{exact:true}).waitFor();
  assert.equal(await route.locator('.plugin-identity-filter[aria-pressed="true"]').getAttribute('title'),'Creator 1');
  await route.getByRole('tab',{name:'My following',exact:true}).click();await tick();await route.getByText('Creator 1',{exact:true}).waitFor();
  const count=messages.filter(m=>m.navigationHandle===nav(5)).length;await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,count,'following list has no timer');
  await route.getByRole('tab',{name:'All activity',exact:true}).click();await tick();await route.getByText('A creative afternoon 1',{exact:true}).waitFor();
  const unchangedCount=messages.filter(m=>m.navigationHandle===nav(5)).length;await tick(91000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,unchangedCount+1,'unchanged check runs once');
  assert.equal(await route.locator('.plugin-updates-button').count(),0,'unchanged content has no notice');
  await context.setOffline(true);await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,unchangedCount+1,'offline page never polls');
  await context.setOffline(false);await tick();
  await route.locator('.plugin-page-cards').evaluate(n=>{window.savedReadingCard=n.firstElementChild;n.closest('.page-view').parentElement.scrollTop=280;});
  const scroll=await route.evaluate(n=>n.parentElement.scrollTop);revision='B';await tick(91000);
  await route.getByRole('button',{name:'New activity · Click to view',exact:true}).waitFor();
  assert(await page.evaluate(()=>window.savedReadingCard===document.querySelector('.plugin-page-cards').firstElementChild),'check retains original card DOM');
  assert.equal(await route.evaluate(n=>n.parentElement.scrollTop),scroll,'notice does not jump reading position');
  const afterNotice=messages.filter(m=>m.navigationHandle===nav(5)).length;await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,afterNotice,'unread notice suspends additional checks');
  await route.getByRole('button',{name:'New activity · Click to view',exact:true}).click();await tick();await route.getByText('New activity 1',{exact:true}).waitFor();
  fail=true;await tick(91000);await route.getByRole('button',{name:'Update checks paused · Click to retry',exact:true}).waitFor();
  const failedCount=messages.filter(m=>m.navigationHandle===nav(5)).length;await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,failedCount,'error does not loop retry');
  await route.getByRole('button',{name:'Update checks paused · Click to retry',exact:true}).click();await tick();
  // A slow background check must yield immediately to deliberate category navigation.
  hold=true;await tick(91000);assert(held?.navigationHandle===nav(5));const stale=held;
  hold=false;await route.getByRole('tab',{name:'My following',exact:true}).click();await tick();
  await page.evaluate(x=>window.Auralis.setPluginPage(x),{...stale,handle:null,page:doc(stale)});
  assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'My following');
  await route.getByRole('tab',{name:'All activity',exact:true}).click();await tick();
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  const hiddenCount=messages.filter(m=>m.navigationHandle===nav(5)).length;await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,hiddenCount,'hidden page never polls');
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));});
  await route.evaluate(n=>n.parentElement.scrollTop=0);await tick();await page.screenshot({path:path.join(out,`${theme}-${scale}.png`)});
  await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));await tick(180000);
  assert.equal(messages.filter(m=>m.navigationHandle===nav(5)).length,hiddenCount,'revoked provider cancels timers');
  assert.equal(messages.filter(hasSideEffect).length,sideEffectsBefore,'browsing has no media/account writes');
  assert.deepEqual(errors,[]);console.log(`PASS updates ${theme} ${scale}: rail, tabs, keyboard, polling, notice, retained DOM/scroll, error stop, stale, hidden, revocation`);
  await context.close();
 }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
