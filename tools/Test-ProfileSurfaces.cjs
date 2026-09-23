'use strict';
// Shared host presentation with synthetic metadata/artwork; never a real account or platform request.
const {chromium}=require('playwright'),assert=require('node:assert/strict');
const fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const out=path.resolve(__dirname,'../artifacts/profile-surfaces-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error('path');
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const nav=n=>'page-'+n.toString(16).padStart(32,'0'),handle=n=>'community-'+n.toString(16).padStart(32,'0');
// Original vector fixture artwork, not platform media or a mock UI screenshot.
const cover=n=>`<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360" viewBox="0 0 640 360"><defs><linearGradient id="sky" x2="0" y2="1"><stop stop-color="${['#1d4760','#604b78','#98614d'][n%3]}"/><stop offset="1" stop-color="#b8cece"/></linearGradient></defs><rect width="640" height="360" fill="url(#sky)"/><circle cx="${420-n*32}" cy="98" r="43" fill="#f4e4ba"/><path d="M0 248L126 125L240 238L402 149L640 269V360H0Z" fill="#345366"/><path d="M0 297Q160 235 320 292T640 281V360H0Z" fill="#71979c"/><path d="M0 335Q170 287 380 330T640 314" fill="none" stroke="#d5e3d9" stroke-width="3"/></svg>`;
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[],artRequests=[];let missing=false,missingFails=true;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>{
      const url=r.request().url();
      if(url.startsWith('https://platform-art.auralis.local/cover-')){
        artRequests.push(url);const name=new URL(url).pathname;
        return r.fulfill(name.endsWith('cover-5')&&!new URL(url).search?{status:502,body:''}:{contentType:'image/svg+xml',body:cover(Number(name.split('-').at(-1)))});
      }
      if(url.startsWith('https://platform-art.auralis.local/')){artRequests.push(url);return r.fulfill(new URL(url).pathname.endsWith('/missing')&&missingFails?{status:404,body:''}:{contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96"><rect width="96" height="96" rx="48" fill="#739fb5"/><circle cx="48" cy="37" r="17" fill="#e9dcc0"/><path d="M17 92Q17 56 48 56T79 92" fill="#3e596d"/></svg>'});}
      return url.startsWith(origin)?r.continue():r.abort();
    });
    const title='晨间电台 · Morning Sessions',description='分享音乐与创作的日常。\nOriginal performances, small observations, and stories behind the songs.';
    const card=n=>({handle:handle(n),author:title,title:'Studio journal '+n,text:'An original demonstration of a readable activity card. '.repeat(12),publishedAt:'2026-09-20T09:00:00Z',open:{label:'Read activity',handle:nav(3)},actions:[]});
    const titles=['海岸的来信 · Letters from the coast','傍晚练习曲 · Evening session','云间漫步 · A quiet walk above the clouds — an extended original performance','暮色的形状 · Shapes of dusk','在下一次日出之前 · Before the next sunrise','山谷里的回声 · Echoes in the valley'];
    const works=titles.map((name,i)=>{const media={kind:'online',id:'track-'+String(i+1).repeat(24),handle:'track-'+String(i+1).repeat(24),providerId:'sample.identity',sourceName:'Profile sample',title:i===4?'Alternate recording title':name,artist:i===2?'Guest musician':title,coverUrl:'https://platform-art.auralis.local/cover-'+i,durationSeconds:204,availability:i===1?'previewonly':i===3?'unavailable':'available',isPlayable:i!==3};return {...card(20+i),author:i===5?null:title,avatar:'https://platform-art.auralis.local/avatar',title:name,text:'',media,open:{label:'Read work',handle:nav(100+i)},discussionHandle:handle(100+i),commentCount:1};});
    const documentFor=m=>{
      if(m.navigationHandle===nav(3))return {version:8,title:'Activity details',layout:'detail',cards:[{...card(1),open:null}],actions:[]};
      const work=works.find(w=>w.open.handle===m.navigationHandle);
      if(work)return {version:8,title:'Work details',layout:'detail',cards:[{...work,open:null,text:'Original performance notes.\nRecorded during a quiet morning by the sea.'}],actions:[]};
      const uploads=m.navigationHandle===nav(2);
      return {version:8,title:missing?'无头像的作者 · '+title:title,description,image:'https://platform-art.auralis.local/'+(missing?'missing':'avatar'),layout:uploads?'cards':'feed',cards:uploads?works:[card(1),card(2),card(3)],actions:[],tabs:[{action:{label:'Activity',handle:nav(1)},selected:!uploads},{action:{label:'Uploads',handle:nav(2)},selected:uploads}]};
    };
    await page.exposeFunction('profileBridge',async m=>{
      messages.push(m);
      if(m.action==='readPluginGlobalPage')assert(Number.isInteger(m.preferredPageSize)&&m.preferredPageSize>=6&&m.preferredPageSize<=20,'viewport hint stays bounded for every scale');
      if(m.action==='readPluginGlobalPage'&&m.providerId==='sample.qq')return page.evaluate(p=>window.Auralis.setPluginPage(p),{...m,handle:null,page:{version:6,title:'Musician discovery',layout:'cards',cards:Array.from({length:6},(_,i)=>({handle:handle(1200+i),author:'Musician '+i,
        avatar:i===1?null:'https://platform-art.auralis.local/avatar',text:'Original musician profile '+i,
        actions:[{label:'View profile',handle:nav(400+i)}]})),actions:[]}});
      if(m.action==='readPluginGlobalPage')await page.evaluate(p=>window.Auralis.setPluginPage(p),{...m,handle:null,page:documentFor(m)});
      if(m.action==='playPlatformResult')await page.evaluate(p=>window.Auralis.setPlatformPlaybackResult(p),{handle:m.handle,success:true,item:works.find(w=>w.media.handle===m.handle).media});
      if(m.action==='requestPlatformExtras')await page.evaluate(p=>window.Auralis.setPlatformExtras(p),{...m,items:[{handle:handle(900),author:'晨光 · Listener',text:m.kind==='replies'?'Thank you for listening.':'A gentle melody for the morning.',publishedAt:'2026-09-20T10:00:00Z',replyCount:m.kind==='replies'?0:1}]});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.profileBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),{id:'sample.identity',name:'Profile sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','StreamResolution','Comments','CommentReplies'],pages:[{id:'home',label:'创作主页',labelEn:'Creator space',placement:'global',presentation:'page',documentVersion:8}]});
    await page.locator('[data-plugin-navigation="sample.identity:home"]').click();
    const route=page.locator('.plugin-page-route'),header=route.locator('.plugin-page-header'),avatar=header.locator('.plugin-profile-avatar');
    await avatar.locator('img').waitFor();await page.waitForFunction(()=>document.querySelector('.plugin-profile-avatar img')?.naturalWidth>0);
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    assert.equal(await route.locator('h1').count(),1);assert.equal(await header.locator('h1').textContent(),title);
    assert.equal(await header.locator('.plugin-profile-description').textContent(),description);
    const back=header.locator('[data-plugin-back]');
    assert.equal(await back.textContent(),'Back','the compact icon retains its localized accessible button label');
    assert(await back.evaluate(n=>{
      const rect=n.getBoundingClientRect(),style=getComputedStyle(n),icon=getComputedStyle(n,'::before');
      return Math.abs(rect.width-40)<1&&Math.abs(rect.height-40)<1&&style.fontSize==='0px'&&
        icon.content!=='none'&&icon.backgroundColor==='rgba(0, 0, 0, 0)'&&n.title===n.getAttribute('aria-label');
    }),'back navigation is a 40px unbadged Fluent control with a matching tooltip');
    assert(await back.evaluate(n=>{n.hidden=true;const hidden=getComputedStyle(n).display==='none';n.hidden=false;return hidden;}),'a page without back history has no stray navigation target');
    assert.equal(artRequests.length,1,'the blurred background does not issue another artwork request');
    assert(await header.evaluate(n=>{const a=n.querySelector('.plugin-profile-avatar').getBoundingClientRect(),t=n.querySelector('h1').getBoundingClientRect(),r=n.getBoundingClientRect();return t.left>=a.right&&t.right<=r.right&&n.scrollWidth<=n.clientWidth+1;}),'identity layout fits and text does not overlap the avatar');
    if(scale===1||scale===2){
      await page.setViewportSize({width:2200,height:900});
      assert(await route.evaluate(n=>{const r=n.getBoundingClientRect(),available=n.parentElement.getBoundingClientRect();return r.width>=1800&&(available.width-r.width)<320;}),'wide UP pages use the available logical viewport');
      assert(await route.locator('.plugin-page-cards').evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length===3),'wide activity feed gains a third masonry column');
      assert(await route.evaluate(n=>{
        const previous=n.dataset.readingLayout;n.dataset.readingLayout='cards';
        const catalogue=document.createElement('div');catalogue.className='plugin-page-cards is-artwork-catalogue';
        for(let i=0;i<6;i++){const card=document.createElement('article');card.className='plugin-page-card';catalogue.append(card);}
        n.append(catalogue);
        const columns=getComputedStyle(catalogue).gridTemplateColumns.split(' ').length;
        const fits=catalogue.scrollWidth<=catalogue.clientWidth+1;catalogue.remove();n.dataset.readingLayout=previous;return columns>=4&&fits;
      }),'wide declarative creator albums fill the logical viewport without provider-specific CSS');
      assert(await route.evaluate(n=>{
        const previous=n.dataset.readingLayout;n.dataset.readingLayout='cards';
        const page=document.createElement('section');page.className='plugin-page-route';page.dataset.readingLayout='cards';
        const grid=document.createElement('div');grid.className='plugin-page-cards is-identity-catalogue';
        for(let i=0;i<6;i++){const card=document.createElement('article');card.className='plugin-page-card';grid.append(card);}
        page.append(grid);n.append(page);
        const columns=getComputedStyle(grid).gridTemplateColumns.split(' ').length;
        const fits=grid.scrollWidth<=grid.clientWidth+1;page.remove();n.dataset.readingLayout=previous;return columns>=4&&fits;
      }),'wide global musician discovery cards also avoid a narrow centered strip');
      assert((await page.locator('#pageContent').evaluate(n=>getComputedStyle(n).backgroundImage)).startsWith('linear-gradient('),'reading surface blends into the bottom background');
      await page.setViewportSize(scale===2?{width:640,height:620}:{width:1280,height:820});
    }
    assert.equal(await header.evaluate(n=>getComputedStyle(n,'::before').filter),'blur(24px)');
    assert.equal(await route.locator('.plugin-page-tabs').evaluate(n=>getComputedStyle(n).backdropFilter),'blur(10px)');
    assert(await route.locator('.plugin-page-card,.plugin-profile-description,.plugin-profile-avatar').evaluateAll(ns=>ns.every(n=>getComputedStyle(n).filter==='none'&&getComputedStyle(n).backdropFilter==='none')),'data is not blurred');
    await page.screenshot({path:path.join(out,`${theme}-${scale}-profile.png`)});
    await route.locator('.plugin-page-card').last().scrollIntoViewIfNeeded();
    assert(await route.getByRole('tab',{name:'Activity',exact:true}).evaluate(n=>{const r=n.getBoundingClientRect();return r.top>=0&&r.bottom<innerHeight-70;}),'sticky sections remain above the playback bar');
    await route.getByRole('button',{name:'Studio journal 1',exact:true}).click();
    await route.getByRole('heading',{name:'Activity details',exact:true}).waitFor();assert.equal(await avatar.count(),0,'detail does not retain creator decoration');
    await page.keyboard.press('Escape');await avatar.waitFor();
    await page.waitForFunction(()=>document.querySelector('.plugin-page-route')?.dataset.readingLayout==='feed');
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    assert.equal(await header.locator('h1').textContent(),title);
    assert.equal(await header.locator('.plugin-profile-description').textContent(),description,'uploads keep the same real creator biography');
    const profileWidth=await header.evaluate(n=>n.getBoundingClientRect().width);
    await route.getByRole('tab',{name:'Uploads',exact:true}).click();
    const catalogue=route.locator('.is-media-catalogue:not(.is-list)'),cards=catalogue.locator(':scope > article');await catalogue.waitFor();
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    assert.equal(await cards.count(),6);
    assert.equal(await cards.first().locator('.plugin-media-cover img').getAttribute('loading'),'eager','first-screen covers begin loading before they enter view');
    assert.equal(await header.evaluate(n=>n.getBoundingClientRect().width),profileWidth,'profile header stays stable across sections');
    assert.equal(await cards.first().locator('.plugin-card-media').count(),0,'matching author and cover duration are not duplicated');
    assert.equal(await cards.first().locator('.plugin-cover-duration').textContent(),'3:24');
    assert.equal(await cards.nth(1).locator('.plugin-media-availability').textContent(),'Preview only');
    assert.equal(await cards.nth(2).locator('.plugin-card-media').textContent(),'Guest musician','different artist credit survives');
    assert(await cards.nth(3).locator('[data-page-media="play"]').isDisabled());
    assert.equal(await cards.nth(4).locator('.plugin-card-media strong').textContent(),'Alternate recording title');
    assert.equal(await cards.nth(5).locator('.plugin-card-media').textContent(),title,'missing author keeps the media artist');
    assert(await cards.first().locator('time').evaluate(n=>n.title===n.getAttribute('aria-label')&&n.title.length>n.textContent.length&&!!n.dateTime),'compact date retains full accessible timestamp');
    assert(await cards.nth(2).locator('h3 button').evaluate(n=>n.title===n.textContent&&n.clientHeight<=43),'long title is two lines with complete tooltip and name');
    assert(await catalogue.locator('.plugin-page-actions button').evaluateAll(ns=>ns.every(n=>n.offsetHeight>=40)),'quiet actions retain accessible hit areas');
    assert(await catalogue.evaluate(n=>{const cards=[...n.children],top=cards[0].getBoundingClientRect().top,first=cards.filter(c=>Math.abs(c.getBoundingClientRect().top-top)<1);const bottoms=first.map(c=>c.querySelector('.plugin-page-actions').getBoundingClientRect().bottom);return Math.max(...bottoms)-Math.min(...bottoms)<1;}),'each catalogue row aligns its actions despite distinct credits and availability');
    const lastCover=cards.nth(5).locator('[data-media-cover]');await lastCover.scrollIntoViewIfNeeded();
    await lastCover.getByText('Image unavailable. Click to retry',{exact:true}).waitFor();
    await lastCover.click();await page.waitForFunction(()=>[...document.querySelectorAll('.plugin-media-cover img')].some(n=>n.src.includes('cover-5?retry=1')&&n.naturalWidth>0));
    assert(artRequests.some(url=>url.endsWith('/cover-5?retry=1')),'cover retry bypasses failed proxy cache without opening the upload');
    await route.evaluate(n=>n.parentElement.scrollTop=0);
    await page.waitForFunction(()=>[...document.querySelectorAll('.plugin-media-cover img')].filter(n=>n.getBoundingClientRect().top<innerHeight).every(n=>n.naturalWidth>0));
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    await page.screenshot({path:path.join(out,`${theme}-${scale}-uploads.png`)});
    if(scale===1){
      await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'zh-CN',resolvedLanguage:'zh-CN'}));
      assert(await cards.first().locator('time').evaluate(n=>n.textContent===new Date(n.dateTime).toLocaleDateString('zh-CN')&&n.title===new Date(n.dateTime).toLocaleString('zh-CN')),'live language change updates short and complete dates');
      assert.equal(await cards.first().locator('[data-page-media="play"]').textContent(),'播放');
      assert.equal(await cards.first().locator('[data-page-media="enqueue"]').textContent(),'加入队列');
      assert.equal(await cards.first().locator('[data-inline-discussion]').getAttribute('aria-label'),'查看评论 · 1');
      assert.equal(await cards.first().locator('h3').textContent(),titles[0],'language change never translates plugin content');
      assert.equal(await cards.nth(1).locator('.plugin-media-availability').textContent(),'仅试听');
      await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
      await page.screenshot({path:path.join(out,`${theme}-${scale}-uploads-zh.png`)});
      await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
    }
    // A native-state fixture verifies Web dispatch, not real decoding or audible continuity.
    const plays=()=>messages.filter(m=>m.action==='playPlatformResult');
    assert.equal(plays().length,0,'browsing never starts media');
    await cards.first().locator('[data-page-media="play"]').click();
    await page.waitForFunction(name=>document.querySelector('#miniTitle').textContent===name,titles[0]);
    await page.evaluate(id=>window.Auralis.setPlaybackState({id,isPlaying:true,currentTime:36,duration:204}),works[0].media.handle);
    await cards.nth(1).locator('[data-page-media="enqueue"]').click();
    assert.equal(messages.filter(m=>m.action==='prefetchPlatformTrack'&&m.handle).at(-1)?.handle,works[1].media.handle);
    const playbackActions=new Set(['playPlatformResult','prefetchPlatformTrack','loadTrack','playTrack','pausePlayback','resumePlayback','seekPlayback','setEmbeddedVideo']);
    const playbackCount=messages.filter(m=>playbackActions.has(m.action)).length;
    await cards.first().locator('[data-media-cover]').click();
    await route.getByRole('heading',{name:'Work details',exact:true}).waitFor();
    await route.evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    const alignment=await route.evaluate(n=>{const h=n.querySelector('.plugin-page-header').getBoundingClientRect(),b=n.querySelector('.plugin-detail-layout').getBoundingClientRect(),c=n.querySelector('.plugin-page-context').getBoundingClientRect();return {header:[h.left,h.right],body:[b.left,b.right],context:[c.left,c.right],layout:n.dataset.readingLayout};});
    assert(Math.abs(alignment.header[0]-alignment.body[0])<1&&Math.abs(alignment.header[1]-alignment.body[1])<1&&Math.abs(alignment.context[0]-alignment.body[0])<1,'detail shares one column: '+JSON.stringify(alignment));
    assert(await route.locator('.plugin-card-author time').evaluate(n=>n.textContent===n.title),'detail retains the full date');
    await route.locator('[data-inline-discussion="main"]').click();
    const discussion=route.locator('.plugin-detail-discussion');await discussion.locator('[data-reply]').waitFor();
    await discussion.locator('[data-reply]').click();await discussion.getByRole('heading',{name:'Comment details',exact:true}).waitFor();
    await page.keyboard.press('Escape');await discussion.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    await page.waitForFunction(()=>!document.querySelector('.toast'));
    await route.evaluate(n=>n.parentElement.scrollTop=0);
    await page.screenshot({path:path.join(out,`${theme}-${scale}-detail.png`)});
    await page.keyboard.press('Escape');await catalogue.waitFor();
    assert(await cards.first().locator('[data-media-cover]').evaluate(n=>n===document.activeElement),'Back returns to the exact cover');
    await route.getByRole('tab',{name:'Activity',exact:true}).click();
    await route.getByRole('button',{name:'Studio journal 1',exact:true}).waitFor();
    assert.equal(messages.filter(m=>playbackActions.has(m.action)).length,playbackCount,'detail, comments, replies, Back and sections never interrupt playback or replace its prefetch');
    assert.equal(await page.locator('#miniTitle').textContent(),titles[0]);
    assert.equal(plays().length,1);
    const cdp=await context.newCDPSession(page);
    await cdp.send('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-transparency',value:'reduce'}]});
    await page.waitForFunction(()=>getComputedStyle(document.querySelector('.plugin-page-tabs')).backdropFilter==='none');
    assert.equal(await header.evaluate(n=>getComputedStyle(n,'::before').display),'none');
    assert.equal(await route.locator('.plugin-page-tabs').evaluate(n=>getComputedStyle(n).backdropFilter),'none');
    await cdp.send('Emulation.setEmulatedMedia',{features:[{name:'forced-colors',value:'active'}]});
    await page.waitForFunction(()=>getComputedStyle(document.querySelector('.plugin-page-header'),'::before').filter==='none');
    assert.equal(await header.evaluate(n=>getComputedStyle(n,'::before').filter),'none');
    await cdp.send('Emulation.setEmulatedMedia',{features:[]});
    missing=true;await header.locator('[data-plugin-refresh]').click();
    await avatar.getByRole('button',{name:'Retry avatar',exact:true}).waitFor();
    assert((await avatar.textContent()).startsWith('无'),'failed avatar retains the name fallback');
    assert.equal(await avatar.evaluate(n=>n.clientWidth),scale===2?64:88,'failed avatar keeps its geometry');
    missingFails=false;await avatar.getByRole('button',{name:'Retry avatar',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('.plugin-profile-avatar img')?.naturalWidth>0);
    assert(artRequests.some(url=>url.endsWith('/missing?retry=1')),'avatar retry uses the same approved handle with a fresh request');
    assert.equal(await route.locator('.plugin-page-intro').count(),0,'no duplicate introduction');
    assert(!messages.some(m=>['setEmbeddedVideo','manageOnlineAccount'].includes(m.action)),'browsing remains read-only');
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p,{id:'sample.qq',name:'Musician sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages'],
      pages:[{id:'discover',label:'音乐人',labelEn:'Musicians',placement:'global',presentation:'page',documentVersion:6}]}]}),{
      id:'sample.identity',name:'Profile sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','StreamResolution','Comments','CommentReplies'],
      pages:[{id:'home',label:'创作主页',labelEn:'Creator space',placement:'global',presentation:'page',documentVersion:8}]});
    await page.locator('[data-plugin-navigation="sample.qq:discover"]').click();
    const identityGrid=route.locator('.is-identity-catalogue');await identityGrid.waitFor();
    await page.setViewportSize({width:2200,height:900});
    assert(await route.evaluate(n=>n.getBoundingClientRect().width>=1800),'musician discovery uses the wide route');
    assert(await identityGrid.evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length>=4&&n.scrollWidth<=n.clientWidth+1),
      'musician cards remain adaptive even when an avatar is absent');
    await page.screenshot({path:path.join(out,`${theme}-${scale}-musicians.png`)});
    assert.deepEqual(errors,[]);console.log(`PASS profile ${theme} ${scale}: hierarchy, catalogue metadata, detail alignment, playback/prefetch continuity, bounded blur, sticky tabs, accessibility, Back, avatar failure`);
    await context.close();
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
