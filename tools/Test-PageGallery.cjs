'use strict';
// Real shared gallery + production assets, synthetic proxy images and isolated Edge. No user profiles.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot')),out=path.resolve(__dirname,'../artifacts/gallery-reading-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));if(!file.startsWith(root+path.sep))throw Error('path');
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const proxy='https://platform-art.auralis.local/gallery-';
const picture={contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="360" height="2160"><rect width="360" height="2160" fill="#64939c"/><circle cx="180" cy="180" r="110" fill="#edce92"/><path d="M0 800Q180 500 360 800V2160H0Z" fill="#365862"/><path d="M0 1700Q180 1400 360 1700V2160H0Z" fill="#203b45"/></svg>'};
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),errors=[],requests=[];let slow=[],fail=true;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>{
      const url=r.request().url();if(url.startsWith(proxy)){requests.push(url);
        const imageName=new URL(url).pathname;
        if(imageName.endsWith('slow')){slow.push(r);return;}
        return r.fulfill(imageName.endsWith('fail')&&fail?{status:404,body:''}:picture);
      }return url.startsWith(origin)?r.continue():r.abort();
    });
    await page.addInitScript(()=>localStorage.setItem('auralis:language','en-US'));
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(url=>{
      window.fixtureGallery=window.AuralisPageGallery.create({reduced:()=>matchMedia('(prefers-reduced-motion: reduce)').matches},'test-gallery');
      const button=document.createElement('button');button.id='galleryFixtureOrigin';button.textContent='Open image';
      button.style.cssText='position:fixed;left:260px;top:90px;z-index:20';
      button.onclick=()=>window.fixtureGallery.open([url+'long',url+'slow',url+'fail','https://example.invalid/not-allowed']);
      document.body.append(button);
    },proxy);
    await page.locator('#galleryFixtureOrigin').click();
    const gallery=page.locator('.test-gallery'),body=gallery.locator('.media-dialog-body'),image=gallery.locator('.plugin-gallery-stage img');
    await page.waitForFunction(()=>document.querySelector('.test-gallery .media-dialog-body')?.getAttribute('aria-busy')==='false');
    assert.equal(requests.length,1,'no neighbour prefetch');assert.match(await gallery.locator('header').textContent(),/1 \/ 3/);
    assert(await image.evaluate(n=>{const r=n.getBoundingClientRect(),v=n.closest('.media-dialog-body').getBoundingClientRect();return r.top>=v.top&&r.bottom<=v.bottom+1&&getComputedStyle(n).objectFit==='contain';}),'fit keeps tall image inside viewport');
    await gallery.getByRole('button',{name:'Read tall image',exact:true}).click();
    assert(await body.evaluate(n=>n.scrollHeight>n.clientHeight*2),'reading mode supports long vertical scrolling');
    await body.focus();await page.keyboard.press('PageDown');await page.waitForFunction(()=>document.querySelector('.test-gallery .media-dialog-body').scrollTop>0);
    await page.screenshot({path:path.join(out,theme+'-'+scale+'-reading.png')});
    await gallery.getByRole('button',{name:'Fit to window',exact:true}).click();assert.equal(await body.evaluate(n=>n.scrollTop),0);
    await page.screenshot({path:path.join(out,theme+'-'+scale+'-fit.png')});
    await page.evaluate(()=>{window.galleryHeader=document.querySelector('.test-gallery header');});
    await page.keyboard.press('ArrowRight');await gallery.getByText('Loading image…',{exact:true}).waitFor();
    await page.evaluate(()=>window.galleryStaleLoad=document.querySelector('.test-gallery img').onload);
    await page.keyboard.press('ArrowRight');await gallery.getByRole('button',{name:'Retry',exact:true}).waitFor();
    await page.evaluate(()=>window.galleryStaleLoad());
    for(const r of slow.splice(0))await r.fulfill(picture).catch(()=>{});
    assert(await gallery.getByRole('button',{name:'Retry',exact:true}).isVisible(),'old load does not replace the current error');
    assert(await page.evaluate(()=>window.galleryHeader===document.querySelector('.test-gallery header')),'switching preserves toolbar DOM');
    assert.equal(await gallery.locator('img').count(),0,'failed image is released');
    const reads=requests.length;await page.waitForTimeout(100);assert.equal(requests.length,reads,'failure does not retry itself');
    fail=false;await gallery.getByRole('button',{name:'Retry',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('.test-gallery .media-dialog-body').getAttribute('aria-busy')==='false'&&!!document.querySelector('.test-gallery img'));
    assert(requests.at(-1).endsWith('/gallery-fail?retry=1'),'explicit retry bypasses a cached failed proxy response without changing the approved handle');
    assert(await body.evaluate(n=>n===document.activeElement),'retry success keeps focus in the reader');
    if(scale===1.5)assert.equal(await gallery.evaluate(n=>n.getAnimations({subtree:true}).filter(a=>a.playState==='running').length),0);
    await page.clock.install();await page.keyboard.press('ArrowLeft');await gallery.getByText('Loading image…',{exact:true}).waitFor();
    await page.clock.fastForward(20001);await gallery.getByRole('button',{name:'Retry',exact:true}).waitFor();
    await gallery.getByRole('button',{name:'Retry',exact:true}).click();await gallery.getByText('Loading image…',{exact:true}).waitFor();
    await page.evaluate(()=>window.galleryStaleLoad=document.querySelector('.test-gallery img').onload);
    await page.keyboard.press('Escape');await gallery.waitFor({state:'hidden'});
    await page.evaluate(()=>window.galleryStaleLoad());assert.equal(await gallery.locator('img').count(),0);
    assert(await page.locator('#galleryFixtureOrigin').evaluate(n=>n===document.activeElement),'close restores original focus');
    for(const r of slow.splice(0))await r.fulfill(picture).catch(()=>{});
    await page.locator('#galleryFixtureOrigin').click();await page.waitForFunction(()=>document.querySelector('.test-gallery .media-dialog-body').getAttribute('aria-busy')==='false');
    assert.equal(await body.getAttribute('data-gallery-mode'),'fit','new opening resets reading mode');
    await page.evaluate(()=>window.fixtureGallery.close(true));await gallery.waitFor({state:'hidden'});
    assert.deepEqual(errors,[]);await context.close();console.log('PASS gallery '+theme+' '+scale+': fit/reading, keyboard, stable chrome, late load, timeout/retry, release, focus and reduced motion');
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
