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
    const page=await context.newPage(),errors=[],messages=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',r=>r.request().url().startsWith(origin)?r.continue():r.abort());
    await page.route('https://platform-art.auralis.local/**',r=>r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#497f89"/><circle cx="160" cy="90" r="60" fill="#c4ded9"/></svg>'}));
    const track={id:'work-one',handle:'work-one',kind:'online',providerId:'fixture.community',title:'Synthetic music performance',artist:'Fixture musician',album:'Fixture',durationSeconds:120,isPlayable:true,hasMusicVideo:true};
    const provider={id:track.providerId,name:'Fixture',capabilities:['TrackSearch','CreatorFeed','Comments','CommentReplies','VideoResolution'],configured:true};
    let hold=null,failReplies=false,failCommentPage=false,profileOnly=false;
    await page.exposeFunction('communityBridge',async m=>{
      messages.push(m);
      if(m.action==='searchPlatformCreators') {
        if(hold==='creator-search')return;
        await page.evaluate(payload=>window.Auralis.setCreatorSearchResult(payload),{...m,items:[{handle:m.pageHandle?'found-two':'found-one',displayName:'Search musician',description:'Creator search result, not a media item.',avatarUrl:'https://platform-art.auralis.local/avatar-one'}],nextPageHandle:m.pageHandle?null:'creator-next'});
        return;
      }
      if(m.action==='platformSearchTracks')await page.evaluate(payload=>window.Auralis.setPlatformSearchResult(payload),{...m,items:[track],totalCount:1});
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
        items=Array.from({length:20},(_,i)=>({handle:`root-${index*20+i}`,author:`Reader ${index*20+i}`,text:'A readable comment with a nested thread. '.repeat(3),publishedAt:'2026-09-12T03:01:00Z',replyCount:3,likeCount:5,avatarUrl:'https://platform-art.auralis.local/avatar-one'}));
        nextPageHandle=index<25?`comments-${index+1}`:null;
        if(failCommentPage&&index===1){failCommentPage=false;items=[];nextPageHandle=null;error={code:'InvalidResponse',message:'平台未返回有效评论分页，暂不能确认评论是否加载完整。请稍后重试。'};}
      }
      if(m.kind==='replies'){
        if(failReplies){error={message:'Temporary fixture error'};failReplies=false;}
        else{items=[{handle:m.pageHandle?'reply-two':'reply-one',author:'Reply author',replyToAuthor:'Another reader',text:'Reply to a reply <script>unsafe()</script>',publishedAt:'2026-09-12T03:06:00Z',avatarUrl:'https://platform-art.auralis.local/avatar-one'}];nextPageHandle=m.pageHandle?null:'replies-next';}
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
    await creator.evaluate(d=>Promise.allSettled(d.getAnimations().map(a=>a.finished)));
    await page.evaluate(()=>Promise.allSettled([...document.querySelectorAll('.topbar,.search-box')].flatMap(n=>n.getAnimations()).map(a=>a.finished)));
    await page.screenshot({path:path.join(output,`${theme}-${scale}-creator.png`)});
    assert.equal(await page.locator('dialog[open]').count(),0,'activity is a main page, not a modal');
    await creator.locator('[data-community-action="image"]').first().click();
    await page.locator('.creator-gallery[open] img').waitFor();
    await page.keyboard.press('Escape');await page.locator('.creator-gallery').waitFor({state:'hidden'});
    assert(await creator.isVisible(),'gallery returns to full activity page');
    await creator.locator('[data-community-action="comments"]').first().click();
    const comments=page.locator('dialog[aria-labelledby="mediaDialogTitle"]');await page.waitForSelector('.media-comments article');
    assert.equal(await comments.locator('.comment-meta time').first().getAttribute('datetime'),'2026-09-12T03:01:00.000Z');
    assert(messages.some(m=>m.kind==='comments'&&m.handle==='discussion-0'));
    failReplies=true;
    await comments.locator('[data-media-action="replies"]').first().click();
    await comments.locator('[data-media-action="more-replies"]').click();
    await page.waitForSelector('.comment-reply');await comments.locator('[data-media-action="more-replies"]').click();
    await page.waitForFunction(()=>document.querySelectorAll('.comment-reply').length===2);
    assert.equal(await comments.locator('.comment-reply time').count(),2);
    assert.equal(await comments.locator('script').count(),0);
    assert.match(await comments.locator('.comment-reply').first().textContent(),/Another reader/);
    await page.screenshot({path:path.join(output,`${theme}-${scale}-replies.png`)});
    await comments.locator('[data-sort="newest"]').click();await page.waitForSelector('.media-comments article');
    assert(messages.some(m=>m.kind==='comments'&&m.sort==='newest'));
    if(theme==='light'&&scale===1){
      failCommentPage=true;
      await comments.locator('.media-dialog-body').evaluate(n=>{n.scrollTop=n.scrollHeight;});
      await comments.locator('.media-inline-error').waitFor();
      assert.equal(await comments.locator('.media-comments > article').count(),20,'invalid page preserves already read comments');
      await page.waitForFunction(()=>document.querySelector('.media-inline-error')?.textContent.includes('Loading is not confirmed complete'));
      await comments.locator('[data-media-action="retry-extras"]').click();
      for(let i=1;i<=26;i++){
        await comments.locator('.media-dialog-body').evaluate(n=>{n.scrollTop=n.scrollHeight;});
        await page.waitForFunction(count=>document.querySelectorAll('.media-comments > article').length>=count,i*20);
      }
      assert(await comments.locator('.media-comments > article').count()>500,'comments are not capped at 500');
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
    await page.locator('.creator-search-footer button').click();
    await page.locator('[data-creator-result="found-two"]').waitFor();
    assert.equal(await page.locator('.creator-search-card').count(),2,'creator results append independently of tracks');
    await page.screenshot({path:path.join(output,`${theme}-${scale}-creator-search.png`)});
    await page.locator('[data-creator-result="found-one"]').click();await page.waitForSelector('.creator-post');
    assert(messages.some(m=>m.kind==='creator'&&m.handle==='found-one'),'search result opens the precise creator handle');
    assert(!messages.some(m=>m.action==='playPlatformResult'&&m.handle==='found-one'),'creator cannot be played');
    await page.keyboard.press('Escape');await creator.waitFor({state:'hidden'});
    await page.waitForFunction(()=>document.activeElement?.dataset.creatorResult==='found-one');
    assert.equal(await page.locator('#searchInput').inputValue(),'Fixture','return retains query');
    assert.equal(await page.locator('.creator-search-card').count(),2,'return retains creator result pages');
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
