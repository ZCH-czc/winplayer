/* Shared presentation only. Request ownership, cancellation and timeouts stay with each caller. */
window.AuralisLoading = (() => {
  function create(text, {initial = false, reduced = false} = {}) {
    const host = document.createElement('div');
    host.className = 'loading-state' + (initial ? ' is-initial' : '');
    host.dataset.reduced = String(reduced);
    const line = document.createElement('div');line.className = 'loading-state-line';
    const ring = document.createElement('span');ring.className = 'loading-ring';ring.setAttribute('aria-hidden', 'true');
    const label = document.createElement('p');label.textContent = text;
    line.append(ring, label);host.append(line);
    if (initial) {
      // A bounded neutral placeholder, not fictional posts, counts or progress.
      const placeholder = document.createElement('div');placeholder.className = 'loading-placeholder';
      placeholder.setAttribute('aria-hidden', 'true');
      for (let i = 0; i < 3; i++) placeholder.append(document.createElement('span'));
      host.append(placeholder);
    }
    return host;
  }
  const continuations = new WeakMap();
  // Keep one keyboard target while More becomes Loading, Retry or End. Callers still own
  // cursors and cancellation; this presenter never schedules network requests.
  function continuation(host, {pending=false, initial=false, text='', label='', action=null, kind='more', reduced=false} = {}) {
    let state=continuations.get(host);
    if(!state || state.row.parentElement!==host){
      const row=document.createElement('div');row.className='continuation-row';
      const message=document.createElement('p');message.className='continuation-message';
      const control=document.createElement('button');control.type='button';control.className='continuation-button';
      const icon=document.createElement('span');icon.className='continuation-cue';icon.setAttribute('aria-hidden','true');
      icon.innerHTML='<svg viewBox="0 0 20 20" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path/></svg>';
      const caption=document.createElement('span');caption.className='continuation-caption';
      const loading=create('');loading.hidden=true;
      control.append(icon,caption,loading);row.append(message,control);host.replaceChildren(row);
      state={row,message,control,caption,icon,loading};continuations.set(host,state);
    }
    const {row,message,control,caption,icon,loading}=state;
    const focused=host.contains(document.activeElement),before=row.dataset.phase;
    row.dataset.phase=pending?'pending':action?'action':'end';host.tabIndex=-1;
    message.textContent=pending?'':text;message.hidden=!message.textContent;
    control.hidden=!pending&&!action;control.setAttribute('aria-disabled',String(pending));
    control.onclick=()=>{if(!pending)action?.();};
    caption.textContent=label;caption.hidden=pending;icon.hidden=pending;
    icon.querySelector('path').setAttribute('d',kind==='retry'?'M15 6a6 6 0 1 0 1 7 M15 2v4h-4':kind==='browse'?'m8 5 5 5-5 5':'m5 8 5 5 5-5');
    loading.hidden=!pending;loading.dataset.reduced=String(reduced);
    if(pending)control.append(loading);else loading.remove();
    loading.classList.toggle('is-initial',initial);
    loading.querySelector('p').textContent=pending?text:'';
    // The initial skeleton is separate from the persistent paging target.
    state.placeholder?.remove();state.placeholder=null;
    if(pending&&initial){state.placeholder=create('',{initial:true,reduced}).querySelector('.loading-placeholder');row.append(state.placeholder);}
    if(focused)(control.hidden?host:control).focus({preventScroll:true});
    if(before&&before!==row.dataset.phase)window.AuralisPageMotion?.fade(pending?loading:control.hidden?message:caption,reduced);
    return control;
  }
  function image(img) {
    if(!img)return;img.classList.add('soft-image');
    const ready=()=>{if(img.naturalWidth)img.classList.add('is-ready');};
    img.addEventListener('load',ready,{once:true});if(img.complete)ready();
  }
  // A manual retry must issue a new request even if WebView cached the failed response.
  // Only the already-approved opaque artwork handle is reused; no upstream URL is exposed.
  function retryImageUrl(url, attempt) {
    if(typeof url!=='string'||!/^https:\/\/platform-art\.auralis\.local\/[a-z0-9-]+$/i.test(url)||
       !Number.isSafeInteger(attempt)||attempt<1)return '';
    return url+'?retry='+attempt;
  }
  function visibility() { document.documentElement.dataset.loadingPaused = String(document.hidden); }
  document.addEventListener('visibilitychange', visibility);visibility();
  return {create,continuation,image,retryImageUrl};
})();
