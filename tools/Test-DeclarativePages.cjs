'use strict';
// Real application resources, synthetic provider data, isolated Edge. No accounts or network.
const {chromium}=require('playwright');
const assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/declarative-pages-ui');
const server=http.createServer(async(req,res)=>{
  try{
    const pathname=new URL(req.url,'http://localhost').pathname;
    let file=path.resolve(root,'.'+decodeURIComponent(pathname==='/'?'/index.html':pathname));
    if(!file.startsWith(root+path.sep))throw Error();
    if(!process.env.AURALIS_UI_ROOT && file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
    res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
    res.end(await fs.readFile(file));
  }catch{res.statusCode=404;res.end();}
});
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),errors=[],messages=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const track={id:'demo-track',handle:'demo-track',kind:'online',providerId:'arbitrary.pages',title:'Original demonstration',artist:'Demo artist',album:'Demo',durationSeconds:120,isPlayable:true};
    const provider={id:track.providerId,name:'Independent fixture',capabilities:['TrackSearch','Pages'],configured:true,
      pages:[{id:'biography',label:'音乐人',labelEn:'Musician',placement:'creator'},{id:'new-page',label:'新页面',labelEn:'New page',placement:'media'}]};
    let hold=false,fail=false,last;
    const doc={title:'A plugin-owned page',description:'Plain text <script>unsafe()</script>',layout:'cards',
      cards:Array.from({length:6},(_,i)=>({title:'Card '+i,text:'An original demonstration paragraph. '.repeat(12),publishedAt:'2026-09-13T01:00:00Z',actions:[]})),
      actions:[{label:'New plugin route',handle:'page-'+'a'.repeat(32)}]};
    await page.exposeFunction('pagesBridge',async m=>{
      messages.push(m);
      if(m.action==='platformSearchTracks')await page.evaluate(x=>window.Auralis.setPlatformSearchResult(x),{...m,items:[track],totalCount:1});
      if(m.action==='playPlatformResult')await page.evaluate(x=>window.Auralis.setPlatformPlaybackResult(x),{handle:m.handle,success:true,item:track});
      if(m.action!=='readPluginPage')return;
      last=m;if(hold)return;
      const payload={...m,page:m.navigationHandle?{...doc,title:'New route without a core branch',layout:'list',actions:[]}:doc,error:fail?{message:'Temporary error'}:null};
      fail=false;await page.evaluate(x=>window.Auralis.setPluginPage(x),payload);
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:platform-search-provider','arbitrary.pages');localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.pagesBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.locator('#sidebarSearchButton').click();await page.locator('#searchInput').fill('Demo');
    const author=page.locator('[data-media-action="track-creator"]:visible');await author.waitFor();await author.focus();await page.keyboard.press('Enter');
    const dialog=page.locator('.plugin-page-dialog');
    await page.waitForFunction(()=>document.querySelector('#pluginPageTitle')?.textContent==='A plugin-owned page');
    assert.equal(await dialog.locator('[data-plugin-back]').isVisible(),false,'initial page has no back control');
    assert.equal(await page.locator('.creator-feed-dialog[open]').count(),0,'does not use hardcoded creator renderer');
    assert.equal(await dialog.locator('script').count(),0,'data cannot inject scripts');
    assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').length,0,'no special creator protocol');
    await dialog.evaluate(d=>Promise.allSettled(d.getAnimations().map(a=>a.finished)));
    assert(await dialog.evaluate(d=>{const r=d.getBoundingClientRect(),body=d.querySelector('.media-dialog-body');return r.top>=0&&r.bottom<=innerHeight+1&&body.scrollWidth<=body.clientWidth+1;}),'fits viewport with internal scrolling');
    await page.screenshot({path:path.join(out,theme+'-'+scale+'.png')});
    fail=true;await dialog.getByRole('button',{name:'New plugin route',exact:true}).click();
    await dialog.getByText('Temporary error',{exact:true}).waitFor();
    assert.equal(await dialog.locator('.plugin-page-card').count(),6,'failed navigation retains content');
    await dialog.getByRole('button',{name:'Retry',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('#pluginPageTitle')?.textContent==='New route without a core branch');
    await dialog.locator('[data-plugin-back]').click();
    await page.waitForFunction(()=>document.querySelector('#pluginPageTitle')?.textContent==='A plugin-owned page');
    hold=true;await dialog.getByRole('button',{name:'New plugin route',exact:true}).click();
    await page.waitForTimeout(40);const stale={...last,page:{...doc,title:'STALE'}};
    await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});
    await page.evaluate(x=>window.Auralis.setPluginPage(x),stale);
    assert.equal(await dialog.isVisible(),false,'late response cannot reopen');
    hold=false;await author.click();await dialog.waitFor({state:'visible'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:1}]}),provider);
    await dialog.waitFor({state:'hidden'});
    await author.click();await dialog.waitFor({state:'visible'});
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    await dialog.waitFor({state:'hidden'});
    assert(!messages.some(m=>['playPlatformResult','loadTrack','manageOnlineAccount'].includes(m.action)),'browsing never starts playback or login');
    // Explicit synthetic playback only: the full-screen toolbar exposes plugin-declared pages.
    const expanded={...provider,pages:[provider.pages[0],...Array.from({length:7},(_,i)=>({id:'extra-'+i,label:'新页面 '+i,labelEn:'New page '+i,placement:'media'}))]};
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),expanded);
    await page.locator('.row-play[data-platform-play-handle="demo-track"]').click();
    await page.waitForFunction(()=>document.querySelector('#miniTitle').textContent==='Original demonstration');
    await page.locator('#miniCover').click();await page.locator('#nowPlayingOverlay.open').waitFor();
    assert.equal(await page.locator('.plugin-page-entries button').count(),1,'many entries cannot overflow the toolbar');
    const reads=messages.filter(m=>m.action==='readPluginPage').length;
    await page.locator('.plugin-page-entries button').click();
    assert.equal(messages.filter(m=>m.action==='readPluginPage').length,reads,'entry chooser is inert');
    await dialog.getByRole('button',{name:'New page 5',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('#pluginPageTitle')?.textContent==='A plugin-owned page');
    assert.equal(last.entryId,'extra-5','new arbitrary entry routes without a platform-specific branch');
    await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});
    assert(await page.locator('.plugin-page-entries button').evaluate(n=>n===document.activeElement),'chooser restores external toolbar focus');
    assert.deepEqual(errors,[]);
    await context.close();console.log('PASS declarative UI '+theme+' '+scale+': data renderer, keyboard, layout, back/retry, stale and missing-plugin.');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
