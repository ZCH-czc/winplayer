/* Host-owned, read-only discussion surface. No provider IDs, routes, URLs or write commands. */
window.AuralisPageDiscussion = (() => {
  function create(api, allocateId, lifecycle = {}) {
    const t=s=>window.AuralisI18n?.t(s)||s;
    const node=(tag,text,cls)=>{const n=document.createElement(tag);if(text!=null)n.textContent=text;if(cls)n.className=cls;return n;};
    const button=(text,fn)=>{const n=node('button',text,'secondary-button');n.type='button';n.onclick=fn;return n;};
    let host,subject,revision,rootView,replyView,selected,observer,pending,origin,language,replyCapability,reduced;
    const fresh=()=>({items:[],next:null,error:'',loaded:false,scroll:0,windows:[]});
    // The owner supplies its lifetime, not platform rules. Both containers share the same
    // revision/subject guards; a drawer must not inherit the main-page route requirement.
    const active=()=>!!host?.isConnected&&(lifecycle.active?lifecycle.active(subject):api.state.currentPage==='plugin')&&revision===api.pageContext?.(subject)&&api.capabilities(subject).includes('Comments');
    const heading=()=>lifecycle.heading?.()||host?.querySelector('h2');
    const view=()=>selected?replyView:rootView;
    const kind=()=>selected?'replies':'comments';
    let entering;
    // Keep the discussion heading still; only the reading pane follows its navigation direction.
    function transition(backwards=false){
      entering?.cancel();entering=window.AuralisPageMotion?.enter(host?.querySelector('.discussion-scroll'),api.reduced(),backwards?-1:1);
      const current=entering;current?.finished.then(()=>{if(entering===current)entering=null;},()=>{if(entering===current)entering=null;});
    }
    function cancel(){observer?.disconnect();observer=null;if(pending){clearTimeout(pending.timer);api.post('cancelPlatformExtras',{kind:pending.kind});pending=null;}}
    function close(preserveContent=false){cancel();entering?.cancel();entering=null;host?.getAnimations().forEach(a=>a.cancel());if(!preserveContent){host?.replaceChildren();host?.classList.remove('is-open');}host=null;subject=null;rootView=replyView=selected=null;}
    function back(){
      if(!selected)return false;
      cancel();selected=null;replyView=null;render();host.querySelector('.discussion-scroll').scrollTop=rootView.scroll;
      [...host.querySelectorAll('[data-reply]')].find(n=>n.dataset.reply===origin)?.focus({preventScroll:true});transition(true);return true;
    }
    function open(target,item,{focus=true}={}){
      lifecycle.beforeOpen?.();close();host=target;subject=item;revision=api.pageContext?.(item);rootView=fresh();selected=null;language=document.documentElement.lang;
      if(!active()){close();return;}
      host.classList.add('is-open','discussion-surface');render();if(focus)heading()?.focus({preventScroll:true});request();if(focus)transition();
    }
    function request(page=null,segment=false){
      if(!active()||pending||selected&&!api.capabilities(subject).includes('CommentReplies'))return;
      const v=view(),req={id:allocateId(),kind:kind(),handle:subject.handle,root:selected?.handle,page,segment,v};
      pending=req;v.error='';
      req.timer=setTimeout(()=>{if(pending!==req)return;cancel();v.error=t('读取超时，请重试。');v.failed=req;render();},35000);
      render();api.post('requestPlatformExtras',{handle:req.handle,kind:req.kind,requestId:req.id,pageHandle:page,
        sort:rootView.sort||'recommended',...(req.root?{rootHandle:req.root}:{})});
    }
    function receive(p){
      if(!pending||p?.requestId!==pending.id||p.handle!==pending.handle||p.kind!==pending.kind)return false;
      if(!active()){close();lifecycle.invalidated?.();return true;}
      if(selected&&!api.capabilities(subject).includes('CommentReplies')){rootView.list=null;back();return true;}
      const req=pending,v=req.v,firstLoad=!v.loaded;clearTimeout(req.timer);pending=null;
      if(p.error){v.error=p.error.message||t('评论暂不可用');v.failed=req;render();return true;}
      if(!Array.isArray(p.items)||p.items.length>100){v.error=t('插件页面格式不受支持或超出限制。');v.failed=req;render();return true;}
      if(req.segment){
        v.windows.push({items:v.items,next:v.next,scroll:host.querySelector('.discussion-scroll').scrollTop});
        if(v.windows.length>3)v.windows.shift();v.items=[];v.list=null;
      }
      const known=new Set(v.items.map(c=>c.handle));
      const batch=p.items.filter(c=>c&&/^community-[a-f0-9]{32}$/.test(c.handle||'')&&!known.has(c.handle)&&known.add(c.handle));
      if(v.items.length+batch.length>200){v.error=t('插件页面格式不受支持或超出限制。');v.failed=req;render();return true;}
      v.items.push(...batch);v.loaded=true;v.next=p.nextPageHandle&&p.nextPageHandle!==req.page?p.nextPageHandle:null;
      // Empty/stalled batches never start another automatic request.
      v.manual=batch.length===0;v.failed=null;render();
      if(req.segment)host.querySelector('.discussion-scroll').scrollTop=0;
      // Initial content can grow beyond the loading placeholder on narrow windows. Keep the
      // explicitly focused discussion reachable above the persistent playback bar.
      if(!lifecycle.heading&&firstLoad&&host.contains(document.activeElement)&&matchMedia('(max-width:1100px)').matches)host.scrollIntoView({block:'start'});
      return true;
    }
    function textContent(c){
      const p=node('p',null,'discussion-text'),text=String(c.text||'').slice(0,32000);
      const emotes=new Map((api.reduced()?[]:c.emotes||[]).slice(0,64).filter(e=>typeof e.text==='string'&&e.text.length>0&&e.text.length<=100&&window.AuralisPageGallery.art(e.url)).map(e=>[e.text,e.url]));
      if(!emotes.size){p.textContent=text;return p;}
      const pattern=new RegExp([...emotes.keys()].sort((a,b)=>b.length-a.length).map(s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')).join('|'),'g');
      let at=0,count=0;for(const match of text.matchAll(pattern)){
        if(++count>100)break;
        p.append(document.createTextNode(text.slice(at,match.index)));const img=node('img');img.className='comment-emote';img.alt=match[0];img.src=emotes.get(match[0]);img.loading='lazy';img.referrerPolicy='no-referrer';
        img.onerror=()=>img.replaceWith(document.createTextNode(img.alt));p.append(img);at=match.index+match[0].length;
      }p.append(document.createTextNode(text.slice(at)));return p;
    }
    function row(c,isRoot=false){
      const article=node('article',null,'discussion-comment'),avatar=node('span',Array.from(c.author||'?')[0],'comment-avatar');article.dataset.i18nSkip='';avatar.setAttribute('aria-hidden','true');
      if(window.AuralisPageGallery.art(c.avatarUrl)){
        const source=c.avatarUrl,img=node('img');let retries=0;
        img.alt='';img.loading='lazy';img.referrerPolicy='no-referrer';
        img.onerror=()=>{
          img.remove();avatar.removeAttribute('aria-hidden');
          const retry=button('↻',()=>{retries++;retry.remove();img.src=window.AuralisLoading.retryImageUrl(source,retries);avatar.append(img);});
          retry.className='plugin-avatar-retry';retry.setAttribute('aria-label',t('重试头像'));retry.title=t('重试头像');avatar.append(retry);
        };
        img.onload=()=>avatar.setAttribute('aria-hidden','true');img.src=source;window.AuralisLoading.image(img);avatar.append(img);
      }
      const body=node('div',null,'comment-content');body.append(node('strong',c.author||''));
      if(c.replyToAuthor)body.append(node('span',' ↪ '+c.replyToAuthor,'discussion-reply-to'));
      body.append(textContent(c));const meta=node('div',null,'comment-meta');
      const date=new Date(c.publishedAt);if(c.publishedAt&&Number.isFinite(date.getTime())){const when=node('time',date.toLocaleString(document.documentElement.lang));when.dateTime=date.toISOString();meta.append(when);}
      meta.append(node('span','♡ '+Math.max(0,Number(c.likeCount)||0)));body.append(meta);
      if(!isRoot&&!selected&&c.replyCount>0&&api.capabilities(subject).includes('CommentReplies')){
        const b=button(t('查看回复')+' · '+c.replyCount,()=>{
          if(!active()||!api.capabilities(subject).includes('CommentReplies'))return;
          rootView.scroll=host.querySelector('.discussion-scroll').scrollTop;origin=c.handle;cancel();selected=c;replyView=fresh();render();host.querySelector('.discussion-scroll').scrollTop=0;heading()?.focus({preventScroll:true});request();transition();
        });b.dataset.reply=c.handle;meta.append(b);
      }
      article.append(avatar,body);return article;
    }
    function render(){
      if(!active())return;
      replyCapability=api.capabilities(subject).includes('CommentReplies');reduced=api.reduced();
      observer?.disconnect();observer=null;
      const old=host.querySelector('.discussion-scroll'),scroll=old?.scrollTop||0,focused=host.contains(document.activeElement)?document.activeElement:null,focus=focused?.dataset.control,replyFocus=focused?.dataset.reply;
      const reuse=old?.dataset.viewKind===kind()&&old.dataset.sort===(rootView.sort||'recommended')&&old.dataset.language===language;
      if(!reuse)host.replaceChildren();
      const header=reuse?host.querySelector('header'):node('header');
      if(!reuse){
        const title=lifecycle.heading?.()||node('h2');title.textContent=t(selected?'评论详情':'评论');title.tabIndex=-1;title.dataset.control='heading';
        if(!lifecycle.heading)header.append(title);
        const context=node('p',subject.title||'','discussion-subject');context.dataset.i18nSkip='';header.append(context);
        if(selected){const b=button(t('返回评论'),()=>back());b.dataset.control='back';header.prepend(b);}
        else{
          const sort=node('div',null,'comment-sort');sort.setAttribute('aria-label',t('评论排序'));
          for(const [value,label] of [['recommended','推荐'],['newest','最新']]){
            const b=button(t(label),()=>{if((rootView.sort||'recommended')===value)return;cancel();rootView=fresh();rootView.sort=value;render();request();});
            b.dataset.control=value;b.setAttribute('aria-pressed',String((rootView.sort||'recommended')===value));sort.append(b);
          }header.append(sort);
        }
      }
      const body=reuse?old:node('div',null,'discussion-scroll');body.tabIndex=0;body.dataset.control='scroll';body.setAttribute('aria-label',t(selected?'评论详情':'评论'));
      body.dataset.viewKind=kind();body.dataset.sort=rootView.sort||'recommended';body.dataset.language=language;
      const v=view();body.setAttribute('aria-busy',String(!!pending));
      if(reuse&&(!v.list||body.querySelector('.discussion-list')!==v.list))body.replaceChildren();
      if(selected&&!body.querySelector('.discussion-root')){const root=row(selected,true);root.classList.add('discussion-root');body.append(root);}
      const list=v.list||(v.list=node('div',null,'discussion-list'));
      const added=[];for(const c of v.items.slice(list.children.length)){const n=row(c);list.append(n);added.push(n);}body.append(list);
      const footer=v.footer||(v.footer=node('div',null,'discussion-footer'));footer.setAttribute('role','status');
      let text='',label='',action=null;
      if(pending)text=t('正在读取…');
      else if(v.error){text=v.error;label=t('重试');action=()=>request(v.failed.page,v.failed.segment);}
      else if(v.next){const segment=v.items.length>=100;label=t(segment?'继续阅读':'加载更多');action=()=>request(v.next,segment);}
      else if(v.loaded)text=t(v.items.length?'已加载当前可见的评论':'暂无评论。');
      window.AuralisLoading.continuation(footer,{pending:!!pending,initial:!v.loaded&&!v.items.length,text,label,action,kind:v.error?'retry':'more',reduced:api.reduced()});
      footer.querySelector('[data-previous-window]')?.remove();
      if(v.windows.length){const previous=button(t('上一段'),()=>{cancel();const saved=v.windows.pop();Object.assign(v,saved,{error:'',manual:true,list:null});render();host.querySelector('.discussion-scroll').scrollTop=saved.scroll;});previous.dataset.previousWindow='';previous.className='continuation-button';footer.append(previous);}
      [...footer.querySelectorAll('button')].forEach((b,i)=>b.dataset.control='footer-'+i);
      body.append(footer);if(!reuse)host.append(header,body);body.scrollTop=scroll;
      // A fast response replaces the placeholder while retaining the in-flight pane motion.
      if(entering?.playState==='running')entering.effect.target=body;
      else window.AuralisPageMotion?.reveal(added,api.reduced());
      if(focus)[...host.querySelectorAll('[data-control]')].find(b=>b.dataset.control===focus)?.focus({preventScroll:true});
      if(replyFocus)([...host.querySelectorAll('[data-reply]')].find(b=>b.dataset.reply===replyFocus)||heading())?.focus({preventScroll:true});
      // The full-page detail owns an infinite reading pane. Compact modal comments retain
      // an explicit continuation after 100 rows so they cannot race past keyboard readers.
      if(!pending&&!v.error&&v.next&&!v.manual&&!document.hidden&&(!lifecycle.heading||v.items.length<100)){
        observer=new IntersectionObserver(rows=>{if(rows.some(r=>r.isIntersecting)&&active()&&!pending&&!document.hidden&&(lifecycle.canAutoLoad?lifecycle.canAutoLoad():!document.querySelector('dialog[open],#nowPlayingOverlay.open')))request(v.next,v.items.length>=100);},{root:body,rootMargin:'0px 0px 100px'});observer.observe(footer);
      }
    }
    document.addEventListener('visibilitychange',()=>{if(document.hidden){observer?.disconnect();observer=null;}else if(active())render();});
    window.addEventListener('beforeunload',()=>close());
    return {open,close,receive,back,sync:()=>{
      if(host&&!active()){close();lifecycle.invalidated?.();}
      else if(host&&selected&&!api.capabilities(subject).includes('CommentReplies')){rootView.list=null;back();}
      else if(host&&(language!==document.documentElement.lang||replyCapability!==api.capabilities(subject).includes('CommentReplies')||reduced!==api.reduced())){language=document.documentElement.lang;rootView.list=null;if(replyView)replyView.list=null;render();}
    }};
  }
  return {create};
})();
