'use strict';
// Real app resources, synthetic Native bridge, isolated Edge. No plugins, user state or external network.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||'Auralis/wwwroot'),out=path.resolve('artifacts/settings-v2-ui');
const server=http.createServer(async(req,res)=>{try{
 const file=path.resolve(root,'.'+new URL(req.url,'http://localhost').pathname.replace(/\/$/,'/index.html'));
 if(!file.startsWith(root+path.sep))throw Error();
 res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
 res.end(await fs.readFile(file));
}catch{res.writeHead(404);res.end();}});
(async()=>{
 await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
 try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
  const context=await browser.newContext({viewport:scale===2?{width:640,height:600}:scale===1.5?{width:1920,height:1080}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1?'no-preference':'reduce'});
  const page=await context.newPage(),messages=[],errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
  await page.exposeFunction('bridge',m=>messages.push(m));await page.addInitScript(()=>window.chrome={webview:{postMessage:m=>window.bridge(m)}});
  await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);
  if(scale===2)await page.evaluate(()=>window.Auralis.receiveLibrary([]));
  const config=providers=>page.evaluate(providers=>window.Auralis.setPlatformConfiguration({providers}),providers);
  const language=lang=>page.evaluate(lang=>window.Auralis.setUiLanguageState({preference:lang,resolvedLanguage:lang}),lang);
  const group={id:'connection',label:'连接配置',labelEn:'Connection settings',description:'只影响这个演示插件，不影响本地播放。',descriptionEn:'Only affects this sample plugin. Local playback is unchanged.'};
  const settings=[{key:'mode',label:'连接方式',labelEn:'Connection mode',kind:'choice',group,value:'automatic',enabled:true,
    choices:[{value:'automatic',label:'自动',labelEn:'Automatic'},{value:'custom',label:'自定义',labelEn:'Custom'}]},
   {key:'server',label:'服务地址',labelEn:'Service address',description:'只接受 HTTPS 或本机 HTTP 地址。',descriptionEn:'HTTPS or loopback HTTP only.',kind:'endpoint',required:true,group,value:'https://example.test/previous',enabled:false,
    when:{key:'mode',value:'custom',hint:'先选择自定义，再设置服务地址。',hintEn:'Choose Custom before editing the service address.'}}];
  const provider={id:'sample.settings',name:'Independent plugin',capabilities:['TrackSearch'],configured:true,settings};
  const custom=()=>({...provider,settings:settings.map(s=>s.key==='mode'?{...s,value:'custom'}:{...s,enabled:true})});
  await config([]);await page.locator('[data-page="settings"]').click();await page.locator('[data-settings-section="online"]').click();
  await page.locator('[data-settings-group="online"]').waitFor();await page.waitForTimeout(350);
  assert.equal(await page.locator('[data-plugin-setting-group]').count(),0);
  await config([provider]);
  assert.equal(await page.locator('[data-plugin-setting-group]').count(),1,'same group merges into one heading');
  assert.equal(await page.locator('[data-declared-setting]').count(),2);
  const input=()=>page.locator('[data-provider-setting-input="server"]'),mode=()=>page.locator('[data-fluent-select="pluginSetting:sample.settings:mode"]');
  assert(await input().isDisabled());assert.equal(await input().inputValue(),'https://example.test/previous');
  for(const lang of ['zh-CN','en-US']){
   await language(lang);await page.waitForTimeout(500);
   const english=lang==='en-US';
   assert.equal(await page.locator('[data-plugin-setting-group] h3').innerText(),'Independent plugin · '+(english?'Connection settings':'连接配置'));
   assert.equal(await mode().innerText(),english?'Automatic':'自动');
   assert.equal(await page.locator('.plugin-setting-hint').innerText(),english?settings[1].when.hintEn:settings[1].when.hint);
   assert.equal(await input().getAttribute('aria-label'),english?'Service address':'服务地址');
   await page.screenshot({path:path.join(out,theme+'-'+scale+'-'+lang+'.png')});
  }
  const before=messages.filter(m=>m.action==='saveOnlineProviderSetting').length;
  await mode().click();await page.keyboard.press('End');await page.keyboard.press('Enter');await page.waitForTimeout(70);
  const selected=messages.findLast(m=>m.action==='saveOnlineProviderSetting');
  assert.equal(selected.key,'mode');assert.equal(selected.value,'custom');
  assert(await input().isDisabled(),'unacknowledged parent selection cannot enable dependent control');
  assert(await mode().isDisabled(),'one pending save locks choice controls too');
  await page.evaluate(m=>window.Auralis.setOnlineProviderSettingResult({...m,error:null}),{...selected,settings:custom().settings,configured:true});
  assert(await input().isEnabled());assert.equal(await page.locator('.plugin-setting-hint').count(),0);
  await input().fill('https://example.test/new');
  await page.locator('[data-action="save-provider-setting"]').click();await page.waitForTimeout(70);
  const saved=messages.findLast(m=>m.action==='saveOnlineProviderSetting');
  assert.equal(saved.key,'server');
  await config([provider]);
  assert(await input().isDisabled(),'dependency revoked while save pending');
  await page.evaluate(m=>window.Auralis.setOnlineProviderSettingResult({...m,error:null}),{...saved,settings:custom().settings,configured:true});
  assert(await input().isDisabled(),'late response cannot restore inapplicable setting');
  await config([custom()]);
  assert.equal(await input().inputValue(),'https://example.test/previous','inactive draft was removed');
  await input().fill('https://example.test/retry');
  await page.locator('[data-action="save-provider-setting"]').click();await page.waitForTimeout(70);
  const failed=messages.findLast(m=>m.action==='saveOnlineProviderSetting');
  await page.evaluate(m=>window.Auralis.setOnlineProviderSettingResult({...m,error:'Synthetic save failure'}),failed);
  assert.equal(await input().inputValue(),'https://example.test/retry');
  const poison={...custom(),settings:custom().settings.map(s=>({...s,labelEn:'<img src=x onerror=alert(1)>',group:{...group,labelEn:'<script>literal</script>'}}))};
  await config([poison]);assert.equal(await page.locator('[data-plugin-setting-group] script, [data-declared-setting] img').count(),0,'plugin labels render as plain text');
  assert.equal(await input().inputValue(),'https://example.test/retry','label-only refresh preserves draft');
  assert(await page.locator('#pageContent').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'no horizontal overflow');
  for(const el of [input(),mode(),page.locator('[data-action="save-provider-setting"]')]){
   const box=await el.boundingBox();assert(box.height>=40 && box.x>=0 && box.x+box.width<=page.viewportSize().width+1,'40px controls remain inside viewport');
  }
  await config([]);
  assert.equal(await page.locator('[data-plugin-setting-group]').count(),0,'missing plugin removes all grouping and hints');
  if(scale===2){
   await page.evaluate(()=>window.Auralis.receiveLibrary([{id:'local-only',title:'Original offline fixture',artist:'Demo',album:'Demo',durationSeconds:90}]));
   await config([provider]);
   assert(await input().isDisabled(),'populating a local library does not enable a plugin dependency');
   await config([]);
  }
  assert.equal(messages.filter(m=>m.action==='saveOnlineProviderSetting').length,before+3,'only explicit selections and saves emit writes');
  assert(!messages.some(m=>['manageOnlineAccount','playPlatformTrack','acquirePlatformStream'].includes(m.action)),'settings do not invoke accounts or playback');
  assert.deepEqual(errors,[]);
  console.log('PASS settings v2 UI '+theme+' '+scale*100+'% (both languages, pending/condition/late replies, no plugin)');await context.close();
 }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
