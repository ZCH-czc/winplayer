'use strict';
// Documentation fixtures only: actual app resources, isolated browser, no native playback or user data.
const {chromium} = require('playwright');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const repo = path.resolve(__dirname, '..');
const root = path.resolve(process.env.AURALIS_UI_ROOT || path.join(repo, 'Auralis/wwwroot'));
const output = path.join(repo, 'docs/screenshots');
const mime = {'.html':'text/html','.js':'text/javascript','.css':'text/css','.png':'image/png','.svg':'image/svg+xml'};
const server = http.createServer(async (req,res) => {
  try {
    const name = decodeURIComponent(new URL(req.url,'http://localhost').pathname);
    const file = path.resolve(root, '.' + (name === '/' ? '/index.html' : name));
    if (!file.startsWith(root + path.sep)) throw Error('Outside resource root');
    const bytes = await fs.readFile(file).catch(async error => {
      if (name !== '/assets/auralis-icon.png') throw error;
      return fs.readFile(path.join(repo,'Auralis/Assets/AuralisIcon.png'));
    });
    res.setHeader('Content-Type',mime[path.extname(file)] || 'application/octet-stream');res.end(bytes);
  } catch {res.writeHead(404);res.end();}
});
async function settle(page) {
  await page.evaluate(()=>Promise.allSettled(document.getAnimations().filter(a=>a.effect?.getComputedTiming().iterations!==Infinity).map(a=>a.finished)));
  await page.evaluate(()=>document.fonts.ready);
  await page.waitForTimeout(200);
}
(async()=>{
  await fs.mkdir(output,{recursive:true});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({channel:'msedge',headless:true});
  try {
    for (const language of ['zh-CN','en-US']) {
      for (const scene of ['library','player','customize']) {
        const dark = scene === 'player';
        const context = await browser.newContext({viewport:{width:1440,height:960},deviceScaleFactor:1,colorScheme:dark?'dark':'light',reducedMotion:'reduce'});
        await context.route('**/*',route=>new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
        const page = await context.newPage();const errors=[];
        page.on('pageerror',e=>errors.push(e.message));
        await page.addInitScript(()=>{
          localStorage.setItem('auralis:player-style','record');
          window.chrome={webview:{postMessage:()=>{}}};
        });
        await page.goto(`${origin}/?ui-test=1&ui-test-motion=reduced&ui-test-theme=${dark?'dark':'light'}`);
        await page.locator('#splashScreen').waitFor({state:'hidden'});
        await page.evaluate(({language,origin})=>{
          const english=language==='en-US';
          const titles=english?['Quiet Geometry','First Light','Soft Horizons','Still Water','After the Rain','Paper Skies','A Small Journey','Evening Glow']:['静谧几何','晨光微响','柔和地平线','静水流深','雨后的风','纸上晴空','一段小小旅程','暮色余温'];
          window.Auralis.setUiLanguageState({preference:language,resolvedLanguage:language});
          window.Auralis.receiveLibrary(titles.map((title,i)=>({id:`demo-${i}`,title,artist:'Auralis',album:english?'Quiet Geometry':'静谧几何',fileName:`demo-${i}.flac`,extension:'.flac',size:24000000,durationSeconds:204+i*13,dateAdded:'2026-09-01T00:00:00Z',coverUrl:`${origin}/assets/public-preview.svg`})));
          window.Auralis.playLocalTrack('demo-0');
          window.Auralis.setLyrics({trackId:'demo-0',source:english?'Local LRC':'本地 LRC',selectedSource:'local',isSynced:true,instrumental:false,
            lines:(english?['Light settles on the windowsill','A quiet rhythm fills the room','Leave a little space for wonder','Let the moment find its melody','Every note a softer color','Every pause a place to stay','Carry this small song with you']:['晨光停在窗沿','安静的节拍落满房间','留一点空白给想象','让此刻找到自己的旋律','每个音符都有柔和的颜色','每次停顿都值得停留','带着这段小小的旋律出发']).map((text,i)=>({timeSeconds:i*14,text}))});
          window.Auralis.setPlaybackState({id:'demo-0',isPlaying:false,currentTime:46,duration:204,audioInformation:{codec:'FLAC',bitrateKbps:941,sampleRateHz:48000,bitsPerSample:24,channels:2,isLossless:true,isAverageBitrate:true}});
        },{language,origin});
        if(scene==='player') await page.locator('#nowPlayingButton').click();
        if(scene==='customize') {
          await page.locator('[data-page="settings"]').click();
          await page.locator('[data-settings-section="fullscreen"]').click();
        }
        await settle(page);
        assert.deepEqual(errors,[]);
        assert(await page.locator('[data-stream-quality]').first().textContent());
        await page.screenshot({path:path.join(output,`${scene}-${language}.png`)});
        console.log(`PASS screenshot ${scene}-${language}: 1440x960, ${dark?'dark':'light'}, actual resources, original fixtures.`);
        await context.close();
      }
    }
  } finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
