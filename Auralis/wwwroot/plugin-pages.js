/* Host-owned Fluent renderer. Plugins supply bounded plain data, never HTML/CSS/JS. */
window.AuralisPluginPages = (() => {
  const t = s => window.AuralisI18n?.t(s) || s;
  function create(api, openComments, discussion) {
    const dialog=document.createElement('dialog'),page=document.createElement('section');
    dialog.className='media-hub-dialog plugin-page-dialog';dialog.setAttribute('aria-labelledby','pluginPageTitle');
    page.className='page-view plugin-page-route';page.setAttribute('aria-labelledby','pluginPageTitle');document.body.append(dialog);
    const gallery=window.AuralisPageGallery.create(api),art=window.AuralisPageGallery.art;
    let track,entry,origin,pending,serial=0,history=[],closing,contextKey,main=false,active=false,observer,next,collection,emptyPages=0,readingLayout='cards';
    const cardsSeen=new Set();let queryDirty=false,uiLanguage='',layoutObserver,layoutFrame=0,textSequence=0,eagerCovers=0;
    // Localize host controls in place; plugin text must never be treated as a translation key.
    // Weak references follow the bounded reading snapshots and do not retain discarded pages.
    const localControls=new WeakMap();
    const discussionSubjects=new WeakMap();
    function showDetailDiscussion(subject,focus=true){
      const panel=surface().querySelector('.plugin-detail-discussion');
      if(!panel||!subject)return;
      discussion?.open(panel,subject,{focus});
      if(focus)panel.scrollIntoView({block:'nearest'});
    }
    function openDetailDiscussion(quoted=false,focus=false){
      if(!main||readingLayout!=='detail')return;
      const control=surface().querySelector('[data-inline-discussion="'+(quoted?'quote':'main')+'"]')||
        surface().querySelector('[data-inline-discussion="quote"]');
      showDetailDiscussion(discussionSubjects.get(control),focus);
    }
    const motion=window.AuralisPageMotion.create(api.reduced);
    const updates=window.AuralisPageUpdates?.create({visible:()=>visible()&&allowed(),
      idle:()=>!pending&&!queryDirty&&!document.querySelector('dialog[open],#nowPlayingOverlay.open'),
      check:handle=>read(handle,'check'),reload:handle=>{if(validAction({handle}))read(handle,'updates');},
      notice:value=>{const host=surface().querySelector('.plugin-page-updates');if(!host)return;
        host.replaceChildren();host.hidden=!value;if(!value)return;
        const b=button('',value.reload);b.className='plugin-updates-button';
        localControl(b,()=>b.textContent=t(value.kind==='new'?'有新动态，点击查看':'自动检查已暂停，点击重试'));
        host.append(b);window.AuralisPageMotion.fade(host,api.reduced());}});
    function localControl(n,refresh){n.dataset.pageLocalized='';localControls.set(n,refresh);refresh();return n;}
    // One reading window reserves a full (100-card) server batch. Never discard overflow rows.
    const windowLimit=200, batchLimit=100;
    function stopLayout(){layoutObserver?.disconnect();layoutObserver=null;cancelAnimationFrame(layoutFrame);layoutFrame=0;}
    function layoutCards(){
      stopLayout();const grid=surface().querySelector('.plugin-page-cards');if(!grid)return;
      let measuredWidth=-1;
      const clearPlacement=card=>{card.style.gridColumn='';card.style.gridRow='';};
      const arrange=()=>{layoutFrame=0;if(!visible()||document.hidden)return;
        const cards=[...grid.children];
        const catalogue=readingLayout==='cards'&&cards.length>0&&cards.every(c=>c.classList.contains('is-media-card'));
        grid.classList.toggle('is-media-catalogue',catalogue);
        // Clear old explicit tracks before a width change is measured, otherwise a former
        // second column would become an implicit column in the narrow single-column layout.
        // Keep fractional CSS pixels at DPI/zoom breakpoints; clientWidth rounds them.
        const width=getComputedStyle(grid).width;
        if(width!==measuredWidth){cards.forEach(clearPlacement);measuredWidth=width;}
        // Reuse the bounded layout observer; short/wide text needs no redundant expansion control.
        for(const toggle of grid.querySelectorAll('.plugin-text-toggle')){
          const text=toggle.previousElementSibling;
          toggle.hidden=toggle.getAttribute('aria-expanded')!=='true'&&text.scrollHeight<=text.clientHeight+1;
        }
        const style=getComputedStyle(grid),columns=style.gridTemplateColumns.split(' ').length;
        const staggered=!catalogue&&(readingLayout==='feed'||!grid.classList.contains('is-list'))&&columns>1;
        grid.classList.toggle('is-staggered',staggered);
        if(!staggered){cards.forEach(clearPlacement);return;}
        // Place each next card at the shortest column, without moving its DOM node or
        // changing tab order. Read untransformed CSS heights: entrance animations and
        // browser/native scale must not feed back into the layout observer.
        const heights=cards.map(card=>Math.max(1,card.offsetHeight));
        const bottoms=Array(columns).fill(0),gap=parseFloat(style.columnGap)||16;
        for(let i=0;i<cards.length;i++){
          const top=Math.min(...bottoms),column=bottoms.indexOf(top),height=heights[i];
          cards[i].style.gridColumn=String(column+1);
          cards[i].style.gridRow=(top+1)+' / span '+height;
          bottoms[column]=top+height+Math.ceil(gap);
        }
      };
      const schedule=()=>{if(!layoutFrame&&!document.hidden)layoutFrame=requestAnimationFrame(arrange);};
      arrange();layoutObserver=new ResizeObserver(schedule);layoutObserver.observe(grid);
      for(const card of grid.children)layoutObserver.observe(card);
    }
    // Session-only reading views. Native still validates every navigation before restoration.
    const readingViews=new Set();let viewRenderedAt=0,viewLanguage='';
    const viewKey=doc=>JSON.stringify([...(doc.tabs||[]),...(doc.navigation||[])].map(tab=>[tab.action.label,tab.selected]));
    function clearReadingViews(){for(const saved of readingViews)saved.nodes=null;readingViews.clear();history=[];}
    function discardView(saved){
      readingViews.delete(saved);saved.nodes=null;saved.cards=[];
      // Eviction must release index entries too: labels/tab combinations can change indefinitely.
      for(const frame of history){
        if(frame.snapshot===saved)frame.snapshot=null;
        for(const [key,value] of frame.views||[])if(value===saved)frame.views.delete(key);
      }
    }
    function rememberView(){
      const current=history.at(-1);if(!current||cardsSeen.size>windowLimit)return;
      const content=surface().querySelector('.plugin-page-content');if(!content)return;
      const old=current.snapshot;if(old)discardView(old);
      const saved={nodes:[...content.childNodes],title:surface().querySelector('#pluginPageTitle').textContent,
        readingLayout,
        next,collection,emptyPages,queryDirty,cards:[...cardsSeen],scroll:current.scroll??scroller().scrollTop,
        focus:current.focus,created:viewRenderedAt,language:viewLanguage,updates:current.updates};
      current.snapshot=saved;current.views??=new Map();current.views.set(current.viewKey,saved);readingViews.add(saved);
      while(readingViews.size>6)discardView(readingViews.values().next().value);
    }
    function restoreView(saved,mode){
      if(!saved?.nodes||Date.now()-saved.created>60000||saved.language!==document.documentElement.lang)return false;
      surface().querySelector('.plugin-page-content').replaceChildren(...saved.nodes);
      readingLayout=saved.readingLayout;surface().dataset.readingLayout=readingLayout;
      surface().querySelector('#pluginPageTitle').textContent=saved.title;
      cardsSeen.clear();saved.cards.forEach(key=>cardsSeen.add(key));
      next=saved.next;collection=saved.collection;emptyPages=saved.emptyPages;queryDirty=saved.queryDirty;viewRenderedAt=saved.created;viewLanguage=saved.language;
      status();openDetailDiscussion();layoutCards();scroller().scrollTop=saved.scroll;updates?.configure(saved.updates);
      const buttons=[...surface().querySelectorAll('.plugin-page-content button')],candidate=buttons[saved.focus?.index];
      const target=mode==='filter'?surface().querySelector('.plugin-identity-filter[aria-pressed="true"]'):mode==='tab'?surface().querySelector('[role="tab"][aria-selected="true"]'):
        candidate?.textContent===saved.focus?.text?candidate:surface().querySelector('#pluginPageTitle');
      target?.focus({preventScroll:true});return true;
    }
    function navigationContext(){
      const back=surface().querySelector('[data-plugin-back]');back.hidden=!main&&history.length<2;
      back.setAttribute('aria-label',history.length>1?t('返回')+' · '+history.at(-2).title:t('返回'));
      back.title=back.getAttribute('aria-label');
      const context=surface().querySelector('.plugin-page-context');if(!context)return;
      const titles=[label(entry),...history.slice(0,-1).map(frame=>frame.title)].filter(Boolean);
      context.textContent=[...new Set(titles)].join(' › ');context.hidden=!context.textContent;
    }
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
      updates?.pause();
      motion.cancel();
      observer?.disconnect();observer=null;
      if(pending){clearTimeout(pending.timer);pending=null;api.post('cancelPlatformExtras',{kind:'plugin-page'});}
      syncNavigation();
    }
    function close(immediate=false){
      updates?.clear();
      discussion?.close();
      cancel();gallery.close(true);stopLayout();
      if(!active)return;active=false;clearReadingViews();
      if(main){api.leaveCreatorPage();return;}
      if(!dialog.open)return;
      dialog.getAnimations().forEach(a=>a.cancel());closing?.cancel();closing=null;
      const finish=()=>{dialog.close();origin?.isConnected&&origin.focus({preventScroll:true});};
      if(immediate||api.reduced()){finish();return;}
      closing=dialog.animate([{opacity:1,transform:'none'},{opacity:0,transform:'translateY(6px)'}],{duration:140,easing:'ease-in'});
      const current=closing;current.finished.then(()=>{if(closing===current){finish();current.cancel();closing=null;}}).catch(()=>{});
    }
    function status(text='',retry){
      syncNavigation();
      observer?.disconnect();observer=null;
      const feedback=surface().querySelector('.plugin-page-status');if(!feedback)return;
      const operation=pending||retry;
      feedback.classList.toggle('is-navigation-status',!!(main&&operation&&!['append','window'].includes(operation.mode)));
      const initial=!!pending&&!history.length;
      feedback.classList.toggle('is-initial-loading',initial);
      let label='',action=null;
      surface().querySelector('.media-dialog-body').setAttribute('aria-busy',String(!!pending));
      for(const b of surface().querySelectorAll('[data-page-navigation]'))b.setAttribute('aria-disabled',String(!!pending&&b.getAttribute('role')!=='tab'));
      if(retry){label=t('重试');action=()=>read(retry.navigation,retry.mode,retry.inputValues);}
      else if(!pending&&!queryDirty&&validAction(next)&&cardsSeen.size>windowLimit-batchLimit){
        text=t('已到本段末尾，继续阅读下一段；可返回当前段。');
        label=t('继续阅读');action=()=>read(next.handle,'window',history.at(-1)?.inputValues);
      }
      else if(!pending&&!queryDirty&&validAction(next)){
        label=next.label||t('加载更多');action=()=>{emptyPages=0;read(next.handle,'append');};
        if(visible()&&emptyPages<3){
          const margin=readingLayout==='cards'
            ? Math.min(600,Math.round(scroller().clientHeight*.7))
            : Math.min(360,Math.round(scroller().clientHeight*.4));
          observer=new IntersectionObserver(rows=>{
            if(rows.some(r=>r.isIntersecting)&&visible()&&!pending&&!document.hidden&&navigator.onLine!==false&&
              !document.querySelector('dialog[open]:not(.plugin-page-dialog)')&&!document.querySelector('#nowPlayingOverlay.open'))read(next.handle,'append');
          },{root:scroller(),rootMargin:'0px 0px '+margin+'px'});observer.observe(feedback);
        }
      }
      const more=window.AuralisLoading.continuation(feedback,{pending:!!pending,initial,text,label,action,kind:retry?'retry':'more',reduced:api.reduced()});
      more.dataset.i18nSkip='';
    }
    function read(navigation=null,mode='push',inputValues){
      if(pending?.mode==='check'&&mode!=='check')cancel();
      if(pending||closing||!entry||!allowed()||!visible())return;
      updates?.pause();
      motion.cancel();
      if(history.length&&!['append','back','check'].includes(mode)){
        const current=history.at(-1),buttons=[...surface().querySelectorAll('.plugin-page-content button')],focused=document.activeElement;
        current.scroll=scroller().scrollTop;
        if(buttons.includes(focused))current.focus={index:buttons.indexOf(focused),text:focused.textContent};
      }
      observer?.disconnect();
      pending={requestId:++serial,handle:track.handle,providerId:track.providerId,entryId:entry.id,navigation,mode,inputValues};const current=pending;
      current.timer=setTimeout(()=>{if(pending===current){cancel();if(mode==='check')updates?.fail();else status(t('读取超时，请重试。'),current);}},35000);
      if(mode!=='check')status(t('正在读取…'));
      const context=entry.placement==='global'?{providerId:track.providerId}:{handle:track.handle};
      const preferredPageSize=window.AuralisCataloguePagination.pageSize(surface().querySelector('.plugin-page-content').clientWidth,scroller().clientHeight);
      api.post(entry.placement==='global'?'readPluginGlobalPage':'readPluginPage',{...context,entryId:entry.id,requestId:current.requestId,navigationHandle:navigation,language:document.documentElement.lang,preferredPageSize,forceRefresh:['refresh','check','updates'].includes(mode),...(inputValues?{inputValues}:{})});
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
        if(tab.selected){const marker=node('span',null,'plugin-tab-indicator');marker.setAttribute('aria-hidden','true');b.append(marker);}
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
      n.addEventListener('error',()=>n.remove(),{once:true});window.AuralisLoading.image(n);return n;
    }
    function avatarPicture(host,url,fallback,{eager=false}={}){
      host.append(node('span',fallback));if(!art(url))return;
      let retries=0;
      const load=()=>{
        host.querySelector('.plugin-avatar-retry')?.remove();
        const picture=node('img');picture.alt='';picture.loading=eager?'eager':'lazy';picture.decoding='async';picture.referrerPolicy='no-referrer';
        if(eager)picture.fetchPriority='high';
        picture.addEventListener('error',()=>{
          picture.remove();host.removeAttribute('aria-hidden');
          const retry=button('↻',()=>{retries++;load();});retry.className='plugin-avatar-retry';
          retry.setAttribute('aria-label',t('重试头像'));retry.title=t('重试头像');host.append(retry);
        },{once:true});
        picture.addEventListener('load',()=>host.setAttribute('aria-hidden','true'),{once:true});
        picture.src=retries?window.AuralisLoading.retryImageUrl(url,retries):url;
        window.AuralisLoading.image(picture);host.append(picture);
      };
      load();
    }
    function identityNavigation(list,parent){
      if(!list?.length)return;
      const row=node('div',null,'plugin-identity-rail');row.setAttribute('role','group');row.setAttribute('aria-label',t('作者筛选'));
      for(const [index,item] of list.entries()){
        const b=button('',()=>{if(pending)cancel();if(item.selected){status();updates?.schedule();return;}read(item.action.handle,'filter');});
        b.className='plugin-identity-filter'+(index===0&&!art(item.image)?' is-all-activity':'');b.dataset.pageNavigation='';b.dataset.i18nSkip='';b.title=item.action.label;
        b.setAttribute('aria-pressed',String(item.selected));
        const avatar=node('span',null,'plugin-identity-avatar');avatar.setAttribute('aria-hidden','true');
        avatar.append(node('span',Array.from(item.action.label)[0]||'·'));
        if(art(item.image)){
          let failed=false,retries=0;
          const load=()=>{
            avatar.querySelector('.plugin-identity-retry')?.remove();
            const pic=node('img');pic.alt='';pic.loading='lazy';pic.decoding='async';pic.referrerPolicy='no-referrer';
            pic.addEventListener('error',()=>{
              pic.remove();
              failed=true;b.setAttribute('aria-keyshortcuts','R');b.title=t('头像加载失败，点击头像或按 R 重试');
              avatar.append(node('span','↻','plugin-identity-retry'));
            },{once:true});avatar.append(pic);
            pic.src=retries?window.AuralisLoading.retryImageUrl(item.image,retries):item.image;
            window.AuralisLoading.image(pic);
          };
          load();
          const retry=()=>{if(!failed)return false;failed=false;retries++;b.removeAttribute('aria-keyshortcuts');b.title=item.action.label;load();return true;};
          b.addEventListener('click',e=>{if(avatar.contains(e.target)&&retry()){e.stopImmediatePropagation();e.preventDefault();}},true);
          b.addEventListener('keydown',e=>{if(e.key.toLowerCase()==='r'&&retry()){e.preventDefault();e.stopPropagation();}});
        }
        b.append(avatar,node('span',item.action.label,'plugin-identity-label'));row.append(b);
      }
      row.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;
        const children=[...row.children],index=children.indexOf(document.activeElement);if(index<0)return;
        e.preventDefault();e.stopPropagation();const target=e.key==='Home'?0:e.key==='End'?children.length-1:(index+(e.key==='ArrowRight'?1:-1)+children.length)%children.length;
        children[target].focus({preventScroll:true});children[target].scrollIntoView({block:'nearest',inline:'nearest'});
      });parent.append(row);
    }
    // A generic identity surface, driven by creator placement / introductory artwork, never provider IDs.
    // Keep the one existing heading and navigation controls: no duplicate names or extra artwork requests.
    function profileHeader(doc){
      const header=surface().querySelector('.plugin-page-header');if(!header)return false;
      const enabled=doc.layout!=='detail'&&(entry.placement==='creator'||!!art(doc.image)&&!!doc.tabs?.length);
      // Preserve the identity surface across sibling tabs: no avatar reload or header flash.
      const identity=enabled?JSON.stringify([doc.title,doc.image,doc.description]):'';
      if(header.dataset.profileIdentity===identity)return enabled;
      header.dataset.profileIdentity=identity;
      header.classList.toggle('is-profile-header',enabled);surface().classList.toggle('has-profile-header',enabled);
      header.querySelectorAll('.plugin-profile-avatar,.plugin-profile-description').forEach(n=>n.remove());
      if(!enabled)return false;
      const avatar=node('span',null,'plugin-profile-avatar');avatar.setAttribute('aria-hidden','true');avatar.dataset.i18nSkip='';
      avatarPicture(avatar,doc.image,Array.from(String(doc.title||label(entry)).trim())[0]||'♪',{eager:true});
      header.append(avatar);
      if(doc.description){const description=node('p',doc.description,'plugin-profile-description');description.dataset.i18nSkip='';header.append(description);}
      return true;
    }
    // Public artwork proxies only; reserve the visual slot and allow an explicit, bounded retry.
    // Never retry automatically or manufacture an upstream URL when the plugin supplied no image.
    function visual(url,className=''){
      const slot=node('span',null,'plugin-artwork '+className);let failed=false,retries=0,retryLoading=false;
      const load=()=>{
        failed=false;retryLoading=retries>0;slot.classList.remove('is-artwork-failed');slot.replaceChildren();
        const control=slot.closest('button');
        if(retryLoading&&control){
          if(!Object.hasOwn(control.dataset,'artworkAction'))control.dataset.artworkAction=control.getAttribute('aria-label')||'';
          control.setAttribute('aria-label',t('正在加载图片…'));
        }
        if(!art(url)){slot.append(node('span',t('暂无封面'),'plugin-artwork-label'));return;}
        const img=node('img');img.alt='';img.loading='lazy';img.decoding='async';img.referrerPolicy='no-referrer';
        if(className==='plugin-media-cover'&&eagerCovers++<6){img.loading='eager';if(eagerCovers<=2)img.fetchPriority='high';}
        img.addEventListener('error',()=>{
          failed=true;retryLoading=false;slot.removeAttribute('aria-busy');slot.classList.add('is-artwork-failed');slot.replaceChildren(node('span',t('图片加载失败，点击重试'),'plugin-artwork-label'));
          const control=slot.closest('button');if(control){
            if(!Object.hasOwn(control.dataset,'artworkAction'))control.dataset.artworkAction=control.getAttribute('aria-label')||'';
            control.setAttribute('aria-label',t('重试图片'));control.title=t('重试图片');
          }
          else {const retry=button(t('重试图片'),()=>{retries++;load();});retry.className='plugin-single-retry';slot.append(retry);}
        },{once:true});
        img.addEventListener('load',()=>{
          retryLoading=false;slot.removeAttribute('aria-busy');slot.querySelector('.plugin-retry-loading')?.remove();
          const control=slot.closest('button');if(control&&Object.hasOwn(control.dataset,'artworkAction')){
            control.setAttribute('aria-label',control.dataset.artworkAction);delete control.dataset.artworkAction;control.removeAttribute('title');
          }
        },{once:true});
        if(retryLoading){slot.setAttribute('aria-busy','true');slot.append(node('span',t('正在加载图片…'),'plugin-artwork-label plugin-retry-loading'));}
        img.src=retries?window.AuralisLoading.retryImageUrl(url,retries):url;
        window.AuralisLoading.image(img);slot.append(img);
      };
      load();return {slot,retry:()=>{if(retryLoading)return true;if(!failed)return false;retries++;load();return true;}};
    }
    function formatDate(n){
      const d=new Date(n.dateTime),language=document.documentElement.lang;
      n.title=d.toLocaleString(language);
      n.textContent=n.dataset.compactDate!==undefined?d.toLocaleDateString(language):n.title;
      n.setAttribute('aria-label',n.title);
    }
    function date(value,compact=false){
      const d=new Date(value);if(!value||!Number.isFinite(d.getTime()))return null;
      const n=node('time');n.dateTime=d.toISOString();if(compact)n.dataset.compactDate='';formatDate(n);return n;
    }
    function card(item,quoted=false,parentOpen=null){
      const result=node(quoted?'blockquote':'article',null,quoted?'plugin-page-quote':'plugin-page-card');
      if(quoted){result.dataset.quoteStatus=item.status;result.append(node('div',t('转发原文'),'plugin-quote-label'));}
      if(item.author){
        const header=node('header',null,'plugin-card-author'),avatar=node('span',null,'comment-avatar');
        avatar.dataset.i18nSkip='';avatar.setAttribute('aria-hidden','true');avatarPicture(avatar,item.avatar,Array.from(item.author)[0]||'?');
        const text=node('div'),name=node('strong');
        if(validAction(item.authorAction)){const link=button(item.author,()=>read(item.authorAction.handle));link.className='plugin-reading-link';link.dataset.pageNavigation='';link.setAttribute('aria-label',item.authorAction.label+' · '+item.author);name.append(link);}else name.textContent=item.author;
        text.append(name);const when=date(item.publishedAt,!quoted&&readingLayout==='cards'&&!!item.media);if(when)text.append(when);header.append(avatar,text);result.append(header);
      }else{const when=date(item.publishedAt,!quoted&&readingLayout==='cards'&&!!item.media);if(when)result.append(when);}
      const single=art(item.image)?visual(item.image,'plugin-card-single').slot:null;if(single)result.append(single);
      if(item.title){const heading=node('h3');if(validAction(item.open)){
        const link=button(item.title,()=>read(item.open.handle));link.className='plugin-reading-link';link.dataset.pageNavigation='';heading.append(link);
      }else heading.textContent=item.title;result.append(heading);}
      if(item.text){
        const text=node('p',null,'plugin-card-text');
        // Runs are bounded and Native-approved; no innerHTML, arbitrary link or executable node.
        if(item.body?.length&&item.body.length<=128&&item.body.map(r=>r.text).join('')===item.text){
          for(const run of item.body){const picture=!api.reduced()&&art(run.image)?node('img',null,'plugin-body-emote'):null;
            if(picture){picture.alt=run.text;picture.loading='lazy';picture.decoding='async';picture.referrerPolicy='no-referrer';picture.src=run.image;picture.addEventListener('error',()=>picture.replaceWith(document.createTextNode(run.text)),{once:true});text.append(picture);}
            else text.append(document.createTextNode(run.text));}
        }else text.textContent=item.text;
        result.append(text);
        if((item.text.length>280||item.text.split('\n').length>7)&&readingLayout!=='detail'){
          text.className='plugin-card-text is-collapsed';
          text.id='plugin-card-text-'+(++textSequence);
          const toggle=button(t('展开全文'),()=>{
            const expanded=toggle.getAttribute('aria-expanded')!=='true';
            toggle.setAttribute('aria-expanded',String(expanded));text.classList.toggle('is-collapsed',!expanded);
            toggle.textContent=t(expanded?'收起全文':'展开全文');
            // Collapsing a tall post must not strand the focused button above the reading viewport.
            // No animated height or smooth scrolling: one explicit layout change, including reduced motion.
            if(!expanded)toggle.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});
          });toggle.classList.add('plugin-text-toggle');toggle.setAttribute('aria-expanded','false');toggle.setAttribute('aria-controls',text.id);result.append(toggle);
        }
      }
      const images=(item.images||[]).filter(art).slice(0,9);
      if(images.length){
        const group=node('div',null,'plugin-card-images');
        images.forEach((url,index)=>{
          const b=button('',()=>gallery.open(images,index));b.className='plugin-image-button';b.setAttribute('aria-label',t('查看图片')+' '+(index+1));
          const artwork=visual(url);b.addEventListener('click',e=>{if(artwork.retry()){e.stopImmediatePropagation();e.preventDefault();}},true);
          b.append(artwork.slot);group.append(b);
        });result.append(group);
      }
      if(!quoted&&item.quote)result.append(card(item.quote,true,item.open));
      if(!quoted&&(Number.isSafeInteger(item.likeCount)||Number.isSafeInteger(item.repostCount))){
        const stats=node('div',null,'plugin-card-stats');
        if(Number.isSafeInteger(item.repostCount))stats.append(node('span','↪ '+new Intl.NumberFormat(document.documentElement.lang).format(Math.max(0,item.repostCount)),'plugin-repost-count'));
        if(Number.isSafeInteger(item.likeCount))stats.append(node('span','♡ '+new Intl.NumberFormat(document.documentElement.lang).format(Math.max(0,item.likeCount)),'plugin-like-count'));
        result.append(stats);
      }
      const row=node('div',null,'plugin-page-actions plugin-card-actions');actions(item.actions,row);
      if(validAction(item.open)&&!item.title){const link=button(item.open.label,()=>read(item.open.handle));link.dataset.pageNavigation='';row.append(link);}
      if(/^community-[a-f0-9]{32}$/.test(item.discussionHandle||'')&&api.capabilities(track).includes('Comments')){
        const count=Number.isSafeInteger(item.commentCount)?' · '+Math.max(0,item.commentCount):'';
        const subject={...track,kind:'online',handle:item.discussionHandle,title:[quoted?t('转发原文'):'',item.author,item.title||item.text?.slice(0,100)||track.title].filter(Boolean).join(' · ')};
        // A global page has no media kind. The validated discussion handle is still an online
        // comment subject; do not let the media-hub gate silently discard this navigation.
        const commentsButton=button(t('查看评论')+count,()=>{
          const destination=quoted?parentOpen:item.open;
          if(main&&readingLayout!=='detail'&&validAction(destination)){read(destination.handle,quoted?'quote-discussion':'discussion');return;}
          if(readingLayout==='detail'&&main){
            showDetailDiscussion(subject);
          }else openComments?.(subject,false);
        });commentsButton.dataset.inlineDiscussion=quoted?'quote':'main';
        discussionSubjects.set(commentsButton,subject);
        if(!quoted&&readingLayout==='cards'&&item.media){
          const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');
          icon.setAttribute('viewBox','0 0 24 24');icon.setAttribute('aria-hidden','true');
          const path=document.createElementNS(icon.namespaceURI,'path');
          path.setAttribute('d','M5 4h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-8l-6 3v-3a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z');icon.append(path);
          const label=node('span');commentsButton.replaceChildren(icon,label);
          commentsButton.classList.add('plugin-media-comments');
          localControl(commentsButton,()=>{const name=t('查看评论')+count;label.textContent=count?String(Math.max(0,item.commentCount)):t('评论');commentsButton.setAttribute('aria-label',name);commentsButton.title=name;});
        }else localControl(commentsButton,()=>{commentsButton.textContent=t('查看评论')+count;});
        row.append(commentsButton);
      }
      if(item.media && item.media.providerId===track.providerId && /^track-[a-f0-9]{24}$/.test(item.media.handle||'') && api.capabilities(track).includes('StreamResolution')) {
        const media=api.normalize(item.media,track.providerId);
        if(!quoted)result.classList.add('is-media-card');
        const details=node('div',null,'plugin-card-media');
        const select=play=>{if(visible()&&allowed())api.selectPageMedia?.(media,play);};
        if(!single&&!images.length){
          const artwork=visual(media.coverUrl,'plugin-media-cover');
          const cover=button('',()=>{if(artwork.retry())return;if(validAction(item.open))read(item.open.handle);else select(true);});
          cover.className='plugin-cover-button';cover.dataset.mediaCover='';
          localControl(cover,()=>cover.setAttribute('aria-label',(validAction(item.open)?item.open.label:t('播放'))+' · '+media.title));
          cover.disabled=media.unavailable&&!validAction(item.open);cover.append(artwork.slot);
          cover.append(node('span',api.formatTime(media.durationSeconds),'plugin-cover-duration'));
          result.prepend(cover);
        }
        if(!validAction(item.open)&&item.title){
          const heading=result.querySelector('h3'),link=button(item.title,()=>select(true));
          link.className='plugin-reading-link';link.disabled=media.unavailable;link.dataset.mediaTitle='';heading.replaceChildren(link);
        }
        if(readingLayout==='cards'){
          const author=result.querySelector('.plugin-card-author'),heading=result.querySelector('h3');
          if(author&&heading)heading.after(author);
          if(heading){
            // Clamp only the text, not the focusable button or its focus ring.
            const label=heading.querySelector('button')||heading;
            const text=node('span',item.title,'plugin-media-title-text');label.replaceChildren(text);label.title=item.title;
          }
        }
        const text=node('div');if(media.title!==item.title)text.append(node('strong',media.title));
        // Catalogue metadata has one owner. Do not repeat the author or the cover's duration;
        // distinct credits, alternate titles and availability must remain visible.
        const compact=readingLayout==='cards'&&!quoted,metadata=[];
        if(!compact||media.artist!==item.author)metadata.push(media.artist);
        if(!compact||!result.querySelector('.plugin-cover-duration'))metadata.push(api.formatTime(media.durationSeconds));
        if(metadata.length)text.append(node('span',metadata.filter(Boolean).join(' · ')));
        if(text.childNodes.length)details.append(text);
        const play=button(t('播放'),()=>select(true)),add=button(t('加入队列'),()=>select(false));
        play.dataset.pageMedia='play';add.dataset.pageMedia='enqueue';
        localControl(play,()=>{play.textContent=t('播放');play.setAttribute('aria-label',t('播放')+' · '+media.title);});
        localControl(add,()=>{add.textContent=t('加入队列');add.setAttribute('aria-label',t('加入队列')+' · '+media.title);});
        play.disabled=add.disabled=media.unavailable;
        if(media.unavailable||media.previewOnly){const hint=node('span',null,'plugin-media-availability');details.append(localControl(hint,()=>{hint.textContent=t(media.unavailable?'当前不可播放':'仅试听');}));}
        if(details.childNodes.length)result.append(details);
        row.prepend(play,add);
      }
      result.append(row);
      if(!quoted&&readingLayout==='detail'&&!item.quote){
        const cover=result.querySelector(':scope > .plugin-cover-button');
        if(cover){
          const copy=node('div',null,'plugin-media-detail-copy');
          for(const child of [...result.children])if(child!==cover)copy.append(child);
          result.append(copy);result.classList.add('has-media-summary');
        }
      }
      return result;
    }
    function receive(payload){
      if(!pending||!visible()||!allowed()||payload?.requestId!==pending.requestId||
        (entry.placement==='global'?payload.providerId!==pending.providerId||payload.handle!=null:payload.handle!==pending.handle)||payload.entryId!==pending.entryId)return;
      const request=pending;if(request.ready)return;clearTimeout(request.timer);
      if(request.mode==='check'){
        pending=null;
        if(payload.error||payload.page?.version!==9||payload.page.layout!=='feed'||payload.page.append||payload.page.cards?.length!==0||!payload.page.updates||!window.AuralisPageUpdates.valid(payload.page.updates)){updates?.fail();return;}
        updates?.receive(payload.page.updates);status();return;
      }
      if(payload.error||!payload.page){pending=null;status(t(payload.error?.message||'无法读取插件页面，请重试。'),request);return;}
      const continuation=request.mode==='window'||request.mode==='back'&&history.at(-2)?.continuation||request.mode==='refresh'&&history.at(-1)?.continuation;
      const append=request.mode==='append',expectedAppend=append||!!continuation;
      let doc=payload.page;
      const sections=doc.tabs||[];
      const rail=doc.navigation||[];
      if(doc.query!=null && (append || !window.AuralisPageQuery?.valid(doc.query)) ||
        !Array.isArray(rail)||rail.length>24||rail.some(n=>!validAction(n.action)||typeof n.selected!=='boolean')||
        rail.length>0&&(doc.version!==9||rail.filter(n=>n.selected).length!==1||append)||
        doc.updates!=null&&(doc.version!==9||doc.layout!=='feed'||append||!window.AuralisPageUpdates?.valid(doc.updates))||
        !Array.isArray(sections)||sections.length>8||sections.some(t=>!validAction(t.action)||typeof t.selected!=='boolean')||
        (sections.length>0&&(sections.length<2||sections.filter(t=>t.selected).length!==1))||(append&&sections.length>0)||
        !Array.isArray(doc.cards)||doc.cards.length>100||!['cards','list','feed','detail'].includes(doc.layout)||
        (['feed','detail'].includes(doc.layout)&&(!main||![7,8,9].includes(doc.version)))||
        (doc.layout==='detail'&&(doc.cards.length!==1||doc.next||doc.query||append))||(append&&doc.layout!==readingLayout)||
        doc.cards.some(c=>c.open!=null&&(!validAction(c.open)||![7,8,9].includes(doc.version))||
          ((c.body?.length||c.quote||c.authorAction)&&![8,9].includes(doc.version))||
          (c.quote&&(c.quote.quote||!['available','unavailable'].includes(c.quote.status))))||
        !!doc.append!==expectedAppend||((append||request.mode==='window')&&(!collection||collection!==doc.collectionHandle))){
        pending=null;status(t('插件页面格式不受支持或超出限制。'),request);return;
      }
      if(continuation){const shell=(request.mode==='back'?history.at(-2):history.at(-1))?.shell;
        if(!shell){pending=null;status(t('无法读取插件页面，请重试。'),request);return;}
        doc={...shell,cards:doc.cards,next:doc.next,collectionHandle:doc.collectionHandle};
      }
      request.ready=true;
      const oldTabs=[...surface().querySelectorAll('[role="tab"]')];
      const priorIndicator=surface().querySelector('.plugin-tab-indicator')?.getBoundingClientRect();
      const oldIndex=oldTabs.findIndex(n=>n.getAttribute('aria-selected')==='true'),newIndex=sections.findIndex(n=>n.selected);
      const sameTabs=request.mode==='tab'&&oldTabs.length===sections.length&&oldTabs.every((n,i)=>n.textContent===sections[i].action.label);
      const oldFilter=[...surface().querySelectorAll('.plugin-identity-filter')].findIndex(n=>n.getAttribute('aria-pressed')==='true');
      const direction=request.mode==='back'||sameTabs&&newIndex<oldIndex||request.mode==='filter'&&rail.findIndex(n=>n.selected)<oldFilter?-1:1;
      const commit=()=>{
        pending=null;commitPage(doc,request,continuation);
        if(sameTabs)window.AuralisPageMotion.indicator(surface().querySelector('.plugin-tab-indicator'),priorIndicator,api.reduced());
        // An explicitly opened discussion owns its own pane motion; do not double its
        // displacement/opacity by also animating the containing panel.
        return surface().querySelector(['discussion','quote-discussion'].includes(request.mode)?'.plugin-page-cards':'#pluginTabPanel');
      };
      if(append||request.mode==='refresh'){commit();return;}
      motion.swap(surface().querySelector('#pluginTabPanel'),commit,{direction,
        valid:()=>pending===request&&visible()&&allowed()});
    }
    function commitPage(doc,request,continuation){
      const append=request.mode==='append';
      if(!append){
        discussion?.close();
        const previous=history.at(-1);rememberView();
        readingLayout=doc.layout;surface().dataset.readingLayout=readingLayout;
        if(request.mode==='back')history.pop();
        else if(['tab','filter','query','updates'].includes(request.mode)&&history.length)history[history.length-1]={navigation:request.navigation,inputValues:request.inputValues,scroll:0,views:['tab','filter'].includes(request.mode)?previous.views:new Map()};
        else if(request.mode!=='refresh'||!history.length)history.push({navigation:request.navigation,inputValues:request.inputValues,scroll:0,continuation:!!continuation});
        if(history.length>64)history.shift();
        const current=history.at(-1),key=viewKey(doc);
        current.shell={...doc,cards:[],next:null};
        const saved=request.mode==='back'?current?.snapshot:['tab','filter'].includes(request.mode)?current?.views?.get(key):null;
        current.updates=continuation?null:doc.updates;
        current.title=String(doc.title||label(entry));current.viewKey=key;
        navigationContext();
        const profile=profileHeader(doc);
        if(saved?.title===current.title&&restoreView(saved,request.mode)){current.snapshot=saved;return;}
        if(request.mode==='refresh'){current.views=new Map();current.snapshot=null;}
        viewRenderedAt=Date.now();viewLanguage=document.documentElement.lang;
        surface().querySelector('#pluginPageTitle').textContent=String(doc.title||label(entry));
        const railScroll=surface().querySelector('.plugin-identity-rail')?.scrollLeft||0;
        stopLayout();const content=surface().querySelector('.plugin-page-content');content.replaceChildren();
        if(!profile){const intro=node('div',null,'plugin-page-intro'),picture=image(doc.image);if(picture)intro.append(picture);
          if(doc.description)intro.append(node('p',doc.description,'plugin-page-description'));if(intro.childNodes.length)content.append(intro);}
        identityNavigation(doc.navigation||[],content);
        if(['tab','filter'].includes(request.mode)){const row=content.querySelector('.plugin-identity-rail');if(row)row.scrollLeft=railScroll;}
        tabs(doc.tabs||[],content);
        const updateNotice=node('div',null,'plugin-page-updates');updateNotice.hidden=true;updateNotice.setAttribute('role','status');content.append(updateNotice);
        if(doc.query)content.append(window.AuralisPageQuery.create(doc.query,(handle,values)=>{
          cancel();read(handle,'query',values);
        },()=>{queryDirty=true;cancel();status();}));
        const panel=node('div');panel.id='pluginTabPanel';
        if(doc.tabs?.length){panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby','pluginTab-'+doc.tabs.findIndex(t=>t.selected));panel.tabIndex=0;}
        const grid=node('div',null,'plugin-page-cards '+(doc.layout!=='cards'?'is-list':''));panel.append(grid);
        if(doc.layout==='cards'&&doc.cards.length){
          // Catalogue density follows declarative card data, never a platform ID. Long-form posts
          // keep their comfortable reading columns even when the creator header spans a wide window.
          if(doc.cards.every(item=>art(item.image)&&!item.media&&!item.discussionHandle&&!item.images?.length))grid.classList.add('is-artwork-catalogue');
          else if(doc.cards.every(item=>item.author&&!item.title&&!item.media&&!item.discussionHandle&&!item.images?.length&&item.actions?.length))grid.classList.add('is-identity-catalogue');
        }
        if(doc.layout==='detail'){const comments=node('section',null,'plugin-detail-discussion');comments.setAttribute('aria-label',t('评论'));panel.append(comments);panel.classList.add('plugin-detail-layout');}
        const footer=node('div',null,'plugin-page-actions');actions(doc.actions,footer);panel.append(footer);content.append(panel);
        cardsSeen.clear();collection=doc.collectionHandle;emptyPages=0;queryDirty=false;eagerCovers=0;
      }
      const grid=surface().querySelector('.plugin-page-cards');let added=0;const revealed=[];
      for(const item of doc.cards){
        const key=item.handle||('legacy-'+cardsSeen.size);if(cardsSeen.has(key))continue;
        cardsSeen.add(key);const element=card(item);grid.append(element);revealed.push(element);added++;
      }
      emptyPages=append&&added===0?emptyPages+1:0;next=validAction(doc.next)?doc.next:null;
      status(cardsSeen.size?'':!next&&doc.query?t('暂无内容'):doc.description||surface().querySelector('.plugin-page-intro,.plugin-profile-avatar')?'':t('暂无内容'));
      layoutCards();
      navigationContext();
      if(!append){scroller().scrollTop=['back','refresh'].includes(request.mode)?(history.at(-1)?.scroll||0):0;
        (request.mode==='filter'?surface().querySelector('.plugin-identity-filter[aria-pressed="true"]'):request.mode==='tab'?surface().querySelector('[role="tab"][aria-selected="true"]'):null)?.focus({preventScroll:true});
        if(request.mode==='query')surface().querySelector('.plugin-query-text,.plugin-query-submit')?.focus({preventScroll:true});
        else if(!['tab','filter','refresh'].includes(request.mode)){
          const saved=request.mode==='back'?history.at(-1)?.focus:null;
          const candidate=saved?[...surface().querySelectorAll('.plugin-page-content button')][saved.index]:null;
          (candidate&&candidate.textContent===saved.text?candidate:surface().querySelector('#pluginPageTitle')).focus({preventScroll:true});
        }
      }
      // A refresh updates existing reading content without replaying its entrance or stealing focus.
      if(request.mode==='refresh')surface().querySelector('[data-plugin-refresh]')?.focus({preventScroll:true});
      else if(append||!['refresh','discussion','quote-discussion'].includes(request.mode))
        window.AuralisPageMotion?.reveal(revealed,api.reduced(),request.mode==='back');
      // Opening a detail is the reading intent. Load its declared discussion once without
      // stealing focus/scroll from the post; explicit comment navigation focuses the pane.
      if(!append)openDetailDiscussion(request.mode==='quote-discussion',['discussion','quote-discussion'].includes(request.mode));
      if(!append)updates?.configure(history.at(-1)?.updates);else updates?.schedule();
    }
    function open(item,pageEntry){
      discussion?.close();
      if(!pageEntry||!entries(item).some(e=>e.id===pageEntry.id))return;
      cancel();gallery.close(true);stopLayout();closing?.cancel();closing=null;
      if(!active)origin=document.activeElement;
      if(dialog.open)dialog.close();
      clearReadingViews();track=item;entry=pageEntry;cardsSeen.clear();next=null;collection=null;emptyPages=0;queryDirty=false;active=true;main=entry.presentation==='page';
      updates?.clear();
      contextKey=api.pageContext?.(track);
      dialog.getAnimations().forEach(a=>a.cancel());dialog.replaceChildren();page.replaceChildren();page.classList.remove('has-profile-header');
      const header=node('header',null,main?'page-header plugin-page-header':'');
      const back=button(t('返回'),()=>{if(pending)cancel();if(history.length>1){const prior=history[history.length-2];read(prior.navigation,'back',prior.inputValues);}else if(main)close();});
      back.dataset.pluginBack='';back.hidden=!main;
      const title=node(main?'h1':'h2',label(entry));title.id='pluginPageTitle';title.tabIndex=-1;title.dataset.i18nSkip='';
      const exit=button('',()=>close());exit.className='icon-button';exit.setAttribute('aria-label',t('关闭'));exit.append(window.AuralisPageGallery.closeIcon());
      const refresh=button(t('刷新'),()=>{if(pending)cancel();const current=history.at(-1);read(current?.navigation||null,'refresh',current?.inputValues);});
      refresh.dataset.pluginRefresh='';refresh.dataset.i18nSkip='';back.dataset.i18nSkip='';uiLanguage=document.documentElement.lang;
      header.append(back,title,refresh);if(!main)header.append(exit);
      const body=node('div',null,'media-dialog-body');if(!main)body.tabIndex=0;
      const content=node('div',null,'plugin-page-content');content.dataset.i18nSkip='';
      const context=node('div',null,'plugin-page-context');context.dataset.i18nSkip='';context.hidden=true;body.append(context);
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
        e.preventDefault();e.stopImmediatePropagation();if(discussion?.back())return;const back=surface().querySelector('[data-plugin-back]');back?.click();
      }
    },true);
    window.addEventListener('beforeunload',()=>{cancel();stopLayout();});
    function pauseUpdates(){updates?.pause();if(pending?.mode==='check')cancel();}
    function resumeContinuation(){const feedback=surface().querySelector('.plugin-page-status');if(observer&&feedback&&visible()){observer.unobserve(feedback);observer.observe(feedback);}}
    document.addEventListener('visibilitychange',()=>{if(document.hidden){stopLayout();pauseUpdates();}else if(visible()){layoutCards();resumeContinuation();updates?.schedule();}});
    window.addEventListener('offline',pauseUpdates);window.addEventListener('online',()=>{resumeContinuation();updates?.schedule();});
    function choose(item){
      const options=entries(item).filter(e=>e.placement==='media');if(options.length===1){open(item,options[0]);return;}if(!options.length)return;
      cancel();clearReadingViews();closing?.cancel();closing=null;origin=document.activeElement;track=item;entry=null;main=false;active=true;contextKey=api.pageContext?.(track);
      dialog.getAnimations().forEach(a=>a.cancel());dialog.replaceChildren();
      const header=node('header'),title=node('h2',t('插件页面'));title.id='pluginPageTitle';header.append(title,button(t('关闭'),()=>close()));
      const body=node('div',null,'media-dialog-body'),choices=node('div',null,'plugin-page-actions');
      for(const option of options){const b=button(label(option),()=>open(item,option));b.dataset.i18nSkip='';choices.append(b);}
      body.append(choices);dialog.append(header,body);if(!dialog.open)dialog.showModal();choices.firstElementChild.focus();
    }
    function syncNavigation(){
      if(!navigation)return;
      const discovering=api.state.platformConfiguration?.loaded===false;
      if(discovering){
        const signature='loading:'+document.documentElement.lang;
        navigation.hidden=false;navigation.setAttribute('aria-busy','true');
        if(navigationSignature!==signature){
          navigationSignature=signature;
          const loading=window.AuralisLoading.create(t('正在读取…'),{initial:true,reduced:api.reduced()});
          loading.setAttribute('role','status');
          navigation.replaceChildren(node('div',t('插件页面'),'nav-section-label'),loading);
        }
        return;
      }
      navigation.removeAttribute('aria-busy');
      const providers=api.pageProviders?.()||[],rows=[];
      for(const p of providers)if(api.capabilities(p).includes('GlobalPages'))for(const e of entries(p))
        if(e.placement==='global'&&e.presentation==='page'&&e.documentVersion>=3&&e.documentVersion<=9)rows.push({p,e});
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
        const busy=selected&&!!pending&&pending.mode!=='check';
        b.setAttribute('aria-busy',String(busy));
        const icon=b.querySelector('.nav-icon'),ring=icon.querySelector('.loading-ring');
        if(busy&&!ring){const n=node('span',null,'loading-ring');n.setAttribute('aria-hidden','true');icon.append(n);}
        else if(!busy)ring?.remove();
      }
      if(globalActive)for(const b of document.querySelectorAll('.sidebar > .navigation .nav-item,.sidebar > .settings-link'))b.classList.remove('active');
    }
    function syncLanguage(){
      if(!active||uiLanguage===document.documentElement.lang)return;uiLanguage=document.documentElement.lang;
      const refresh=surface().querySelector('[data-plugin-refresh]'),back=surface().querySelector('[data-plugin-back]');
      if(refresh)refresh.textContent=t('刷新');if(back)back.textContent=t('返回');
      for(const label of surface().querySelectorAll('.plugin-quote-label'))label.textContent=t('转发原文');
      for(const toggle of surface().querySelectorAll('.plugin-text-toggle'))toggle.textContent=t(toggle.getAttribute('aria-expanded')==='true'?'收起全文':'展开全文');
      for(const control of surface().querySelectorAll('[data-page-localized]'))localControls.get(control)?.();
      for(const when of surface().querySelectorAll('time[datetime]'))formatDate(when);
      navigationContext();
    }
    function render(){if(active&&main){api.content.replaceChildren(page);page.classList.remove('is-leaving');layoutCards();}syncLanguage();syncNavigation();}
    return {open,choose,receive,entries,label,render,creator:item=>entries(item).find(e=>e.placement==='creator'),
      sync:()=>{
        if(active&&!allowed())close(true);
        else if(active&&main&&api.state.currentPage!=='plugin'){discussion?.close();cancel();updates?.clear();gallery.close(true);stopLayout();active=false;clearReadingViews();}
        syncLanguage();
        syncNavigation();
      }};
  }
  return {create};
})();
