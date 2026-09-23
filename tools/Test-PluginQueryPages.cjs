'use strict';
// Actual published/source UI, isolated Edge, synthetic plugin documents. No accounts or external HTTP.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/plugin-query-pages-ui');
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
    const page=await context.newPage(),messages=[],errors=[],held=[];let hold=false,fail=false;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    const provider={id:'independent.query',name:'Independent studio',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages'],
      pages:[{id:'find',label:'发现音乐人',labelEn:'Discover creators',placement:'global',presentation:'page',documentVersion:5}]};
    const makeDoc=m=>{
      const detail=m.navigationHandle===handle('d'),values=m.inputValues||{query:'',kind:'all'};
      return {title:detail?'Creator profile':'Discover creators',description:detail?'Original biography.':'Explore original demonstration creators. Search and filters are declared entirely by a plugin.',
        layout:'cards',append:false,collectionHandle:'collection-results',actions:[],
        cards:detail?[{handle:'card-profile',title:'Profile content',text:'Read-only page navigation.',actions:[]}]:
          values.query?[{handle:'card-result',author:'Original creator',title:'Result: '+values.query,text:'Selected collection: '+values.kind,actions:[{label:'View profile',handle:handle('d')}]}]:[],
        query:detail?null:{submit:{label:'Search creators',handle:handle('a')},fields:[
          {key:'query',kind:'text',label:'Creator name',placeholder:'Enter a name',value:values.query,minLength:1,maxLength:32,options:[]},
          {key:'kind',kind:'choice',label:'Collection',placeholder:'',value:values.kind,minLength:0,maxLength:128,options:[{label:'All',value:'all'},{label:'Music',value:'music'}]}
        ]}};
    };
    await page.exposeFunction('queryBridge',async m=>{
      messages.push(m);if(m.action!=='readPluginGlobalPage')return;
      assert.equal(m.providerId,provider.id);assert(!('handle' in m));
      if(hold){held.push(m);return;}
      if(fail){fail=false;return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,error:{message:'Temporary query failure'}});}
      return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:makeDoc(m)});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.queryBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    if(scale===2)await page.evaluate(()=>window.Auralis.receiveLibrary([]));
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden());
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    const reads=()=>messages.filter(m=>m.action==='readPluginGlobalPage');
    assert.equal(reads().length,0);
    const nav=page.locator('[data-plugin-navigation="independent.query:find"]'),route=page.locator('.plugin-page-route');
    await nav.click();const input=route.getByRole('textbox',{name:'Creator name'});await input.waitFor();
    assert.equal(reads().length,1);assert(!('inputValues' in reads()[0]));
    await route.getByRole('button',{name:'Search creators',exact:true}).click();assert.equal(reads().length,1,'required input does not read');
    await input.fill('泠鸢yousa');await route.locator('.plugin-query-choices label').filter({hasText:'Music'}).click();
    assert.equal(reads().length,1,'typing/choice selection are inert');
    await input.focus();await input.dispatchEvent('compositionstart');
    await page.keyboard.press('Enter');await input.dispatchEvent('compositionend');
    assert.equal(reads().length,1,'IME Enter cannot submit');
    await page.keyboard.press('Enter');await route.getByText('Result: 泠鸢yousa',{exact:true}).waitFor();
    assert.deepEqual(reads().at(-1).inputValues,{query:'泠鸢yousa',kind:'music'});
    assert(await input.evaluate(n=>n===document.activeElement),'query keeps input focus after results');
    await route.evaluate(n=>window.originalSearchCard=n.querySelector('.plugin-page-card'));
    await route.getByRole('button',{name:'View profile',exact:true}).click();await route.getByText('Profile content',{exact:true}).waitFor();
    assert(!('inputValues' in reads().at(-1)),'ordinary navigation cannot override criteria');
    await route.locator('[data-plugin-back]').click();await input.waitFor();
    assert.equal(await input.inputValue(),'泠鸢yousa');assert(await route.getByRole('radio',{name:'Music',exact:true}).isChecked());
    assert.deepEqual(reads().at(-1).inputValues,{query:'泠鸢yousa',kind:'music'},'back replays exact query snapshot');
    assert(await page.evaluate(()=>window.originalSearchCard===document.querySelector('.plugin-page-card')),'validated Back retains original result DOM and reading state');
    assert(await route.getByRole('button',{name:'View profile',exact:true}).evaluate(n=>n===document.activeElement),'back restores originating entity-link keyboard focus');
    fail=true;await route.getByRole('button',{name:'View profile',exact:true}).click();
    await route.getByText('Temporary query failure',{exact:true}).waitFor();
    assert(await route.getByText('Result: 泠鸢yousa',{exact:true}).isVisible(),'failed target keeps search content');
    await route.getByRole('button',{name:'Retry',exact:true}).click();await route.getByText('Profile content',{exact:true}).waitFor();
    await page.keyboard.press('Escape');await input.waitFor();
    assert(await route.getByRole('button',{name:'View profile',exact:true}).evaluate(n=>n===document.activeElement),'Escape restores entity-link focus after retry');
    fail=true;await input.fill('Retry case');await input.press('Enter');await route.getByText('Temporary query failure',{exact:true}).waitFor();
    assert.equal(await input.inputValue(),'Retry case');assert(await route.getByText('Result: 泠鸢yousa',{exact:true}).isVisible());
    await route.getByRole('button',{name:'Retry',exact:true}).click();await route.getByText('Result: Retry case',{exact:true}).waitFor();
    hold=true;await input.fill('Older');await input.press('Enter');await input.fill('Newer');await input.press('Enter');
    await page.waitForTimeout(40);assert.equal(held.length,2);
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...held[1],handle:null,page:makeDoc(held[1])});
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...held[0],handle:null,page:makeDoc(held[0])});
    await route.getByText('Result: Newer',{exact:true}).waitFor();assert.equal(await input.inputValue(),'Newer');
    assert.equal(await route.getByText('Result: Older',{exact:true}).count(),0);
    assert(messages.some(m=>m.action==='cancelPlatformExtras'));
    hold=false;
    // Malformed query schema is rejected by the renderer as well as by Native.
    assert(await page.evaluate(()=>!window.AuralisPageQuery.valid({submit:{label:'Bad',handle:'page-'+'a'.repeat(32)},fields:[]})));
    assert(await page.evaluate(()=>{
      const keys=['addEventListener','elements','submit','__proto__'];
      const schema={submit:{label:'Read',handle:'page-'+'a'.repeat(32)},fields:keys.map(key=>({
        key,kind:'text',label:key,placeholder:'',value:'valid',minLength:0,maxLength:32,options:[]
      }))};
      let submitted;const form=window.AuralisPageQuery.create(schema,(_h,values)=>submitted=values,()=>{});
      document.body.append(form);form.dispatchEvent(new Event('submit',{cancelable:true}));form.remove();
      return keys.every(key=>submitted[key]==='valid')&&Object.keys(submitted).length===4;
    }),'plugin field names cannot clobber DOM form methods or object prototypes');
    await input.fill('<script>never run</script>');await input.press('Enter');
    await route.getByText('Result: <script>never run</script>',{exact:true}).waitFor();assert.equal(await route.locator('script').count(),0);
    const stored=await page.evaluate(()=>JSON.stringify({...localStorage}));assert(!stored.includes('never run')&&!stored.includes('泠鸢yousa'),'query is not persisted');
    await route.evaluate(n=>Promise.allSettled(n.getAnimations().map(a=>a.finished)));
    assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'narrow form does not overflow');
    await page.screenshot({path:path.join(out,theme+'-'+scale+'.png')});
    // A late query must not reopen a revoked page.
    hold=true;await input.fill('Revoked');await input.press('Enter');await page.waitForTimeout(40);const late=held.at(-1);
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[{...p,pageRevision:2}]}),provider);
    await route.waitFor({state:'hidden'});await page.evaluate(x=>window.Auralis.setPluginPage(x),{...late,handle:null,page:makeDoc(late)});
    assert(await route.isHidden());await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert(await page.locator('#pluginNavigation').isHidden());
    assert(!messages.some(m=>['readPluginPage','playPlatformResult','manageOnlineAccount'].includes(m.action)));assert.deepEqual(errors,[]);
    await context.close();console.log('PASS query pages '+theme+' '+scale+': inert input, IME, choices, back/retry, stale cancellation, missing plugins.');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
