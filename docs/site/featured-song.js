/* Official embed only. Never resolve or expose backend media leases. */
(() => {
  'use strict';
  const section = document.getElementById('featured-song');
  const button = document.getElementById('featured-song-toggle');
  const container = document.getElementById('featured-song-player');
  const status = document.getElementById('featured-song-status');
  let frame = null;
  const isChinese = () => document.documentElement.lang === 'zh-CN';
  function close() {
    // Removing the frame destroys its playback session, not merely its visibility.
    frame?.remove();
    frame = null;
    container.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    button.textContent = '播放《羁绊环游线》';
    status.textContent = '';
  }
  button.addEventListener('click', () => {
    if (!isChinese()) return;
    if (frame) { close(); return; }
    const next = document.createElement('iframe');
    next.title = 'Bilibili 官方播放器：泠鸢yousa《羁绊环游线》';
    next.setAttribute('allow', 'autoplay; fullscreen; picture-in-picture');
    next.setAttribute('allowfullscreen', '');
    next.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-presentation allow-popups');
    next.referrerPolicy = 'strict-origin-when-cross-origin';
    // Created only by an explicit click; never preload third-party scripts or media.
    next.src = 'https://player.bilibili.com/player.html?bvid=BV1jY8t6JE9K&p=1&autoplay=1&muted=0&poster=1&danmaku=0';
    frame = next;
    container.hidden = false;
    container.append(next);
    button.setAttribute('aria-expanded', 'true');
    button.textContent = '关闭试听';
    status.textContent = '若未开始播放，请点击官方播放器的播放键。若平台限制站外播放，可打开下方原作品链接。';
    // A cross-origin iframe load is not evidence that media actually started.
  });
  function languageChanged() {
    if (!isChinese()) close();
    section.hidden = !isChinese();
  }
  window.addEventListener('auralis-language-change', languageChanged);
  window.addEventListener('pagehide', close);
  languageChanged();
})();
