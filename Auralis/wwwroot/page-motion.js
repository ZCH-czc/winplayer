/* Shared content motion: bounded, cancellable and independent of provider identity. */
window.AuralisPageMotion = (() => {
  const running = new Set(), swaps = new Map();
  function track(animation) {
    running.add(animation);
    animation.finished.then(() => { running.delete(animation); animation.cancel(); }, () => running.delete(animation));
    return animation;
  }
  function stop(commit = true) {
    // A hidden/reduced view still commits a validated response; it must never remain inert.
    for (const [finish, cancel] of [...swaps]) (commit ? finish : cancel)();
    for (const animation of running) animation.cancel();
    running.clear();
  }
  const off = reduced => reduced || document.hidden || document.documentElement.dataset.motion === 'reduced';
  function enter(element, reduced = false, direction = 1) {
    if (!element?.isConnected || off(reduced)) return null;
    return track(element.animate([
      {opacity: 0, transform: `translate(${direction * 14}px, 3px)`},
      {opacity: 1, transform: 'none'}
    ], {duration: 240, easing: 'cubic-bezier(.16,1,.3,1)', fill: 'backwards'}));
  }
  // One owner, one cancellable transaction. Only a ready response leaves the old view;
  // request failures never erase it, and a new intent can revoke the pending commit.
  function create(reduced) {
    let cancelExit, incoming;
    function cancel() { cancelExit?.(); cancelExit = null; incoming?.cancel(); incoming = null; }
    function swap(old, commit, {direction = 1, valid = () => true} = {}) {
      cancel();
      const apply = () => { if (valid()) incoming = enter(commit(), reduced(), direction); };
      if (!old?.isConnected || off(reduced())) { apply(); return; }
      const wasInert = old.inert, focus = old.contains(document.activeElement) ? document.activeElement : null;
      old.inert = true;
      const animation = old.animate([{opacity: 1, transform: 'none'},
        {opacity: 0, transform: `translateX(${-direction * 6}px)`}],
        {duration: 100, easing: 'cubic-bezier(.4,0,1,1)', fill: 'forwards'});
      let done = false;
      const release = () => { done = true; swaps.delete(finish); animation.cancel(); old.inert = wasInert; cancelExit = null; };
      const finish = () => { if (done) return; release(); apply(); };
      cancelExit = () => { if (done) return; release(); if (focus?.isConnected && document.activeElement === document.body) focus.focus({preventScroll:true}); };
      swaps.set(finish, cancelExit);
      animation.finished.then(finish, () => {});
    }
    return {swap, cancel};
  }
  function indicator(element, previous, reduced = false) {
    if (!element || !previous || off(reduced)) return;
    const current = element.getBoundingClientRect();
    if (!current.width || Math.abs(current.top - previous.top) > 8) return;
    track(element.animate([{transform: `translateX(${previous.left-current.left}px) scaleX(${previous.width/current.width})`},
      {transform: 'none'}], {duration: 280, easing: 'cubic-bezier(.16,1,.3,1)', fill: 'backwards'}));
  }
  function fade(element, reduced=false) {
    if(!element?.isConnected||off(reduced))return;
    element.getAnimations().forEach(a=>a.cancel());
    return track(element.animate([{opacity:.35},{opacity:1}],{duration:180,easing:'ease-out'}));
  }
  function reveal(elements, reduced = false, backwards = false) {
    if (off(reduced)) return;
    // Only new visible nodes participate; long feeds never wait for hundreds of stagger slots.
    let index = 0;
    for (const element of elements) {
      if (index >= 12) break;
      const bounds = element.getBoundingClientRect();
      if (bounds.bottom < 0 || bounds.top > innerHeight) continue;
      const animation = element.animate([
        {opacity: 0, transform: `translateY(${backwards ? -6 : 8}px)`},
        {opacity: 1, transform: 'none'}
      ], {duration: 300, delay: Math.min(index++ * 32, 192), easing: 'cubic-bezier(.16,1,.3,1)', fill: 'backwards'});
      track(animation);
    }
  }
  document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
  new MutationObserver(() => { if (document.documentElement.dataset.motion === 'reduced') stop(); })
    .observe(document.documentElement, {attributes:true, attributeFilter:['data-motion']});
  window.addEventListener('resize', () => stop());
  window.addEventListener('beforeunload', () => stop(false));
  return {reveal, enter, create, indicator, fade};
})();
