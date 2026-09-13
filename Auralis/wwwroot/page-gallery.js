/* Shared read-only image viewer. Only host artwork handles, never navigation URLs. */
window.AuralisPageGallery = (() => {
  const art = url => typeof url==='string' && /^https:\/\/platform-art\.auralis\.local\/[a-z0-9-]+$/i.test(url);
  function create(api) {
    const t=s=>window.AuralisI18n?.t(s)||s,dialog=document.createElement('dialog');
    dialog.className='media-hub-dialog plugin-gallery';
    dialog.setAttribute('aria-label',t('查看图片'));document.body.append(dialog);
    let images=[],index=0,origin,closing;
    const button=(text,fn)=>{const n=document.createElement('button');n.className='secondary-button';n.textContent=text;n.addEventListener('click',fn);return n;};
    function close(immediate=false){
      if(!dialog.open)return;closing?.cancel();
      const finish=()=>{dialog.close();dialog.replaceChildren();origin?.isConnected&&origin.focus({preventScroll:true});};
      if(immediate||api.reduced()){finish();return;}
      closing=dialog.animate([{opacity:1},{opacity:0}],{duration:140});const current=closing;
      current.finished.then(()=>{if(closing===current){finish();current.cancel();closing=null;}}).catch(()=>{});
    }
    function render(focus='close'){
      dialog.replaceChildren();const header=document.createElement('header'),counter=document.createElement('span');
      counter.textContent=(index+1)+' / '+images.length;const exit=button('',()=>close());exit.className='icon-button';exit.setAttribute('aria-label',t('关闭'));exit.append(closeIcon());
      header.append(counter,exit);
      const body=document.createElement('div');body.className='media-dialog-body';const image=document.createElement('img');
      image.src=images[index];image.alt=t('动态图片');image.referrerPolicy='no-referrer';
      image.addEventListener('error',()=>{body.textContent=t('图片暂不可用');},{once:true});body.append(image);
      const footer=document.createElement('footer'),previous=button(t('上一张'),()=>{index--;render('previous');}),next=button(t('下一张'),()=>{index++;render('next');});
      previous.disabled=index===0;next.disabled=index===images.length-1;footer.append(previous,next);dialog.append(header,body,footer);
      const target=focus==='previous'?previous:focus==='next'?next:exit;(target.disabled?exit:target).focus();
    }
    dialog.addEventListener('cancel',e=>{e.preventDefault();close();});
    dialog.addEventListener('keydown',e=>{
      e.stopPropagation();
      if(e.key==='ArrowLeft'&&index>0){e.preventDefault();index--;render('previous');}
      if(e.key==='ArrowRight'&&index+1<images.length){e.preventDefault();index++;render('next');}
    });
    return {close,open:(values,selected=0)=>{
      images=(values||[]).filter(art).slice(0,9);index=Math.max(0,Math.min(selected,images.length-1));if(!images.length)return;
      closing?.cancel();closing=null;origin=document.activeElement;render();if(!dialog.open)dialog.showModal();
      if(!api.reduced())dialog.animate([{opacity:0,transform:'translateY(6px)'},{opacity:1,transform:'none'}],{duration:220,easing:'ease-out'});
    }};
  }
  function closeIcon(){
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');
    const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d','m6 6 12 12M18 6 6 18');path.setAttribute('stroke','currentColor');path.setAttribute('stroke-width','1.5');
    svg.append(path);return svg;
  }
  return {create,art,closeIcon};
})();
