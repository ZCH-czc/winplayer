'use strict';
// Production renderer and an isolated synthetic bridge; no account, provider or media reads.
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const rich=process.env.AURALIS_READING_RICH==='1',version=rich?8:7;
const out=path.resolve(__dirname,'../artifacts/plugin-reading-v'+version+'-ui');
const server=http.createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname;
  let file=path.resolve(root,'.'+decodeURIComponent(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep))throw Error();
  if(!process.env.AURALIS_UI_ROOT&&file===path.join(root,'assets','auralis-icon.png'))file=path.resolve(__dirname,'../Auralis/Assets/AuralisIcon.png');
  res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');
  res.end(await fs.readFile(file));
}catch{res.statusCode=404;res.end();}});
const nav=n=>'page-'+n.toString(16).padStart(32,'0'),community=n=>'community-'+n.toString(16).padStart(32,'0');
(async()=>{
  await fs.mkdir(out,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:620}:{width:1440,height:900},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),messages=[],errors=[];let fail=false,hold=false,held,pagination=false,heldAppend;
    page.on('pageerror',e=>errors.push(e.message));
    const picture='https://platform-art.auralis.local/reading-fixture';
    await page.route('**/*',r=>{
      if(r.request().url()===picture+'-portrait')return r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="240" height="960"><rect width="240" height="960" fill="#679aa3"/><circle cx="120" cy="100" r="70" fill="#edce92"/><path d="M0 850H240V960H0Z" fill="#204753"/></svg>'});
      if(r.request().url().startsWith(picture))return r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#679aa3"/><circle cx="430" cy="105" r="65" fill="#edce92"/><path d="M0 230 Q160 100 320 230 T640 230 V360 H0Z" fill="#346571"/><path d="M0 280 Q160 200 320 280 T640 280 V360 H0Z" fill="#204753"/></svg>'});
      return r.request().url().startsWith(origin)?r.continue():r.abort();
    });
    const settle=()=>page.evaluate(()=>Promise.allSettled(document.getAnimations().filter(a=>a.effect?.getTiming().iterations!==Infinity).map(a=>a.finished)));
    const assertPacking=async columns=>{
      await settle();
      await page.waitForFunction(count=>{
        const grid=document.querySelector('.plugin-page-cards'),cards=[...grid.children];
        if(count===1)return !grid.classList.contains('is-staggered')&&cards.every(n=>!n.style.gridColumn&&!n.style.gridRow);
        if(!grid.classList.contains('is-staggered'))return false;
        const bounds=grid.getBoundingClientRect(),bottoms=Array(count).fill(bounds.top);
        for(const card of cards){
          const r=card.getBoundingClientRect(),top=Math.min(...bottoms),column=bottoms.indexOf(top);
          if(Number(card.style.gridColumnStart)!==column+1||Math.abs(r.top-top)>1.5)return false;
          bottoms[column]=r.bottom+16;
        }
        return Math.abs(bounds.bottom-(Math.max(...bottoms)-16))<2;
      },columns);
      assert(await page.locator('.plugin-page-cards').evaluate(n=>n.scrollWidth<=n.clientWidth+1),'packing never creates horizontal overflow');
    };
    const provider={id:'sample.reading',name:'Reading sample',configured:true,pageRevision:1,capabilities:['Pages','GlobalPages','Comments','CommentReplies','StreamResolution'],
      pages:[{id:'read',label:'阅读',labelEn:'Reading',placement:'global',presentation:'page',documentVersion:version}]};
    const card=n=>({handle:community(n),title:'A day beside the sea '+n,author:'Demo musician',publishedAt:'2026-09-20T01:00:00Z',
      text:rich&&n===1?Array.from({length:32},(_,i)=>'创作日记 '+(i+1)+' · Keep the reading position.').join('\n'):rich&&n===4?'i'.repeat(300):'Original demonstration of a chronological reading surface.\n\n'+('Paragraphs remain readable with room for discussion. '.repeat(8)),
      avatar:picture,images:n===2?[rich?picture+'-portrait':picture]:rich&&n===3?[picture,picture]:[],actions:[],open:{label:'Read post',handle:nav(n)},discussionHandle:community(100+n),commentCount:450,likeCount:42,repostCount:7,
      ...(pagination?{media:{handle:'track-'+n.toString(16).padStart(24,'0'),kind:'online',providerId:'sample.reading',title:'A day beside the sea '+n,
        artist:'Demo musician',durationSeconds:120,isPlayable:true,coverUrl:picture}}:{}),
      ...(rich?{quote:{status:n===4?'unavailable':'available',title:n===4?'Original unavailable':'Original note',author:n===4?'':'Original musician',
        text:n===4?'Not returned by the source':'A note [sea] <b>plain</b>',body:n===4?[]:[{text:'A note '},{text:'[sea]',image:picture},{text:' <b>plain</b>'}],
        images:n===1?[picture,picture,picture,picture]:[],discussionHandle:n===4?null:community(200+n),commentCount:n===4?null:5}}:{})});
    const comments=(m)=>{
      const n=Number(m.pageHandle||0),reply=m.kind==='replies';
      return {...m,items:Array.from({length:reply?8:50},(_,i)=>({handle:community(1000+n+i+(reply?10000:0)),author:'Reader '+(n+i),
        text:(reply?'Reply ':'Comment ')+(n+i)+' · This is an original test discussion. <b>Plain text</b> [sea]',avatarUrl:picture,emotes:[{text:'[sea]',url:picture}],publishedAt:'2026-09-20T01:00:00Z',likeCount:3,replyCount:reply?0:8,replyToAuthor:reply?'Reader 0':null})),
        nextPageHandle:!reply&&n<400?String(n+50):null};
    };
    await page.exposeFunction('readingBridge',async m=>{
      messages.push(m);
      if(m.action==='readPluginGlobalPage'){
        if(m.navigationHandle===nav(500)){heldAppend=m;return;}
        const detail=!!m.navigationHandle,n=detail?parseInt(m.navigationHandle.slice(5),16):0;
        const doc={version,title:detail?'Post details':'Activity',layout:detail?'detail':'feed',append:false,actions:[],
          ...(!detail&&pagination?{collectionHandle:'reading-collection',next:{label:'Load more',handle:nav(500)}}:{}),
          cards:detail?[{...card(n),open:null}]:[1,2,3,4].map(card)};
        return page.evaluate(x=>window.Auralis.setPluginPage(x),{...m,handle:null,page:doc});
      }
      if(m.action==='requestPlatformExtras'){
        if(hold){held=m;return;}
        if(fail){fail=false;return page.evaluate(x=>window.Auralis.setPlatformExtras(x),{...m,error:{message:'Synthetic temporary failure'}});}
        return page.evaluate(x=>window.Auralis.setPlatformExtras(x),comments(m));
      }
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.readingBridge(m)}};});
    await page.goto(origin+'/?ui-test=1&ui-test-theme='+theme);await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.evaluate(p=>window.Auralis.setPlatformConfiguration({providers:[p]}),provider);
    await page.locator('[data-plugin-navigation="sample.reading:read"]').click();
    const route=page.locator('.plugin-page-route'),panel=route.locator('.plugin-detail-discussion');
    await route.getByRole('button',{name:'A day beside the sea 1',exact:true}).waitFor();
    assert.equal(await route.locator('.plugin-page-cards').evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length),scale===2?1:2,'available CSS width controls the reading columns');
    assert(await route.locator('.plugin-page-cards').evaluate(n=>Math.abs(n.getBoundingClientRect().width-n.closest('.plugin-page-route').getBoundingClientRect().width)<2),'feed uses the profile width without a narrow central shelf');
    assert.deepEqual(await route.locator('.plugin-page-cards > article .plugin-reading-link').allTextContents(),[1,2,3,4].map(n=>'A day beside the sea '+n),'two columns retain chronological DOM/keyboard order');
    await assertPacking(scale===2?1:2);
    if(scale===2){
      await page.setViewportSize({width:1600,height:900});
      await page.waitForFunction(()=>getComputedStyle(document.querySelector('.plugin-page-cards')).gridTemplateColumns.split(' ').length===2);
      await assertPacking(2);
      await route.evaluate(n=>n.style.width='980.25px');await assertPacking(2);
      await route.evaluate(n=>n.style.width='979.75px');await assertPacking(1);
      await route.evaluate(n=>n.style.width='');await assertPacking(2);
      await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-wide-feed.png`)});
      await page.setViewportSize({width:640,height:620});
      await page.waitForFunction(()=>getComputedStyle(document.querySelector('.plugin-page-cards')).gridTemplateColumns.split(' ').length===1);
      await assertPacking(1);
    }
    if(rich){
      const firstCard=route.locator('.plugin-page-cards > .plugin-page-card').first(),toggle=firstCard.locator(':scope > .plugin-text-toggle');
      await page.waitForFunction(()=>document.querySelectorAll('.plugin-page-cards > article')[3]?.querySelector('.plugin-text-toggle')?.hidden);
      assert.equal(await toggle.getAttribute('aria-expanded'),'false');
      const textId=await toggle.getAttribute('aria-controls');
      assert.equal(await page.locator('[id="'+textId+'"]').count(),1,'long text has one accessible control target');
      await toggle.click();assert.equal(await toggle.getAttribute('aria-expanded'),'true');
      await assertPacking(scale===2?1:2);
      await toggle.scrollIntoViewIfNeeded();await toggle.click();
      assert.equal(await toggle.getAttribute('aria-expanded'),'false');
      assert(await toggle.evaluate(n=>{const r=n.getBoundingClientRect(),bar=document.querySelector('#playerBar').getBoundingClientRect();return r.top>=38&&r.bottom<=bar.top;}),'collapse keeps its control visible above the player');
      assert(await toggle.evaluate(n=>n===document.activeElement),'collapse retains keyboard focus');
      await assertPacking(scale===2?1:2);
      const single=route.locator('.plugin-page-cards > article').nth(1).locator('.plugin-card-images'),pair=route.locator('.plugin-page-cards > article').nth(2).locator('.plugin-card-images');
      assert.equal(await pair.evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length),2,'two pictures use two equal columns');
      await single.scrollIntoViewIfNeeded();await page.waitForFunction(()=>document.querySelector('img[src$="-portrait"]')?.naturalHeight===960);
      assert(await single.locator('img').evaluate(n=>{const r=n.getBoundingClientRect(),slot=n.parentElement.getBoundingClientRect();return Math.abs(r.height-slot.height)<1&&Math.abs(r.width-slot.width)<1&&getComputedStyle(n).objectFit==='contain';}),'portrait fits its reserved slot without fixed-height overflow or cropping');
      await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-portrait.png`)});
      assert(await route.locator('.plugin-card-actions button').evaluateAll(ns=>ns.every(n=>n.getBoundingClientRect().height>=40)),'quiet actions keep 40px hit targets');
      assert(await route.locator('.plugin-page-card,.plugin-page-quote').evaluateAll(ns=>ns.every(n=>getComputedStyle(n).filter==='none'&&getComputedStyle(n).backdropFilter==='none')),'reading cards do not accumulate blur');
      await route.evaluate(n=>n.parentElement.scrollTop=0);
      await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-card-rhythm.png`)});
      assert.equal(await route.locator('.plugin-page-quote').count(),4);
      assert.equal(await route.locator('.plugin-page-quote .plugin-card-images').first().evaluate(n=>getComputedStyle(n).gridTemplateColumns.split(' ').length),2,'four-image quotation uses a two-by-two gallery');
      assert.equal(await route.locator('.plugin-page-quote b').count(),0,'runs never become markup');
      assert.equal(await route.locator('.plugin-body-emote').count(),scale===1.5?0:3);
      assert.equal(await route.locator('[data-quote-status="unavailable"] button').count(),0);
      if(scale!==1.5){const first=route.locator('.plugin-body-emote').first();await first.evaluate(n=>n.dispatchEvent(new Event('error')));
        assert((await route.locator('.plugin-page-quote .plugin-card-text').first().textContent()).includes('[sea]'),'failed emote retains fallback');}
      await route.locator('[data-inline-discussion="quote"]').first().click();
      await panel.locator('.discussion-list > article').first().waitFor();
      assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').at(-1).handle,community(201),'quote opens original subject, never sharing subject');
      assert((await panel.locator('.discussion-subject').textContent()).includes('Original musician'));
      await route.locator('[data-plugin-back]').click();
      await page.waitForFunction(()=>document.querySelector('.plugin-page-route')?.dataset.readingLayout==='feed');
      await route.locator('[data-inline-discussion="quote"]').first().waitFor();
      assert(await route.locator('[data-inline-discussion="quote"]').first().evaluate(n=>n===document.activeElement),'quote roundtrip restores exact source focus');
    }
    // Refresh once into a pageable fixture; hold its next batch while we remember the old
    // nodes and positions, then append uneven cards. No old card should be recreated/moved.
    pagination=true;await route.locator('[data-plugin-refresh]').click();
    await page.waitForFunction(()=>document.querySelector('.plugin-page-status button')?.textContent==='Load more');
    await assertPacking(scale===2?1:2);
    await route.evaluate(n=>{window.readingCards=[...n.querySelector('.plugin-page-cards').children];
      window.readingPlacements=window.readingCards.map(c=>[c.style.gridColumn,c.style.gridRow]);
      n.parentElement.scrollTop=n.parentElement.scrollHeight;});
    await page.waitForFunction(()=>document.querySelector('.plugin-page-status .loading-state'));
    await page.evaluate(x=>window.Auralis.setPluginPage(x),{...heldAppend,handle:null,page:{version,title:'Activity',layout:'feed',append:true,
      collectionHandle:'reading-collection',actions:[],cards:[card(2),card(5),card(6)]}});
    await page.waitForFunction(()=>document.querySelectorAll('.plugin-page-cards > article').length===6);
    await assertPacking(scale===2?1:2);
    assert(await route.evaluate(n=>window.readingCards.every((c,i)=>c===n.querySelector('.plugin-page-cards').children[i]&&
      c.style.gridColumn===window.readingPlacements[i][0]&&c.style.gridRow===window.readingPlacements[i][1])),
    'append preserves old card nodes and positions while filling the shortest column');
    assert.equal(await route.locator('.plugin-page-cards.is-media-catalogue').count(),0,'a video-only activity feed is still a staggered feed, not a catalogue');
    if(scale!==2){
      await route.evaluate(n=>{const grid=n.querySelector('.plugin-page-cards'),scroll=n.parentElement;
        const top=grid.getBoundingClientRect().top-scroll.getBoundingClientRect().top+scroll.scrollTop;
        scroll.scrollTop=top+Math.min(grid.children[0].offsetHeight,grid.children[1].offsetHeight)-160;});
      await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-masonry-continuation.png`)});
    }
    await route.evaluate(n=>n.parentElement.scrollTop=0);
    await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-feed.png`)});
    // Opening the post itself also loads comments without taking focus from its title.
    const beforeDetail=messages.filter(m=>m.action==='requestPlatformExtras').length;
    await route.getByRole('button',{name:'A day beside the sea 2',exact:true}).click();
    await panel.locator('.discussion-list > article').first().waitFor();await settle();
    assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').length,beforeDetail+1,'one initial discussion read per detail');
    assert(await route.locator('#pluginPageTitle').evaluate(n=>n===document.activeElement),'automatic comments do not steal focus');
    await route.locator('[data-plugin-back]').click();
    await page.waitForFunction(()=>document.querySelector('.plugin-page-route')?.dataset.readingLayout==='feed');
    // The feed comment action enters the detail and directly opens its discussion, never a modal.
    await route.locator('[data-inline-discussion="main"]').nth(1).click();
    await panel.locator('.discussion-list > article').first().waitFor();
    assert.match(await route.locator('.plugin-page-card .plugin-card-stats').innerText(),/42/);
    assert.match(await route.locator('.plugin-page-card .plugin-card-stats').innerText(),/7/);
    assert.equal(await route.getAttribute('data-reading-layout'),'detail');
    assert.equal(await page.locator('dialog[open]').count(),0);
    await settle();
    const assertDetail=async split=>{
      await route.evaluate(n=>n.parentElement.scrollTop=0);
      await page.waitForFunction(split=>{
        const layout=document.querySelector('.plugin-detail-layout');
        return getComputedStyle(layout).gridTemplateColumns.split(' ').length===(split?2:1);
      },split);
      const geometry=await panel.evaluate(n=>({post:n.parentElement.querySelector('.plugin-page-cards').getBoundingClientRect().toJSON(),comments:n.getBoundingClientRect().toJSON()}));
      const a=geometry.post,b=geometry.comments;
      assert(split?b.left>=a.right+16&&Math.abs(b.top-a.top)<2:b.top>=a.bottom+16,'detail follows available width, not physical pixels: '+JSON.stringify(geometry));
      assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'detail has no horizontal overflow');
    };
    await route.evaluate(n=>n.parentElement.scrollTop=0);await assertDetail(scale!==2);
    const originalViewport=page.viewportSize(),readsBeforeResize=messages.filter(m=>m.action==='requestPlatformExtras').length;
    await panel.evaluate(n=>{window.detailPost=n.parentElement.querySelector('.plugin-page-cards').firstElementChild;window.detailComment=n.querySelector('.discussion-list').firstElementChild;n.querySelector('.discussion-scroll').scrollTop=80;});
    await page.setViewportSize({width:1920,height:1080});await assertDetail(true);
    await route.evaluate(n=>n.style.width='979.75px');await assertDetail(false);
    await route.evaluate(n=>n.style.width='980.25px');await assertDetail(true);
    await route.evaluate(n=>n.style.width='');
    await page.setViewportSize({width:1920,height:560});await assertDetail(false);
    await page.setViewportSize({width:1920,height:1080});await assertDetail(true);
    assert(await panel.evaluate(n=>n.parentElement.querySelector('.plugin-page-cards').firstElementChild===window.detailPost&&n.querySelector('.discussion-list').firstElementChild===window.detailComment&&n.querySelector('.discussion-scroll').scrollTop===80),'responsive changes preserve nodes and discussion scroll');
    assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').length,readsBeforeResize,'resizing does not reread the discussion');
    await panel.locator('.discussion-scroll').evaluate(n=>n.scrollTop=0);
    await page.screenshot({path:path.join(out,`${theme}-${scale}-split-detail.png`)});
    await page.setViewportSize(originalViewport);await assertDetail(scale!==2);
    if(scale===2)await panel.evaluate(n=>n.scrollIntoView({block:'start'}));
    assert.equal(await panel.locator('.discussion-comment').nth(1).evaluate(n=>getComputedStyle(n).borderTopWidth),'0px','comments use whitespace rather than strong rules');
    assert(await panel.locator('[data-reply]').first().evaluate(n=>parseFloat(getComputedStyle(n).minHeight)>=40&&n.offsetHeight>=40),'compact reply retains an accessible hit target');
    assert.equal(await panel.locator('time').count(),50);
    assert.equal(await panel.locator('b').count(),0,'comment content is not HTML');
    assert.equal(await panel.locator('.comment-avatar img').count(),50,'approved avatar proxy images');
    assert.equal(await panel.locator('.comment-emote').count(),scale===1.5?0:50,'reduced motion uses emote text');
    assert(await route.evaluate(n=>n.scrollWidth<=n.clientWidth+1&&n.parentElement.scrollWidth<=n.parentElement.clientWidth+1),'no horizontal overflow');
    await settle();
    if(scale===2)assert(await panel.evaluate(n=>n.getBoundingClientRect().bottom<=document.querySelector('#playerBar').getBoundingClientRect().top+1),'loaded narrow discussion stays above playback bar');
    await page.screenshot({path:path.join(out,`${theme}-${scale}-discussion.png`)});
    const reply=panel.locator('[data-reply]').nth(3);await reply.scrollIntoViewIfNeeded();
    const scroll=await panel.locator('.discussion-scroll').evaluate(n=>n.scrollTop);
    await reply.click();await panel.getByRole('heading',{name:'Comment details',exact:true}).waitFor();
    await panel.locator('.discussion-list > article').nth(7).waitFor();
    assert.equal(await panel.locator('.discussion-root').count(),1);
    assert.equal(await panel.locator('[data-reply]').count(),0,'replies have their own view, not recursively nested cards');
    assert(await panel.locator('h2').evaluate(n=>n===document.activeElement),'async loading preserves heading focus');
    if(scale===1.5)assert.equal(await panel.evaluate(n=>n.getAnimations({subtree:true}).filter(a=>a.playState==='running').length),0,'reduced motion starts no transition');
    await settle();await page.screenshot({path:path.join(out,`${theme}-${scale}-replies.png`)});
    await page.keyboard.press('Escape');await panel.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    assert(Math.abs(await panel.locator('.discussion-scroll').evaluate(n=>n.scrollTop)-scroll)<2,'root scroll restored');
    assert(await panel.locator('[data-reply]').nth(3).evaluate(n=>n===document.activeElement),'root reply control focus restored');
    fail=true;await panel.getByRole('button',{name:'Newest',exact:true}).click();
    await panel.getByText('Synthetic temporary failure',{exact:true}).waitFor();
    const before=messages.length;await page.waitForTimeout(200);assert.equal(messages.length,before,'errors do not auto retry');
    await panel.getByRole('button',{name:'Retry',exact:true}).click();await panel.locator('.discussion-list > article').first().waitFor();
    assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').at(-1).sort,'newest');
    const reads=messages.filter(m=>m.action==='requestPlatformExtras').length;
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'zh-CN',resolvedLanguage:'zh-CN'}));
    await panel.getByRole('heading',{name:'评论',exact:true}).waitFor();
    if(rich)assert.equal(await route.locator('.plugin-quote-label').textContent(),'转发原文','host quotation label follows UI language without HTTP');
    assert.equal(messages.filter(m=>m.action==='requestPlatformExtras').length,reads,'language change does not reload comments');
    await page.evaluate(()=>window.Auralis.setUiLanguageState({preference:'en-US',resolvedLanguage:'en-US'}));
    await panel.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    if(theme==='light'&&scale===1){
      // Bounded continuation plus previous section; no items silently trimmed from an active page.
      for(let n=0;n<5&&!await panel.getByText('Reader 150',{exact:true}).count();n++){
        await panel.locator('.discussion-scroll').evaluate(node=>node.scrollTop=node.scrollHeight);
        await page.waitForTimeout(120);
      }
      await panel.getByText('Reader 150',{exact:true}).waitFor();
      assert((await panel.locator('.discussion-list > article').count())<=200,'infinite scroll keeps a bounded comment window');
      if(await panel.getByRole('button',{name:'Previous section',exact:true}).count()){
        await panel.getByRole('button',{name:'Previous section',exact:true}).click();
        assert((await panel.locator('.discussion-list > article').count())>0,'previous window remains reachable');
      }
    }
    // Back is validated by native before restoring feed DOM and focus.
    await route.locator('[data-plugin-back]').click();
    await page.waitForFunction(()=>document.querySelector('.plugin-page-route')?.dataset.readingLayout==='feed');
    await route.locator('.plugin-reading-link').first().waitFor();
    assert.equal(await route.getAttribute('data-reading-layout'),'feed');
    await assertPacking(scale===2?1:2);
    assert(await route.locator('[data-inline-discussion="main"]').nth(1).evaluate(n=>n===document.activeElement));
    hold=true;await route.locator('[data-inline-discussion="main"]').first().click();
    await panel.getByText('Loading…',{exact:true}).waitFor();
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    await route.waitFor({state:'hidden'});
    await page.evaluate(x=>window.Auralis.setPlatformExtras(x),comments(held));
    assert.equal(await page.locator('.plugin-detail-discussion.is-open').count(),0,'provider revocation cancels detached discussion');
    assert.deepEqual(errors,[]);
    assert(!messages.some(m=>['playPlatformResult','openPlatformMusicVideo'].includes(m.action)||m.action==='prefetchPlatformTrack'&&m.handle),'reading never resolves media');
    await context.close();console.log(`PASS v${version} reading ${theme} ${scale}: detail/comments/replies/back, retry, cancellation, bounds, no playback`);
  }}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
