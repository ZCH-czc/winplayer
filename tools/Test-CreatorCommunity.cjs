'use strict';
// Full application Web UI with an isolated synthetic bridge. No platform network or personal data.
const {chromium}=require('playwright');
const assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),http=require('node:http');
const root=path.resolve(process.env.AURALIS_UI_ROOT||path.join(__dirname,'../Auralis/wwwroot'));
const output=path.resolve(__dirname,'../artifacts/community-ui');
const server=http.createServer(async(req,res)=>{
  try {const file=path.resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname==='/'?'/index.html':new URL(req.url,'http://localhost').pathname));
    if(!file.startsWith(root+path.sep))throw Error();
    const data=await fs.readFile(file);res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png'})[path.extname(file)]||'application/octet-stream');res.end(data);
  }catch{res.statusCode=404;res.end();}
});
(async()=>{
  await fs.mkdir(output,{recursive:true});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const origin=`http://127.0.0.1:${server.address().port}`,browser=await chromium.launch({channel:'msedge',headless:true});
  try{for(const theme of ['light','dark'])for(const scale of [1,1.5,2]){
    const context=await browser.newContext({viewport:scale===2?{width:640,height:560}:{width:1280,height:820},deviceScaleFactor:scale,reducedMotion:scale===1.5?'reduce':'no-preference'});
    const page=await context.newPage(),errors=[],messages=[],artRequests=[];let failInline=true,failAvatar=true,failSearchAvatar=true;
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    await page.route('https://platform-art.auralis.local/**',r=>{
      const url=r.request().url();artRequests.push(url);
      const name=new URL(url).pathname;
      return r.fulfill(name.endsWith('/post-image')&&failInline||name.endsWith('/avatar-one')&&failAvatar||name.endsWith('/avatar-search')&&failSearchAvatar?{status:502,body:''}:{contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#497f89"/><circle cx="160" cy="90" r="60" fill="#c4ded9"/></svg>'});
    });
    const track={id:'work-one',handle:'work-one',kind:'online',providerId:'fixture.community',title:'Synthetic music performance',artist:'Fixture musician',album:'Fixture',durationSeconds:120,isPlayable:true,hasMusicVideo:true};
    const provider={id:track.providerId,name:'Fixture',capabilities:['TrackSearch','CreatorFeed','Comments','CommentReplies','VideoResolution'],configured:true};
    let hold=null,failReplies=false,failCommentPage=false,profileOnly=false,manyCreators=false,emptyCreators=false;
    await page.exposeFunction('communityBridge',async m=>{
      messages.push(m);
      if(m.action==='searchPlatformCreators') {
        if(hold==='creator-search')return;
        if(emptyCreators){await page.evaluate(payload=>window.Auralis.setCreatorSearchResult(payload),{...m,items:[],nextPageHandle:null});return;}
        if(manyCreators){await page.evaluate(payload=>window.Auralis.setCreatorSearchResult(payload),{...m,items:Array.from({length:5},(_,i)=>({handle:'overview-'+i,displayName:'Overview musician '+i,description:'Synthetic creator'})),nextPageHandle:null});return;}
        await page.evaluate(payload=>window.Auralis.setCreatorSearchResult(payload),{...m,items:[{handle:m.pageHandle?'found-two':'found-one',displayName:'Search musician',description:'Creator search result, not a media item.',avatarUrl:'https://platform-art.auralis.local/avatar-search'}],nextPageHandle:m.pageHandle?null:'creator-next'});
        return;
      }
      if(m.action==='platformSearchTracks')await page.evaluate(payload=>window.Auralis.setPlatformSearchResult(payload),{...m,items:[m.pageHandle?{...track,id:'work-two',handle:'work-two'}:track],nextPageHandle:m.pageHandle?null:'track-next',totalCount:2});
      if(m.action!=='requestPlatformExtras'||m.kind===hold)return;
      let items=[],nextPageHandle=null,error=null;
      if(m.kind==='creator')items=[{handle:'creator-one',displayName:'Fixture musician',description:'A synthetic profile — no real account data.',avatarUrl:'https://platform-art.auralis.local/avatar-one'}];
      if(m.kind==='creator'&&profileOnly)items[0].description=Array.from({length:12},(_,i)=>`Biography paragraph ${i+1}. A synthetic musician profile for wrapping and independent scroll tests.`).join('\n\n');
      if(m.kind==='feed'){
        items=Array.from({length:m.pageHandle?1:6},(_,i)=>({handle:m.pageHandle?'post-end':`post-${i}`,discussionHandle:`discussion-${i}`,title:`Activity ${i+1}`,text:'A musician’s update. <img src=x onerror=alert(1)>\n'+('A longer text passage for responsive layout. '.repeat(i%3+1)),publishedAt:'2026-09-12T02:35:00Z',images:['https://platform-art.auralis.local/post-image'],commentCount:550}));
        nextPageHandle=m.pageHandle?null:'feed-next';
      }
      if(m.kind==='comments'){
        const index=Number(m.pageHandle?.replace('comments-','')||0);
        items=Array.from({length:20},(_,i)=>({handle:'community-'+(index*20+i).toString(16).padStart(32,'0'),author:`Reader ${index*20+i}`,text:'A readable comment with a nested thread. '.repeat(3),publishedAt:'2026-09-12T03:01:00Z',replyCount:3,likeCount:5,avatarUrl:'https://platform-art.auralis.local/avatar-one'}));
        nextPageHandle=index<25?`comments-${index+1}`:null;
        if(failCommentPage&&index===1){failCommentPage=false;items=[];nextPageHandle=null;error={code:'InvalidResponse',message:'平台未返回有效评论分页，暂不能确认评论是否加载完整。请稍后重试。'};}
      }
      if(m.kind==='replies'){
        if(failReplies){error={message:'Temporary fixture error'};failReplies=false;}
        else{items=[{handle:'community-'+(m.pageHandle?10001:10000).toString(16).padStart(32,'0'),author:'Reply author',replyToAuthor:'Another reader',text:'Reply to a reply <script>unsafe()</script>',publishedAt:'2026-09-12T03:06:00Z',avatarUrl:'https://platform-art.auralis.local/avatar-one'}];nextPageHandle=m.pageHandle?null:'replies-next';}
      }
      await page.evaluate(payload=>window.Auralis.setPlatformExtras(payload),{...m,items,nextPageHandle,error});
    });
    await page.addInitScript(()=>{localStorage.setItem('auralis:platform-search-provider','fixture.community');localStorage.setItem('auralis:language','en-US');window.chrome={webview:{postMessage:m=>window.communityBridge(m)}};});
    await page.goto(origin+`/?ui-test=1&ui-test-theme=${theme}`);
    await page.evaluate(provider=>window.Auralis.setPlatformConfiguration({providers:[provider]}),provider);
    await page.locator('#splashScreen').waitFor({state:'hidden'});
    await page.locator('#sidebarSearchButton').click();await page.locator('#searchInput').fill('Fixture');
    const author=page.locator('[data-media-action="track-creator"]:visible');await author.waitFor();
    assert(await page.locator('.platform-mv-cell').evaluate(cell=>{
      const bounds=cell.getBoundingClientRect();
      return [...cell.querySelectorAll('button')].filter(b=>b.getClientRects().length).every(b=>{const r=b.getBoundingClientRect();return r.left>=bounds.left-1&&r.right<=bounds.right+1;});
    }),'creator, comments and video buttons stay within their action column');
    await author.focus();await page.keyboard.press('Enter');
    const creator=page.locator('.creator-feed-page');await page.waitForSelector('.creator-post');
    assert.equal(await creator.locator('h1').textContent(),'Creator profile');
    assert.equal(await creator.locator('.creator-post').count(),6);
    assert.equal(await creator.locator('script').count(),0);
    assert(!messages.some(m=>m.action==='playPlatformResult'),'author click cannot start playback');
    const geometry=await creator.evaluate(d=>{const b=d.getBoundingClientRect();return{width:d.scrollWidth-d.clientWidth,body:d.parentElement.scrollWidth-d.parentElement.clientWidth,bottom:b.bottom,top:b.top,view:innerHeight,columns:getComputedStyle(d.querySelector('.creator-posts')).gridTemplateColumns.split(' ').length};});
    assert(geometry.width<=1&&geometry.body<=1&&geometry.top>=0&&geometry.top<geometry.view,'creator fits viewport without horizontal overflow');
    assert.equal(geometry.columns,scale===2?1:2);
    const profileAvatar=creator.locator('.creator-profile .comment-avatar');
    await profileAvatar.getByRole('button',{name:'Retry avatar',exact:true}).waitFor();
    failAvatar=false;await profileAvatar.getByRole('button',{name:'Retry avatar',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('.creator-profile .comment-avatar img')?.naturalWidth>0);
    assert(artRequests.some(url=>url.endsWith('/avatar-one?retry=1')),'legacy creator avatar reloads without refreshing the feed');
    const firstImage=creator.locator('[data-community-action="image"]').first();
    await firstImage.getByText('Image unavailable. Click to retry',{exact:true}).waitFor();
    failInline=false;await firstImage.click();
    await page.waitForFunction(()=>document.querySelector('[data-community-action="image"] img')?.naturalWidth>0);
    assert(artRequests.some(url=>url.endsWith('/post-image?retry=1')),'failed legacy feed picture reloads the approved proxy handle without opening the gallery');
    await creator.evaluate(d=>Promise.allSettled(d.getAnimations().map(a=>a.finished)));
    await page.evaluate(()=>Promise.allSettled([...document.querySelectorAll('.topbar,.search-box')].flatMap(n=>n.getAnimations()).map(a=>a.finished)));
    await page.screenshot({path:path.join(output,`${theme}-${scale}-creator.png`)});
    assert.equal(await page.locator('dialog[open]').count(),0,'activity is a main page, not a modal');
    await creator.locator('[data-community-action="image"]').first().click();
    await page.locator('.creator-gallery[open] img').waitFor();
    await page.keyboard.press('Escape');await page.locator('.creator-gallery').waitFor({state:'hidden'});
    assert(await creator.isVisible(),'gallery returns to full activity page');
    await creator.locator('[data-community-action="comments"]').first().click();
    const comments=page.locator('dialog[aria-labelledby="mediaDialogTitle"]');await page.waitForSelector('.discussion-list article');
    assert.equal(await comments.locator('.comment-meta time').first().getAttribute('datetime'),'2026-09-12T03:01:00.000Z');
    assert(messages.some(m=>m.kind==='comments'&&m.handle==='discussion-0'));
    failReplies=true;
    await comments.locator('[data-reply]').first().click();
    await comments.getByRole('button',{name:'Retry',exact:true}).click();
    await page.waitForFunction(()=>document.querySelectorAll('.discussion-list > article').length===2);
    assert.equal(await comments.locator('.discussion-list > article time').count(),2);
    assert.equal(await comments.locator('script').count(),0);
    assert.match(await comments.locator('.discussion-list > article').first().textContent(),/Another reader/);
    await page.screenshot({path:path.join(output,`${theme}-${scale}-replies.png`)});
    await page.keyboard.press('Escape');
    assert(await comments.isVisible(),'Escape from replies returns to comments, not the player');
    await comments.locator('[data-control="newest"]').click();await page.waitForSelector('.discussion-list article');
    assert(messages.some(m=>m.kind==='comments'&&m.sort==='newest'));
    if(theme==='light'&&scale===1){
      failCommentPage=true;
      await comments.locator('.discussion-scroll').evaluate(n=>{n.scrollTop=n.scrollHeight;});
      await comments.getByRole('button',{name:'Retry',exact:true}).waitFor();
      assert.equal(await comments.locator('.discussion-list > article').count(),20,'invalid page preserves already read comments');
      await comments.getByRole('button',{name:'Retry',exact:true}).click();
      for(let i=2;i<=25;i++){
        await page.waitForFunction(count=>document.querySelector('.discussion-list')?.textContent.includes('Reader '+count),i*20-1);
        await comments.locator('.discussion-scroll').evaluate(n=>{n.scrollTop=n.scrollHeight;});
        const more=comments.getByRole('button',{name:'Continue reading',exact:true});
        if(await more.count())await more.click();
      }
      await page.waitForFunction(()=>document.querySelector('.discussion-list')?.textContent.includes('Reader 519'));
      assert(await comments.locator('.discussion-list > article').count()<=200,'long discussions use bounded segments, not an unbounded DOM');
      assert(messages.some(m=>m.kind==='comments'&&m.pageHandle==='comments-25'),'later comments remain reachable past 500');

    }
    await page.keyboard.press('Escape');await comments.waitFor({state:'hidden'});assert(await creator.isVisible(),'closing comments restores creator');
    await creator.evaluate(n=>{n.parentElement.scrollTop=n.parentElement.scrollHeight;});await page.waitForFunction(()=>document.querySelectorAll('.creator-post').length===7);
    await page.keyboard.press('Escape');await creator.waitFor({state:'hidden'});
    await page.waitForFunction(()=>document.activeElement?.matches('[data-media-action="track-creator"]'));
    assert.equal(await author.evaluate(n=>document.activeElement===n),true,'focus returns to author link');
    hold='creator';await author.click();const pending=messages.filter(m=>m.kind==='creator').at(-1);
    await page.keyboard.press('Escape');await creator.waitFor({state:'hidden'});
    await page.evaluate(m=>window.Auralis.setPlatformExtras({...m,items:[{handle:'late',displayName:'Late'}]}),pending);
    assert(await creator.isHidden(),'late results do not reopen');hold=null;
    await author.click();await page.waitForSelector('.creator-post');
    await page.evaluate(provider=>window.Auralis.setPlatformConfiguration({providers:[{...provider,capabilities:['TrackSearch']}]}),provider);
    await creator.waitFor({state:'hidden'});assert.equal(await author.count(),0,'capability revocation removes author link');
    const beforeProfileOnly = messages.filter(m=>m.kind==='feed').length;
    profileOnly=true;
    await page.evaluate(provider=>window.Auralis.setPlatformConfiguration({providers:[{...provider,capabilities:['TrackSearch','CreatorProfile']}]}),provider);
    await author.click();
    await page.waitForFunction(()=>document.querySelector('.creator-profile h3')?.textContent==='Fixture musician');
    assert.equal(await creator.locator('.creator-profile + h3').count(),0,'profile-only source has no pretend activity heading');
    assert.equal(messages.filter(m=>m.kind==='feed').length,beforeProfileOnly,'profile-only source never invokes feed');
    await creator.evaluate(d=>Promise.allSettled(d.getAnimations().map(a=>a.finished)));
    assert(await creator.evaluate(d=>{const b=d.parentElement;return b.scrollWidth<=b.clientWidth+1;}),'long profile stays within viewport and wraps');
    await page.screenshot({path:path.join(output,`${theme}-${scale}-profile-only.png`)});
    await page.keyboard.press('Escape');await creator.waitFor({state:'hidden'});
    await page.evaluate(provider=>window.Auralis.setPlatformConfiguration({providers:[{...provider,capabilities:[...provider.capabilities,'CreatorSearch']}]}),provider);
    await page.locator('[data-creator-result="found-one"]').waitFor();
    const searchAvatar=page.locator('[data-creator-result="found-one"] .comment-avatar');
    await searchAvatar.locator('.creator-search-retry').waitFor();failSearchAvatar=false;await searchAvatar.click();
    await page.waitForFunction(()=>document.querySelector('[data-creator-result="found-one"] .comment-avatar img')?.naturalWidth>0);
    assert(await creator.isHidden(),'retrying a search portrait does not navigate to the creator');
    assert(artRequests.some(url=>url.endsWith('/avatar-search?retry=1')),'creator-search retry refreshes its own approved portrait');
    const searchCalls=messages.filter(m=>['searchPlatformCreators','platformSearchTracks'].includes(m.action)).length;
    await page.locator('[data-category="users"]').click();
    assert.equal(await page.locator('#platformTrackResults:visible').count(),0,'users view hides media without discarding it');
    assert(await page.locator('#creatorSearchSection').isVisible());
    await page.locator('[data-category="tracks"]').click();
    assert.equal(await page.locator('#creatorSearchSection:visible').count(),0);
    assert(await page.locator('#platformTrackResults').isVisible());
    await page.locator('[data-category="all"]').click();
    assert(await page.locator('#creatorSearchSection').isVisible());
    assert.equal(messages.filter(m=>['searchPlatformCreators','platformSearchTracks'].includes(m.action)).length,searchCalls,'category changes reuse both caches');
    assert(await page.locator('.platform-search-section #creatorSearchSection').count(),'creators and media share the selected provider section');
    await page.evaluate(()=>window.firstCreatorCard=document.querySelector('[data-creator-result="found-one"]'));
    // A delayed append keeps one keyboard target and never reconstructs old portraits.
    hold='creator-search';
    await page.locator('.creator-search-footer button').focus();
    await page.evaluate(()=>window.creatorMore=document.querySelector('.creator-search-footer button'));
    await page.locator('.creator-search-footer button').click();
    await page.waitForFunction(()=>document.querySelector('.creator-search-footer button')?.getAttribute('aria-disabled')==='true');
    assert(await page.evaluate(()=>window.creatorMore===document.activeElement),'pending pagination retains keyboard focus');
    const append=messages.filter(m=>m.action==='searchPlatformCreators').at(-1);
    const appendCount=messages.filter(m=>m.action==='searchPlatformCreators').length;
    await page.keyboard.press('Enter');
    assert.equal(messages.filter(m=>m.action==='searchPlatformCreators').length,appendCount,'pending target cannot duplicate a request');
    await page.evaluate(m=>window.Auralis.setCreatorSearchResult({...m,error:{message:'Append failed'}}),append);
    assert(await page.evaluate(()=>window.creatorMore===document.querySelector('.creator-search-footer button')),'Retry reuses the same control');
    hold=null;await page.getByRole('button',{name:'Retry',exact:true}).click();
    await page.locator('[data-creator-result="found-two"]').waitFor();
    assert.equal(await page.locator('.creator-search-card').count(),2,'creator results append independently of tracks');
    assert(await page.evaluate(()=>window.firstCreatorCard===document.querySelector('[data-creator-result="found-one"]')),'creator pagination retains original DOM/avatars');
    await page.locator('[data-action="platform-search-next"]').click();
    await page.locator('[data-platform-play-row="work-two"]').waitFor();
    assert(await page.evaluate(()=>window.firstCreatorCard===document.querySelector('[data-creator-result="found-one"]')),'independent track pagination retains the creator subtree');
    await page.locator('.platform-search-section').evaluate(n=>Promise.allSettled(n.getAnimations({subtree:true}).filter(a=>a.effect.getTiming().iterations!==Infinity).map(a=>a.finished)));
    assert.equal(await page.locator('.creator-search-card').first().evaluate(n=>getComputedStyle(n).borderTopColor),'rgba(0, 0, 0, 0)','resting creator cards have no visible outline');
    assert(await page.locator('.platform-track-row').evaluateAll(rows=>rows.every(row=>{
      const bounds=row.getBoundingClientRect();
      return [...row.querySelectorAll('.platform-mv-cell > button')].filter(b=>b.getClientRects().length).every(b=>{
        const r=b.getBoundingClientRect();return r.top>=bounds.top-1&&r.bottom<=bounds.bottom+1&&r.right<=bounds.right+1;
      });
    })),'capability buttons fit their row at every viewport/DPI');
    await page.screenshot({path:path.join(output,`${theme}-${scale}-creator-search.png`)});
    await page.locator('[data-creator-result="found-one"]').click();await page.waitForSelector('.creator-post');
    assert(messages.some(m=>m.kind==='creator'&&m.handle==='found-one'),'search result opens the precise creator handle');
    assert(!messages.some(m=>m.action==='playPlatformResult'&&m.handle==='found-one'),'creator cannot be played');
    await page.keyboard.press('Escape');await creator.waitFor({state:'hidden'});
    await page.waitForFunction(()=>document.activeElement?.dataset.creatorResult==='found-one');
    assert.equal(await page.locator('#searchInput').inputValue(),'Fixture','return retains query');
    assert.equal(await page.locator('.creator-search-card').count(),2,'return retains creator result pages');
    manyCreators=true;await page.locator('#searchInput').fill('Overview');
    await page.locator('[data-creator-result="overview-0"]').waitFor();
    assert.equal(await page.locator('.creator-search-card:visible').count(),3,'combined results keep room for media');
    await page.getByRole('button',{name:'View all users',exact:true}).click();
    assert.equal(await page.locator('.creator-search-card:visible').count(),5,'users category exposes all cached matches');
    assert.equal(await page.locator('#platformTrackResults:visible').count(),0);
    await page.locator('[data-category="all"]').click();
    assert.equal(await page.locator('.creator-search-card:visible').count(),3);
    emptyCreators=true;await page.locator('#searchInput').fill('No matching creators');
    await page.locator('#creatorSearchSection').waitFor({state:'hidden'});
    await page.locator('[data-category="users"]').click();
    assert(await page.locator('#creatorSearchSection').isVisible(),'Users still exposes its explicit empty state');
    assert.match(await page.locator('.creator-search-footer').textContent(),/No matching creators/);
    await page.locator('[data-category="all"]').click();
    assert(await page.locator('#creatorSearchSection').isHidden(),'empty author shelf is absent from combined results');
    hold='creator-search';await page.locator('#searchInput').fill('Failure is not empty');
    await page.waitForFunction(()=>document.querySelector('.creator-search-grid')?.getAttribute('aria-busy')==='true');
    await page.waitForFunction(()=>document.querySelector('.creator-search-footer button')?.getAttribute('aria-disabled')==='true');
    // Wait for the debounced request itself, not just its initial placeholder.
    for(let attempt=0;attempt<100&&messages.filter(m=>m.action==='searchPlatformCreators').at(-1)?.query!=='Failure is not empty';attempt++)await page.waitForTimeout(20);
    const failedSearch=messages.filter(m=>m.action==='searchPlatformCreators').at(-1);
    assert.equal(failedSearch?.query,'Failure is not empty','debounced creator request arrives within the fixture budget');
    await page.evaluate(m=>window.Auralis.setCreatorSearchResult({...m,error:{message:'Synthetic search failure'}}),failedSearch);
    assert(await page.locator('#creatorSearchSection').isVisible(),'errors remain visible and retryable in combined results');
    assert(await page.locator('.creator-search-footer').getByRole('button',{name:'Retry',exact:true}).isVisible());
    emptyCreators=false;
    hold='creator-search';await page.locator('#searchInput').fill('Changed');
    await page.waitForFunction(()=>document.querySelector('.creator-search-grid')?.getAttribute('aria-busy')==='true');
    const oldSearch=messages.filter(m=>m.action==='searchPlatformCreators').at(-1);
    await page.locator('#searchInput').fill('Final');
    await page.evaluate(m=>window.Auralis.setCreatorSearchResult({...m,items:[{handle:'stale',displayName:'Stale'}]}),oldSearch);
    assert.equal(await page.locator('[data-creator-result="stale"]').count(),0,'stale creator query cannot replace results');
    await page.evaluate(()=>window.Auralis.setPlatformConfiguration({providers:[]}));
    assert.equal(await page.locator('#creatorSearchSection:visible').count(),0,'no creator UI without a plugin');
    assert.deepEqual(errors,[]);
    await context.close();console.log(`PASS community UI ${theme} ${scale*100}%: creator, feed, replies, dates, pagination, keyboard, stale, revocation.`);
  }}finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
