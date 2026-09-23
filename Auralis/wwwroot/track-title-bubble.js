/* One accessible, pointer-anchored bubble for truncated online titles. */
(() => {
  let target = null;
  const bubble = document.createElement('div');
  bubble.className = 'platform-title-bubble';
  bubble.id = 'platformTitleBubble';
  bubble.setAttribute('role', 'tooltip');
  document.body.append(bubble);
  function hide() {
    target?.removeAttribute('aria-describedby');
    target = null;
    bubble.classList.remove('is-visible');
  }
  function place(x, y) {
    const width = bubble.offsetWidth, height = bubble.offsetHeight;
    bubble.style.left = `${Math.max(12, Math.min(x + 14, innerWidth - width - 12))}px`;
    bubble.style.top = `${Math.max(12, Math.min(y + 18, innerHeight - height - 12))}px`;
  }
  function show(element, x, y) {
    if (!element || element.scrollWidth <= element.clientWidth + 1) { hide(); return; }
    if (target !== element) {
      hide(); target = element;
      bubble.textContent = element.dataset.fullTitle || element.textContent;
      element.setAttribute('aria-describedby', bubble.id);
      bubble.classList.add('is-visible');
    }
    place(x, y);
  }
  document.addEventListener('pointermove', event => {
    const element = event.target.closest?.('.platform-full-title');
    if (element) show(element, event.clientX, event.clientY);
    else if (target && !target.matches(':focus-visible')) hide();
  }, {passive:true});
  document.addEventListener('focusin', event => {
    const element = event.target.closest?.('.platform-full-title');
    if (element) { const rect = element.getBoundingClientRect(); show(element, rect.left, rect.bottom); }
    else hide();
  });
  document.addEventListener('focusout', () => { if (target && !target.matches(':hover')) hide(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') hide(); });
  addEventListener('scroll', hide, true);
  addEventListener('resize', hide);
})();
