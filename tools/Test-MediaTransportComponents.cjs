// Production Web assets, synthetic native replies only. No user profile, plugin execution or network.
const {chromium} = require('playwright');
const fs = require('node:fs/promises'), path = require('node:path'), http = require('node:http'), assert = require('node:assert/strict');
const root = path.resolve(process.env.AURALIS_UI_ROOT || 'Auralis/wwwroot');
const server = http.createServer(async (req,res) => {
  try {
    const file = path.resolve(root, '.' + new URL(req.url,'http://localhost').pathname.replace(/\/$/,'/index.html'));
    if (!file.startsWith(root + path.sep)) throw Error('path');
    res.setHeader('Content-Type', ({'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(file)] || 'application/octet-stream');
    res.end(await fs.readFile(file));
  } catch { res.writeHead(404); res.end(); }
});
let checks = 0;
const check = (value,label) => { assert(value,label); checks++; };
(async () => {
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({channel:'msedge',headless:true});
  try {
    for (const theme of ['light','dark']) for (const scale of [1,1.5,2]) {
      const context = await browser.newContext({viewport:{width:scale===2?720:scale===1.5?1920:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1?'no-preference':'reduce'});
      const page = await context.newPage(), messages = [], errors = [];
      page.on('pageerror',e=>errors.push(e.message));
      await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
      await page.exposeFunction('testBridge',m=>messages.push(m));
      await page.addInitScript(({english})=>{
        localStorage.setItem('auralis:language',english?'en-US':'zh-CN');
        window.chrome={webview:{postMessage:m=>{ window.testBridge(m); if(m.action==='managePlaybackComponents' && m.operation==='list') queueMicrotask(()=>window.Auralis.setPlaybackComponents({...m,preview:null,inventory:{items:[],selectedId:null,restartRequired:false,current:{displayName:'LibVLC',version:'0.4.0',bundled:true}}})); }}};
      },{english:theme==='dark'});
      await page.goto(`${origin}/?ui-test=1&ui-test-theme=${theme}`);
      // Other sizes retain the populated synthetic library supplied by ui-test mode.
      if (scale === 2) await page.evaluate(()=>window.Auralis.receiveLibrary([]));
      const open = async()=>{
        await page.locator('[data-page="settings"]').click();
        await page.locator('[data-settings-section="plugins"]').click();
        const advanced=page.locator('#advancedComponentsSettings');
        if(!await advanced.evaluate(n=>n.open)) await advanced.locator('summary').first().click();
        await page.locator('#transportComponentsSettings').waitFor();
      };
      const latest = ()=>messages.findLast(m=>m.action==='manageMediaTransportComponents');
      const waitOp = async op=>{ await page.waitForFunction(op=>document.querySelector('#transportComponentsSettings')?.getAttribute('aria-busy')==='true',op); await page.waitForTimeout(30); assert.equal(latest().operation,op); return latest(); };
      let inventory = {items:[],selectedId:null,restartRequired:false,stateError:null,current:{displayName:'HTTP media transport',version:'0.6.0',bundled:true,fellBack:false}};
      const reply = async(extra={})=>page.evaluate(p=>window.Auralis.setMediaTransportComponents(p),{...latest(),inventory,preview:null,...extra});
      const click = async action=>{await page.locator(`[data-tr-action="${action}"]`).first().click();await waitOp(action==='default'?'select':action);};
      await open(); await waitOp('list');
      const platformRequest = messages.findLast(m=>m.action==='requestPluginInventory');
      await page.evaluate(p=>window.Auralis.setPluginInventory({...p,items:[],issues:[]}),platformRequest);
      await reply();
      check(await page.locator('#transportComponentsSettings .plugin-inventory-row').count()===0,'core-only shows no invented component');
      check(await page.locator('[data-tr-action="default"]').isDisabled(),'bundled default is already selected');
      await click('pick');
      await page.locator('#advancedComponentsSettings > summary').click();
      const preview={token:'a'.repeat(32),id:'fixture.transport',displayName:'<img src=x onerror=alert(1)>',version:'1.0.0',manifestSha256:'b'.repeat(64),archiveSha256:'c'.repeat(64),fileCount:12,payloadBytes:1048576,capabilities:'AuthorizedHttp, CompleteBuffering'};
      await reply({preview});
      check(await page.locator('#advancedComponentsSettings').evaluate(n=>n.open),'returned preview reveals collapsed advanced settings');
      check(await page.locator('[data-tr-trust]').evaluate(n=>n===document.activeElement),'revealed preview restores keyboard focus before consent');
      check(await page.locator('#transportComponentsSettings img').count()===0,'untrusted metadata escaped');
      check(await page.locator('[data-tr-action="confirm"]').isDisabled(),'trust is explicit and unchecked');
      check(await page.locator('[data-pb-action="pick"]').isDisabled(),'playback import visibly blocked during transport preview');
      const before = messages.length;
      await page.locator('[data-action="import-plugin"]').click(); await page.waitForTimeout(40);
      check(messages.length===before,'platform picker cannot overlap transport preview');
      await page.locator('[data-tr-trust]').focus(); await page.keyboard.press('Space');
      check(await page.locator('[data-tr-action="confirm"]').isEnabled(),'keyboard consent enables import');
      await click('confirm');
      check(latest().token===preview.token && latest().trust===true && !('path' in latest()),'only opaque token crosses bridge');
      inventory={...inventory,items:[{id:preview.id,displayName:'Fixture transport',version:'1.0.0',enabled:false,available:true,selected:false}],restartRequired:true};
      await reply();
      check(await page.locator('[data-tr-action="enable"]').getAttribute('aria-checked')==='false','import stays disabled');
      await click('enable');
      check(await page.locator('[data-tr-action="enable"]').getAttribute('aria-checked')==='false','no optimistic enable');
      await reply({requestId:latest().requestId-1});
      check(await page.locator('#transportComponentsSettings').getAttribute('aria-busy')==='true','stale reply rejected');
      inventory.items[0].enabled=true;await reply();
      await click('select');
      inventory={...inventory,selectedId:preview.id,items:[{...inventory.items[0],selected:true}]};await reply();
      check(await page.locator('[data-tr-action="select"]').isDisabled(),'selection only follows native acknowledgement');
      await page.locator('[data-tr-action="restart"]').click();await page.waitForTimeout(40);
      check(messages.at(-1).action==='restartForPlugins','reuses existing restart protocol');
      await click('default');check(latest().id===null,'null selects bundled fallback');
      inventory={...inventory,selectedId:null,items:[{...inventory.items[0],available:false,selected:false}],current:{...inventory.current,fellBack:true}};await reply();
      check(await page.locator('[data-tr-action="enable"]').isEnabled(),'broken enabled component can be disabled');
      check(await page.locator('[data-tr-action="select"]').isDisabled(),'broken component cannot be selected');
      await click('enable');await reply({error:'transportManagementFailed'});
      check(await page.locator('[data-tr-action="enable"]').getAttribute('aria-checked')==='true','failure retains authoritative state');
      check(await page.locator('#transportComponentsSettings [role="alert"]').count()===1,'actionable fixed error shown');
      await click('list');await reply();
      await page.locator('#transportComponentsSettings').scrollIntoViewIfNeeded();
      check(await page.locator('#transportComponentsSettings').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'card fits narrow/large viewport');
      await fs.mkdir('artifacts/transport-components-ui',{recursive:true});
      await page.screenshot({path:`artifacts/transport-components-ui/${theme}-${scale}.png`});
      await click('pick');await reply({preview});
      check(!(await page.locator('[data-tr-trust]').isChecked()),'new preview resets trust');
      await page.locator('[data-page="songs"]').click();await page.locator('#transportComponentsSettings').waitFor({state:'detached'});await page.waitForTimeout(40);
      check(latest().operation==='cancel','leaving page cancels preview');
      const cancelId=latest().requestId;await reply({preview,error:'busy'});await page.waitForTimeout(80);
      check(latest().requestId===cancelId,'cancel error does not create retry loop');
      await open();await click('cancel');await reply();
      await click('pick');await page.locator('[data-page="songs"]').click();await page.locator('#transportComponentsSettings').waitFor({state:'detached'});
      await reply({preview});await page.waitForTimeout(80);
      check(latest().operation==='cancel','late picker result after navigation is cancelled');
      await reply();await open();
      await click('list');
      await page.locator('#advancedComponentsSettings > summary').click();
      inventory={...inventory,stateError:'InvalidState'};await reply();
      check(await page.locator('#advancedComponentsSettings').evaluate(n=>n.open),'component errors reopen advanced settings instead of hiding the failure');
      check(await page.locator('[data-tr-action="pick"]').isDisabled(),'invalid installation state fails closed');
      check(errors.length===0,'no browser exceptions: '+errors.join(';'));
      await context.close();
    }
    console.log(`PASS transport component UI: ${checks} checks; production assets, synthetic bridge; 2 themes × 3 DPI factors, narrow/wide, keyboard, reduced motion. Not native picker/restart or acoustic acceptance.`);
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
