'use strict';
// Actual core UI, isolated Edge, original synthetic plugin documents. No accounts or media.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/plugin-global-pages-ui');
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
const handle=c=>'page-'+c.repeat(32);
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[],held=[];let fail=true,hold=false;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const provider={id:'independent.space',name:'Independent studio',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','Comments'],
      pages:[{id:'hub',label:'工作室',labelEn:'Studio',placement:'global',presentation:'page',documentVersion:3},
        {id:'second',label:'参考资料',labelEn:'Reference',placement:'global',presentation:'page',documentVersion:3}]};
    const makeDoc=m=>{
      const selected=m.navigationHandle===handle('b')?1:m.navigationHandle===handle('c')?2:0;
      return {title:m.entryId==='second'?'Reference':'Independent studio',description:'Original demonstration. Supplied by an optional plugin, without selecting a song.',
        layout:'cards',append:false,collectionHandle:'collection-'+selected,cards:[{handle:'card-'+selected,title:['Overview content','Journal content','Archive content'][selected],text:'Plain text <script>never executed</script>',actions:[],discussionHandle:'community-'+'d'.repeat(32),commentCount:1}],actions:[],
        tabs:['Overview','Journal','Archive'].map((label,i)=>({action:{label,handle:handle(['a','b','c'][i])},selected:i===selected}))};
    };
    await page.exposeFunction('globalBridge',async m=>{
      messages.push(m);
      if(m.action==='requestPlatformExtras'){
        assert.equal(m.kind,'comments');assert.equal(m.handle,'community-'+'d'.repeat(32));
        return page.evaluate(x=>window.Auralis.setPlatformExtras(x),{...m,items:[{handle:'community-'+'e'.repeat(32),author:'Reader',text:'Global page discussion',publishedAt:'2026-09-20T01:00:00Z'}]});
      }
      if(m.action!=='readPluginGlobalPage')return;
      assert.equal(m.providerId,provider.id);assert(!('handle' in m),'global bridge must not fabricate a media handle');
      if(hold){held.push(m);return;}
      if(m.navigationHandle===handle('b')&&fail){fail=false;return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,error:{message:'Temporary tab failure'}});}
      return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:makeDoc(m)});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.globalBridge(m)}};});
    // Test the real initial configuration wait, before ui-test fixtures publish an empty snapshot.
    await page.goto(origin+'/');
    await page.locator('#pluginNavigation .loading-placeholder').waitFor();
    assert.equal(await page.locator('#pluginNavigation').getAttribute('aria-busy'),'true');
    assert.equal(await page.locator('#pluginNavigation button').count(),0,'discovery skeleton invents no plugin entries');
    assert(await page.locator('#pluginNavigation').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'loading feedback fits the compact icon sidebar');
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden(),'completed empty inventory removes its placeholder');
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);
    await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden(),'no default plugin section');
    // A media-only plugin must not gain a global navigation entry.
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,capabilities:['Pages']}]}),provider);
    assert(await page.locator('#pluginNavigation').isHidden());
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    assert.equal(messages.filter(m=>m.action==='readPluginGlobalPage').length,0,'inventory never reads page');
    if(scale===2)await page.evaluate(()=>window.Auralis.receiveLibrary([]));
    const nav=page.locator('[data-plugin-navigation="independent.space:hub"]'),route=page.locator('.plugin-page-route');
    assert(await page.locator('.settings-link').evaluate(n=>{
      const previous=document.querySelector('#pluginNavigation').getBoundingClientRect(),r=n.getBoundingClientRect();
      return r.top-previous.bottom>=0&&r.top-previous.bottom<=16;
    }),'Settings follows the bounded plugin list without a flexible blank spacer');
    await nav.click();await route.getByText('Overview content',{exact:true}).waitFor();
    assert.equal(await page.locator('dialog[open]').count(),0);
    await route.getByRole('button',{name:'View comments · 1',exact:true}).click();
    const discussion=page.locator('dialog[aria-labelledby="mediaDialogTitle"]');
    await discussion.getByText('Global page discussion',{exact:true}).waitFor();
    await discussion.locator('.comment-meta time').waitFor();
    await page.keyboard.press('Escape');await discussion.waitFor({state:'hidden'});
    assert(await route.getByText('Overview content',{exact:true}).isVisible(),'global comments return to source, without requiring a selected song');
    assert.equal(await page.locator('.sidebar .nav-item.active').count(),1,'single selected navigation item');
    assert.equal(await nav.getAttribute('aria-current'),'page');
    assert.equal(await route.locator('script').count(),0);
    assert.equal(await page.locator('[role="tabpanel"]').getAttribute('aria-labelledby'),'pluginTab-0');
    await route.getByRole('tab',{name:'Overview',exact:true}).focus();
    await page.keyboard.press('ArrowRight');assert.equal(await page.evaluate(()=>document.activeElement.textContent),'Journal');
    assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'Overview','arrow keys only move focus');
    await page.keyboard.press('Enter');await route.getByText('Temporary tab failure',{exact:true}).waitFor();
    assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'Overview','failure keeps old selection');
    assert(await route.getByText('Overview content',{exact:true}).isVisible());
    await route.getByRole('button',{name:'Retry',exact:true}).click();await route.getByText('Journal content',{exact:true}).waitFor();
    assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'Journal');
    assert(await route.getByRole('tab',{name:'Journal',exact:true}).evaluate(n=>n===document.activeElement),'selected tab receives focus');
    await page.keyboard.press('End');await page.keyboard.press('Enter');await route.getByText('Archive content',{exact:true}).waitFor();
    // Responses deliberately arrive out of order; only the latest requested tab may commit.
    hold=true;await route.getByRole('tab',{name:'Journal',exact:true}).click();
    await route.getByRole('tab',{name:'Overview',exact:true}).click();
    await page.waitForTimeout(50);assert.equal(held.length,2);
    assert.equal(await nav.getAttribute('aria-busy'),'true','selected plugin entry communicates its pending read');
    assert.equal(await nav.locator('.loading-ring').count(),1);
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...held[1],handle:null,page:makeDoc(held[1])});
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...held[0],handle:null,page:makeDoc(held[0])});
    await route.getByText('Overview content',{exact:true}).waitFor();assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'Overview');
    assert.equal(await nav.getAttribute('aria-busy'),'false','settled page clears sidebar busy decoration');
    assert(messages.some(m=>m.action==='cancelPlatformExtras'&&m.kind==='plugin-page'));
    hold=false;
    // A saved tab must not short-circuit backend validation or resurrect unavailable content.
    fail=true;await route.getByRole('tab',{name:'Journal',exact:true}).click();
    await route.getByText('Temporary tab failure',{exact:true}).waitFor();
    assert.equal(await route.getByRole('tab',{selected:true}).textContent(),'Overview');
    assert.equal(await route.getByText('Journal content',{exact:true}).count(),0,'failed validation never restores cached tab');
    await route.getByRole('button',{name:'Retry',exact:true}).click();await route.getByText('Journal content',{exact:true}).waitFor();
    // Opening a second global entry while already in route plugin must actually replace the document.
    await page.locator('[data-plugin-navigation="independent.space:second"]').click();
    await route.getByRole('heading',{name:'Reference',exact:true}).waitFor();
    await nav.click();await route.getByRole('heading',{name:'Independent studio',exact:true}).waitFor();
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).map(a=>a.finished)));
    assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'narrow page has no horizontal overflow');
    const settings=page.locator('.settings-link');assert(await settings.evaluate(n=>{const r=n.getBoundingClientRect();return r.top>=38&&r.bottom<window.innerHeight-60;}),'settings remains visible at short height');
    await page.screenshot({path:path.join(out,theme+'-'+scale+'.png')});
    await route.evaluate(n=>window.beforeLocaleCard=n.querySelector('.plugin-page-card'));
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'zh-CN',resolvedLanguage:'zh-CN'}));
    assert.match(await nav.getAttribute('aria-label'),/工作室/,'plugin navigation uses declared Chinese label');
    await route.getByRole('tab',{name:'Journal',exact:true}).click();await route.getByText('Journal content',{exact:true}).waitFor();
    await route.getByRole('tab',{name:'Overview',exact:true}).click();await route.getByText('Overview content',{exact:true}).waitFor();
    assert(await route.evaluate(n=>window.beforeLocaleCard!==n.querySelector('.plugin-page-card')),'language change cannot restore an old-language reading view');
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
    assert.match(await nav.getAttribute('aria-label'),/Studio/);
    await page.keyboard.press('Escape');await route.waitFor({state:'hidden'});assert.equal(await page.locator('.nav-item[data-page="songs"]').getAttribute('class'),'nav-item active');
    // Revocation while reading closes route; held responses cannot reopen it.
    hold=true;await nav.click();await page.waitForTimeout(50);const late=held.at(-1);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:2}]}),provider);
    await route.waitFor({state:'hidden'});
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...late,handle:null,page:makeDoc(late)});assert(await route.isHidden());
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,configured:false}]}),provider);
    assert(await page.locator('#pluginNavigation').isHidden(),'missing required config removes global entry');
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden());
    assert(!messages.some(m=>['readPluginPage','playPlatformResult','manageOnlineAccount'].includes(m.action)),'global browsing never invokes media/auth commands');
    assert.deepEqual(errors,[]);
    await context.close();console.log('PASS global pages '+theme+' '+scale+': inert navigation, tabs, keyboard, error/retry, stale cancellation, empty/missing plugins.');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
