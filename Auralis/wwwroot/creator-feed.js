/* Generic creator browsing; no platform endpoints or raw provider identities. */
window.AuralisCreatorFeed = (() => {
  const t = text => window.AuralisI18n?.t(text) || text;
  const art = url => typeof url === 'string' && /^https:\/\/platform-art\.auralis\.local\/[a-z0-9-]+$/i.test(url) ? url : '';
  function date(value, escape) {
    if (!value) return '';
    const instant = new Date(value);
    if (!Number.isFinite(instant.getTime())) return '';
    return '<time datetime="'+escape(instant.toISOString())+'" title="'+escape(instant.toLocaleString(document.documentElement.lang))+'">'+escape(instant.toLocaleString(document.documentElement.lang,{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'}))+'</time>';
  }
  function create(api, openComments) {
    const esc=api.escape,page=document.createElement('section');
    page.className='page-view creator-feed-page';page.setAttribute('aria-labelledby','creatorFeedTitle');
    let track=null,profile=null,next=null,error='',request=null,serial=0,observer=null,context='',active=false;
    const posts=new Map();
    const hasFeed=()=>api.capabilities(track).includes('CreatorFeed');
    const allowed=()=>track?.kind==='online'&&(hasFeed()||api.capabilities(track).includes('CreatorProfile'));
    const visible=()=>active&&api.state.currentPage==='creator';
    function cancel(){
      if(request){api.post('cancelPlatformExtras',{kind:request.kind});clearTimeout(request.timer);request=null;}
      observer?.disconnect();observer=null;
    }
    function close(){cancel();gallery.close(true);active=false;api.leaveCreatorPage();}
    function send(kind,pageHandle=null){
      if(request||!allowed()||!visible())return;
      const handle=kind==='creator'?track.handle:profile.handle;
      request={kind,handle,requestId:++serial,pageHandle};const current=request;
      current.timer=setTimeout(()=>{api.post('cancelPlatformExtras',{kind});receive({...current,error:{message:t('读取超时，请重试。')}});},35000);
      error='';renderFooter();api.post('requestPlatformExtras',{kind,handle,requestId:current.requestId,pageHandle});
    }
    function renderFooter(){
      observer?.disconnect();const footer=page.querySelector('.creator-footer');if(!footer)return;
      footer.hidden=!!profile&&!hasFeed()&&!error&&!request;
      footer.innerHTML=request?'<p>'+t('正在读取…')+'</p>':error?'<p>'+esc(error)+'</p><button class="secondary-button" data-community-action="more">'+t('重试')+'</button>':
        next?'<button class="secondary-button" data-community-action="more">'+t('加载更多')+'</button>':'<p>'+t(posts.size?'已加载当前可见的动态':'暂无可见动态')+'</p>';
      if(request)footer.replaceChildren(window.AuralisLoading.create(t('正在读取…'),{initial:!posts.size,reduced:api.reduced()}));
      page.querySelector('.creator-posts').setAttribute('aria-busy',String(!!request));
      if(!request&&!error&&next&&visible()){
        observer=new IntersectionObserver(entries=>{if(entries.some(e=>e.isIntersecting))send('feed',next);},{root:api.content,rootMargin:'0px 0px 120px'});observer.observe(footer);
      }
    }
    function avatar(name,url){const image=art(url);return '<span class="comment-avatar"><span>'+esc(Array.from(name||'?')[0])+'</span>'+(image?'<img src="'+esc(image)+'" alt="" loading="lazy" referrerpolicy="no-referrer">':'')+'</span>';}
    function imageFallbacks(node){
      for(const image of node.querySelectorAll('img')){
        const avatar=image.closest('.comment-avatar'),tile=image.closest('.creator-image-button');
        if(avatar){
          const source=art(image.getAttribute('src'));let retries=0;
          const failed=()=>{
            image.remove();
            const retry=document.createElement('button');retry.type='button';retry.className='creator-avatar-retry';retry.textContent='↻';
            retry.setAttribute('aria-label',t('重试头像'));retry.title=t('重试头像');
            retry.addEventListener('click',event=>{
              event.stopPropagation();retries++;retry.remove();
              image.src=window.AuralisLoading.retryImageUrl(source,retries);avatar.append(image);
            });avatar.append(retry);
          };
          image.addEventListener('error',failed);
        }else if(tile){
          const source=art(image.getAttribute('src'));let retries=0;
          image.addEventListener('error',()=>{
            image.hidden=true;tile.classList.add('image-unavailable');tile.dataset.imageSource=source;
            tile.setAttribute('aria-label',t('图片加载失败，点击重试'));
            if(!tile.querySelector('.creator-image-retry')){
              const label=document.createElement('span');label.className='creator-image-retry';label.textContent=t('图片加载失败，点击重试');tile.append(label);
            }
          });
          image.addEventListener('load',()=>{
            image.hidden=false;tile.classList.remove('image-unavailable');delete tile.dataset.imageSource;
            tile.querySelector('.creator-image-retry')?.remove();tile.setAttribute('aria-label',t('查看图片')+' '+(Number(tile.dataset.index)+1));
          });
          tile.addEventListener('click',event=>{
            if(!tile.classList.contains('image-unavailable'))return;
            event.stopImmediatePropagation();event.preventDefault();
            retries++;tile.classList.remove('image-unavailable');tile.querySelector('.creator-image-retry')?.remove();
            image.src=window.AuralisLoading.retryImageUrl(source,retries);image.hidden=false;
          },true);
        }
      }
    }
    function receive(payload){
      if(!['creator','feed'].includes(payload?.kind))return false;
      if(!request||request.kind!==payload.kind||request.handle!==payload.handle||request.requestId!==payload.requestId||!visible()||!allowed()||context!==api.pageContext(track))return true;
      const previous=request.pageHandle;clearTimeout(request.timer);request=null;
      if(payload.error){error=payload.error.message||t('此平台暂不支持这项功能');renderFooter();return true;}
      if(payload.kind==='creator'){
        profile=payload.items?.[0];
        if(!profile?.handle){error=t('作者主页暂不可用');renderFooter();return true;}
        page.querySelector('.creator-profile').innerHTML=avatar(profile.displayName,profile.avatarUrl)+'<div><h3 data-i18n-skip>'+esc(profile.displayName)+'</h3><p data-i18n-skip>'+esc(profile.description)+'</p></div>';
        imageFallbacks(page.querySelector('.creator-profile'));
        if(hasFeed())send('feed');else renderFooter();return true;
      }
      next=payload.nextPageHandle!==previous?payload.nextPageHandle:null;
      const list=page.querySelector('.creator-posts');
      for(const post of(payload.items||[]).slice(0,100)){
        if(!post.handle||posts.has(post.handle))continue;posts.set(post.handle,post);
        const card=document.createElement('article');card.className='creator-post';
        const images=(post.images||[]).map(art).filter(Boolean).slice(0,9);
        card.innerHTML='<header class="creator-post-author">'+avatar(profile.displayName,profile.avatarUrl)+'<div><strong data-i18n-skip>'+esc(profile.displayName)+'</strong>'+date(post.publishedAt,esc)+'</div></header><div data-i18n-skip>'+(post.title?'<h3>'+esc(post.title)+'</h3>':'')+'<p>'+esc(post.text)+'</p></div>'+
          (images.length?'<div class="creator-post-images">'+images.map((u,i)=>'<button class="creator-image-button" data-community-action="image" data-post="'+esc(post.handle)+'" data-index="'+i+'" aria-label="'+esc(t('查看图片'))+' '+(i+1)+'"><img src="'+esc(u)+'" alt="'+esc(t('动态图片'))+' '+(i+1)+'" loading="lazy" decoding="async" referrerpolicy="no-referrer"></button>').join('')+'</div>':'')+
          '<footer>'+(post.discussionHandle&&api.capabilities(track).includes('Comments')?'<button class="secondary-button" data-community-action="comments" data-post="'+esc(post.handle)+'">'+t('查看评论')+(Number.isFinite(post.commentCount)?' · '+Math.max(0,post.commentCount):'')+'</button>':'<small>'+t('此动态未提供评论入口')+'</small>')+'</footer>';
        imageFallbacks(card);list.append(card);
      }
      renderFooter();return true;
    }
    const gallery=window.AuralisPageGallery.create(api,'creator-gallery');
    function open(item){
      if(!(api.capabilities(item).includes('CreatorFeed')||api.capabilities(item).includes('CreatorProfile')))return;
      cancel();gallery.close(true);track=item;context=api.pageContext(track);profile=null;posts.clear();next=null;error='';active=true;
      page.classList.toggle('profile-only',!hasFeed());
      page.innerHTML='<header class="page-header creator-page-header"><button class="settings-back-button" data-community-action="close" aria-label="'+t('返回')+'">←</button><div><h1 id="creatorFeedTitle" tabindex="-1">'+t('作者主页')+'</h1><p data-i18n-skip>'+esc(api.providerName(track.providerId))+'</p></div></header><section class="creator-profile"></section>'+
        (hasFeed()?'<h2>'+t('作者动态')+'</h2><p class="community-availability">'+t('仅显示平台允许访问的内容；已删除或受限内容可能不可见。')+'</p>':'')+'<div class="creator-posts"></div><div class="creator-footer" role="status"></div>';
      api.enterCreatorPage();send('creator');
    }
    function render(){if(!active)return;api.content.replaceChildren(page);page.classList.remove('is-leaving');renderFooter();}
    page.addEventListener('click',event=>{
      const button=event.target.closest('[data-community-action]');if(!button)return;
      if(button.dataset.communityAction==='close')close();
      if(button.dataset.communityAction==='more')send(profile?'feed':'creator',next);
      const post=posts.get(button.dataset.post);
      if(button.dataset.communityAction==='comments'&&post?.discussionHandle)openComments({...track,handle:post.discussionHandle,title:post.title||post.text.slice(0,100)},false);
      if(button.dataset.communityAction==='image'&&post)gallery.open((post.images||[]).map(art).filter(Boolean),Number(button.dataset.index)||0);
    });
    document.addEventListener('keydown',event=>{
      if(!visible()||document.querySelector('dialog[open]')||document.querySelector('#nowPlayingOverlay.open'))return;
      if(event.key==='Escape'){event.preventDefault();event.stopImmediatePropagation();close();}
    },true);
    return {open,receive,render,sync:()=>{
      if(active&&(!allowed()||context!==api.pageContext(track))){close();}
      else if(active&&api.state.currentPage!=='creator'){cancel();active=false;gallery.close(true);}
    }};
  }
  return {create,date,art};
})();
