/* Host-owned Fluent renderer. Plugins supply bounded plain data, never HTML/CSS/JS. */
window.AuralisPluginPages = (() => {
  const t = s => window.AuralisI18n?.t(s) || s;
  function create(api, openComments) {
    const dialog=document.createElement('dialog'),page=document.createElement('section');
    dialog.className='media-hub-dialog plugin-page-dialog';dialog.setAttribute('aria-labelledby','pluginPageTitle');
    page.className='page-view plugin-page-route';page.setAttribute('aria-labelledby','pluginPageTitle');document.body.append(dialog);
    const gallery=window.AuralisPageGallery.create(api),art=window.AuralisPageGallery.art;
    let track,entry,origin,pending,serial=0,history=[],closing,contextKey,main=false,active=false,observer,next,collection,emptyPages=0;
    const cardsSeen=new Set();let queryDirty=false;
    const navigation=document.getElementById('pluginNavigation');let navigationSignature='';
    const entries=item=>api.capabilities(item).includes('Pages')?(api.pageEntries?.(item)||[]):[];
    const label=e=>document.documentElement.lang.startsWith('en')?e.labelEn:e.label;
    const allowed=()=>contextKey===api.pageContext?.(track)&&(entry?entries(track).some(e=>JSON.stringify(e)===JSON.stringify(entry)):entries(track).length>0);
    const surface=()=>main?page:dialog;
    const visible=()=>active&&(main?api.state.currentPage==='plugin':dialog.open);
    const scroller=()=>main?api.content:dialog.querySelector('.media-dialog-body');
    const node=(tag,text,className)=>{const n=document.createElement(tag);if(text!=null)n.textContent=text;if(className)n.className=className;return n;};
    const button=(text,callback)=>{const n=node('button',text,'secondary-button');n.type='button';n.addEventListener('click',callback);return n;};
    const validAction=a=>typeof a?.handle==='string'&&/^page-[a-f0-9]{32}$/.test(a.handle);
    function cancel(){
      observer?.disconnect();observer=null;
      if(pending){clearTimeout(pending.timer);pending=null;api.post('cancelPlatformExtras',{kind:'plugin-page'});}
    }
    function close(immediate=false){
      cancel();gallery.close(true);
      if(!active)return;active=false;
      if(main){api.leaveCreatorPage();return;}
      if(!dialog.open)return;
      dialog.getAnimations().forEach(a=>a.cancel());closing?.cancel();closing=null;
      const finish=()=>{dialog.close();origin?.isConnected&&origin.focus({preventScroll:true});};
      if(immediate||api.reduced()){finish();return;}
      closing=dialog.animate([{opacity:1,transform:'none'},{opacity:0,transform:'translateY(6px)'}],{duration:140,easing:'ease-in'});
      const current=closing;current.finished.then(()=>{if(closing===current){finish();current.cancel();closing=null;}}).catch(()=>{});
    }
    function status(text='',retry){
      observer?.disconnect();observer=null;
      const feedback=surface().querySelector('.plugin-page-status');if(!feedback)return;
      feedback.replaceChildren(node('p',text));
      surface().querySelector('.media-dialog-body').setAttribute('aria-busy',String(!!pending));
      for(const b of surface().querySelectorAll('[data-page-navigation]'))b.setAttribute('aria-disabled',String(!!pending&&b.getAttribute('role')!=='tab'));
      if(retry)feedback.append(button(t('重试'),()=>read(retry.navigation,retry.mode,retry.inputValues)));
      else if(!pending&&!queryDirty&&validAction(next)&&cardsSeen.size<1000){
        const more=button(next.label||t('加载更多'),()=>{emptyPages=0;read(next.handle,'append');});more.dataset.i18nSkip='';feedback.append(more);
        if(visible()&&emptyPages<3){
          observer=new IntersectionObserver(rows=>{
            if(rows.some(r=>r.isIntersecting)&&visible()&&!pending&&
              !document.querySelector('dialog[open]:not(.plugin-page-dialog)')&&!document.querySelector('#nowPlayingOverlay.open'))read(next.handle,'append');
          },{root:scroller(),rootMargin:'0px 0px 120px'});observer.observe(feedback);
        }
      }else if(!pending&&cardsSeen.size>=1000)feedback.append(node('p',t('已达到页面显示上限，请重新打开页面。')));
    }
    function read(navigation=null,mode='push',inputValues){
      if(pending||closing||!entry||!allowed()||!visible())return;
      if(history.length&&mode!=='append'&&mode!=='back'){
        const current=history.at(-1),buttons=[...surface().querySelectorAll('.plugin-page-content button')],focused=document.activeElement;
        current.scroll=scroller().scrollTop;
        if(buttons.includes(focused))current.focus={index:buttons.indexOf(focused),text:focused.textContent};
      }
      observer?.disconnect();
      pending={requestId:++serial,handle:track.handle,providerId:track.providerId,entryId:entry.id,navigation,mode,inputValues};const current=pending;
      current.timer=setTimeout(()=>{if(pending===current){cancel();status(t('读取超时，请重试。'),current);}},35000);
      status(t('正在读取…'));
      const context=entry.placement==='global'?{providerId:track.providerId}:{handle:track.handle};
      api.post(entry.placement==='global'?'readPluginGlobalPage':'readPluginPage',{...context,entryId:entry.id,requestId:current.requestId,navigationHandle:navigation,language:document.documentElement.lang,...(inputValues?{inputValues}:{})});
    }
    function tabs(list,parent){
      if(!list?.length)return;
      const row=node('div',null,'plugin-page-tabs');row.setAttribute('role','tablist');row.setAttribute('aria-label',t('页面分区'));
      list.forEach((tab,index)=>{
        const b=button(tab.action.label,()=>{
          if(pending)cancel();
          if(tab.selected){status();return;}
          read(tab.action.handle,'tab');
        });
        b.id='pluginTab-'+index;b.dataset.i18nSkip='';b.dataset.pageNavigation='';
        b.setAttribute('role','tab');b.setAttribute('aria-selected',String(tab.selected));b.tabIndex=tab.selected?0:-1;
        b.setAttribute('aria-controls','pluginTabPanel');row.append(b);
      });
      row.addEventListener('keydown',e=>{
        const buttons=[...row.children],index=buttons.indexOf(document.activeElement);
        if(index<0||!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;
        e.preventDefault();e.stopPropagation();
        const target=e.key==='Home'?0:e.key==='End'?buttons.length-1:(index+(e.key==='ArrowRight'?1:-1)+buttons.length)%buttons.length;
        buttons.forEach((b,i)=>b.tabIndex=i===target?0:-1);buttons[target].focus();
      });parent.append(row);
    }
    function actions(list,parent){
      for(const a of(list||[]).slice(0,8)){
        if(!validAction(a))continue;const b=button(String(a.label||''),()=>read(a.handle));b.dataset.i18nSkip='';b.dataset.pageNavigation='';parent.append(b);
      }
    }
    function image(url,className=''){
      if(!art(url))return null;
      const n=node('img',null,className);n.src=url;n.alt='';n.loading='lazy';n.decoding='async';n.referrerPolicy='no-referrer';
      n.addEventListener('error',()=>n.remove(),{once:true});return n;
    }
    function date(value){
      const d=new Date(value);if(!value||!Number.isFinite(d.getTime()))return null;
      const n=node('time',d.toLocaleString(document.documentElement.lang));n.dateTime=d.toISOString();return n;
    }
    function card(item){
      const result=node('article',null,'plugin-page-card');
      if(item.author){
        const header=node('header',null,'plugin-card-author'),avatar=node('span',null,'comment-avatar');
        avatar.textContent=Array.from(item.author)[0]||'?';const picture=image(item.avatar);if(picture){avatar.replaceChildren(picture);picture.addEventListener('error',()=>avatar.textContent=Array.from(item.author)[0]||'?',{once:true});}
        const text=node('div');text.append(node('strong',item.author));const when=date(item.publishedAt);if(when)text.append(when);header.append(avatar,text);result.append(header);
      }else{const when=date(item.publishedAt);if(when)result.append(when);}
      const single=image(item.image);if(single)result.append(single);
      if(item.title)result.append(node('h3',item.title));
      if(item.text)result.append(node('p',item.text));
      const images=(item.images||[]).filter(art).slice(0,9);
      if(images.length){
        const group=node('div',null,'plugin-card-images');
        images.forEach((url,index)=>{
          const b=button('',()=>gallery.open(images,index));b.className='plugin-image-button';b.setAttribute('aria-label',t('查看图片')+' '+(index+1));
          b.append(image(url));group.append(b);
        });result.append(group);
      }
      const row=node('div',null,'plugin-page-actions');actions(item.actions,row);
      if(/^community-[a-f0-9]{32}$/.test(item.discussionHandle||'')&&api.capabilities(track).includes('Comments')){
        const count=Number.isSafeInteger(item.commentCount)?' · '+Math.max(0,item.commentCount):'';
        row.append(button(t('查看评论')+count,()=>openComments?.({...track,handle:item.discussionHandle,title:item.title||item.text?.slice(0,100)||track.title},false)));
      }
      if(item.media && item.media.providerId===track.providerId && /^track-[a-f0-9]{24}$/.test(item.media.handle||'') && api.capabilities(track).includes('StreamResolution')) {
        const media=api.normalize(item.media,track.providerId);
        const details=node('div',null,'plugin-card-media');
        const cover=image(media.coverUrl);if(cover&&!single&&!images.length)details.append(cover);
        const text=node('div');if(media.title!==item.title)text.append(node('strong',media.title));text.append(node('span',media.artist+' · '+api.formatTime(media.durationSeconds)));
        details.append(text);result.append(details);
        const select=play=>{if(visible()&&allowed())api.selectPageMedia?.(media,play);};
        const play=button(t('播放'),()=>select(true)),add=button(t('加入队列'),()=>select(false));
        play.dataset.pageMedia='play';add.dataset.pageMedia='enqueue';
        play.setAttribute('aria-label',t('播放')+' · '+media.title);add.setAttribute('aria-label',t('加入队列')+' · '+media.title);
        play.disabled=add.disabled=media.unavailable;
        if(media.unavailable)details.append(node('span',t('当前不可播放'),'plugin-media-availability'));
        else if(media.previewOnly)details.append(node('span',t('仅试听'),'plugin-media-availability'));
        row.prepend(play,add);
      }
      result.append(row);return result;
    }
    function receive(payload){
      if(!pending||!visible()||!allowed()||payload?.requestId!==pending.requestId||
        (entry.placement==='global'?payload.providerId!==pending.providerId||payload.handle!=null:payload.handle!==pending.handle)||payload.entryId!==pending.entryId)return;
      const request=pending;clearTimeout(request.timer);pending=null;
      if(payload.error||!payload.page){status(t(payload.error?.message||'无法读取插件页面，请重试。'),request);return;}
      const doc=payload.page,append=request.mode==='append';
      const sections=doc.tabs||[];
      if(doc.query!=null && (append || !window.AuralisPageQuery?.valid(doc.query)) ||
        !Array.isArray(sections)||sections.length>8||sections.some(t=>!validAction(t.action)||typeof t.selected!=='boolean')||
        (sections.length>0&&(sections.length<2||sections.filter(t=>t.selected).length!==1))||(append&&sections.length>0)||
        !Array.isArray(doc.cards)||doc.cards.length>100||!['cards','list'].includes(doc.layout)||
        !!doc.append!==append||(append&&(!collection||collection!==doc.collectionHandle))){
        status(t('插件页面格式不受支持或超出限制。'),request);return;
      }
      if(!append){
        if(request.mode==='back')history.pop();
        else if(['tab','query'].includes(request.mode)&&history.length)history[history.length-1]={navigation:request.navigation,inputValues:request.inputValues,scroll:0};
        else if(request.mode!=='refresh')history.push({navigation:request.navigation,inputValues:request.inputValues,scroll:0});
        if(history.length>64)history.shift();
        surface().querySelector('#pluginPageTitle').textContent=String(doc.title||label(entry));
        const content=surface().querySelector('.plugin-page-content');content.replaceChildren();
        const intro=node('div',null,'plugin-page-intro'),picture=image(doc.image);if(picture)intro.append(picture);
        if(doc.description)intro.append(node('p',doc.description,'plugin-page-description'));if(intro.childNodes.length)content.append(intro);
        tabs(sections,content);
        if(doc.query)content.append(window.AuralisPageQuery.create(doc.query,(handle,values)=>{
          cancel();read(handle,'query',values);
        },()=>{queryDirty=true;cancel();status();}));
        const panel=node('div');panel.id='pluginTabPanel';
        if(sections.length){panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby','pluginTab-'+sections.findIndex(t=>t.selected));panel.tabIndex=0;}
        const grid=node('div',null,'plugin-page-cards '+(doc.layout==='list'?'is-list':''));panel.append(grid);
        const footer=node('div',null,'plugin-page-actions');actions(doc.actions,footer);panel.append(footer);content.append(panel);
        cardsSeen.clear();collection=doc.collectionHandle;emptyPages=0;queryDirty=false;
      }
      const grid=surface().querySelector('.plugin-page-cards');let added=0;
      for(const item of doc.cards){
        if(cardsSeen.size>=1000)break;
        const key=item.handle||('legacy-'+cardsSeen.size);if(cardsSeen.has(key))continue;
        cardsSeen.add(key);grid.append(card(item));added++;
      }
      emptyPages=append&&added===0?emptyPages+1:0;next=validAction(doc.next)?doc.next:null;
      status(cardsSeen.size||doc.description||surface().querySelector('.plugin-page-intro')?'':t('暂无内容'));
      const back=surface().querySelector('[data-plugin-back]');back.hidden=!main&&history.length<2;
      if(!append){scroller().scrollTop=request.mode==='back'?(history.at(-1)?.scroll||0):0;
        (request.mode==='tab'?surface().querySelector('[role="tab"][aria-selected="true"]'):null)?.focus({preventScroll:true});
        if(request.mode==='query')surface().querySelector('.plugin-query-text,.plugin-query-submit')?.focus({preventScroll:true});
        else if(request.mode!=='tab'){
          const saved=request.mode==='back'?history.at(-1)?.focus:null;
          const candidate=saved?[...surface().querySelectorAll('.plugin-page-content button')][saved.index]:null;
          (candidate&&candidate.textContent===saved.text?candidate:surface().querySelector('#pluginPageTitle')).focus({preventScroll:true});
        }
        else if(!api.reduced())surface().querySelector('#pluginTabPanel')?.animate(
          [{opacity:.5,transform:'translateY(4px)'},{opacity:1,transform:'none'}],{duration:160,easing:'ease-out'});
      }
    }
    function open(item,pageEntry){
      if(!pageEntry||!entries(item).some(e=>e.id===pageEntry.id))return;
      cancel();gallery.close(true);closing?.cancel();closing=null;
      if(!active)origin=document.activeElement;
      if(dialog.open)dialog.close();
      track=item;entry=pageEntry;history=[];cardsSeen.clear();next=null;collection=null;emptyPages=0;queryDirty=false;active=true;main=entry.presentation==='page';
      contextKey=api.pageContext?.(track);
      dialog.getAnimations().forEach(a=>a.cancel());dialog.replaceChildren();page.replaceChildren();
      const header=node('header',null,main?'page-header plugin-page-header':'');
      const back=button(t('返回'),()=>{if(pending)cancel();if(history.length>1){const prior=history[history.length-2];read(prior.navigation,'back',prior.inputValues);}else if(main)close();});
      back.dataset.pluginBack='';back.hidden=!main;
      const title=node(main?'h1':'h2',label(entry));title.id='pluginPageTitle';title.tabIndex=-1;title.dataset.i18nSkip='';
      const exit=button('',()=>close());exit.className='icon-button';exit.setAttribute('aria-label',t('关闭'));exit.append(window.AuralisPageGallery.closeIcon());
      header.append(back,title);if(!main)header.append(exit);
      const body=node('div',null,'media-dialog-body');if(!main)body.tabIndex=0;
      const content=node('div',null,'plugin-page-content');content.dataset.i18nSkip='';
      const feedback=node('div',null,'plugin-page-status');feedback.setAttribute('role','status');body.append(content,feedback);surface().append(header,body);
      if(main){api.enterCreatorPage('plugin');render();}else{
        dialog.showModal();
        if(!api.reduced())dialog.animate([{opacity:0,transform:'translateY(8px)'},{opacity:1,transform:'none'}],{duration:220,easing:'ease-out'});
        exit.focus();
      }
      read();
    }
    dialog.addEventListener('cancel',e=>{e.preventDefault();close();});
    dialog.addEventListener('close',()=>{if(!dialog.open&&!main)cancel();});
    dialog.addEventListener('keydown',e=>e.stopPropagation());
    document.addEventListener('keydown',e=>{
      if(main&&visible()&&!document.querySelector('dialog[open]')&&!document.querySelector('#nowPlayingOverlay.open')&&e.key==='Escape'){
        e.preventDefault();e.stopImmediatePropagation();const back=surface().querySelector('[data-plugin-back]');back?.click();
      }
    },true);
    window.addEventListener('beforeunload',cancel);
    function choose(item){
      const options=entries(item).filter(e=>e.placement==='media');if(options.length===1){open(item,options[0]);return;}if(!options.length)return;
      cancel();closing?.cancel();closing=null;origin=document.activeElement;track=item;entry=null;main=false;active=true;history=[];contextKey=api.pageContext?.(track);
      dialog.getAnimations().forEach(a=>a.cancel());dialog.replaceChildren();
      const header=node('header'),title=node('h2',t('插件页面'));title.id='pluginPageTitle';header.append(title,button(t('关闭'),()=>close()));
      const body=node('div',null,'media-dialog-body'),choices=node('div',null,'plugin-page-actions');
      for(const option of options){const b=button(label(option),()=>open(item,option));b.dataset.i18nSkip='';choices.append(b);}
      body.append(choices);dialog.append(header,body);if(!dialog.open)dialog.showModal();choices.firstElementChild.focus();
    }
    function syncNavigation(){
      if(!navigation)return;
      const providers=api.pageProviders?.()||[],rows=[];
      for(const p of providers)if(api.capabilities(p).includes('GlobalPages'))for(const e of entries(p))
        if(e.placement==='global'&&e.presentation==='page'&&e.documentVersion>=3&&e.documentVersion<=6)rows.push({p,e});
      const signature=JSON.stringify([document.documentElement.lang,rows]);
      if(signature!==navigationSignature){
        const focused=document.activeElement?.dataset.pluginNavigation;
        navigationSignature=signature;navigation.replaceChildren();navigation.hidden=rows.length===0;
        const heading=node('div',t('插件页面'),'nav-section-label'),nav=node('nav',null,'navigation');
        nav.setAttribute('aria-label',t('插件页面'));
        for(const {p,e} of rows){
          const text=label(e),b=button('',()=>open({providerId:p.providerId,handle:null,title:text},e));
          b.className='nav-item';b.dataset.pluginNavigation=p.providerId+':'+e.id;b.dataset.i18nSkip='';
          b.title=p.name+' · '+text;b.setAttribute('aria-label',b.title);
          const icon=node('span',null,'nav-icon');icon.innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="18" height="18" rx="3"/><path d="M3 9h18M9 9v12"/></svg>';
          b.append(icon,node('span',text));nav.append(b);
        }navigation.append(heading,nav);
        if(focused)[...nav.children].find(b=>b.dataset.pluginNavigation===focused)?.focus({preventScroll:true});
      }
      const globalActive=visible()&&main&&entry?.placement==='global';
      for(const b of navigation.querySelectorAll('button')){
        const selected=globalActive&&b.dataset.pluginNavigation===track.providerId+':'+entry.id;
        b.classList.toggle('active',selected);if(selected)b.setAttribute('aria-current','page');else b.removeAttribute('aria-current');
      }
      if(globalActive)for(const b of document.querySelectorAll('.sidebar > .navigation .nav-item,.sidebar > .settings-link'))b.classList.remove('active');
    }
    function render(){if(active&&main){api.content.replaceChildren(page);page.classList.remove('is-leaving');}syncNavigation();}
    return {open,choose,receive,entries,label,render,creator:item=>entries(item).find(e=>e.placement==='creator'),
      sync:()=>{
        if(active&&!allowed())close(true);
        else if(active&&main&&api.state.currentPage!=='plugin'){cancel();gallery.close(true);active=false;}
        syncNavigation();
      }};
  }
  return {create};
})();
