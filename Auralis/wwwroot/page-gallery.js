/* Shared read-only image viewer. Only host artwork handles, never navigation URLs. */
window.AuralisPageGallery = (() => {
  const art = url => typeof url==='string' && /^https:\/\/platform-art\.auralis\.local\/[a-z0-9-]+$/i.test(url);
  function create(api, className='') {
    const t=s=>window.AuralisI18n?.t(s)||s,dialog=document.createElement('dialog');
    dialog.className='media-hub-dialog plugin-gallery'+(className?' '+className:'');
    document.body.append(dialog);
    let images=[],index=0,retries=0,origin,closing,entering,reveal,active=false,revision=0,timer,image;
    let body,stage,status,counter,previous,next,exit,fit,reading,mode='fit';
    const button=(text,fn)=>{const n=document.createElement('button');n.type='button';n.className='secondary-button';n.textContent=text;n.addEventListener('click',fn);return n;};
    function cancelLoad(){
      revision++;clearTimeout(timer);timer=null;reveal?.cancel();reveal=null;
      if(image){image.onload=image.onerror=null;image.removeAttribute('src');image=null;}
    }
    function close(immediate=false){
      if(!dialog.open)return;
      active=false;cancelLoad();entering?.cancel();closing?.cancel();
      const finish=()=>{dialog.close();dialog.replaceChildren();images=[];origin?.isConnected&&origin.focus({preventScroll:true});origin=null;};
      if(immediate||api.reduced()||document.hidden){finish();closing=null;return;}
      closing=dialog.animate([{opacity:1},{opacity:0}],{duration:140});const current=closing;
      current.finished.then(()=>{if(closing===current){finish();current.cancel();closing=null;}}).catch(()=>{});
    }
    function setMode(value){
      mode=value;body.dataset.galleryMode=mode;body.scrollTop=body.scrollLeft=0;
      fit.setAttribute('aria-pressed',String(mode==='fit'));reading.setAttribute('aria-pressed',String(mode==='reading'));
    }
    function load(retry=false){
      if(retry)retries++;
      cancelLoad();const request=revision;
      // Only the selected image is requested. No neighbour prefetch or unbounded decoded image cache.
      body.scrollTop=body.scrollLeft=0;body.setAttribute('aria-busy','true');
      if(status.contains(document.activeElement))body.focus({preventScroll:true});
      status.replaceChildren(window.AuralisLoading.create(t('正在加载图片…'),{reduced:api.reduced()}));status.hidden=false;
      stage.replaceChildren();stage.hidden=true;counter.textContent=(index+1)+' / '+images.length;
      previous.disabled=index===0;next.disabled=index===images.length-1;
      const current=document.createElement('img');image=current;current.alt=t('动态图片');current.referrerPolicy='no-referrer';current.decoding='async';
      const valid=()=>active&&request===revision&&image===current;
      const failed=()=>{
        if(!valid())return;cancelLoad();stage.replaceChildren();stage.hidden=true;body.setAttribute('aria-busy','false');
        const text=document.createElement('p');text.textContent=t('图片暂不可用');
        const again=button(t('重试'),()=>load(true));again.dataset.gallery='retry';status.replaceChildren(text,again);status.hidden=false;
      };
      current.onload=()=>{
        if(!valid())return;clearTimeout(timer);timer=null;current.onload=current.onerror=null;
        body.setAttribute('aria-busy','false');status.replaceChildren();status.hidden=true;stage.hidden=false;
        if(!api.reduced()&&!document.hidden)reveal=current.animate([{opacity:0},{opacity:1}],{duration:160,easing:'ease-out'});
      };
      current.onerror=failed;stage.append(current);timer=setTimeout(failed,20000);
      current.src=retries?window.AuralisLoading.retryImageUrl(images[index],retries):images[index];
    }
    function move(delta){
      if(!active||index+delta<0||index+delta>=images.length)return;
      index+=delta;retries=0;load();
      if(document.activeElement===previous&&previous.disabled)next.focus({preventScroll:true});
      if(document.activeElement===next&&next.disabled)previous.focus({preventScroll:true});
    }
    function render(){
      dialog.replaceChildren();dialog.setAttribute('aria-label',t('查看图片'));
      const header=document.createElement('header');counter=document.createElement('span');counter.setAttribute('role','status');counter.setAttribute('aria-live','polite');
      exit=button('',()=>close());exit.className='icon-button';exit.dataset.gallery='close';exit.setAttribute('aria-label',t('关闭'));exit.append(closeIcon());
      const modes=document.createElement('div');modes.className='plugin-gallery-modes';modes.setAttribute('role','group');modes.setAttribute('aria-label',t('图片显示方式'));
      fit=button(t('适应窗口'),()=>setMode('fit'));reading=button(t('长图阅读'),()=>setMode('reading'));modes.append(fit,reading);header.append(counter,modes,exit);
      body=document.createElement('div');body.className='media-dialog-body';body.tabIndex=0;body.setAttribute('aria-label',t('图片阅读区域'));
      stage=document.createElement('div');stage.className='plugin-gallery-stage';status=document.createElement('div');status.className='plugin-gallery-status';status.setAttribute('role','status');
      body.append(stage,status);
      const footer=document.createElement('footer');previous=button(t('上一张'),()=>move(-1));next=button(t('下一张'),()=>move(1));
      previous.dataset.gallery='previous';next.dataset.gallery='next';footer.append(previous,next);dialog.append(header,body,footer);setMode('fit');
    }
    dialog.addEventListener('cancel',e=>{e.preventDefault();close();});
    dialog.addEventListener('keydown',e=>{
      e.stopPropagation();if(!active)return;
      if(e.key==='ArrowLeft'){e.preventDefault();move(-1);}
      if(e.key==='ArrowRight'){e.preventDefault();move(1);}
    });
    document.addEventListener('visibilitychange',()=>{if(document.hidden){entering?.cancel();reveal?.cancel();if(closing)close(true);}});
    return {close,open:(values,selected=0)=>{
      const approved=(values||[]).filter(art).slice(0,9);if(!approved.length)return;
      closing?.cancel();closing=null;entering?.cancel();cancelLoad();
      if(!dialog.open)origin=document.activeElement;
      images=approved;index=Number.isInteger(selected)?Math.max(0,Math.min(selected,images.length-1)):0;retries=0;active=true;
      render();if(!dialog.open)dialog.showModal();exit.focus({preventScroll:true});load();
      if(!api.reduced()&&!document.hidden)entering=dialog.animate([{opacity:0,transform:'translateY(6px)'},{opacity:1,transform:'none'}],{duration:220,easing:'ease-out'});
    }};
  }
  function closeIcon(){
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');
    const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d','m6 6 12 12M18 6 6 18');path.setAttribute('stroke','currentColor');path.setAttribute('stroke-width','1.5');
    svg.append(path);return svg;
  }
  return {create,art,closeIcon};
})();
