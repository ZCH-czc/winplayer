'use strict';
// Real generic renderer, original synthetic content; no personal accounts or media.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/plugin-reading-windows-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error();
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
  res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const nav=n=>'page-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];let fail=false;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const provider={id:'sample.reading',name:'Reading sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages'],
      pages:[{id:'read',label:'阅读',labelEn:'Reading',placement:'global',presentation:'page',documentVersion:3}]};
    await page.exposeFunction('readingBridge',async m=>{
      messages.push(m);if(m.action!=='readPluginGlobalPage')return;
      if(fail){fail=false;return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,error:{message:'Synthetic retry'}});}
      const n=m.navigationHandle?parseInt(m.navigationHandle.slice(5),16):0;
      const first=!m.navigationHandle;
      const doc={title:'Reading sample',layout:'cards',append:!first,collectionHandle:'collection-reading',
        tabs:first?[{selected:true,action:{label:'Reading',handle:nav(0)}},{selected:false,action:{label:'About',handle:nav(999)}}]:[],
        cards:Array.from({length:100},(_,i)=>({handle:'card-'+(n*100+i),title:'Item '+(n*100+i),author:'Sample author',
          text:i%3===0?'Short text.':'Long original demonstration. '.repeat(35),publishedAt:'2026-09-20T01:00:00Z',actions:[]})),
        next:{label:'More',handle:nav(n+1)}};
      return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:doc});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.readingBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('[data-plugin-navigation="sample.reading:read"]').click();
    const route=page.locator('.plugin-page-route');
    await route.getByText('Item 0',{exact:true}).waitFor();
    await route.getByRole('button',{name:'Read more',exact:true}).first().click();
    assert.equal(await route.locator('.plugin-text-toggle[aria-expanded="true"]').count(),1);
    // Arrange settles after layout; verify same-column cards never overlap and DOM order is readable.
    await page.waitForTimeout(80);
    assert(await route.locator('.plugin-page-cards').evaluate(grid=>{
      const rects=[...grid.children].slice(0,12).map(n=>n.getBoundingClientRect());
      return rects.every((r,i)=>!i||r.top>=rects[i-1].top-1)&&rects.every((r,i)=>rects.slice(0,i).every(p=>Math.abs(r.left-p.left)>2||r.top>=p.bottom-1));
    }),'staggered card layout preserves order without overlap');
    await page.screenshot({path:path.join(out,`${theme}-${scale}.png`)});
    await route.getByRole('button',{name:'More',exact:true}).click();
    await route.getByRole('button',{name:'Continue reading',exact:true}).waitFor();
    assert.equal(await route.locator('.plugin-page-card').count(),200);
    fail=true;await route.getByRole('button',{name:'Continue reading',exact:true}).click();
    await route.getByText('Synthetic retry',{exact:true}).waitFor();assert.equal(await route.locator('.plugin-page-card').count(),200);
    await route.getByRole('button',{name:'Retry',exact:true}).click();
    await route.getByText('Item 200',{exact:true}).waitFor();assert.equal(await route.getByText('Item 0',{exact:true}).count(),0);
    assert.equal(await route.getByRole('tab').count(),2,'continuation retains navigation shell');
    await route.locator('[data-plugin-back]').click();
    await route.getByText('Item 0',{exact:true}).waitFor();
    assert.equal(await route.locator('.plugin-page-card').count(),200,'validated back restores the prior bounded window');
    assert.equal(await route.locator('.plugin-text-toggle[aria-expanded="true"]').count(),1);
    // Switching UI language must not throw away read content or send implicit network requests.
    const count=messages.filter(m=>m.action==='readPluginGlobalPage').length;
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'zh-CN',resolvedLanguage:'zh-CN'}));
    assert.equal(await route.locator('[data-plugin-refresh]').textContent(),'刷新');
    assert.equal(await route.locator('[data-plugin-back]').textContent(),'返回');
    assert.equal(messages.filter(m=>m.action==='readPluginGlobalPage').length,count);
    assert.equal(await route.locator('.plugin-page-card').count(),200);
    if(theme==='light'&&scale===1){
      // More than six snapshots: old segments must fall back to validated fresh content.
      await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
      for(let segment=1;segment<=7;segment++){
        await route.getByRole('button',{name:'Continue reading',exact:true}).click();
        await route.getByText('Item '+segment*200,{exact:true}).waitFor();
        await route.getByRole('button',{name:'More',exact:true}).click();
        await route.getByRole('button',{name:'Continue reading',exact:true}).waitFor();
        assert.equal(await route.locator('.plugin-page-card').count(),200);
      }
      for(let segment=6;segment>=0;segment--){
        await route.locator('[data-plugin-back]').click();
        await route.getByText('Item '+segment*200,{exact:true}).waitFor();
        assert(await route.locator('.plugin-page-card').count()<=200);
      }
      // Its old near-bottom scroll anchor can trigger a fresh append; expansion must not
      // come back from the evicted DOM, and the new window must remain bounded.
      assert(await route.locator('.plugin-page-card').count()<=200);
      assert.equal(await route.locator('.plugin-text-toggle[aria-expanded="true"]').count(),0);
    }
    assert.deepEqual(errors,[]);
    assert(!messages.some(m=>['playPlatformResult','openPlatformMusicVideo'].includes(m.action)||m.action==='prefetchPlatformTrack'&&m.handle));
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    await route.waitFor({state:'hidden'});
    assert.equal(await page.locator('.plugin-page-route:visible').count(),0);
    await context.close();console.log(`PASS reading windows ${theme} ${scale}: bounded continuation/back, retry, layout, language, no playback`);
  }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
