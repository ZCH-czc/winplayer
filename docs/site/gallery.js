/* Atomic image changes: load first, then crossfade; a stale load never wins. */
(() => {
  'use strict';
  const pending=new WeakMap();
  window.AuralisGallery={show(container,src,alt){
    if(pending.get(container)?.src===src)return;
    const ticket={src};pending.set(container,ticket);
    const image=new Image();image.className='gallery-image';image.alt=alt;
    image.onload=()=>{
      if(pending.get(container)!==ticket)return;
      const old=[...container.querySelectorAll('img')];
      // Keep just the visible outgoing picture when clicks arrive mid-transition.
      old.filter(el=>!el.classList.contains('is-current')).forEach(el=>el.remove());
      container.append(image);
      requestAnimationFrame(()=>{
        if(pending.get(container)!==ticket){image.remove();return;}
        image.classList.add('is-current');
        old.forEach(el=>el.classList.remove('is-current'));
        setTimeout(()=>old.forEach(el=>el.remove()),420);
      });
      container.dataset.image=src;delete container.dataset.error;
    };
    image.onerror=()=>{if(pending.get(container)===ticket){pending.delete(container);container.dataset.error='true';}};
    image.src=src;
  }};
})();
