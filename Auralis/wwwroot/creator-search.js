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
        for(const item of(payload.items||[]).slice(0,100))if(typeof item.handle==='string'&&!seen.has(item.handle)){items.push(item);seen.add(item.handle);}
        next=payload.nextPageHandle!==previous?payload.nextPageHandle:null;
      }
      paint();
    }
    function paint(){
      const section=document.getElementById('creatorSearchSection');if(!section)return;
      section.hidden=!allowed()||!query;if(section.hidden)return;
      const esc=api.escape;
      section.innerHTML='<div class="section-title-row"><h2>'+t('UP 主 / 作者')+'</h2><span data-i18n-skip>'+esc(api.providerName(providerId))+'</span></div><div class="creator-search-grid" aria-busy="'+!!pending+'"></div><div class="creator-search-footer" role="status"></div>';
      const grid=section.querySelector('.creator-search-grid');
      for(const item of items){
        const button=document.createElement('button');button.className='creator-search-card';button.dataset.creatorResult=item.handle;
        const url=window.AuralisCreatorFeed.art(item.avatarUrl);
        button.innerHTML='<span class="comment-avatar">'+(url?'<img src="'+esc(url)+'" alt="" loading="lazy" referrerpolicy="no-referrer">':esc(Array.from(item.displayName||'?')[0]))+'</span><span><strong data-i18n-skip>'+esc(item.displayName)+'</strong><small data-i18n-skip>'+esc(item.description)+'</small></span>';
        button.querySelector('img')?.addEventListener('error',e=>e.target.remove(),{once:true});
        button.addEventListener('click',()=>open({...item,kind:'online',providerId,title:item.displayName,artist:item.displayName}));grid.append(button);
      }
      const footer=section.querySelector('.creator-search-footer');
      if(pending||timer)footer.textContent=t('正在读取…');
      else if(error){const message=document.createElement('p');message.textContent=error;footer.append(message);addButton(t('重试'),()=>send(next));}
      else if(next)addButton(t('加载更多'),()=>send(next));
      else footer.textContent=t(items.length?'已加载当前可见的作者':'没有匹配的作者');
      function addButton(label,action){const b=document.createElement('button');b.className='secondary-button';b.textContent=label;b.addEventListener('click',action);footer.append(b);}
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
