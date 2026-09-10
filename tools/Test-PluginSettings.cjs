// Deterministic UI checks: no account, user profile, real plugin installation or external requests.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const root = path.resolve(process.env.AURALIS_UI_ROOT || path.join(__dirname, '../Auralis/wwwroot'));
const server = http.createServer(async (req,res) => {
  try {
    const file = path.resolve(root, '.' + new URL(req.url,'http://localhost').pathname.replace(/\/$/,'/index.html'));
    if (!file.startsWith(root + path.sep)) throw Error('path');
    res.setHeader('Content-Type', ({'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(file)] || 'application/octet-stream');
    res.end(await fs.readFile(file));
  } catch { res.writeHead(404); res.end(); }
});
(async () => {
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({channel:'msedge',headless:true});
  try {
    for (const theme of ['light','dark']) for (const scale of [1,1.5,2]) {
      const context = await browser.newContext({viewport:scale===2?{width:720,height:480}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
      const page = await context.newPage(), errors=[], messages=[];
      page.on('pageerror', e=>errors.push(e.message));
      await page.route('**/*', r=>r.request().url().startsWith(origin)?r.continue():r.abort());
      await page.exposeFunction('testBridge', m=>messages.push(m));
      await page.addInitScript(({english})=>{
        localStorage.setItem('auralis:language',english?'en-US':'zh-CN');
        window.chrome={webview:{postMessage:m=>{
          window.testBridge(m);
          if(m.action==='managePlaybackComponents' && m.operation==='list') setTimeout(()=>window.Auralis.setPlaybackComponents({...m,preview:null,inventory:{items:[],selectedId:null,restartRequired:false,current:{displayName:'LibVLC',version:'0.4.0',bundled:true}}}),0);
          if(m.action==='manageMediaTransportComponents' && m.operation==='list') setTimeout(()=>window.Auralis.setMediaTransportComponents({...m,preview:null,inventory:{items:[],selectedId:null,restartRequired:false,current:{displayName:'HTTP media transport',version:'0.6.0',bundled:true}}}),0);
        },postMessageWithAdditionalObjects:(m,files)=>window.testBridge({...m,testFiles:files.map(f=>f.name)})}};
      },{english:theme==='dark'});
      await page.goto(`${origin}/?ui-test=1&ui-test-theme=${theme}`);
      await page.locator('[data-page="settings"]').click();
      await page.locator('[data-settings-section="plugins"]').click();
      await page.waitForFunction(()=>document.querySelector('#pluginInventory[aria-busy="true"]'));
      const currentRequest=()=>messages.filter(m=>m.action==='requestPluginInventory').at(-1).requestId;
      const respond=async payload=>{await page.evaluate(p=>window.Auralis.setPluginInventory(p),payload);};
      await respond({requestId:currentRequest(),items:[],issues:[]});
      await page.locator('#pluginInventory .plugin-inventory-empty').waitFor();
      const advanced=page.locator('#advancedComponentsSettings');
      assert.equal(await advanced.count(),1,'built-in component options have one advanced section');
      assert.equal(await advanced.evaluate(n=>n.open),false,'normal plugin management starts focused on online plugins');
      assert(await page.locator('#playbackComponentsSettings').isHidden(),'decoder imports are not presented as a requirement');
      assert(await page.locator('#transportComponentsSettings').isHidden(),'transport imports are not presented as a requirement');
      assert.match(await advanced.innerText(),theme==='dark'?/built in/:/已内置/,'advanced summary explains the built-in defaults');
      const disclosureMessagesStart=messages.length;
      await advanced.locator('summary').focus(); await page.keyboard.press('Enter');
      assert(await page.locator('#playbackComponentsSettings').isVisible(),'keyboard can reveal existing decoder controls');
      assert(await page.locator('#transportComponentsSettings').isVisible(),'keyboard can reveal existing transport controls');
      await advanced.locator('summary').focus(); await page.keyboard.press('Space');
      await page.waitForFunction(()=>!document.querySelector('#advancedComponentsSettings').open);
      assert.equal(await advanced.evaluate(n=>n.open),false,'keyboard can collapse advanced controls without changing selection');
      assert(!messages.some(m=>['managePlaybackComponents','manageMediaTransportComponents'].includes(m.action)&&m.operation!=='list'),'expanding advanced options only reads metadata');
      assert(!messages.slice(disclosureMessagesStart).some(m=>['playTrack','resumePlayback','pausePlayback'].includes(m.action)),'disclosure keyboard activation never toggles playback');
      await fs.mkdir(path.join(__dirname,'../artifacts/plugin-settings-tests'),{recursive:true});
      await advanced.screenshot({path:path.join(__dirname,`../artifacts/plugin-settings-tests/advanced-${theme}-${scale}.png`)});
      assert.match(await page.locator('#pluginImportRegion').innerText(),theme==='dark'?/disabling immediately blocks new requests/:/停用会立即停止新请求/,'initial guidance matches backend disable behavior');
      const refresh=page.locator('[data-action="refresh-plugins"]');
      await refresh.focus(); await page.keyboard.press('Enter');
      await page.waitForFunction(()=>document.querySelector('#pluginInventory[aria-busy="true"]'));
      const requestId=currentRequest();
      const items=['disabled','untrusted','enablePending','incompatible','upgradeRequired','upgradeRequired'].map((state,i)=>({id:`fixture.${i}`,displayName:i===0?'<img src=x onerror=alert(1)>':'Fixture '+i,version:'1.7.0',providers:['Provider'],state,enabled:i===2||i===5,canEnable:i===0||i===2,active:false,compatibilityIssue:i===3?'hostSdkIncompatible':i>=4?'manifestUpgradeRequired':null}));
      await respond({requestId:requestId-1,items,issues:[]});
      assert.equal(await page.locator('.plugin-inventory-row').count(),0,'late response ignored');
      await respond({requestId,items,issues:['DuplicateProviderId']});
      assert.equal(await page.locator('.plugin-inventory-row').count(),6);
      assert.equal(await page.locator('#pluginInventory img').count(),0,'manifest display text is escaped');
      assert(await page.locator('[data-plugin-enable="fixture.3"]').isDisabled(),'incompatible item cannot be enabled');
      assert.match(await page.locator('.plugin-compatibility-note').first().innerText(),/SDK/,'installed incompatibility has an actionable explanation');
      assert(await page.locator('[data-plugin-enable="fixture.4"]').isDisabled(),'old off package cannot be enabled');
      assert(await page.locator('[data-plugin-enable="fixture.5"]').isEnabled(),'old enabled preference can still be turned off');
      assert.match(await page.locator('.plugin-compatibility-note').nth(1).innerText(),theme==='dark'?/account data are preserved/:/账号数据会保留/,'upgrade instructions explain data preservation');
      assert.equal(await page.locator('.plugin-inventory-warning').count(),1);
      await refresh.click();
      await page.waitForFunction(()=>document.querySelector('#pluginInventory[aria-busy="true"]'));
      assert.equal(await page.locator('.plugin-inventory-row').count(),6,'refresh retains last inventory');
      await respond({requestId:currentRequest(),error:'inventoryUnavailable'});
      assert.equal(await page.locator('.plugin-inventory-row').count(),6,'failed refresh retains inventory');
      assert(await refresh.isEnabled(),'retry available');
      const action = name=>messages.filter(m=>m.action===name).at(-1);
      const management = async (name, extra={}) => {
        for(let i=0;i<30&&!action(name);i++) await page.waitForTimeout(10);
        await page.evaluate(p=>window.Auralis.setPluginManagementResult(p),{requestId:action(name).requestId,action:name,...extra});
      };
      assert(await page.locator('[data-plugin-enable="fixture.1"]').isDisabled(),'untrusted package cannot be enabled');
      await page.locator('[data-plugin-enable="fixture.0"]').focus();
      await page.keyboard.press('Space');
      await page.waitForTimeout(30);
      assert.deepEqual({...action('setPluginEnabled'),requestId:0},{action:'setPluginEnabled',requestId:0,id:'fixture.0',enabled:true});
      assert.equal(await page.locator('[data-plugin-enable="fixture.0"]').getAttribute('aria-checked'),'false','no optimistic enable');
      await management('setPluginEnabled');
      await page.waitForTimeout(30);
      items[0].enabled=true; items[0].state='enablePending';
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-plugin-enable="fixture.0"]').getAttribute('aria-checked'),'true');
      assert(await page.locator('[data-plugin-enable="fixture.0"]').evaluate(n=>n===document.activeElement),'toggle focus restored after refresh');
      await page.locator('[data-action="import-plugin"]').click();
      await management('pickPluginPackage',{batch:{token:'1'.repeat(32),items:[
        {fileName:'example.auralis-plugin',preview:{id:'sample.plugin',displayName:'Example plugin',version:'1.0.0',sha256:'A'.repeat(64),providers:['Example'],capabilities:['TrackSearch'],hostRequirements:{minimumHostSdkVersion:'1.1.0',requiredFeatures:['native-login.v1','<img src=x onerror=alert(1)>']},credentialAliases:[{key:'session',scope:'old.example',legacyKey:'account.v1'},{key:'<img src=x onerror=alert(1)>',scope:'x'.repeat(64),legacyKey:'k'.repeat(96)}]}},
        {fileName:'<img src=x onerror=alert(1)>.zip',error:'hostFeatureUnsupported'},
        {fileName:'another.zip',preview:{id:'sample.other',displayName:'Another plugin',version:'1.0.0',sha256:'C'.repeat(64),providers:['Other'],capabilities:[]}},
        {fileName:'old.zip',error:'manifestUpgradeRequired'}
      ]}});
      assert.equal(await page.locator('.plugin-batch-item').count(),4,'multi-picker preview lists valid and failed files');
      assert.match(await page.locator('.plugin-batch-item').nth(3).innerText(),theme==='dark'?/same plugin ID/:/同一插件 ID/,'old import has actionable upgrade text');
      assert.equal(await page.locator('.plugin-batch-item img').count(),0,'archive names are escaped');
      assert(await page.locator('#pluginImportTrust').evaluate(n=>n===document.activeElement),'initial review has keyboard entry');
      await page.locator('.plugin-batch-item details').first().locator('summary').click();
      assert.match(await page.locator('.plugin-host-requirements').innerText(),/1\.1\.0/,'declared minimum SDK is visible');
      assert.equal(await page.locator('.plugin-host-requirements img').count(),0,'required features are escaped metadata');
      assert.match(await page.locator('.plugin-batch-item').nth(1).innerText(),theme==='dark'?/required plugin feature/:/必需的功能/,'missing host features have their own error text');
      assert.equal(await page.locator('.plugin-credential-review img').count(),0,'credential metadata is escaped');
      assert.match(await page.locator('.plugin-credential-review').innerText(),/old\.example \/ account\.v1/,'exact credential access is visible before approval');
      assert.equal(await page.locator('.plugin-credential-review').getAttribute('aria-label'),theme==='dark'?'Legacy account access':'旧账号兼容访问');
      assert(await page.locator('.plugin-credential-review').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'long credential addresses wrap without horizontal overflow');
      assert(await page.locator('[data-action="confirm-plugin-import"]').isDisabled(),'trust must be explicit');
      if(theme==='dark') assert.equal(await page.locator('[data-action="cancel-plugin-import"]').innerText(),'Cancel');
      await page.locator('#pluginImportTrust').focus();
      assert(await page.locator('#pluginImportTrust').evaluate(n=>n===document.activeElement),'approval remains keyboard reachable after inspecting requirements');
      assert(await page.locator('.plugin-import-review').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'review and SHA-256 fit narrow viewport');
      const reviewOutput=path.resolve(__dirname,'../artifacts/plugin-settings-tests');
      await fs.mkdir(reviewOutput,{recursive:true});
      await page.screenshot({path:path.join(reviewOutput,`review-${theme}-${scale}.png`)});
      await page.locator('#pluginImportTrust').check();
      await page.locator('[data-action="confirm-plugin-import"]').click();
      await page.waitForTimeout(30);
      assert.equal(action('confirmPluginImport').token,'1'.repeat(32)); assert.equal(action('confirmPluginImport').trust,true);
      await management('confirmPluginImport',{error:'pluginManagementFailed'});
      await page.waitForTimeout(30); await respond({requestId:currentRequest(),items,issues:[]});
      assert(await page.locator('.plugin-import-review').isVisible(),'failed import retains review, does not claim success');
      assert(!(await page.locator('#pluginImportTrust').isChecked()),'retry requires fresh confirmation');
      await page.locator('[data-action="cancel-plugin-import"]').click();
      await management('cancelPluginImport');
      await page.waitForTimeout(30); await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('.plugin-import-review').count(),0);
      await page.locator('[data-action="import-plugin"]').click();
      await page.waitForTimeout(30);
      await management('pickPluginPackage',{preview:{token:'2'.repeat(32),id:'sample.plugin',displayName:'Example plugin',version:'1.0.0',sha256:'B'.repeat(64),providers:['Example'],capabilities:[]}});
      assert.equal(await page.locator('.plugin-credential-review').count(),0,'no permission panel for plugins without legacy claims');
      await page.locator('#pluginImportTrust').check(); await page.locator('[data-action="confirm-plugin-import"]').click();
      await page.waitForTimeout(30); await management('confirmPluginImport');
      await page.waitForTimeout(30);
      items.push({id:'sample.plugin',displayName:'Example plugin',version:'1.0.0',providers:['Example'],state:'disabled',enabled:false,canEnable:true});
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-plugin-enable="sample.plugin"]').getAttribute('aria-checked'),'false','successful import remains disabled');
      // Incomplete credential cleanup belongs to each plugin, not the last operation.
      await page.locator('[data-plugin-enable="fixture.0"]').click();
      await management('setPluginEnabled',{credentialsCleared:false});
      items[0].enabled=false; items[0].state='disablePending';
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.match(await page.locator('[data-plugin-enable="fixture.0"]').locator('..').innerText(),theme==='dark'?/Disabled/:/已停用/,'disabled state does not promise to wait until restart');
      await page.locator('[data-plugin-enable="fixture.2"]').click();
      await management('setPluginEnabled',{credentialsCleared:false});
      items[2].enabled=false; items[2].state='disablePending';
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-action="retry-plugin-cleanup"]').count(),2,'a second plugin cannot replace the first cleanup failure');
      await page.locator('[data-page="songs"]').click();
      await page.locator('[data-page="settings"]').click();
      await page.locator('[data-settings-section="plugins"]').click();
      await page.locator('[data-action="refresh-plugins"]').click();
      await page.waitForFunction(()=>document.querySelector('#pluginInventory[aria-busy="true"]'));
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-action="retry-plugin-cleanup"]').count(),2,'leaving and returning to settings retains unresolved cleanup');
      await page.locator('[data-plugin-enable="sample.plugin"]').click();
      await management('setPluginEnabled');
      items.at(-1).enabled=true; items.at(-1).state='enablePending';
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-action="retry-plugin-cleanup"]').count(),2,'enabling another plugin retains unresolved cleanup');
      const retryCleanup=page.locator('[data-action="retry-plugin-cleanup"][data-cleanup-plugin="fixture.0"]');
      await retryCleanup.click();
      assert(await page.locator('[data-action="retry-plugin-cleanup"]').first().isDisabled(),'pending cleanup locks repeat requests');
      assert.equal(action('setPluginEnabled').id,'fixture.0','retry addresses its own plugin');
      assert.equal(action('setPluginEnabled').enabled,false);
      await management('setPluginEnabled',{error:'pluginManagementFailed'});
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('[data-action="retry-plugin-cleanup"]').count(),2,'an unconfirmed retry retains both failures');
      await retryCleanup.focus(); await page.keyboard.press('Enter');
      await management('setPluginEnabled',{credentialsCleared:true});
      await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await retryCleanup.count(),0,'only confirmed cleanup removes its warning');
      assert.equal(await page.locator('[data-action="retry-plugin-cleanup"][data-cleanup-plugin="fixture.2"]').count(),1,'other plugin cleanup remains actionable');
      assert.equal(await page.locator('.plugin-cleanup-row img').count(),0,'cleanup plugin names are escaped');
      assert(await page.locator('.plugin-cleanup-row').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'cleanup row remains inside card');
      const checkRestartLayout = async () => {
        const geometry = await page.locator('[data-action="restart-for-plugins"]').evaluate(button => {
          const row = button.parentElement, card = row.closest('.settings-card');
          const rect = n => { const r=n.getBoundingClientRect(); return {left:r.left,right:r.right,top:r.top,bottom:r.bottom}; };
          return {button:rect(button),row:rect(row),text:rect(row.querySelector('span')),
            importButton:rect(card.querySelector('[data-action="import-plugin"]')),
            heading:rect(card.querySelector('h2')),overflow:row.scrollWidth-row.clientWidth};
        });
        assert(Math.abs(geometry.button.right-geometry.importButton.right)<=1,'restart aligns with import button, not card edge');
        assert(Math.abs(geometry.text.left-geometry.heading.left)<=1,'restart explanation aligns with card heading');
        assert(geometry.row.bottom-geometry.button.bottom>=15,'restart button clears the following divider');
        assert(geometry.overflow<=1,'restart explanation and button fit the available width');
        assert(geometry.text.right<=geometry.button.left-7 || geometry.text.bottom<=geometry.button.top-7,'restart text never overlaps button, including wrapped layout');
      };
      await checkRestartLayout();
      const originalViewport=page.viewportSize();
      await page.setViewportSize({width:480,height:640});
      await checkRestartLayout();
      assert(await page.locator('.plugin-cleanup-row').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'cleanup warning and action wrap at 480px');
      await page.locator('#pluginImportRegion').screenshot({path:path.join(reviewOutput,`restart-narrow-${theme}-${scale}.png`)});
      await page.setViewportSize(originalViewport);
      await page.locator('#pluginImportRegion').locator('..').screenshot({path:path.join(reviewOutput,`restart-${theme}-${scale}.png`)});
      await page.locator('[data-action="restart-for-plugins"]').focus();
      assert(await page.locator('[data-action="restart-for-plugins"]').evaluate(n=>n===document.activeElement),'restart remains keyboard reachable');
      await page.locator('[data-action="restart-for-plugins"]').click();
      await page.waitForTimeout(30);
      assert(messages.some(m=>m.action==='restartForPlugins'&&Object.keys(m).length===1),'restart accepts no caller executable/path');
      // File drag from another page uses native-backed objects, not JSON paths or ZIP contents.
      await page.locator('[data-page="songs"]').click();
      await page.waitForTimeout(200);
      const drop = async (names, type='drop') => page.evaluate(({names,type})=>{
        const dataTransfer=new DataTransfer();
        names.forEach(name=>dataTransfer.items.add(new File(['fixture'],name)));
        const event=new DragEvent(type,{dataTransfer,bubbles:true,cancelable:true});
        document.body.dispatchEvent(event); return event.defaultPrevented;
      },{names,type});
      await drop(['one.zip','two.auralis-plugin'],'dragenter');
      assert(await page.locator('.plugin-drop-hint').isVisible(),'file drag shows explanatory surface');
      assert(await drop(['one.zip','two.auralis-plugin']),'drop prevents browser navigation');
      await page.waitForTimeout(200);
      assert.deepEqual(action('dropPluginPackages').testFiles,['one.zip','two.auralis-plugin']);
      assert.deepEqual(Object.keys(action('dropPluginPackages')).sort(),['action','requestId','testFiles'],'no JSON path/bytes escape hatch');
      const dropRequest=action('dropPluginPackages').requestId;
      await drop(['again.zip']);
      assert.equal(action('dropPluginPackages').requestId,dropRequest,'busy drop cannot replace pending batch');
      await management('dropPluginPackages',{batch:{token:'3'.repeat(32),items:[
        {fileName:'one.zip',preview:{id:'drop.one',displayName:'Drop fixture',version:'1.0.0',sha256:'D'.repeat(64),providers:[],capabilities:[]}},
        {fileName:'two.auralis-plugin',error:'duplicatePlugin'}
      ]}});
      await page.locator('.plugin-import-review').waitFor();
      assert(await page.locator('.plugin-drop-hint').isHidden(),'drop hint removed');
      assert(await page.locator('#pluginImportTrust').isEnabled());
      assert(await page.locator('[data-action="confirm-plugin-import"]').isDisabled(),'drop never grants trust');
      await page.locator('#pluginImportTrust').check();await page.locator('[data-action="confirm-plugin-import"]').click();
      await page.waitForTimeout(30);
      assert.equal(action('confirmPluginImport').token,'3'.repeat(32));
      await management('confirmPluginImport',{results:[{fileName:'one.zip',id:'drop.one'},{fileName:'two.auralis-plugin',error:'duplicatePlugin'}]});
      await page.waitForTimeout(30);await respond({requestId:currentRequest(),items,issues:[]});
      assert.equal(await page.locator('.plugin-batch-results li').count(),2,'partial success remains visible per file');
      await drop(['bad.zip']);await page.waitForTimeout(30);
      await management('dropPluginPackages',{batch:{token:'4'.repeat(32),items:[{fileName:'bad.zip',error:'invalidPackage'}]}});
      assert(await page.locator('#pluginImportTrust').isDisabled(),'all-invalid batch cannot be approved');
      await page.locator('[data-action="cancel-plugin-import"]').click();await management('cancelPluginImport');
      await page.waitForTimeout(30);await respond({requestId:currentRequest(),items,issues:[]});
      const lastDrop=action('dropPluginPackages').requestId;
      await drop(Array.from({length:17},(_,i)=>`${i}.zip`));await drop(['music.flac']);
      assert.equal(action('dropPluginPackages').requestId,lastDrop,'excess and non-plugin drops are not imported');
      await page.evaluate(()=>{delete window.chrome.webview.postMessageWithAdditionalObjects;});
      await drop(['one.zip']);assert.equal(action('dropPluginPackages').requestId,lastDrop,'older runtime uses explicit picker fallback');
      if(scale===2) await page.emulateMedia({forcedColors:'active'});
      const layout=await page.locator('.plugin-settings-card').first().evaluate(n=>({
        overflow:n.scrollWidth-n.clientWidth,
        rowOverflow:[...n.querySelectorAll('.setting-row')].some(r=>r.scrollWidth>r.clientWidth+1),
        badges:[...n.querySelectorAll('.plugin-state')].map(b=>getComputedStyle(b).borderTopStyle)
      }));
      assert(layout.overflow<=1&&!layout.rowOverflow,'narrow / DPI layout does not overflow');
      assert(layout.badges.every(b=>b==='solid'),'status has a visible border including forced colors');
      if(theme==='dark') assert.match(await page.locator('.settings-detail-title h1').innerText(),/Platform plugins/);
      await page.locator('[data-action="open-plugin-folder"]').click();
      await page.waitForTimeout(60);
      assert(messages.some(m=>m.action==='openPluginFolder'&&Object.keys(m).length===1),'folder action accepts no Web path');
      assert.deepEqual(errors,[]);
      const output=path.resolve(__dirname,'../artifacts/plugin-settings-tests');
      await fs.mkdir(output,{recursive:true});
      await page.screenshot({path:path.join(output,`${theme}-${scale}.png`)});
      console.log(`PASS plugin UI ${theme} ${scale}: empty, refresh, late response, trust/restart/error, escaped text, keyboard, layout, language.`);
      await context.close();
    }
  } finally { await browser.close(); await new Promise(r=>server.close(r)); }
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
