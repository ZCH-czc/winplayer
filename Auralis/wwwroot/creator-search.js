/* Capability-driven creator search. Results are not playable tracks and never enter queues. */
window.AuralisCreatorSearch = (() => {
  const t = s => window.AuralisI18n?.t(s) || s;
  function create(api,open) {
    let key='',query='',providerId='',items=[],next=null,error='',pending=null,serial=0,timer=null,searched=false,revision='';
    const active=()=>api.state.currentPage==='search';
    const current=()=>({kind:'online',providerId:api.state.platformSearch.providerId});
    const allowed=()=>api.capabilities(current()).includes('CreatorSearch');
    function cancel(){clearTimeout(timer);timer=null;if(pending){if(!items.length)searched=false;clearTimeout(pending.timer);api.post('cancelPlatformExtras',{kind:'creator-search'});pending=null;}}
    function reset(){cancel();items=[];next=null;error='';searched=false;}
    function sync(){
      if(!active()){cancel();return;}
      const q=api.state.query.trim(),p=api.state.platformSearch.providerId,r=api.pageContext(current());
      const k=JSON.stringify([q,p,r,allowed()]);
      if(k!==key){reset();key=k;query=q;providerId=p;revision=r;mount();}
    }
    function send(pageHandle=null){
      if(pending||!active()||!allowed()||!query)return;
      clearTimeout(timer);timer=null;error='';searched=true;
      pending={providerId,query,requestId:++serial,pageHandle};const request=pending;
      request.timer=setTimeout(()=>{api.post('cancelPlatformExtras',{kind:'creator-search'});receive({...request,error:{message:t('读取超时，请重试。')}});},35000);
      paint();api.post('searchPlatformCreators',{providerId,query,requestId:request.requestId,pageHandle});
    }
    function receive(payload){
      if(!pending||payload.requestId!==pending.requestId||payload.query!==query||payload.providerId!==providerId||
          (payload.pageHandle||null)!==(pending.pageHandle||null)||!active()||!allowed()||revision!==api.pageContext(current()))return;
      const previous=pending.pageHandle;clearTimeout(pending.timer);pending=null;
      if(payload.error){error=payload.error.message||t('作者搜索暂不可用');}
      else {
        const seen=new Set(items.map(i=>i.handle));
        for(const item of(payload.items||[]).slice(0,100))if(items.length<200&&typeof item.handle==='string'&&!seen.has(item.handle)){items.push(item);seen.add(item.handle);}
        next=payload.nextPageHandle!==previous?payload.nextPageHandle:null;
      }
      paint();
    }
    function paint(){
      const section=document.getElementById('creatorSearchSection');if(!section)return;
      // A completed empty overview has no author shelf. Loading and failures remain actionable,
      // and the Users category still explains an empty result rather than becoming a blank page.
      const category=api.state.platformSearch.category||'all';
      section.hidden=!allowed()||!query||category==='tracks'||
        (category==='all'&&searched&&!pending&&!timer&&!error&&!items.length);if(section.hidden)return;
      section.dataset.category=api.state.platformSearch.category||'all';
      const esc=api.escape;
      if(section.dataset.searchKey!==key||!section.querySelector('.creator-search-grid')){
        section.dataset.searchKey=key;
        section.innerHTML='<div class="section-title-row"><h2>'+t('UP 主 / 作者')+'</h2><span data-i18n-skip>'+esc(api.providerName(providerId))+'</span></div><div class="creator-search-grid"></div><div class="creator-search-footer" role="status"></div>';
      }
      const grid=section.querySelector('.creator-search-grid');
      grid.setAttribute('aria-busy',String(!!pending||!!timer));
      const added=[];
      for(const item of items.slice(grid.children.length)){
        const button=document.createElement('button');button.className='creator-search-card';button.dataset.creatorResult=item.handle;
        const url=window.AuralisCreatorFeed.art(item.avatarUrl);
        button.innerHTML='<span class="comment-avatar"><span>'+esc(Array.from(item.displayName||'?')[0])+'</span>'+(url?'<img src="'+esc(url)+'" alt="" loading="lazy" decoding="async" referrerpolicy="no-referrer">':'')+'</span><span><strong data-i18n-skip>'+esc(item.displayName)+'</strong><small data-i18n-skip>'+esc(item.description)+'</small></span>';
        const avatar=button.querySelector('.comment-avatar');let failed=false,retries=0;
        function retryAvatar(){
          if(!failed||!url)return;failed=false;retries++;avatar.querySelector('.creator-search-retry')?.remove();
          button.removeAttribute('aria-keyshortcuts');button.removeAttribute('title');
          const image=document.createElement('img');image.alt='';image.loading='lazy';image.decoding='async';image.referrerPolicy='no-referrer';
          image.addEventListener('error',()=>{
            image.remove();failed=true;button.setAttribute('aria-keyshortcuts','R');button.title=t('头像加载失败，点击头像或按 R 重试');
            const mark=document.createElement('span');mark.className='creator-search-retry';mark.textContent='↻';mark.setAttribute('aria-hidden','true');avatar.append(mark);
          },{once:true});
          image.src=window.AuralisLoading.retryImageUrl(url,retries);window.AuralisLoading.image(image);avatar.append(image);
        }
        const initial=avatar.querySelector('img');if(initial){
          initial.addEventListener('error',()=>{
            initial.remove();failed=true;button.setAttribute('aria-keyshortcuts','R');button.title=t('头像加载失败，点击头像或按 R 重试');
            const mark=document.createElement('span');mark.className='creator-search-retry';mark.textContent='↻';mark.setAttribute('aria-hidden','true');avatar.append(mark);
          },{once:true});window.AuralisLoading.image(initial);
        }
        button.addEventListener('click',event=>{if(failed&&avatar.contains(event.target)){event.stopImmediatePropagation();retryAvatar();}},true);
        button.addEventListener('keydown',event=>{if(failed&&event.key.toLowerCase()==='r'){event.preventDefault();event.stopPropagation();retryAvatar();}});
        button.addEventListener('click',()=>open({...item,kind:'online',providerId,title:item.displayName,artist:item.displayName}));grid.append(button);added.push(button);
      }
      const footer=section.querySelector('.creator-search-footer');
      window.AuralisPageMotion?.reveal(added,api.reduced());
      let text='',label='',action=null;
      if(pending||timer)text=t('正在读取…');
      else if(error){text=error;label=t('重试');action=()=>send(next);}
      else if(section.dataset.category==='all'&&items.length>3){label=t('查看全部用户');action=()=>document.querySelector('[data-action="search-result-category"][data-category="users"]')?.click();}
      else if(items.length>=200)text=t('已达到页面显示上限，请重新打开页面。');
      else if(next){label=t('加载更多');action=()=>send(next);}
      else text=t(items.length?'已加载当前可见的作者':'没有匹配的作者');
      window.AuralisLoading.continuation(footer,{pending:!!pending||!!timer,initial:!items.length,text,label,action,
        kind:error?'retry':section.dataset.category==='all'&&items.length>3?'browse':'more',reduced:api.reduced()});
    }
    function mount(){
      if(!active())return;
      const k=JSON.stringify([api.state.query.trim(),api.state.platformSearch.providerId,api.pageContext(current()),allowed()]);
      if(k!==key){sync();return;}
      if(allowed()&&query&&!searched&&!timer)timer=setTimeout(()=>send(),300);
      paint();
    }
    return {mount,sync,receive};
  }
  return {create};
})();
