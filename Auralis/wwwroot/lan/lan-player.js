(() => {
  'use strict';

  const language = navigator.language?.toLowerCase().startsWith('zh') ? 'zh-CN' : 'en-US';
  const messages = {
    'en-US': {
      connecting: 'Connecting', connected: 'Connected', loading: 'Loading your paired library…',
      disconnected: 'Connection failed', reconnect: 'Reconnect', nothingPlaying: 'Nothing is playing',
      chooseMusic: 'Choose music from the library', library: 'Library', queue: 'Queue',
      waitingLibrary: 'Waiting for your paired library', tracks: '{count} tracks', search: 'Search songs, artists, or albums',
      play: 'Play', pause: 'Pause', previous: 'Previous', next: 'Next', shuffle: 'Shuffle',
      repeatOff: 'Repeat off', repeatAll: 'Repeat all', repeatOne: 'Repeat one', position: 'Playback position',
      volume: 'Volume', loadingTracks: 'Loading music…', noTracks: 'No music is available in this paired library.',
      noResults: 'No matches for “{query}”.', queueEmpty: 'Your play queue is empty.',
      pairAgain: 'Pair this browser again', pairingHelp: 'Open a new Auralis LAN Player link from your desktop, then try again.',
      tryAgain: 'Try again', libraryError: 'The paired library could not be loaded.',
      browserNotPaired: 'This browser is not paired or the link has expired.', unknownArtist: 'Unknown artist',
      upNext: 'Up next', lanPlayer: 'LAN Player', streamError: 'This track could not be played. Check that this browser supports {format}, then choose it again.',
      streamRetry: 'The connection was interrupted. Retrying this track once…', unsupportedFormat: '{format} may not be supported by this browser.',
      queueTrack: 'Queue {title}', currentTrack: 'Current track: {title}', libraryVersion: 'Library v{version}'
    },
    'zh-CN': {
      connecting: '正在连接', connected: '已连接', loading: '正在加载已配对的音乐库…',
      disconnected: '连接失败', reconnect: '重新连接', nothingPlaying: '还没有播放音乐',
      chooseMusic: '从音乐库中选择一首歌曲', library: '音乐库', queue: '播放队列',
      waitingLibrary: '正在等待已配对的音乐库', tracks: '{count} 首歌曲', search: '搜索歌曲、艺术家或专辑',
      play: '播放', pause: '暂停', previous: '上一首', next: '下一首', shuffle: '随机播放',
      repeatOff: '顺序播放', repeatAll: '列表循环', repeatOne: '单曲循环', position: '播放进度',
      volume: '音量', loadingTracks: '正在加载音乐…', noTracks: '这个已配对的音乐库中没有可用歌曲。',
      noResults: '没有与“{query}”匹配的歌曲。', queueEmpty: '播放队列还是空的。',
      pairAgain: '请重新配对此浏览器', pairingHelp: '请在桌面 Auralis 中打开新的 LAN 播放器链接，然后再试一次。',
      tryAgain: '重试', libraryError: '无法加载已配对的音乐库。',
      browserNotPaired: '此浏览器尚未配对，或配对链接已过期。', unknownArtist: '未知艺术家',
      upNext: '接下来播放', lanPlayer: '局域网播放器', streamError: '无法播放这首歌曲。请确认当前浏览器支持 {format}，然后重新选择。',
      streamRetry: '音频连接中断，正在自动重试一次…', unsupportedFormat: '当前浏览器可能不支持 {format}。',
      queueTrack: '将 {title} 加入播放队列', currentTrack: '当前歌曲：{title}', libraryVersion: '音乐库 v{version}'
    }
  };

  const text = (key, values = {}) => String(messages[language][key] || messages['en-US'][key] || key)
    .replace(/\{(\w+)\}/g, (_, name) => String(values[name] ?? ''));
  const $ = selector => document.querySelector(selector);
  const root = document.documentElement;
  const app = $('#lanApp');
  const audio = $('#audio');
  const elements = {
    connection: $('#connectionState'), connectionText: $('#connectionText'), reconnect: $('#reconnectButton'),
    pairing: $('#pairingCard'), pairingTitle: $('#pairingTitle'), pairingMessage: $('#pairingMessage'), pairingRetry: $('#pairingRetryButton'),
    theme: $('#themeButton'), nowTitle: $('#nowPlayingTitle'), nowArtist: $('#nowArtist'), nowCover: $('#nowCover'),
    coverFallback: $('.cover-fallback'), play: $('#playButton'), previous: $('#previousButton'), next: $('#nextButton'),
    nowControls: $('.now-controls'), volumeLabel: $('label[for="volumeSlider"]'),
    shuffle: $('#shuffleButton'), repeat: $('#repeatButton'), progress: $('#progressSlider'), currentTime: $('#currentTime'),
    duration: $('#duration'), volume: $('#volumeSlider'), libraryTitle: $('#libraryTitle'), librarySummary: $('#librarySummary'),
    libraryStatus: $('#libraryStatus'), trackList: $('#trackList'), search: $('#searchInput'), queueTitle: $('#queueTitle'),
    queueCount: $('#queueCount'), queueList: $('#queueList'), version: $('#libraryVersion'), nowEyebrow: $('#nowEyebrow')
  };

  const storedTheme = localStorage.getItem('auralis:lan:theme') || 'system';
  const storedVolume = Math.max(0, Math.min(1, Number(localStorage.getItem('auralis:lan:volume') ?? .75)));
  const state = {
    accessToken: '', sessionReady: false, tracks: [], filteredTracks: [], queue: [], currentIndex: -1,
    currentTrack: null, query: '', isPlaying: false, shuffle: localStorage.getItem('auralis:lan:shuffle') === 'true',
    repeat: localStorage.getItem('auralis:lan:repeat') || 'off', theme: ['system', 'light', 'dark'].includes(storedTheme) ? storedTheme : 'system',
    version: '', volume: Number.isFinite(storedVolume) ? storedVolume : .75, coverLoadToken: 0,
    streamAttemptTrackId: '', streamRetryUsed: false
  };

  function setStaticText() {
    document.documentElement.lang = language;
    document.title = `Auralis ${text('lanPlayer')}`;
    $('.skip-link').textContent = language === 'zh-CN' ? '跳到音乐库' : 'Skip to library';
    elements.reconnect.textContent = text('reconnect');
    elements.reconnect.setAttribute('aria-label', text('reconnect'));
    elements.libraryTitle.textContent = text('library');
    elements.queueTitle.textContent = text('queue');
    elements.nowEyebrow.textContent = 'AURALIS LAN';
    elements.search.placeholder = text('search');
    elements.search.setAttribute('aria-label', text('search'));
    elements.progress.setAttribute('aria-label', text('position'));
    elements.volume.setAttribute('aria-label', text('volume'));
    elements.previous.setAttribute('aria-label', text('previous'));
    elements.previous.title = text('previous');
    elements.next.setAttribute('aria-label', text('next'));
    elements.next.title = text('next');
    elements.play.setAttribute('aria-label', text('play'));
    elements.play.title = text('play');
    elements.nowControls.setAttribute('aria-label', language === 'zh-CN' ? '播放控制' : 'Playback controls');
    elements.volumeLabel.textContent = text('volume');
    elements.shuffle.setAttribute('aria-label', text('shuffle'));
    elements.shuffle.title = text('shuffle');
    elements.trackList.setAttribute('aria-label', text('library'));
    elements.queueList.setAttribute('aria-label', text('queue'));
    elements.pairingTitle.textContent = text('pairAgain');
    elements.pairingMessage.textContent = text('pairingHelp');
    elements.pairingRetry.textContent = text('tryAgain');
  }

  function applyTheme() {
    const effective = state.theme === 'system'
      ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
      : state.theme;
    root.dataset.theme = effective;
    app.dataset.theme = state.theme;
    const next = state.theme === 'system' ? 'light' : state.theme === 'light' ? 'dark' : 'system';
    elements.theme.setAttribute('aria-label', language === 'zh-CN' ? `主题：${state.theme}，切换到 ${next}` : `Theme: ${state.theme}; switch to ${next}`);
    elements.theme.title = elements.theme.getAttribute('aria-label');
  }

  function updateConnection(kind, value) {
    elements.connection.dataset.state = kind;
    elements.connectionText.textContent = value;
  }

  function extractAccessToken() {
    const fragment = new URLSearchParams(location.hash.slice(1));
    const token = fragment.get('access') || '';
    if (location.hash) history.replaceState(null, document.title, `${location.pathname}${location.search}`);
    return token;
  }

  function sameOriginUrl(value) {
    if (!value) return '';
    try {
      const url = new URL(value, location.origin);
      return url.origin === location.origin && (url.protocol === 'https:' || url.protocol === 'http:') ? url.href : '';
    } catch {
      return '';
    }
  }

  function request(url, options = {}) {
    return fetch(url, {
      credentials: 'include',
      cache: 'no-store',
      ...options,
      headers: { Accept: 'application/json', ...(options.headers || {}) }
    });
  }

  async function establishSession() {
    const token = state.accessToken;
    if (!token) {
      // A successful pairing is represented by an HttpOnly cookie. Refreshes intentionally do
      // not have another fragment token, so try that existing session before asking the user to
      // create a new link.
      try {
        state.sessionReady = true;
        hidePairing();
        await loadLibrary();
        return true;
      } catch (error) {
        state.sessionReady = false;
        const pairingRequired = error?.status === 401 || error?.status === 403;
        const message = pairingRequired ? text('browserNotPaired') : text('libraryError');
        showPairing(message);
        updateConnection('error', text('disconnected'));
        elements.libraryStatus.textContent = message;
        return false;
      }
    }

    updateConnection('connecting', text('connecting'));
    hidePairing();
    elements.libraryStatus.textContent = text('loadingTracks');
    try {
      const response = await request('/api/lan/v1/session', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ accessToken: token })
      });
      if (!response.ok) {
        if (response.status === 401 || response.status === 403) state.accessToken = '';
        throw Object.assign(new Error('session'), { status: response.status });
      }
      state.accessToken = '';
      state.sessionReady = true;
      await loadLibrary();
      return true;
    } catch (error) {
      state.sessionReady = false;
      const pairingRequired = error?.status === 401 || error?.status === 403;
      const message = pairingRequired ? text('browserNotPaired') : text('libraryError');
      if (pairingRequired || !state.sessionReady) showPairing(message);
      updateConnection('error', text('disconnected'));
      elements.libraryStatus.textContent = message;
      return false;
    }
  }

  async function loadLibrary() {
    updateConnection('connecting', text('loading'));
    const response = await request('/api/lan/v1/library');
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) showPairing(text('browserNotPaired'));
      throw Object.assign(new Error('library'), { status: response.status });
    }
    const payload = await response.json();
    const tracks = Array.isArray(payload?.tracks) ? payload.tracks : [];
    state.version = String(payload?.version || '');
    state.tracks = tracks.map(normalizeTrack).filter(Boolean);
    state.queue = [...state.tracks];
    state.currentIndex = -1;
    state.currentTrack = null;
    applyFilter();
    updateConnection('connected', text('connected'));
    elements.libraryStatus.textContent = '';
    elements.librarySummary.textContent = text('tracks', { count: Number(payload?.trackCount) || state.tracks.length });
    elements.version.textContent = state.version ? text('libraryVersion', { version: state.version }) : '';
    renderQueue();
  }

  function normalizeTrack(track) {
    if (!track || typeof track.id !== 'string' || !track.id) return null;
    const audioUrl = sameOriginUrl(track.audioUrl);
    if (!audioUrl) return null;
    return {
      id: track.id,
      title: String(track.title || track.id),
      artist: String(track.artist || text('unknownArtist')),
      album: String(track.album || ''),
      extension: String(track.extension || ''),
      contentType: String(track.contentType || ''),
      size: Number(track.size) || 0,
      durationSeconds: Math.max(0, Number(track.durationSeconds) || 0),
      dateAdded: String(track.dateAdded || ''),
      audioUrl,
      coverUrl: sameOriginUrl(track.coverUrl)
    };
  }

  function browserPlaybackSupport(track) {
    if (!track?.contentType || typeof audio.canPlayType !== 'function') return 'maybe';
    const result = audio.canPlayType(track.contentType);
    return result === 'probably' ? 'probably' : result === 'maybe' ? 'maybe' : 'unknown';
  }

  function applyFilter() {
    const query = state.query.trim().toLocaleLowerCase(language);
    state.filteredTracks = query
      ? state.tracks.filter(track => [track.title, track.artist, track.album].some(value => value.toLocaleLowerCase(language).includes(query)))
      : [...state.tracks];
    renderTrackList();
  }

  function createIcon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', name === 'pause' ? 'M8 6v12M16 6v12' : 'm9 6 9 6-9 6Z');
    svg.append(path);
    return svg;
  }

  function coverNode(track, className) {
    const cover = document.createElement('div');
    cover.className = className;
    if (track.coverUrl) {
      const image = new Image();
      image.alt = '';
      image.loading = 'lazy';
      image.src = track.coverUrl;
      cover.append(image);
    } else {
      cover.textContent = '♫';
    }
    return cover;
  }

  function renderTrackList() {
    const fragment = document.createDocumentFragment();
    const tracks = state.filteredTracks;
    if (!tracks.length) {
      const empty = document.createElement('div');
      empty.className = 'empty-list';
      empty.textContent = state.tracks.length ? text('noResults', { query: state.query.trim() }) : text('noTracks');
      fragment.append(empty);
    }
    tracks.forEach((track, index) => {
      const row = document.createElement('article');
      row.className = `track-row entering stagger-${Math.min(index, 12)}${track.id === state.currentTrack?.id ? ' is-current' : ''}`;
      row.setAttribute('role', 'listitem');
      row.append(coverNode(track, 'track-cover'));
      const copy = document.createElement('div');
      copy.className = 'track-copy';
      const title = document.createElement('strong');
      title.textContent = track.title;
      title.title = track.title;
      const artist = document.createElement('span');
      const support = browserPlaybackSupport(track);
      artist.textContent = support === 'unknown'
        ? `${track.artist} · ${text('unsupportedFormat', { format: track.extension || track.contentType || 'audio' })}`
        : track.artist;
      artist.title = track.artist;
      copy.append(title, artist);
      row.append(copy);
      const album = document.createElement('div');
      album.className = 'track-album';
      album.textContent = track.album || '—';
      album.title = track.album;
      row.append(album);
      const length = document.createElement('div');
      length.className = 'track-duration';
      length.textContent = formatTime(track.durationSeconds);
      row.append(length);
      const button = document.createElement('button');
      button.className = 'row-play';
      button.type = 'button';
      const isCurrentPlaying = track.id === state.currentTrack?.id && state.isPlaying;
      const rowAction = text(isCurrentPlaying ? 'pause' : 'play');
      button.setAttribute('aria-label', rowAction + ` ${track.title}`);
      button.title = rowAction;
      button.append(createIcon(isCurrentPlaying ? 'pause' : 'play'));
      button.addEventListener('click', () => activateTrack(track, tracks));
      row.addEventListener('dblclick', () => activateTrack(track, tracks));
      row.append(button);
      fragment.append(row);
    });
    elements.trackList.replaceChildren(fragment);
  }

  function renderQueue() {
    const fragment = document.createDocumentFragment();
    const remaining = state.currentIndex >= 0 ? state.queue.slice(state.currentIndex + 1) : state.queue;
    elements.queueCount.textContent = String(remaining.length);
    if (!remaining.length) {
      const empty = document.createElement('div');
      empty.className = 'empty-list';
      empty.textContent = text('queueEmpty');
      fragment.append(empty);
    }
    remaining.slice(0, 120).forEach((track, index) => {
      const row = document.createElement('article');
      row.className = `queue-row entering stagger-${Math.min(index, 12)}${track.id === state.currentTrack?.id ? ' is-current' : ''}`;
      row.setAttribute('role', 'listitem');
      row.append(coverNode(track, 'queue-cover'));
      const copy = document.createElement('div');
      copy.className = 'queue-copy';
      const title = document.createElement('strong');
      title.textContent = track.title;
      title.title = track.title;
      const artist = document.createElement('span');
      artist.textContent = track.artist;
      copy.append(title, artist);
      row.append(copy);
      const play = document.createElement('button');
      play.className = 'row-play';
      play.type = 'button';
      play.setAttribute('aria-label', text('play') + ` ${track.title}`);
      play.append(createIcon('play'));
      play.addEventListener('click', () => activateTrack(track, state.queue));
      row.append(play);
      fragment.append(row);
    });
    elements.queueList.replaceChildren(fragment);
  }

  function playTrack(track, sourceQueue = state.queue) {
    const index = sourceQueue.findIndex(item => item.id === track.id);
    state.queue = [...sourceQueue];
    state.currentIndex = index;
    state.currentTrack = track;
    state.coverLoadToken += 1;
    const token = state.coverLoadToken;
    if (track.coverUrl) {
      elements.nowCover.hidden = false;
      elements.nowCover.src = track.coverUrl;
      elements.nowCover.onload = () => { if (token === state.coverLoadToken) elements.coverFallback.hidden = true; };
      elements.nowCover.onerror = () => { if (token === state.coverLoadToken) { elements.nowCover.hidden = true; elements.coverFallback.hidden = false; } };
    } else {
      elements.nowCover.hidden = true;
      elements.coverFallback.hidden = false;
      elements.nowCover.removeAttribute('src');
    }
    elements.nowCover.alt = track.coverUrl ? `${track.title} cover` : '';
    elements.nowTitle.textContent = track.title;
    elements.nowTitle.title = track.title;
    elements.nowArtist.textContent = [track.artist, track.album].filter(Boolean).join(' · ') || text('unknownArtist');
    elements.nowTitle.setAttribute('aria-label', text('currentTrack', { title: track.title }));
    elements.nowTitle.closest('.now-playing-card').classList.remove('track-change');
    void elements.nowTitle.offsetWidth;
    elements.nowTitle.closest('.now-playing-card').classList.add('track-change');
    state.streamAttemptTrackId = track.id;
    state.streamRetryUsed = false;
    elements.libraryStatus.textContent = browserPlaybackSupport(track) === 'unknown'
      ? text('unsupportedFormat', { format: track.extension || track.contentType || 'audio' })
      : '';
    audio.pause();
    audio.src = track.audioUrl;
    audio.load();
    syncMediaSession(track);
    audio.play().catch(() => showStreamError());
    renderTrackList();
    renderQueue();
  }

  function activateTrack(track, sourceQueue) {
    if (state.currentTrack?.id === track.id) {
      togglePlayback();
      return;
    }
    playTrack(track, sourceQueue);
  }

  function syncMediaSession(track) {
    if (!('mediaSession' in navigator) || typeof MediaMetadata === 'undefined') return;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: track.title,
        artist: track.artist,
        album: track.album,
        artwork: track.coverUrl ? [{ src: track.coverUrl }] : []
      });
    } catch {
      // Media Session support varies between browsers; the in-page player remains authoritative.
    }
  }

  function setPlayback(playing) {
    state.isPlaying = Boolean(playing);
    app.classList.toggle('is-playing', state.isPlaying);
    elements.play.setAttribute('aria-label', text(state.isPlaying ? 'pause' : 'play'));
    elements.play.title = elements.play.getAttribute('aria-label');
    if ('mediaSession' in navigator) {
      try { navigator.mediaSession.playbackState = state.isPlaying ? 'playing' : 'paused'; } catch { }
    }
    renderTrackList();
  }

  function togglePlayback() {
    if (!state.currentTrack) {
      if (state.filteredTracks.length) playTrack(state.filteredTracks[0], state.filteredTracks);
      return;
    }
    if (audio.paused) audio.play().catch(() => showStreamError());
    else audio.pause();
  }

  function moveQueue(delta) {
    if (!state.queue.length) return;
    let nextIndex;
    if (state.shuffle && delta > 0 && state.queue.length > 1) {
      do { nextIndex = Math.floor(Math.random() * state.queue.length); } while (nextIndex === state.currentIndex);
    } else {
      nextIndex = state.currentIndex + delta;
    }
    if (nextIndex < 0) nextIndex = state.repeat === 'all' ? state.queue.length - 1 : 0;
    if (nextIndex >= state.queue.length) {
      if (state.repeat === 'all') nextIndex = 0;
      else { setPlayback(false); return; }
    }
    playTrack(state.queue[nextIndex], state.queue);
  }

  function cycleRepeat() {
    state.repeat = state.repeat === 'off' ? 'all' : state.repeat === 'all' ? 'one' : 'off';
    localStorage.setItem('auralis:lan:repeat', state.repeat);
    syncRepeatButton();
  }

  function syncRepeatButton() {
    elements.repeat.setAttribute('aria-pressed', String(state.repeat !== 'off'));
    elements.repeat.classList.toggle('repeat-one-active', state.repeat === 'one');
    const label = text(state.repeat === 'off' ? 'repeatOff' : state.repeat === 'all' ? 'repeatAll' : 'repeatOne');
    elements.repeat.setAttribute('aria-label', label);
    elements.repeat.title = label;
  }

  function syncSliders() {
    const duration = Number.isFinite(audio.duration) ? audio.duration : state.currentTrack?.durationSeconds || 0;
    const position = duration ? Math.max(0, Math.min(1, audio.currentTime / duration)) : 0;
    elements.progress.value = String(Math.round(position * 1000));
    elements.currentTime.textContent = formatTime(audio.currentTime);
    elements.duration.textContent = formatTime(duration);
    elements.progress.setAttribute('aria-valuetext', `${formatTime(audio.currentTime)} / ${formatTime(duration)}`);
    if ('mediaSession' in navigator && duration > 0 && Number.isFinite(duration)) {
      try {
        navigator.mediaSession.setPositionState({
          duration,
          playbackRate: audio.playbackRate || 1,
          position: Math.max(0, Math.min(duration, audio.currentTime || 0))
        });
      } catch { }
    }
  }

  function setVolume(value) {
    state.volume = Math.max(0, Math.min(1, Number(value)));
    audio.volume = state.volume;
    elements.volume.value = String(state.volume);
    localStorage.setItem('auralis:lan:volume', String(state.volume));
  }

  function formatTime(value) {
    const seconds = Math.max(0, Math.floor(Number(value) || 0));
    const minutes = Math.floor(seconds / 60);
    return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
  }

  function showPairing(message) {
    elements.pairing.hidden = false;
    elements.pairingMessage.textContent = message;
  }

  function hidePairing() { elements.pairing.hidden = true; }

  function showStreamError() {
    const format = state.currentTrack?.extension || state.currentTrack?.contentType || 'audio';
    elements.libraryStatus.textContent = text('streamError', { format });
    // A decoder/codec failure is track-local. The paired HTTP session can still be healthy, so
    // keep the connection indicator truthful and let the user choose another song or reconnect.
    updateConnection(state.sessionReady ? 'connected' : 'error', text(state.sessionReady ? 'connected' : 'disconnected'));
  }

  function retryCurrentStreamOnce() {
    const track = state.currentTrack;
    if (!track || state.streamAttemptTrackId !== track.id || state.streamRetryUsed) return false;
    state.streamRetryUsed = true;
    elements.libraryStatus.textContent = text('streamRetry');
    const retryUrl = new URL(track.audioUrl);
    retryUrl.searchParams.set('retry', String(Date.now()));
    const resumeAt = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
    audio.src = retryUrl.href;
    audio.load();
    const restorePosition = () => {
      audio.removeEventListener('loadedmetadata', restorePosition);
      if (resumeAt > 0 && Number.isFinite(audio.duration)) audio.currentTime = Math.min(resumeAt, audio.duration);
    };
    audio.addEventListener('loadedmetadata', restorePosition);
    audio.play().catch(showStreamError);
    return true;
  }

  async function reconnect() {
    if (!state.sessionReady) return establishSession();
    updateConnection('connecting', text('loading'));
    elements.libraryStatus.textContent = text('loadingTracks');
    try {
      await loadLibrary();
    } catch (error) {
      const pairingRequired = error?.status === 401 || error?.status === 403;
      updateConnection('error', text('disconnected'));
      elements.libraryStatus.textContent = pairingRequired ? text('browserNotPaired') : text('libraryError');
      if (pairingRequired) {
        state.sessionReady = false;
        showPairing(text('browserNotPaired'));
      }
    }
  }

  function bindEvents() {
    elements.reconnect.addEventListener('click', reconnect);
    elements.pairingRetry.addEventListener('click', reconnect);
    elements.theme.addEventListener('click', () => {
      state.theme = state.theme === 'system' ? 'light' : state.theme === 'light' ? 'dark' : 'system';
      localStorage.setItem('auralis:lan:theme', state.theme);
      applyTheme();
    });
    elements.search.addEventListener('input', event => { state.query = event.target.value; applyFilter(); });
    elements.play.addEventListener('click', togglePlayback);
    elements.previous.addEventListener('click', () => moveQueue(-1));
    elements.next.addEventListener('click', () => moveQueue(1));
    elements.shuffle.addEventListener('click', () => {
      state.shuffle = !state.shuffle;
      elements.shuffle.setAttribute('aria-pressed', String(state.shuffle));
      localStorage.setItem('auralis:lan:shuffle', String(state.shuffle));
    });
    elements.repeat.addEventListener('click', cycleRepeat);
    elements.progress.addEventListener('input', event => {
      const duration = Number.isFinite(audio.duration) ? audio.duration : state.currentTrack?.durationSeconds || 0;
      if (duration) audio.currentTime = (Number(event.target.value) / 1000) * duration;
      syncSliders();
    });
    elements.volume.addEventListener('input', event => setVolume(event.target.value));
    audio.addEventListener('play', () => setPlayback(true));
    audio.addEventListener('pause', () => setPlayback(false));
    audio.addEventListener('timeupdate', syncSliders);
    audio.addEventListener('loadedmetadata', syncSliders);
    audio.addEventListener('ended', () => { if (state.repeat === 'one') { audio.currentTime = 0; audio.play().catch(showStreamError); } else moveQueue(1); });
    audio.addEventListener('error', () => {
      if (!state.currentTrack) return;
      // MEDIA_ERR_DECODE/NOT_SUPPORTED will not improve on retry. Network and aborted reads often
      // occur when a phone changes Wi-Fi power state, so those get one bounded same-origin retry.
      if (audio.error?.code === 2 && retryCurrentStreamOnce()) return;
      showStreamError();
    });
    document.addEventListener('keydown', event => {
      const typing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target?.isContentEditable;
      if (typing) return;
      if (event.code === 'Space') { event.preventDefault(); togglePlayback(); }
      if (event.key === 'ArrowLeft') { event.preventDefault(); audio.currentTime = Math.max(0, audio.currentTime - 5); }
      if (event.key === 'ArrowRight') { event.preventDefault(); audio.currentTime = Math.min(audio.duration || Infinity, audio.currentTime + 5); }
      if (event.key.toLowerCase() === 'n') moveQueue(1);
      if (event.key.toLowerCase() === 'p') moveQueue(-1);
    });
    document.addEventListener('visibilitychange', () => { root.dataset.documentHidden = String(document.hidden); });
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (state.theme === 'system') applyTheme(); });
    if ('mediaSession' in navigator) {
      const seekBy = seconds => { audio.currentTime = Math.max(0, Math.min(audio.duration || Infinity, audio.currentTime + seconds)); };
      const handlers = {
        play: () => audio.play().catch(showStreamError),
        pause: () => audio.pause(),
        previoustrack: () => moveQueue(-1),
        nexttrack: () => moveQueue(1),
        seekbackward: details => seekBy(-(details.seekOffset || 10)),
        seekforward: details => seekBy(details.seekOffset || 10),
        seekto: details => {
          if (Number.isFinite(details.seekTime)) audio.currentTime = Math.max(0, Math.min(audio.duration || Infinity, details.seekTime));
        }
      };
      Object.entries(handlers).forEach(([action, handler]) => {
        try { navigator.mediaSession.setActionHandler(action, handler); } catch { }
      });
    }
  }

  function initialise() {
    state.accessToken = extractAccessToken();
    setStaticText();
    setVolume(state.volume);
    elements.shuffle.setAttribute('aria-pressed', String(state.shuffle));
    syncRepeatButton();
    applyTheme();
    bindEvents();
    elements.nowTitle.textContent = text('nothingPlaying');
    elements.nowArtist.textContent = text('chooseMusic');
    elements.librarySummary.textContent = text('waitingLibrary');
    updateConnection('connecting', text('connecting'));
    establishSession();
  }

  initialise();
})();
