(() => {
  'use strict';
  let mediaHub = null;
  let requestedInlineVideo = '';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const root = document.documentElement;
  const lyricsClock = new window.AuralisLyricsClock();
  let lyricsAnimationFrame = 0;
  let lyricElements = [];
  let lanPairingLinkVisible = false;
  const i18n = window.AuralisI18n;
  const t = (keyOrSource, args = {}) => i18n?.t(keyOrSource, args) ?? String(keyOrSource);
  const localizedCount = (noun, value) => i18n?.count(noun, value) ?? `${Number(value) || 0}`;

  const palettes = [
    ['#786ee6', '#3c3d78'], ['#ce6d8c', '#773a67'], ['#4d9bb8', '#2d536f'],
    ['#d18a55', '#784b48'], ['#5ca378', '#315e58'], ['#9a72cb', '#51427b'],
    ['#d35f65', '#6f374d'], ['#5a85cb', '#354b7f'], ['#bc8d4d', '#72563e']
  ];

  const defaultShortcuts = {
    playPause: 'Space', previous: 'ArrowLeft', next: 'ArrowRight',
    volumeUp: 'ArrowUp', volumeDown: 'ArrowDown', mute: 'KeyM', fullscreen: 'KeyI'
  };

  const platformProviders = Object.create(null);

  const lyricFontOptions = [
    ['system', '跟随系统'],
    ['Microsoft YaHei UI', '微软雅黑 UI'],
    ['DengXian', '等线'],
    ['SimHei', '黑体'],
    ['KaiTi', '楷体']
  ];


  const motionOptions = [
    ['system', '跟随 Windows'],
    ['full', '完整动画'],
    ['reduced', '减少动画']
  ];

  const trayTimerOptions = [
    ['0', '不启用'],
    ['15', '15 分钟后'],
    ['30', '30 分钟后'],
    ['45', '45 分钟后'],
    ['60', '1 小时后'],
    ['90', '1.5 小时后'],
    ['120', '2 小时后']
  ];

  const audioChannelOptions = [
    ['stereo', '立体声'],
    ['reverseStereo', '左右声道交换'],
    ['left', '仅左声道'],
    ['right', '仅右声道'],
    ['dolby', 'Dolby 环绕声']
  ];

  const languageOptions = () => [
    ['system', t('language.system')],
    ['zh-CN', t('language.zh-CN')],
    ['en-US', t('language.en-US')]
  ];

  const reducedMotionMedia = window.matchMedia('(prefers-reduced-motion: reduce)');
  const storedMotionPreference = localStorage.getItem('auralis:motion');

  const desktopColorPresets = [
    '#73BCFC', '#4CC2FF', '#5B9CF6', '#8B7CF6', '#D778BA',
    '#F76363', '#F09A45', '#F7C948', '#63B879', '#202124',
    '#6B6F76', '#FFFFFF'
  ];

  function readEnumPreference(key, allowedValues, fallback) {
    const value = localStorage.getItem(key);
    return allowedValues.includes(value) ? value : fallback;
  }

  const state = {
    tracks: [],
    musicFolders: [],
    lanMusicSharing: {
      enabled: false,
      running: false,
      pending: false,
      status: 'stopped',
      port: 43821,
      baseUrls: [],
      pairingUrls: [],
      pairingExpiresAt: null,
      trackCount: 0,
      activeSessionCount: 0,
      errorCode: null,
      errorMessage: null
    },
    currentPage: 'songs',
    settingsSection: 'home',
    languagePreference: i18n?.preference || 'system',
    resolvedLanguage: i18n?.language || 'zh-CN',
    currentTrackId: null,
    currentIndex: -1,
    currentItemKind: null,
    onlineQueue: [],
    mixedQueue: [],
    queueKind: 'local',
    isPlaying: false,
    currentTime: 0,
    duration: 0,
    streamQuality: null,
    audioInformation: null,
    audioInformationTrackId: null,
    volume: Math.max(0, Math.min(1, Number(localStorage.getItem('auralis:volume') ?? .75))),
    muted: false,
    favorites: new Set(readJson('auralis:favorites', [])),
    recent: readJson('auralis:recent', []),
    shuffle: localStorage.getItem('auralis:shuffle') === 'true',
    repeat: localStorage.getItem('auralis:shuffle') === 'true' ? 'off' : readEnumPreference('auralis:repeat', ['off', 'all', 'one'], 'off'),
    theme: localStorage.getItem('auralis:theme') || 'system',
    accent: localStorage.getItem('auralis:accent') === 'violet'
      ? 'blue'
      : (localStorage.getItem('auralis:accent') || 'blue'),
    density: localStorage.getItem('auralis:density') || 'comfortable',
    motion: motionOptions.some(([value]) => value === storedMotionPreference) ? storedMotionPreference : 'system',
    playerStyle: readEnumPreference('auralis:player-style', ['immersive', 'record'], 'immersive'),
    windowEffect: localStorage.getItem('auralis:window-effect') || 'none',
    customBackground: localStorage.getItem('auralis:custom-background') || '',
    fullscreenBackground: readEnumPreference('auralis:fullscreen-background', ['static', 'dynamic'], 'static'),
    coverTransition: readEnumPreference('auralis:cover-transition', ['fade', 'left', 'alternate'], 'fade'),
    immersiveFade: localStorage.getItem('auralis:immersive-fade') === 'true',
    lyricFontSize: Number(localStorage.getItem('auralis:lyric-font-size') || 28),
    lyricAdaptive: localStorage.getItem('auralis:lyric-adaptive') !== 'false',
    lyricFont: localStorage.getItem('auralis:lyric-font') || 'system',
    lyricVertical: Number(localStorage.getItem('auralis:lyric-vertical') || .5),
    lyricBlur: localStorage.getItem('auralis:lyric-blur') !== 'false',
    lyricSpring: localStorage.getItem('auralis:lyric-spring') !== 'false',
    backgroundSpeed: Number(localStorage.getItem('auralis:background-speed') || 2),
    backgroundFps: Number(localStorage.getItem('auralis:background-fps') || 30),
    shortcuts: { ...defaultShortcuts, ...readJson('auralis:shortcuts', {}) },
    recordingShortcut: null,
    lyricsPreference: localStorage.getItem('auralis:lyrics-preference') || 'localFirst',
    lyricsOffset: Number(localStorage.getItem('auralis:lyrics-offset') || 0),
    lyricsCacheIndex: [],
    lyricsCacheLoaded: false,
    showArtistInitial: localStorage.getItem('auralis:show-artist-initial') !== 'false',
    artistCoverBlur: localStorage.getItem('auralis:artist-cover-blur') !== 'false',
    artistImages: readJson('auralis:artist-images', {}),
    onlineLyricsEnabled: localStorage.getItem('auralis:online-lyrics') === 'true',
    lyrics: [],
    lyricsTrackId: null,
    lyricsSource: '本地歌词',
    lyricsSourceSelection: 'auto',
    lyricsResolvedSource: 'none',
    lyricsAvailability: null,
    lyricsLoading: false,
    lyricsSynced: false,
    lyricsInstrumental: false,
    activeLyricIndex: -1,
    rememberPlayback: localStorage.getItem('auralis:remember-playback') !== 'false',
    autoPlayOnLaunch: localStorage.getItem('auralis:auto-play') === 'true',
    mediaKeys: localStorage.getItem('auralis:media-keys') !== 'false',
    closeToTray: localStorage.getItem('auralis:close-to-tray') === 'true' || localStorage.getItem('auralis:tray-enabled') === 'true',
    trayEnabled: localStorage.getItem('auralis:close-to-tray') === 'true' || localStorage.getItem('auralis:tray-enabled') === 'true',
    trayMinimizeMinutes: 0,
    trayMinimizeTimer: { active: false, remainingSeconds: 0 },
    audioOutput: (() => {
      const stored = readJson('auralis:audio-output', {});
      return {
        outputModule: String(stored.outputModule || 'auto'),
        outputDeviceId: String(stored.outputDeviceId || ''),
        channel: audioChannelOptions.some(([value]) => value === stored.channel) ? stored.channel : 'stereo',
        bufferMilliseconds: Math.max(100, Math.min(5000, Number(stored.bufferMilliseconds) || 1000))
      };
    })(),
    audioDevices: {
      loaded: false,
      modules: [],
      devices: [],
      activeDeviceId: '',
      outputFormat: 'Windows 音频引擎协商',
      sampleRateBehavior: '保持音源采样率；设备不支持时由 Windows 转换',
      dsdBehavior: 'PCM；当前后端不声明 DSD 直通或独占位完美输出',
      endpointLatency: {
        endpointId: '',
        deviceName: '',
        isBluetooth: false,
        estimatedLatencyMilliseconds: null,
        streamLatencyMilliseconds: null,
        enginePeriodMilliseconds: null,
        status: 'loading'
      }
    },
    dpiScale: 1,
    startupEnabled: localStorage.getItem('auralis:startup-enabled') === 'true',
    taskbarWidgetEnabled: localStorage.getItem('auralis:taskbar-widget-enabled') === 'true',
    taskbarAutoShowOnPlayback: localStorage.getItem('auralis:taskbar-auto-show-on-playback') === 'true',
    taskbarWidgetControls: localStorage.getItem('auralis:taskbar-widget-controls') !== 'false',
    desktopLyrics: {
      enabled: localStorage.getItem('auralis:desktop-lyrics-enabled') === 'true',
      fontSize: Number(localStorage.getItem('auralis:desktop-lyrics-font-size') || 30),
      fontWeight: Number(localStorage.getItem('auralis:desktop-lyrics-font-weight') || 600),
      activeColor: localStorage.getItem('auralis:desktop-lyrics-active') || '#73BCFC',
      inactiveColor: localStorage.getItem('auralis:desktop-lyrics-inactive') || 'rgba(32,33,36,0.58)',
      shadowColor: localStorage.getItem('auralis:desktop-lyrics-shadow') || 'rgba(255,255,255,0.72)',
      maskColor: 'rgba(255,255,255,0.36)',
      maskBrightness: (() => {
        const stored = Number(localStorage.getItem('auralis:desktop-lyrics-mask-brightness') || 36);
        return Number.isFinite(stored) ? Math.max(10, Math.min(90, stored)) : 36;
      })(),
      alignment: localStorage.getItem('auralis:desktop-lyrics-alignment') || 'center',
      showMask: localStorage.getItem('auralis:desktop-lyrics-show-mask') !== 'false',
      animate: localStorage.getItem('auralis:desktop-lyrics-animate') !== 'false',
      wordHighlight: localStorage.getItem('auralis:desktop-lyrics-word-highlight') !== 'false',
      showTranslation: localStorage.getItem('auralis:desktop-lyrics-translation') !== 'false',
      doubleLine: localStorage.getItem('auralis:desktop-lyrics-double-line') !== 'false',
      alwaysShowSongInfo: localStorage.getItem('auralis:desktop-lyrics-song-info') !== 'false',
      locked: localStorage.getItem('auralis:desktop-lyrics-locked') === 'true'
    },
    session: readJson('auralis:session', null),
    lastSessionSave: 0,
    sortMode: localStorage.getItem('auralis:sort') || 'added',
    playbackRate: Number(localStorage.getItem('auralis:playback-rate') || 1),
    fullscreen: false,
    query: '',
    maximized: false,
    scanning: false,
    platformSearch: {
      providerId: localStorage.getItem('auralis:platform-search-provider') || '',
      requestId: 0,
      pendingRequestId: 0,
      pending: false,
      pendingPageIndex: 0,
      pendingPageHandle: '',
      query: '',
      items: [],
      pages: [],
      pageIndex: 0,
      pageSize: 50,
      nextPageHandle: '',
      totalCount: null,
      error: '',
      errorCode: '',
      retryAvailableAt: 0
    },
    platformPlayback: {
      pendingHandle: '',
      pendingTrack: null,
      pendingQueue: []
    },
    platformVideo: {
      pendingHandle: ''
    },
    platformConfiguration: {
      loaded: false,
      pending: false,
      error: ''
    }
  };



  // Migrate the former dark-card desktop lyric defaults to the new always-light translucent
  // surface. Explicit non-default custom colors remain untouched.
  if (state.desktopLyrics.inactiveColor === 'rgba(255,255,255,0.58)') {
    state.desktopLyrics.inactiveColor = 'rgba(32,33,36,0.58)';
  }
  if (state.desktopLyrics.shadowColor === 'rgba(0,0,0,0.72)') {
    state.desktopLyrics.shadowColor = 'rgba(255,255,255,0.72)';
  }
  localStorage.removeItem('auralis:taskbar-flyout-enabled');
  localStorage.removeItem('auralis:taskbar-flyout-on-track-change');
  localStorage.setItem('auralis:tray-enabled', String(state.trayEnabled));
  localStorage.setItem('auralis:close-to-tray', String(state.trayEnabled));

  let platformSearchDebounceTimer = 0;
  let platformSearchRetryTimer = 0;
  let audioBufferUpdateTimer = 0;

  const accentOptions = {
    blue: ['#6b9dca', '107, 157, 202'],
    green: ['#55a36f', '85, 163, 111'],
    amber: ['#d18a3f', '209, 138, 63'],
    coral: ['#d45d67', '212, 93, 103'],
    violet: ['#8b70c9', '139, 112, 201'],
    teal: ['#238f91', '35, 143, 145']
  };

  const accentNames = {
    blue: '蓝色', green: '绿色', amber: '橙色', coral: '红色', violet: '紫色', teal: '青色'
  };

  const audio = $('#audio');
  const pageContent = $('#pageContent');
  const playerBar = $('#playerBar');
  const progressSlider = $('#progressSlider');
  const overlayProgressSlider = $('#overlayProgressSlider');
  const volumeSlider = $('#volumeSlider');
  const overlayVolumeSlider = $('#overlayVolumeSlider');

  function readJson(key, fallback) {
    try { return JSON.parse(localStorage.getItem(key)) ?? fallback; }
    catch { return fallback; }
  }

  function nativePost(action, extra = {}) {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage({ action, ...extra });
    }
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#039;');
  }

  function hexToRgba(hex, alpha) {
    const value = String(hex || '').replace('#', '');
    if (!/^[0-9a-f]{6}$/i.test(value)) return `rgba(255,255,255,${alpha})`;
    const red = parseInt(value.slice(0, 2), 16);
    const green = parseInt(value.slice(2, 4), 16);
    const blue = parseInt(value.slice(4, 6), 16);
    return `rgba(${red},${green},${blue},${alpha})`;
  }

  function colorToHex(color, fallback = '#FFFFFF') {
    const value = String(color || '').trim();
    const shortHex = value.match(/^#([0-9a-f]{3})$/i);
    if (shortHex) return `#${[...shortHex[1]].map(character => character.repeat(2)).join('')}`.toUpperCase();
    const fullHex = value.match(/^#([0-9a-f]{6})$/i);
    if (fullHex) return `#${fullHex[1]}`.toUpperCase();
    const rgb = value.match(/^rgba?\(\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)/i);
    if (!rgb) return fallback;
    const channels = rgb.slice(1, 4).map(channel => Math.max(0, Math.min(255, Math.round(Number(channel)))));
    return `#${channels.map(channel => channel.toString(16).padStart(2, '0')).join('')}`.toUpperCase();
  }

  function fluentSelectMarkup(kind, value, options, ariaLabel, className = '') {
    const selected = options.find(([optionValue]) => optionValue === value) || options[0];
    return `<button type="button" class="fluent-select-trigger ${className}" data-fluent-select="${escapeHtml(kind)}" aria-label="${escapeHtml(t(ariaLabel))}" aria-haspopup="listbox" aria-expanded="false"><span>${escapeHtml(t(selected?.[1] || value))}</span><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m6 8 4 4 4-4"/></svg></button>`;
  }

  function fluentColorMarkup(property, value, ariaLabel) {
    const hex = colorToHex(value);
    return `<button type="button" class="fluent-color-trigger" data-fluent-color="${escapeHtml(property)}" aria-label="${escapeHtml(ariaLabel)}，当前 ${hex}" aria-haspopup="dialog" aria-expanded="false"><span class="fluent-color-swatch" style="--picker-color:${hex}"></span><span class="fluent-color-value">${hex}</span><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m6 8 4 4 4-4"/></svg></button>`;
  }

  function shortcutLabel(code) {
    return ({ Space: '空格', ArrowLeft: '←', ArrowRight: '→', ArrowUp: '↑', ArrowDown: '↓' })[code]
      || String(code || '').replace(/^Key/, '').replace(/^Digit/, '');
  }

  function hashIndex(value, length = palettes.length) {
    let hash = 0;
    for (let i = 0; i < value.length; i += 1) hash = ((hash << 5) - hash + value.charCodeAt(i)) | 0;
    return Math.abs(hash) % length;
  }

  function coverStyle(track) {
    const [a, b] = palettes[hashIndex(track.id || track.title)];
    const image = track.coverUrl ? `;background-image:url(&quot;${escapeHtml(track.coverUrl)}&quot;)` : '';
    return `--cover-a:${a};--cover-b:${b}${image}`;
  }

  function platformDisplayName(providerId) {
    return platformProviders[providerId] || providerId || '在线来源';
  }

  function platformProviderNeedsConfiguration(providerId) {
    return onlineSettingsProviders.find(p => p.id === providerId)?.configured === false;
  }
  function currentPlatformCapabilities(track) {
    const provider = onlineSettingsProviders.find(p => p.id === track?.providerId);
    return provider?.configured !== false ? provider?.capabilities || [] : [];
  }

  function safePlatformImageUrl(value) {
    const candidate = String(value || '').trim();
    if (!candidate) return '';
    if (/^data:image\/(?:png|jpeg|webp);base64,[a-z0-9+/=]+$/i.test(candidate)) return candidate;
    try {
      const url = new URL(candidate);
      const loopback = ['localhost', '127.0.0.1', '[::1]', '::1'].includes(url.hostname);
      const safeProtocol = url.protocol === 'https:' || (url.protocol === 'http:' && loopback);
      return safeProtocol && !url.username && !url.password ? url.href : '';
    } catch {
      return '';
    }
  }

  function platformErrorText(error, fallback = '在线服务暂时不可用') {
    if (!error) return '';
    if (typeof error === 'string') return error.trim() || fallback;
    return String(error.message || error.code || fallback);
  }

  function platformErrorCode(error) {
    return typeof error === 'object' && error
      ? String(error.code || '').trim().toLowerCase()
      : '';
  }

  function platformArtistText(item) {
    if (typeof item?.artist === 'string' && item.artist.trim()) return item.artist.trim();
    if (typeof item?.artistName === 'string' && item.artistName.trim()) return item.artistName.trim();
    if (Array.isArray(item?.artists)) {
      const names = item.artists
        .map(artist => typeof artist === 'string' ? artist : artist?.name)
        .filter(Boolean);
      if (names.length) return names.join(' / ');
    }
    return '未知艺术家';
  }

  function platformAlbumText(item) {
    if (typeof item?.album === 'string') return item.album || '在线音乐';
    return item?.album?.title || item?.albumTitle || '在线音乐';
  }

  function platformDurationSeconds(item) {
    const numeric = Number(item?.durationSeconds ?? item?.duration);
    if (Number.isFinite(numeric) && numeric >= 0) return numeric;
    const text = String(item?.duration || '');
    const match = text.match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/);
    if (!match) return 0;
    return (Number(match[1]) || 0) * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]);
  }

  function normalizePlatformTrack(item, providerId) {
    if (!item || typeof item !== 'object') return null;
    const handle = String(item.handle || item.resultHandle || '').trim();
    if (!handle) return null;
    const owner = String(item.providerId || providerId || state.platformSearch.providerId || '').toLowerCase();
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(owner)) return null;
    const availability = String(item.availability || 'unknown').toLowerCase();
    return {
      kind: 'online',
      id: String(item.uiId || item.playbackId || item.id || `platform:${owner}:${handle}`),
      handle,
      providerId: owner,
      title: String(item.title || '未知歌曲'),
      artist: platformArtistText(item),
      album: platformAlbumText(item),
      coverUrl: safePlatformImageUrl(item.coverUrl || item.artworkUrl),
      durationSeconds: platformDurationSeconds(item),
      availability,
      previewOnly: availability.includes('preview'),
      unavailable: availability.includes('unavailable') || item.playable === false || item.isPlayable === false,
      hasMusicVideo: item.hasMusicVideo === true
    };
  }

  function activePlaybackQueue() {
    return state.queueKind === 'mixed' ? state.mixedQueue : state.queueKind === 'online' ? state.onlineQueue : state.tracks;
  }

  function currentPlaybackItem() {
    return activePlaybackQueue().find(track => track.id === state.currentTrackId) || null;
  }

  function formatTime(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const minutes = Math.floor(seconds / 60);
    const remainder = Math.floor(seconds % 60).toString().padStart(2, '0');
    return `${minutes}:${remainder}`;
  }

  function formatBytes(bytes) {
    if (!Number.isFinite(bytes)) return '—';
    const mb = bytes / 1024 / 1024;
    return `${i18n?.number(mb, { maximumFractionDigits: mb >= 100 ? 0 : 1, minimumFractionDigits: mb >= 100 ? 0 : 1 }) ?? (mb >= 100 ? mb.toFixed(0) : mb.toFixed(1))} MB`;
  }

  function icon(name) {
    const paths = {
      plus: '<path d="M12 5v14M5 12h14"/>',
      folder: '<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2h8.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5Z"/>',
      music: '<path d="M9 18V5l11-2v13M9 8l11-2M5.5 21C7.43 21 9 19.88 9 18.5S7.43 16 5.5 16 2 17.12 2 18.5 3.57 21 5.5 21Zm11-2c1.93 0 3.5-1.12 3.5-2.5S18.43 14 16.5 14 13 15.12 13 16.5s1.57 2.5 3.5 2.5Z"/>',
      album: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="2.5"/><path d="M12 3a9 9 0 0 0 0 18"/>',
      artist: '<circle cx="12" cy="8" r="4"/><path d="M4.5 21c.65-4.18 3.15-6.5 7.5-6.5s6.85 2.32 7.5 6.5"/>',
      heart: '<path d="M20.8 4.9a5.4 5.4 0 0 0-7.64 0L12 6.06 10.84 4.9a5.4 5.4 0 1 0-7.64 7.64L12 21.3l8.8-8.76a5.4 5.4 0 0 0 0-7.64Z"/>',
      play: '<path d="m9 6 9 6-9 6Z"/>',
      pause: '<path d="M8 6v12M16 6v12"/>',
      more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
      trash: '<path d="M4 7h16M9 7V4h6v3m3 0-1 14H7L6 7m4 4v6m4-6v6"/>',
      clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
      search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
      sort: '<path d="M4 7h12M4 12h9M4 17h6m8-12v14m0 0-3-3m3 3 3-3"/>',
      camera: '<path d="M4 8.5h3l1.5-2h7l1.5 2h3v10H4Z"/><circle cx="12" cy="13.5" r="3.25"/>',
      volume: '<path d="M11 5 6.5 9H3v6h3.5l4.5 4Zm4 4a4 4 0 0 1 0 6m2-9a8 8 0 0 1 0 12"/>',
      video: '<rect x="3" y="5" width="18" height="14" rx="3"/><path d="m10 9 5 3-5 3Z"/>',
      'chevron-left': '<path d="m15 18-6-6 6-6"/>',
      'chevron-right': '<path d="m9 6 6 6-6 6"/>',
      devices: '<rect x="3" y="5" width="13" height="11" rx="2"/><path d="M8 20h3m-1.5-4v4M18 9.5h3v9h-6v-3"/><path d="M17 12.5h2"/>'
    };
    return `<svg viewBox="0 0 24 24" aria-hidden="true">${paths[name] || ''}</svg>`;
  }

  function effectiveTheme(preference = state.theme) {
    return preference === 'system'
      ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
      : preference;
  }

  let lyricsCacheQuery = '';
  let queueAnimationTimer = null;
  let queueNeedsRender = true;
  let initialUiRevealed = false;
  let initialUiFallbackTimer = 0;
  let trackRowsRevision = 0;
  let syncedTrackRowsRevision = -1;
  let syncedTrackId = null;
  let syncedTrackPlaying = false;
  let vinylRotationAnimation = null;
  let pageTransitionTimer = null;
  let pageMotionCleanupTimer = null;
  let settingsNavigationToken = 0;
  let settingsTransitionInProgress = false;
  let settingsRenderPending = false;
  let settingsHomeScrollTop = 0;
  let trackMotionTimer = null;
  let transportMotionTimer = null;
  let activeFluentFlyout = null;
  let activeFluentFlyoutAnchor = null;
  let pageMotionDirection = 'forward';
  const pageOrder = ['home', 'search', 'songs', 'albums', 'artists', 'favorites', 'recent', 'onlinePlaylists', 'onlineCollection', 'savedPlaylists', 'settings'];

  function isMotionReduced() {
    return state.motion === 'reduced' || (state.motion === 'system' && reducedMotionMedia.matches);
  }

  function applyMotionPreference() {
    root.dataset.motion = isMotionReduced() ? 'reduced' : 'full';
    syncLyricsAnimation();
  }

  function restartMotionClass(element, className, duration = 520) {
    if (!element || isMotionReduced()) return;
    element.classList.remove(className);
    void element.offsetWidth;
    element.classList.add(className);
    setTimeout(() => element.classList.remove(className), duration);
  }

  function preparePageMotion() {
    const view = $('.page-view', pageContent);
    if (!view) return;
    clearTimeout(pageMotionCleanupTimer);
    pageContent.scrollTop = 0;
    if (isMotionReduced()) return;

    view.classList.add('page-motion', pageMotionDirection === 'backward' ? 'from-left' : 'from-right');
    const staged = $$([
      '.page-header', '.hero', '.stat-card', '.media-card', '.artist-card',
      '.settings-card', '.platform-inline-state', '.empty-state', '.section-title-row',
      '.online-provider-filters', '.online-account-manager'
    ].join(','), view);
    staged.slice(0, 18).forEach((element, index) => {
      element.classList.add('motion-item');
      element.style.setProperty('--motion-index', String(Math.min(index, 10)));
    });
    $$('.track-row', view).slice(0, 18).forEach((element, index) => {
      element.style.setProperty('--enter-index', String(Math.min(index, 12)));
    });
    pageMotionCleanupTimer = setTimeout(() => {
      if (!view.isConnected) return;
      view.classList.remove('page-motion', 'from-left', 'from-right');
      staged.forEach(element => element.classList.remove('motion-item'));
    }, 760);
  }

  function animateTrackChange() {
    if (isMotionReduced()) return;
    clearTimeout(trackMotionTimer);
    const elements = [$('#nowPlayingButton'), $('.immersive-track-info'), $('.record-heading')].filter(Boolean);
    elements.forEach(element => {
      element.classList.remove('track-change-motion');
      void element.offsetWidth;
      element.classList.add('track-change-motion');
    });
    trackMotionTimer = setTimeout(() => elements.forEach(element => element.classList.remove('track-change-motion')), 560);
  }

  function pulseTransport() {
    if (isMotionReduced()) return;
    clearTimeout(transportMotionTimer);
    const buttons = [$('#playButton'), $('#overlayPlayButton')].filter(Boolean);
    buttons.forEach(button => restartMotionClass(button, 'transport-state-motion', 420));
    transportMotionTimer = setTimeout(() => buttons.forEach(button => button.classList.remove('transport-state-motion')), 440);
  }

  function syncVinylRotation() {
    const disc = $('.vinyl-disc');
    if (!disc) {
      vinylRotationAnimation?.cancel();
      vinylRotationAnimation = null;
      return;
    }

    // The same wrapper contains the square immersive cover and the circular record
    // surface. Pausing a Web Animation preserves its current transform, so merely
    // pausing here can leave the immersive cover stranded at an arbitrary angle.
    // Leaving record mode must remove that animation effect and restore upright art.
    if (state.playerStyle !== 'record') {
      vinylRotationAnimation?.cancel();
      vinylRotationAnimation = null;
      disc.style.removeProperty('transform');
      return;
    }

    if (typeof disc.animate !== 'function') return;
    if (!vinylRotationAnimation || vinylRotationAnimation.effect?.target !== disc) {
      vinylRotationAnimation?.cancel();
      vinylRotationAnimation = disc.animate(
        [{ transform: 'rotate(0deg)' }, { transform: 'rotate(360deg)' }],
        { duration: 22000, iterations: Infinity, easing: 'linear' }
      );
      vinylRotationAnimation.pause();
    }

    const overlay = $('#nowPlayingOverlay');
    const shouldRotate = !isMotionReduced() &&
      state.isPlaying && !!state.currentTrackId &&
      overlay.classList.contains('open');
    if (shouldRotate) {
      if (vinylRotationAnimation.playState !== 'running') vinylRotationAnimation.play();
    } else if (vinylRotationAnimation.playState === 'running') {
      vinylRotationAnimation.pause();
    }
  }

  function syncFullscreenThemeSelector() {
    $$('[data-fullscreen-theme]').forEach(button => {
      const active = button.dataset.fullscreenTheme === state.theme;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    });
    $$('[data-day-night-toggle]').forEach(toggle => {
      const current = effectiveTheme();
      const next = current === 'dark' ? 'light' : 'dark';
      toggle.dataset.theme = current;
      toggle.setAttribute('aria-pressed', String(current === 'dark'));
      toggle.setAttribute('aria-label', `切换到${next === 'dark' ? '深色' : '浅色'}主题`);
      toggle.title = `切换到${next === 'dark' ? '深色' : '浅色'}主题`;
    });
  }

  function handleThemeSelectorClick(event) {
    const preferenceButton = event.target.closest('[data-fullscreen-theme]');
    if (preferenceButton) {
      applyTheme(preferenceButton.dataset.fullscreenTheme);
      return;
    }
    if (event.target.closest('[data-day-night-toggle]')) {
      applyTheme(effectiveTheme() === 'dark' ? 'light' : 'dark');
    }
  }

  function fluentSelectOptions(kind) {
    if (kind === 'language') return languageOptions();
    if (kind === 'lyricFont') return lyricFontOptions;
    if (kind === 'platformProvider') return Object.entries(platformProviders);
    if (kind.startsWith('pluginSetting:')) return providerSettingForKind(kind)?.setting?.choices.map(c => [c.value,c.label]) || [];
    if (kind === 'motion') return motionOptions;
    if (kind === 'trayTimer') return trayTimerOptions;
    if (kind === 'audioModule') {
      const supported = state.audioDevices.modules
        .filter(module => ['mmdevice', 'directsound', 'waveout'].includes(String(module.id || '').toLowerCase()))
        .map(module => {
          const id = String(module.id);
          const label = {
            mmdevice: 'Windows 音频设备 · 推荐',
            directsound: 'DirectSound · 兼容模式',
            waveout: 'WaveOut · 传统兼容'
          }[id.toLowerCase()] || String(module.displayName || id);
          return [id, label];
        });
      return [['auto', '跟随 Windows 默认输出'], ...supported];
    }
    if (kind === 'audioDevice') {
      const module = state.audioOutput.outputModule;
      const devices = module === 'auto'
        ? []
        : state.audioDevices.devices
            .filter(device => device.moduleId === module)
            .map(device => [String(device.id), String(device.displayName || device.id)]);
      return [['', '跟随该后端的默认设备'], ...devices];
    }
    if (kind === 'audioChannel') return audioChannelOptions;
    return [];
  }

  function fluentSelectValue(kind) {
    if (kind === 'language') return state.languagePreference;
    if (kind === 'lyricFont') return state.lyricFont;
    if (kind === 'platformProvider') return state.platformSearch.providerId;
    if (kind.startsWith('pluginSetting:')) return providerSettingForKind(kind)?.setting?.value || '';
    if (kind === 'motion') return state.motion;
    if (kind === 'trayTimer') return String(state.trayMinimizeMinutes);
    if (kind === 'audioModule') return state.audioOutput.outputModule;
    if (kind === 'audioDevice') return state.audioOutput.outputDeviceId;
    if (kind === 'audioChannel') return state.audioOutput.channel;
    return '';
  }

  function closeFluentFlyout({ restoreFocus = false } = {}) {
    const flyout = activeFluentFlyout;
    const anchor = activeFluentFlyoutAnchor;
    if (!flyout) return;
    flyout.classList.remove('open');
    anchor?.setAttribute('aria-expanded', 'false');
    activeFluentFlyout = null;
    activeFluentFlyoutAnchor = null;
    setTimeout(() => flyout.remove(), isMotionReduced() ? 0 : 130);
    if (restoreFocus) anchor?.focus();
  }

  function placeFluentFlyout(flyout, anchor, preferredWidth) {
    const rect = anchor.getBoundingClientRect();
    const width = Math.min(preferredWidth, innerWidth - 24);
    flyout.style.width = `${width}px`;
    flyout.style.left = `${Math.max(12, Math.min(innerWidth - width - 12, rect.right - width))}px`;
    const measuredHeight = Math.min(flyout.scrollHeight, innerHeight - 24);
    const openAbove = innerHeight - rect.bottom < Math.min(measuredHeight + 10, 270) && rect.top > innerHeight - rect.bottom;
    flyout.classList.toggle('open-above', openAbove);
    flyout.style.top = openAbove
      ? `${Math.max(12, rect.top - measuredHeight - 8)}px`
      : `${Math.min(innerHeight - measuredHeight - 12, rect.bottom + 8)}px`;
  }

  function openFluentSelect(anchor) {
    const kind = anchor.dataset.fluentSelect;
    const options = fluentSelectOptions(kind);
    if (!options.length) return;
    if (activeFluentFlyoutAnchor === anchor) {
      closeFluentFlyout({ restoreFocus: true });
      return;
    }
    closeFluentFlyout();
    const selectedValue = fluentSelectValue(kind);
    const flyout = document.createElement('div');
    flyout.className = 'fluent-flyout fluent-select-flyout';
    flyout.setAttribute('role', 'listbox');
    flyout.setAttribute('aria-label', anchor.getAttribute('aria-label') || t('选择选项'));
    flyout.addEventListener('keydown', event => {
      if (!['ArrowDown','ArrowUp','Home','End'].includes(event.key)) return;
      event.preventDefault(); event.stopPropagation();
      const items = Array.from(flyout.querySelectorAll('[role="option"]'));
      const current = items.indexOf(document.activeElement);
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
        : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items[next]?.focus();
    });
    flyout.innerHTML = options.map(([value, label]) => `<button type="button" class="fluent-select-option ${value === selectedValue ? 'selected' : ''}" data-fluent-option="${escapeHtml(value)}" role="option" aria-selected="${value === selectedValue}"><span>${escapeHtml(t(label))}</span><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m4.5 10 3.2 3.2 7.8-7.8"/></svg></button>`).join('');
    document.body.appendChild(flyout);
    activeFluentFlyout = flyout;
    activeFluentFlyoutAnchor = anchor;
    anchor.setAttribute('aria-expanded', 'true');
    placeFluentFlyout(flyout, anchor, Math.max(176, anchor.getBoundingClientRect().width));
    requestAnimationFrame(() => flyout.classList.add('open'));
    flyout.querySelector('.selected')?.focus();
  }

  function applyUiLanguageState({ preference, resolvedLanguage, notifyNative = false } = {}) {
    const nextPreference = ['system', 'zh-CN', 'en-US'].includes(preference)
      ? preference
      : state.languagePreference;
    if (notifyNative) nativePost('setUiLanguage', { preference: nextPreference });
    const changed = i18n?.setPreference(nextPreference, { resolved: resolvedLanguage }) ?? false;
    state.languagePreference = i18n?.preference || nextPreference;
    state.resolvedLanguage = i18n?.language || resolvedLanguage || nextPreference;
    closeFluentFlyout();
    if (!changed) {
      syncSettingsControls();
      i18n?.localize(document);
      return;
    }



    renderPage();
    renderQueue();
    if ($('#lyricsCacheDialog')?.classList.contains('open')) renderLyricsCacheDialog();
    syncPlaybackUi();
    i18n?.localize(document);
  }

  function applyFluentSelectValue(kind, value) {
    if (kind === 'language' && ['system', 'zh-CN', 'en-US'].includes(value)) {
      applyUiLanguageState({ preference: value, notifyNative: true });
      return;
    } else if (kind === 'platformProvider') {
      selectPlatformProvider(value);
    } else if (kind.startsWith('pluginSetting:')) {
      const match = providerSettingForKind(kind);
      if (match?.setting) saveProviderSetting(match.provider.id, match.setting.key, value);
    } else if (kind === 'lyricFont') {
      state.lyricFont = value;
      localStorage.setItem('auralis:lyric-font', state.lyricFont);
      applyEffectPreferences();
    } else if (kind === 'motion' && motionOptions.some(([candidate]) => candidate === value)) {
      state.motion = value;
      localStorage.setItem('auralis:motion', state.motion);
      applyMotionPreference();
      syncVinylRotation();
      postTaskbarMediaOptions();
    } else if (kind === 'trayTimer' && trayTimerOptions.some(([candidate]) => candidate === value)) {
      state.trayMinimizeMinutes = Number(value);
      nativePost('setTrayMinimizeTimer', { minutes: state.trayMinimizeMinutes });
      showToast(state.trayMinimizeMinutes
        ? `Auralis 将在 ${trayTimerOptions.find(([candidate]) => candidate === value)?.[1] || value}缩小到托盘`
        : '已取消定时缩小');
    } else if (kind === 'audioModule' && fluentSelectOptions('audioModule').some(([candidate]) => candidate === value)) {
      state.audioOutput.outputModule = value;
      state.audioOutput.outputDeviceId = '';
      persistAudioOutput();
      if (value !== 'auto') showToast('输出后端将从下一首歌起完整生效');
    } else if (kind === 'audioDevice') {
      state.audioOutput.outputDeviceId = value;
      persistAudioOutput();
    } else if (kind === 'audioChannel' && audioChannelOptions.some(([candidate]) => candidate === value)) {
      state.audioOutput.channel = value;
      persistAudioOutput();
    }
    syncSettingsControls();
  }

  function syncFluentColorTrigger(property, hex) {
    $$(`[data-fluent-color="${property}"]`).forEach(trigger => {
      trigger.querySelector('.fluent-color-swatch')?.style.setProperty('--picker-color', hex);
      const label = trigger.querySelector('.fluent-color-value');
      if (label) label.textContent = hex;
      trigger.setAttribute('aria-label', `${trigger.getAttribute('aria-label')?.split('，当前')[0] || '颜色'}，当前 ${hex}`);
    });
  }

  function applyDesktopColor(property, hex) {
    const normalized = colorToHex(hex, '');
    if (!normalized) return false;
    const alpha = property === 'inactiveColor' ? .58 : property === 'shadowColor' ? .72 : property === 'maskColor' ? .24 : 1;
    state.desktopLyrics[property] = alpha === 1 ? normalized : hexToRgba(normalized, alpha);
    syncFluentColorTrigger(property, normalized);
    persistDesktopLyrics();
    return true;
  }

  function openFluentColorPicker(anchor) {
    const property = anchor.dataset.fluentColor;
    if (!property) return;
    if (activeFluentFlyoutAnchor === anchor) {
      closeFluentFlyout({ restoreFocus: true });
      return;
    }
    closeFluentFlyout();
    const current = colorToHex(state.desktopLyrics[property]);
    const flyout = document.createElement('div');
    flyout.className = 'fluent-flyout fluent-color-flyout';
    flyout.setAttribute('role', 'dialog');
    flyout.setAttribute('aria-label', '选择歌词颜色');
    flyout.innerHTML = `<div class="fluent-color-preview" style="--picker-color:${current}"><span></span><strong>选择颜色</strong><small>预设色或输入十六进制颜色</small></div><div class="fluent-color-presets">${desktopColorPresets.map(color => `<button type="button" data-fluent-color-value="${color}" style="--picker-color:${color}" aria-label="选择 ${color}"></button>`).join('')}</div><label class="fluent-hex-field"><span>#</span><input type="text" value="${current.slice(1)}" maxlength="6" inputmode="text" spellcheck="false" autocomplete="off" aria-label="十六进制颜色"><i aria-hidden="true"></i></label>`;
    document.body.appendChild(flyout);
    activeFluentFlyout = flyout;
    activeFluentFlyoutAnchor = anchor;
    anchor.setAttribute('aria-expanded', 'true');
    placeFluentFlyout(flyout, anchor, 272);
    requestAnimationFrame(() => flyout.classList.add('open'));
    const input = flyout.querySelector('.fluent-hex-field input');
    input?.focus();
    input?.select();
  }

  function postTaskbarMediaOptions() {
    nativePost('setTaskbarMediaOptions', {
      schemaVersion: 3,
      enabled: state.taskbarWidgetEnabled,
      autoShowOnPlayback: state.taskbarAutoShowOnPlayback,
      flyoutEnabled: false,
      showOnTrackChange: false,
      showControls: state.taskbarWidgetControls,
      reduceMotion: isMotionReduced(),
      theme: effectiveTheme(),
      accent: (accentOptions[state.accent] || accentOptions.blue)[0]
    });
  }

  function applyTheme(preference = state.theme, notifyTaskbar = true) {
    const nextPreference = ['system', 'light', 'dark'].includes(preference) ? preference : 'system';
    const effective = effectiveTheme(nextPreference);

    state.theme = nextPreference;
    localStorage.setItem('auralis:theme', nextPreference);
    root.dataset.theme = effective;
    syncFullscreenThemeSelector();
    syncSettingsControls();
    nativePost('themeChanged', { theme: effective });
    if (notifyTaskbar) postTaskbarMediaOptions();
  }

  function applyAccent(name = state.accent, notifyTaskbar = true) {
    const option = accentOptions[name] || accentOptions.blue;
    state.accent = name;
    localStorage.setItem('auralis:accent', name);
    root.style.setProperty('--accent', option[0]);
    root.style.setProperty('--accent-rgb', option[1]);
    if (notifyTaskbar) postTaskbarMediaOptions();
    syncSettingsControls();
  }

  function applyPreferences() {
    root.dataset.density = state.density;
    applyMotionPreference();
    applyPlayerStyle(state.playerStyle);
    applyAccent(state.accent, false);
    applyTheme(state.theme, false, false);
    applyEffectPreferences();
    nativePost('setMediaKeys', { enabled: state.mediaKeys });
    nativePost('setTrayEnabled', { enabled: state.trayEnabled });
    nativePost('setAudioOutputSettings', { settings: state.audioOutput, includeDiagnostics: false });
    nativePost('setStartupEnabled', { enabled: state.startupEnabled });
    nativePost('setDesktopLyricsOptions', { options: { ...state.desktopLyrics, offsetMilliseconds: state.lyricsOffset } });
    postTaskbarMediaOptions();
  }

  function persistAudioOutput() {
    const includeDiagnostics = state.currentPage === 'settings' && state.settingsSection === 'audio';
    localStorage.setItem('auralis:audio-output', JSON.stringify(state.audioOutput));
    if (includeDiagnostics) markAudioEndpointLatencyLoading();
    nativePost('setAudioOutputSettings', {
      settings: state.audioOutput,
      includeDiagnostics
    });
    syncSettingsControls();
  }

  function markAudioEndpointLatencyLoading(renderPage = true) {
    const selectedModule = state.audioOutput.outputModule === 'auto' ? 'mmdevice' : state.audioOutput.outputModule;
    const selectedId = state.audioOutput.outputDeviceId || '';
    const selectedDevice = state.audioDevices.devices.find(device =>
      String(device.moduleId || '').toLowerCase() === String(selectedModule || '').toLowerCase() &&
      String(device.id || '') === selectedId);
    state.audioDevices.endpointLatency = {
      endpointId: selectedId,
      deviceName: String(selectedDevice?.displayName || ''),
      isBluetooth: false,
      estimatedLatencyMilliseconds: null,
      streamLatencyMilliseconds: null,
      enginePeriodMilliseconds: null,
      status: 'loading'
    };
    if (renderPage && state.currentPage === 'settings' && state.settingsSection === 'audio') renderSettings();
  }

  function applyEffectPreferences() {
    root.dataset.windowEffect = state.windowEffect;
    nativePost('setWindowMaterial', { material: state.windowEffect === 'mica' ? 'mica' : 'none' });
    root.dataset.fullscreenBackground = state.fullscreenBackground;
    root.dataset.coverTransition = state.coverTransition;
    root.dataset.immersiveFade = state.immersiveFade ? 'on' : 'off';
    root.dataset.lyricBlur = state.lyricBlur ? 'on' : 'off';
    root.dataset.lyricSpring = state.lyricSpring ? 'on' : 'off';
    root.dataset.lyricAdaptive = state.lyricAdaptive ? 'on' : 'off';
    root.dataset.artistCoverBlur = state.artistCoverBlur ? 'on' : 'off';
    root.style.setProperty('--custom-window-background', state.customBackground ? `url("${state.customBackground}")` : 'none');
    root.style.setProperty('--lyric-font-size', `${Math.max(18, Math.min(48, state.lyricFontSize))}px`);
    root.style.setProperty('--lyric-font-family', state.lyricFont === 'system' ? '"Segoe UI Variable Display", "Microsoft YaHei UI", sans-serif' : `"${state.lyricFont}", "Microsoft YaHei UI", sans-serif`);
    root.style.setProperty('--lyric-vertical', String(Math.max(0, Math.min(1, state.lyricVertical))));
    root.style.setProperty('--lyric-pad-top', `${12 + Math.max(0, Math.min(1, state.lyricVertical)) * 54}%`);
    root.style.setProperty('--lyric-pad-bottom', `${12 + (1 - Math.max(0, Math.min(1, state.lyricVertical))) * 54}%`);
    const backgroundDuration = Math.max(8, 40 / Math.max(.5, state.backgroundSpeed));
    root.style.setProperty('--background-duration', `${backgroundDuration}s`);
    root.style.setProperty('--background-steps', `steps(${Math.max(1, Math.round(backgroundDuration * state.backgroundFps))})`);
  }

  function applyPlayerStyle(style = state.playerStyle) {
    state.playerStyle = style === 'record' ? 'record' : 'immersive';
    localStorage.setItem('auralis:player-style', state.playerStyle);
    $('#nowPlayingOverlay').dataset.playerStyle = state.playerStyle;
    syncVinylRotation();
    syncSettingsControls();
  }

  function artistAvatarMarkup(artist, tracks, compact = false) {
    const customUrl = state.artistImages[artist];
    const fallbackUrl = tracks.find(track => track.coverUrl)?.coverUrl || '';
    const imageUrl = customUrl || fallbackUrl;
    const shouldBlurCover = state.artistCoverBlur && !!fallbackUrl && !customUrl;
    const [a, b] = palettes[hashIndex(artist || '未知艺术家')];
    const style = `--cover-a:${a};--cover-b:${b}${imageUrl ? `;--artist-image:url(&quot;${escapeHtml(imageUrl)}&quot;)` : ''}`;
    const initial = escapeHtml((String(artist || '?').trim().charAt(0) || '?').toUpperCase());
    return `<div class="artist-avatar ${imageUrl ? 'has-image' : ''} ${customUrl ? 'has-custom-image' : ''} ${shouldBlurCover ? 'is-cover-blurred' : ''} ${compact ? 'compact-avatar' : ''}" style="${style}"><span class="artist-avatar-image"></span>${state.showArtistInitial ? `<span class="artist-initial">${initial}</span>` : ''}</div>`;
  }

  function requestPlatformConfiguration(force = false) {
    const configuration = state.platformConfiguration;
    if (configuration.pending || (!force && configuration.loaded)) return;
    configuration.pending = true;
    configuration.error = '';
    nativePost('getPlatformConfiguration');
  }

  function cancelPlatformSearch(clearResults = false) {
    if (platformSearchDebounceTimer) {
      clearTimeout(platformSearchDebounceTimer);
      platformSearchDebounceTimer = 0;
    }
    if (state.platformSearch.pendingRequestId) {
      nativePost('cancelPlatformSearch', { requestId: state.platformSearch.pendingRequestId });
    }
    state.platformSearch.requestId += 1;
    state.platformSearch.pendingRequestId = 0;
    state.platformSearch.pending = false;
    if (clearResults) {
      clearTimeout(platformSearchRetryTimer);
      platformSearchRetryTimer = 0;
      state.platformSearch.items = [];
      state.platformSearch.pages = [];
      state.platformSearch.pageIndex = 0;
      state.platformSearch.pendingPageIndex = 0;
      state.platformSearch.pendingPageHandle = '';
      state.platformSearch.nextPageHandle = '';
      state.platformSearch.totalCount = null;
      state.platformSearch.error = '';
      state.platformSearch.errorCode = '';
      state.platformSearch.retryAvailableAt = 0;
      state.platformSearch.query = '';
    }
  }

  function schedulePlatformSearchRetryAvailability() {
    clearTimeout(platformSearchRetryTimer);
    platformSearchRetryTimer = 0;
    const remaining = state.platformSearch.retryAvailableAt - Date.now();
    if (remaining <= 0) return;
    platformSearchRetryTimer = setTimeout(() => {
      platformSearchRetryTimer = 0;
      if (state.currentPage === 'search') renderSearch();
    }, Math.min(remaining + 80, 2_147_000_000));
  }

  function schedulePlatformSearch() {
    const query = state.query.trim();
    cancelPlatformSearch(true);
    state.platformSearch.query = query;
    if (query.length < 2) return;

    const providerId = state.platformSearch.providerId;
    if (!platformProviders[providerId]) return;
    const needsGateway = platformProviderNeedsConfiguration(providerId);
    if (needsGateway && !state.platformConfiguration.loaded) {
      requestPlatformConfiguration();
      return;
    }
    if (needsGateway) return;

    const requestId = ++state.platformSearch.requestId;
    // Debounce is part of loading, not a completed empty response. Keep the
    // native request ID unset until dispatch so cancellation sends no fake ID.
    state.platformSearch.pending = true;
    platformSearchDebounceTimer = setTimeout(() => {
      platformSearchDebounceTimer = 0;
      if (state.currentPage !== 'search' || state.query.trim() !== query || requestId !== state.platformSearch.requestId) return;
      state.platformSearch.pending = true;
      state.platformSearch.pendingRequestId = requestId;
      state.platformSearch.pendingPageIndex = 0;
      state.platformSearch.pendingPageHandle = '';
      state.platformSearch.error = '';
      renderSearch();
      nativePost('platformSearchTracks', {
        requestId,
        query,
        providerId,
        pageSize: state.platformSearch.pageSize,
        pageHandle: null
      });
    }, 300);
    if (state.currentPage === 'search') renderSearch();
  }

  function showPlatformSearchPage(pageIndex) {
    const search = state.platformSearch;
    if (search.pending || pageIndex < 0) return;
    const cached = search.pages[pageIndex];
    if (cached) {
      search.pageIndex = pageIndex;
      search.items = cached.items;
      search.nextPageHandle = cached.nextPageHandle;
      search.totalCount = cached.totalCount;
      search.error = '';
      search.errorCode = '';
      search.retryAvailableAt = 0;
      renderSearch();
      pageContent.querySelector('.platform-search-section')?.scrollIntoView({ block: 'start', behavior: isMotionReduced() ? 'auto' : 'smooth' });
      return;
    }

    const previous = search.pages[pageIndex - 1];
    if (!previous?.nextPageHandle || state.query.trim().length < 2) return;
    const requestId = ++search.requestId;
    search.pending = true;
    search.pendingRequestId = requestId;
    search.pendingPageIndex = pageIndex;
    search.pendingPageHandle = previous.nextPageHandle;
    search.error = '';
    search.errorCode = '';
    renderSearch();
    nativePost('platformSearchTracks', {
      requestId,
      query: state.query.trim(),
      providerId: search.providerId,
      pageSize: search.pageSize,
      pageHandle: previous.nextPageHandle
    });
  }

  function setPage(page, options = {}) {
    if (page === 'savedPlaylists') mediaHub?.refresh();
    if (!options.force && page === state.currentPage && !options.queryChanged) {
      if (page === 'settings' && state.settingsSection !== 'home') {
        navigateSettingsSection('home');
      }
      return;
    }
    settingsNavigationToken += 1;
    settingsTransitionInProgress = false;
    settingsRenderPending = false;
    closeFluentFlyout();
    const previousIndex = pageOrder.indexOf(state.currentPage);
    const nextIndex = pageOrder.indexOf(page);
    pageMotionDirection = previousIndex >= 0 && nextIndex >= 0 && nextIndex < previousIndex ? 'backward' : 'forward';
    const leavingSearch = state.currentPage === 'search' && page !== 'search';
    state.currentPage = page;
    if (page !== 'onlinePlaylists') onlinePlaylistReturnFocus = '';
    if (page !== 'settings') lanPairingLinkVisible = false;
    if (page === 'settings') state.settingsSection = 'home';
    const navigationPage = page === 'onlineCollection'
      ? 'onlinePlaylists'
      : page;
    $$('.nav-item[data-page]').forEach(button => button.classList.toggle('active', button.dataset.page === navigationPage));
    $('#sidebarSearchButton').classList.toggle('active', page === 'search');
    $('.main').classList.toggle('search-mode', page === 'search');
    if (page !== 'search') {
      $('.topbar').classList.remove('open');
      $('#searchInput').blur();
      if (leavingSearch) {
        cancelPlatformSearch(true);
        state.query = '';
        $('#searchInput').value = '';
        $('.search-box').classList.remove('query-active');
      }
    }
    const existing = $('.page-view', pageContent);
    clearTimeout(pageTransitionTimer);
    if (existing && !isMotionReduced()) {
      existing.classList.add('is-leaving', pageMotionDirection === 'backward' ? 'to-right' : 'to-left');
      pageTransitionTimer = setTimeout(renderPage, 145);
    } else {
      renderPage();
    }
  }

  function navigateSettingsSection(section) {
    if (state.currentPage !== 'settings') return;
    const nextSection = section === 'home' || settingsCategories.some(category => category.id === section)
      ? section
      : 'home';
    if (nextSection === state.settingsSection) return;
    if (nextSection !== 'lan') lanPairingLinkVisible = false;

    closeFluentFlyout();
    const previousSection = state.settingsSection;
    const movingBackward = nextSection === 'home';
    if (previousSection === 'home' && nextSection !== 'home') {
      settingsHomeScrollTop = pageContent.scrollTop;
    }
    pageMotionDirection = movingBackward ? 'backward' : 'forward';
    const existing = $('.page-view', pageContent);
    clearTimeout(pageTransitionTimer);
    const navigationToken = ++settingsNavigationToken;
    settingsTransitionInProgress = true;

    const commitNavigation = () => {
      if (navigationToken !== settingsNavigationToken || state.currentPage !== 'settings') return;
      state.settingsSection = nextSection;
      if (nextSection === 'audio') markAudioEndpointLatencyLoading(false);
      settingsTransitionInProgress = false;
      renderSettings({ force: true });
      preparePageMotion();
      if (nextSection === 'audio') nativePost('requestAudioDevices');
      requestAnimationFrame(() => {
        const focusTarget = nextSection === 'home'
          ? $(`[data-settings-section="${previousSection}"]`, pageContent)
          : $('.settings-back-button', pageContent);
        if (nextSection === 'home') pageContent.scrollTop = settingsHomeScrollTop;
        focusTarget?.focus({ preventScroll: true });
        if (nextSection === 'home') focusTarget?.scrollIntoView({ block: 'nearest' });
      });
    };

    if (existing && !isMotionReduced()) {
      existing.inert = true;
      existing.classList.add('is-leaving', movingBackward ? 'to-right' : 'to-left');
      pageTransitionTimer = setTimeout(commitNavigation, 145);
    } else {
      commitNavigation();
    }
  }

  function renderPage() {
    const renderers = {
      home: renderHome,
      songs: () => renderTrackPage('歌曲', '你的全部本地音乐', state.tracks),
      favorites: () => renderTrackPage('我喜欢的', '随时重温你收藏的旋律', state.tracks.filter(track => state.favorites.has(track.id))),
      recent: () => renderTrackPage('最近播放', '最近在 Auralis 中听过的音乐', recentTracks()),
      albums: renderAlbums,
      artists: renderArtists,
      search: renderSearch,
      onlinePlaylists: renderOnlinePlaylists,
      onlineCollection: renderOnlineCollection,
      savedPlaylists: () => mediaHub?.render(),




      settings: renderSettings
    };
    (renderers[state.currentPage] || renderHome)();
    trackRowsRevision += 1;
    preparePageMotion();
    syncPlaybackUi();
  }

  function renderHome() {
    const artists = new Set(state.tracks.map(track => track.artist)).size;
    const albums = new Set(state.tracks.map(track => track.album)).size;
    const latest = recentTracks().slice(0, 6);
    pageContent.innerHTML = `
      <div class="page-view home-view">
        <div class="hero">
          <div class="hero-content">
            <span class="eyebrow">YOUR MUSIC · YOUR SPACE</span>
            <h1>让本地音乐有个<br><span>好看的家。</span></h1>
            <p>本地曲库始终只属于你；需要时也可以在搜索页临时查找在线歌曲，不会把它们写入本地音乐库。</p>
            <div class="hero-actions">
              <button class="accent-button" data-action="pick-files">${icon('plus')} 添加音乐</button>
              <button class="secondary-button" data-action="pick-folder">${icon('folder')} 添加文件夹</button>
            </div>
          </div>
          <div class="hero-art" aria-hidden="true"><div class="hero-disc"></div><div class="hero-sleeve"></div></div>
        </div>
        <section class="section">
          <div class="stats-grid">
            <div class="stat-card"><div class="stat-icon">${icon('music')}</div><div><span class="stat-value">${state.tracks.length}</span><span class="stat-label">首本地歌曲</span></div></div>
            <div class="stat-card"><div class="stat-icon blue">${icon('album')}</div><div><span class="stat-value">${albums}</span><span class="stat-label">张专辑</span></div></div>
            <div class="stat-card"><div class="stat-icon orange">${icon('artist')}</div><div><span class="stat-value">${artists}</span><span class="stat-label">位艺术家</span></div></div>
          </div>
        </section>
        <section class="section">
          <div class="section-title-row"><h2>${latest.length ? '最近聆听' : '开始你的音乐库'}</h2>${latest.length ? '<button class="text-button" data-page-link="recent">查看全部</button>' : ''}</div>
          ${latest.length ? `<div class="recent-grid">${latest.map(mediaCard).join('')}</div>` : emptyInline()}
        </section>
      </div>`;
  }

  function emptyInline() {
    return `<div class="empty-state">
      <div class="empty-illustration">${icon('music')}</div>
      <h2>这里还很安静</h2>
      <p>添加几个音频文件，或选择一个文件夹。Auralis 只会读取你亲自选择的本地内容。</p>
      <div class="empty-actions"><button class="accent-button" data-action="pick-files">${icon('plus')} 添加音乐</button><button class="secondary-button" data-action="pick-folder">${icon('folder')} 添加文件夹</button></div>
    </div>`;
  }

  function platformProviderSelectMarkup() {
    return `<div class="platform-provider-picker"><span>在线来源</span>${fluentSelectMarkup('platformProvider', state.platformSearch.providerId, Object.entries(platformProviders), '在线搜索来源', 'compact')}</div>`;
  }

  function platformTrackList(tracks, startIndex = 0) {
    return `<div class="list-surface platform-list-surface">
      <div class="track-list-header platform-track-grid"><span>#</span><span>标题</span><span>艺术家</span><span>专辑</span><span>时长</span><span>来源</span><span aria-label="操作"></span></div>
      <div class="track-list">${tracks.map((track, index) => platformTrackRow(track, startIndex + index)).join('')}</div>
    </div>`;
  }

  function platformTrackRow(track, index) {
    const pending = state.platformPlayback.pendingHandle === track.handle;
    const videoPending = state.platformVideo.pendingHandle === track.handle;
    const sourceName = platformDisplayName(track.providerId);
    const availability = track.unavailable ? '不可播放' : track.previewOnly ? '试听' : sourceName;
    const trackHint = pending ? t('正在准备音频…') : track.previewOnly
      ? '仅提供试听片段'
      : '在线结果 · 不会加入本地曲库';
    const videoLabel = '视频';
    const capabilities = currentPlatformCapabilities(track);
    const commentsAction = capabilities.includes('Comments')
      ? `<button class="row-action" data-media-action="track-comments" data-id="${escapeHtml(track.handle)}" aria-label="查看评论" title="查看评论">☷</button>` : '';
    const videoAction = track.hasMusicVideo && capabilities.includes('VideoResolution')
      ? `<button class="platform-mv-button ${videoPending ? 'is-loading' : ''}" data-platform-mv-handle="${escapeHtml(track.handle)}" aria-label="播放 ${escapeHtml(track.title)} 的${videoLabel}" title="全屏播放器内播放视频" ${videoPending ? 'disabled' : ''}><span class="platform-mv-icon">${icon('video')}</span><span class="platform-mv-label">${videoLabel}</span><span class="platform-mv-spinner"></span></button>`
      : '<span class="platform-mv-placeholder" aria-hidden="true">—</span>';
    return `<div class="track-row platform-track-row platform-track-grid ${pending ? 'is-loading' : ''} ${track.unavailable ? 'is-unavailable' : ''}" data-track-id="${escapeHtml(track.id)}" data-platform-play-row="${escapeHtml(track.handle)}" tabindex="${track.unavailable ? '-1' : '0'}" role="button" aria-label="${pending ? t('正在准备音频…') : t('播放')} ${escapeHtml(track.title)}" aria-busy="${pending}" aria-disabled="${track.unavailable ? 'true' : 'false'}">
      <div class="track-index-cell"><span class="track-number">${String(index + 1).padStart(2, '0')}</span><button class="row-play" data-platform-play-handle="${escapeHtml(track.handle)}" aria-label="播放 ${escapeHtml(track.title)}" ${track.unavailable || pending ? 'disabled' : ''}><span class="row-play-icon">${icon('play')}</span><span class="row-pause-icon">${icon('pause')}</span><span class="platform-row-spinner"></span></button></div>
      <div class="track-title-cell"><div class="row-cover" style="${coverStyle(track)}">${track.coverUrl ? '' : escapeHtml(track.title.charAt(0).toUpperCase())}</div><div class="title-stack"><strong>${escapeHtml(track.title)}</strong><span>${trackHint}</span></div></div>
      <span class="track-cell">${escapeHtml(track.artist)}</span><span class="track-cell">${escapeHtml(track.album)}</span>
      <span class="duration-cell">${formatTime(track.durationSeconds)}</span>
      <span class="provider-badge provider-${escapeHtml(track.providerId)} ${track.unavailable ? 'unavailable' : ''}">${escapeHtml(availability)}</span>
      <span class="platform-mv-cell"><button class="row-action" data-media-action="save" data-id="${escapeHtml(track.handle)}" aria-label="加入歌单" title="加入歌单">${icon('plus')}</button>${commentsAction}${videoAction}</span>
    </div>`;
  }

  function openPlatformMusicVideo(handle) {
    if (!handle || state.platformVideo.pendingHandle) return;
    const track = findPlatformTrackByHandle(handle);
    if (!track?.hasMusicVideo || !currentPlatformCapabilities(track).includes('VideoResolution')) return;
    toggleNowPlaying(true);
    if (state.currentTrackId === track.id) mediaHub?.setVideo(true);
    else { requestedInlineVideo = track.handle; requestPlatformPlayback(track); }
  }

  function findPlatformTrackByHandle(handle) {
    return [
      ...state.platformSearch.items,
      ...state.onlineQueue,
      ...state.mixedQueue,
      ...(mediaHub?.allTracks() || []),
      ...(onlineCollection.detail?.tracks || [])
    ].find(item => item.handle === handle);
  }

  function platformSearchSectionMarkup() {
    if (!Object.keys(platformProviders).length) return '';
    const search = state.platformSearch;
    const sourceName = platformDisplayName(search.providerId);
    const pageStartIndex = search.pages.slice(0, search.pageIndex)
      .reduce((total, page) => total + (page?.items?.length || 0), 0);
    const currentPage = search.pages[search.pageIndex];
    const canGoPrevious = search.pageIndex > 0;
    const canGoNext = !!currentPage?.nextPageHandle || !!search.pages[search.pageIndex + 1];
    const pageStatus = Number.isFinite(search.totalCount)
      ? t('search.pageWithTotal', { page: search.pageIndex + 1, total: search.totalCount })
      : t('search.page', { page: search.pageIndex + 1 });
    const paging = search.items.length ? `<nav class="platform-search-pagination" aria-label="${escapeHtml(t('search.pagination'))}">
      <span class="platform-search-page-status">${escapeHtml(pageStatus)}</span>
      <div class="platform-search-page-actions">
        <button type="button" class="secondary-button compact-action" data-action="platform-search-previous" ${!canGoPrevious || search.pending ? 'disabled' : ''} aria-label="${escapeHtml(t('search.previousPage'))}">${icon('chevron-left')}<span>${escapeHtml(t('search.previousPage'))}</span></button>
        <button type="button" class="secondary-button compact-action ${search.pending && search.pendingPageIndex > search.pageIndex ? 'is-loading' : ''}" data-action="platform-search-next" ${!canGoNext || search.pending ? 'disabled' : ''} aria-label="${escapeHtml(t('search.nextPage'))}"><span>${escapeHtml(t('search.nextPage'))}</span>${icon('chevron-right')}<span class="platform-page-button-spinner"></span></button>
      </div>
    </nav>` : '';
    let body;
    if (state.query.trim().length < 2) {
      body = `<div class="platform-inline-state"><strong>继续输入即可搜索在线歌曲</strong><span>至少输入两个字符，减少不必要的网络请求。</span></div>`;
    } else if (platformProviderNeedsConfiguration(search.providerId) && (!state.platformConfiguration.loaded || state.platformConfiguration.pending)) {
      body = '<div class="platform-inline-state is-loading"><span class="platform-spinner"></span><div><strong>正在检查在线搜索配置</strong><span>本机音乐结果不受影响。</span></div></div>';
    } else if (platformProviderNeedsConfiguration(search.providerId)) {
      body = `<div class="platform-inline-state"><div><strong>在线搜索尚未配置</strong><span>请先完成此插件声明的必填设置。本地搜索不受影响。</span></div><button class="secondary-button compact-action" data-page-link="settings">前往设置</button></div>`;
    } else if (search.pending && !search.items.length) {
      body = `<div class="platform-inline-state is-loading"><span class="platform-spinner"></span><div><strong>正在搜索 ${escapeHtml(sourceName)}</strong><span>查询只发送给当前选择的在线来源。</span></div></div>`;
    } else if (search.error && !search.items.length) {
      const retryWaitSeconds = Math.max(0, Math.ceil((search.retryAvailableAt - Date.now()) / 1000));
      const protectedByRateLimit = search.errorCode === 'ratelimited';
      const heading = protectedByRateLimit ? `${escapeHtml(sourceName)} 暂时限制了搜索` : '在线结果暂时不可用';
      const retryLabel = retryWaitSeconds > 0 ? `约 ${retryWaitSeconds} 秒后可重试` : '重试';
      body = `<div class="platform-inline-state is-error"><div><strong>${heading}</strong><span>${escapeHtml(search.error)}</span></div><button class="secondary-button compact-action" data-action="retry-platform-search" ${retryWaitSeconds > 0 ? 'disabled' : ''}>${retryLabel}</button></div>`;
    } else if (search.items.length) {
      const pageError = search.error
        ? `<div class="platform-inline-state is-error platform-page-error"><div><strong>${escapeHtml(t('search.nextPageFailed'))}</strong><span>${escapeHtml(search.error)}</span></div><button class="secondary-button compact-action" data-action="platform-search-next">重试</button></div>`
        : '';
      body = `${platformTrackList(search.items, pageStartIndex)}${paging}${pageError}`;
    } else {
      body = `<div class="platform-inline-state"><strong>${escapeHtml(sourceName)} 没有匹配歌曲</strong><span>本地结果仍会保留，可以换一个关键词或在线来源。</span></div>`;
    }
    const mvCount = search.items.filter(item => item.hasMusicVideo && currentPlatformCapabilities(item).includes('VideoResolution')).length;
    const resultSummary = search.items.length
      ? `${Number.isFinite(search.totalCount) ? search.totalCount : search.items.length} 首${mvCount ? ` · 本页 ${mvCount} 个视频` : ''}`
      : '仅搜索与取流';
    return `<section class="search-section platform-search-section"><div class="section-title-row"><h2>在线歌曲</h2><span>${resultSummary}</span>${platformProviderSelectMarkup()}</div>${body}</section>`;
  }

  const onlinePlaylistProviders = [];
  const onlineSettingsProviders = [];
  let onlinePlaylistReturnFocus = '';
  const onlineCollection = { providerId: '', requestId: 0, pendingRequestId: 0, pendingHandle: '', detail: null, error: '', errorCode: '' };
  function canReadOnlineCollections(provider) {
    return provider.configured !== false && (!provider.capabilities.includes('Authentication') ||
      state.platformConfiguration[provider.authenticationKey]?.status === 'signedin');
  }
  function renderOnlineCollection() {
    const provider = onlinePlaylistProviders.find(p => p.id === onlineCollection.providerId);
    if (!provider) { renderOnlinePlaylists(); return; }
    renderCloudPlaylistPage(onlineCollection, { providerName: provider.name, eyebrow: provider.name, requiresAuthentication: provider.capabilities.includes('Authentication'), canLogin: provider.capabilities.includes('NativeLogin'),
      authentication: state.platformConfiguration[provider.authenticationKey] || {status:'signedout'},
      loginAction: 'generic-platform-login', refreshAction: 'generic-platform-refresh' });
  }
  function normalizeOnlineCollection(item) {
    if (!item?.handle) return null;
    return { handle:String(item.handle), title:String(item.title || '未命名歌单'), owner:String(item.owner || ''),
      trackCount:Math.max(0, Number(item.trackCount) || 0), coverUrl:safePlatformImageUrl(item.coverUrl),
      description:String(item.description || ''), isEditable:!!item.isEditable };
  }

  function onlinePlaylistCardMarkup(provider, playlist, index) {
    return `<button type="button" ${provider.capabilities.includes('PlaylistDetails') ? '' : 'disabled'} class="online-playlist-card provider-${escapeHtml(provider.id)}" data-online-playlist-provider="${escapeHtml(provider.id)}" data-online-playlist-handle="${escapeHtml(playlist.handle)}" style="--online-card-index:${Math.min(index, 12)}" aria-label="打开 ${escapeHtml(playlist.title)}，${escapeHtml(localizedCount('track', playlist.trackCount))}">
      <span class="online-playlist-cover" style="${coverStyle({ id: playlist.handle, title: playlist.title, coverUrl: playlist.coverUrl })}">${playlist.coverUrl ? '' : icon('music')}</span>
      <span class="online-playlist-copy"><strong data-i18n-skip>${escapeHtml(playlist.title)}</strong><span data-i18n-skip>${escapeHtml(playlist.owner || provider.name)}</span><small>${escapeHtml(provider.name)} · ${escapeHtml(localizedCount('track', playlist.trackCount))}</small></span>
      <span class="online-playlist-open" aria-hidden="true">›</span>
    </button>`;
  }

  function onlinePlaylistProviderMarkup(provider) {
    if (!provider.capabilities.includes('Authentication')) return '';
    const configuration = state.platformConfiguration;
    const authentication = configuration[provider.authenticationKey];
    const playlists = configuration[provider.playlistsKey] || [];
    const error = configuration[provider.errorKey] || authentication?.error || '';
    const signedIn = authentication?.status === 'signedin';
    const expired = authentication?.status === 'expired';
    const accountName = authentication?.accountDisplayName || provider.name;
    return `<section class="online-account-row" data-online-provider="${escapeHtml(provider.id)}">
      <div class="online-account-copy"><strong>${escapeHtml(provider.name)}</strong><span>${!configuration.loaded ? t('正在读取账号与歌单') : signedIn ? `${t('已连接')} · ${escapeHtml(accountName)}` : expired ? t('连接已过期') : t('未连接')}</span>${error ? `<p role="status">${escapeHtml(error)}</p>` : ''}</div>
      <div class="online-account-actions">${signedIn ? `<button class="secondary-button" data-action="${escapeHtml(provider.refreshAction)}">${t('刷新')}</button><button class="secondary-button danger-text" data-action="${escapeHtml(provider.signoutAction)}">${t('断开')}</button>` : provider.capabilities.includes('NativeLogin') ? `<button class="accent-button" data-action="${escapeHtml(provider.loginAction)}" ${!configuration.loaded || !provider.configured ? 'disabled' : ''}>${expired ? t('重新登录') : t('登录')}</button>` : '<span>此插件未提供交互式登录。</span>'}</div>
    </section>`;
  }

  function publicPlaylistSourceMarkup(provider) {
    const configured = provider.configured !== false;
    const error = state.platformConfiguration[provider.errorKey];
    return `<section class="online-account-row" data-public-provider="${escapeHtml(provider.id)}"><div class="online-account-copy"><strong data-i18n-skip>${escapeHtml(provider.name)}</strong><span>${t(configured ? '公开歌单 · 无需登录' : '请先完成插件设置')}</span>${error ? `<p role="status">${escapeHtml(error)}</p>` : ''}</div><div class="online-account-actions"><button type="button" class="secondary-button" data-action="${configured ? 'generic-platform-refresh' : provider.settings.length ? 'configure-online-source' : 'manage-platform-plugins'}" data-provider-id="${escapeHtml(provider.id)}">${t(configured ? '刷新' : provider.settings.length ? '前往设置' : '管理插件')}</button></div></section>`;
  }

  function renderOnlinePlaylists() {
    const returnFocus = onlinePlaylistReturnFocus;
    onlinePlaylistReturnFocus = '';
    if (!onlinePlaylistProviders.length) {
      pageContent.innerHTML = `<div class="page-view online-playlists-page"><header class="page-header"><div><h1>${t('在线歌单')}</h1></div></header><div class="online-collection-empty">${icon('music')}<h2>${t('尚未启用歌单插件')}</h2><p>${t('导入并启用支持歌单的插件，重启后将在这里显示对应平台。')}</p><button class="accent-button" data-action="manage-platform-plugins">${t('管理插件')}</button></div></div>`;
      if (!state.platformConfiguration.loaded) requestPlatformConfiguration();
      return;
    }
    const previousProvider = pageContent.querySelector('.online-playlists-page')?.dataset.provider;
    const previousHandles = new Set(Array.from(pageContent.querySelectorAll('[data-online-playlist-handle]'), card => card.dataset.onlinePlaylistHandle));
    const selected = onlinePlaylistProviders.some(p => p.id === state.onlinePlaylistProvider) ? state.onlinePlaylistProvider : 'all';
    const providers = selected === 'all' ? onlinePlaylistProviders : onlinePlaylistProviders.filter(p => p.id === selected);
    const accounts = onlinePlaylistProviders.filter(p => p.capabilities.includes('Authentication'));
    const publicSources = providers.filter(p => !p.capabilities.includes('Authentication'));
    const connected = accounts.filter(p => state.platformConfiguration[p.authenticationKey]?.status === 'signedin');
    const needsAttention = onlinePlaylistProviders.some(p => state.platformConfiguration[p.authenticationKey]?.status === 'expired' || state.platformConfiguration[p.errorKey]);
    const items = providers.flatMap(provider => canReadOnlineCollections(provider)
      ? (state.platformConfiguration[provider.playlistsKey] || []).map(playlist => ({ provider, playlist })) : []);
    const accountsOpen = pageContent.querySelector('#onlineAccountManager')?.open ?? !connected.length;
    const publicSourcesOpen = pageContent.querySelector('#onlinePublicSources')?.open ?? false;
    const publicSummaryFocused = document.activeElement === pageContent.querySelector('#onlinePublicSources > summary');
    const publicStatus = publicSources.some(p => p.configured === false) ? '部分来源需要配置'
      : publicSources.some(p => state.platformConfiguration[p.errorKey]) ? '部分来源需要处理' : '公开歌单 · 无需登录';
    const publicRows = publicSources.map(publicPlaylistSourceMarkup).join('');
    const focusedFilter = returnFocus || document.activeElement?.dataset.onlineProviderFilter;
    const filterMarkup = (id, name, count) => `<button type="button" class="online-provider-filter" data-online-provider-filter="${id}" aria-pressed="${selected === id}">${escapeHtml(name)}<span>${count}</span></button>`;
    const count = p => canReadOnlineCollections(p) ? (state.platformConfiguration[p.playlistsKey] || []).length : 0;
    pageContent.innerHTML = `<div class="page-view online-playlists-page" data-provider="${escapeHtml(selected)}">
      <header class="page-header"><div><h1>${t('在线歌单')}</h1><p>${t('你的音乐收藏，汇聚在这里')}</p></div><span class="online-source-badge">${t('与本地音乐库分离')}</span></header>
      <nav class="online-provider-filters" aria-label="${t('筛选在线平台')}">${filterMarkup('all', t('全部'), onlinePlaylistProviders.reduce((sum, p) => sum + count(p), 0))}${onlinePlaylistProviders.map(p => filterMarkup(p.id, p.name, count(p))).join('')}</nav>
      <div class="online-provider-content">
      ${selected === 'all' && accounts.length ? `<details id="onlineAccountManager" class="online-account-manager" ${accountsOpen ? 'open' : ''}><summary>${t('管理平台账号')}<span>${needsAttention ? t('部分账号需要处理') + ' · ' : ''}${connected.length} / ${accounts.length} ${t('已连接')}</span></summary><div>${accounts.map(onlinePlaylistProviderMarkup).join('')}</div></details>` : selected !== 'all' && providers.some(p => p.capabilities.includes('Authentication')) ? `<div class="online-account-manager">${providers.map(onlinePlaylistProviderMarkup).join('')}</div>` : ''}
      ${publicSources.length > 1 && selected === 'all' ? `<details id="onlinePublicSources" class="online-account-manager online-public-sources" ${publicSourcesOpen ? 'open' : ''}><summary>${t('公开歌单来源')}<span>${publicSources.length} · ${t(publicStatus)}</span></summary><div>${publicRows}</div></details>` : publicRows ? `<div class="online-account-manager">${publicRows}</div>` : ''}
      <section class="online-collection" aria-label="${t('歌单与收藏夹')}"><div class="online-collection-heading"><h2>${t('歌单与收藏夹')}</h2><span>${items.length}</span></div>
      ${!state.platformConfiguration.loaded ? `<div class="online-playlist-empty is-loading" role="status"><span class="platform-spinner"></span>${t('正在读取账号与歌单')}</div>` : items.length ? `<div class="online-playlist-grid">${items.map(({ provider, playlist }, index) => onlinePlaylistCardMarkup(provider, playlist, index)).join('')}</div>` : `<div class="online-collection-empty">${icon('music')}<h2>${t('还没有可显示的歌单')}</h2><p>${t(accounts.length ? '连接平台账号，或刷新已连接账号的收藏。' : '刷新来源，或在插件设置中完成所需配置。')}</p></div>`}
      </section>
      </div>
    </div>`;
    if (focusedFilter) pageContent.querySelector(`[data-online-provider-filter="${focusedFilter}"]`)?.focus({ preventScroll: true });
    else if (publicSummaryFocused) pageContent.querySelector('#onlinePublicSources > summary')?.focus({ preventScroll: true });
    const switchedProvider = previousProvider && previousProvider !== selected;
    if (switchedProvider && !isMotionReduced()) {
      const order = ['all', ...onlinePlaylistProviders.map(provider => provider.id)];
      const direction = order.indexOf(selected) > order.indexOf(previousProvider) ? 1 : -1;
      pageContent.querySelector('.online-provider-content')?.animate(
        [{ opacity: 0, transform: `translateX(${direction * 12}px)` }, { opacity: 1, transform: 'none' }],
        { duration: 240, easing: 'cubic-bezier(.16,1,.3,1)' });
    }
    if (!switchedProvider && !isMotionReduced()) pageContent.querySelectorAll('[data-online-playlist-handle]').forEach((card, index) => {
      if (!previousHandles.has(card.dataset.onlinePlaylistHandle)) card.animate(
        [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }],
        { duration: 240, delay: Math.min(index, 6) * 25, fill: 'backwards', easing: 'cubic-bezier(.16,1,.3,1)' });
    });
    if (!state.platformConfiguration.loaded) requestPlatformConfiguration();
  }

  function renderCloudPlaylistPage(current, options) {
    const collectionName = options.collectionName || '个人歌单';
    if (current.pendingHandle) {
      pageContent.innerHTML = `<div class="page-view online-collection-page"><button type="button" class="settings-back-button online-playlist-detail-back" data-page-link="onlinePlaylists" aria-label="返回在线歌单">←</button><div class="platform-page-loading"><span class="platform-spinner"></span><div><strong>正在读取 ${escapeHtml(options.providerName)} ${escapeHtml(collectionName)}</strong><span>在线内容只用于当前浏览，不会合并到本地歌单。</span></div></div></div>`;
      return;
    }
    if (current.error || !current.detail) {
      const authenticationUnavailable = options.requiresAuthentication !== false && (options.authentication.status !== 'signedin'
        || current.errorCode === 'authenticationrequired');
      const loginAction = authenticationUnavailable && options.canLogin !== false
        ? `<button class="accent-button" data-action="${escapeHtml(options.loginAction)}">重新登录 ${escapeHtml(options.providerName)}</button>`
        : '';
      pageContent.innerHTML = `<div class="page-view online-collection-page"><button type="button" class="settings-back-button online-playlist-detail-back" data-page-link="onlinePlaylists" aria-label="返回在线歌单">←</button><div class="empty-state"><div class="empty-illustration">${icon('music')}</div><h2>${authenticationUnavailable ? `${escapeHtml(options.providerName)} 账号需要重新登录` : `${escapeHtml(options.providerName)} ${escapeHtml(collectionName)}暂不可用`}</h2><p>${escapeHtml(current.error || `请返回“在线歌单”重新选择一个 ${options.providerName} ${collectionName}。`)}</p><div class="empty-actions">${loginAction}<button class="secondary-button" data-action="${escapeHtml(options.refreshAction)}">${t('刷新并返回歌单')}</button></div></div></div>`;
      return;
    }
    const playlist = current.detail.playlist;
    const tracks = current.detail.tracks;
    pageContent.innerHTML = `<div class="page-view online-collection-page">
      <button type="button" class="settings-back-button online-playlist-detail-back" data-page-link="onlinePlaylists" aria-label="返回在线歌单">←</button>
      <header class="online-collection-header">
        <div class="online-collection-cover" style="${coverStyle({ id: playlist.handle, title: playlist.title, coverUrl: playlist.coverUrl })}">${playlist.coverUrl ? '' : icon('music')}</div>
        <div><span class="eyebrow">${escapeHtml(options.eyebrow)} · ${t('在线')}</span><h1>${escapeHtml(playlist.title)}</h1><p>${escapeHtml(playlist.owner)} · ${escapeHtml(localizedCount('song', playlist.trackCount || tracks.length))}</p><small>此处内容来自 ${escapeHtml(options.providerName)}，与 Auralis 本地歌单完全分离。</small></div>
      </header>
      ${tracks.length ? platformTrackList(tracks) : `<div class="platform-inline-state"><strong>歌单中没有可显示的歌曲</strong><span>${escapeHtml(options.providerName)} 可能限制了当前歌单内容。</span></div>`}
    </div>`;
  }

  function renderSearch() {
    const totalAlbums = new Set(state.tracks.map(track => track.album)).size;
    const totalArtists = new Set(state.tracks.map(track => track.artist)).size;
    if (!state.query) {
      pageContent.innerHTML = `<div class="page-view search-view">
        <header class="page-header search-page-header"><div><h1>搜索音乐</h1><p>本机音乐即时过滤；在线来源仅在这里返回搜索数据与临时媒体流</p></div></header>
        ${searchHistoryMarkup()}
        <section class="search-welcome">
          <div class="search-welcome-icon">${icon('search')}</div>
          <h2>从本地开始，需要时连接在线来源</h2>
          <p>输入歌曲名、艺术家、专辑或文件名。本地结果不会离开设备；已配置的在线来源会收到你的搜索关键词。</p>
          <div class="search-library-stats"><span><strong>${state.tracks.length}</strong> 首本地歌曲</span><i></i><span><strong>${totalAlbums}</strong> 张专辑</span><i></i><span><strong>${totalArtists}</strong> 位艺术家</span></div>
          ${state.tracks.length ? '' : `<button class="accent-button" data-action="pick-folder">${icon('folder')} 添加本地音乐文件夹</button>`}
        </section>
      </div>`;
      requestPlatformConfiguration();
      return;
    }

    const matchedTracks = filterTracks(state.tracks);
    const albumNames = [...new Set(matchedTracks.map(track => track.album || '本地音乐'))];
    const artistNames = [...new Set(matchedTracks.map(track => track.artist || '未知艺术家'))];
    const albums = albumNames.map(album => [album, state.tracks.filter(track => (track.album || '本地音乐') === album)]);
    const artists = artistNames.map(artist => [artist, state.tracks.filter(track => (track.artist || '未知艺术家') === artist)]);
    const localBody = matchedTracks.length
      ? trackList(matchedTracks)
      : '<div class="platform-inline-state local-search-empty"><strong>本机音乐库中没有匹配歌曲</strong><span>在线结果会单独显示在下方，不会被加入本地曲库。</span></div>';
    pageContent.innerHTML = `<div class="page-view search-view">
      <header class="page-header search-page-header"><div><h1>“${escapeHtml(state.query)}”</h1><p>本机 ${matchedTracks.length} 首 · 在线结果与本地库保持分离</p></div><div class="page-actions">${matchedTracks.length ? `<button class="accent-button" data-action="play-all" data-first-id="${matchedTracks[0].id}">${icon('play')} 播放本地结果</button>` : ''}</div></header>
      <section class="search-section"><div class="section-title-row"><h2>本机歌曲</h2><span>${matchedTracks.length} 首</span></div>${localBody}</section>
      ${platformSearchSectionMarkup()}
      ${albums.length ? `<section class="search-section"><div class="section-title-row"><h2>本机专辑</h2><span>${localizedCount('album', albums.length)}</span></div><div class="search-card-grid">${albums.slice(0, 6).map(([album, tracks]) => `<article class="media-card" data-play-id="${tracks[0].id}" tabindex="0" role="button" aria-label="播放专辑 ${escapeHtml(album)}"><div class="media-card-cover" style="${coverStyle(tracks[0])}">${tracks[0].coverUrl ? '' : `<span class="cover-letter">${escapeHtml(album.charAt(0).toUpperCase())}</span>`}<button class="card-play" aria-label="播放专辑 ${escapeHtml(album)}">${icon('play')}</button></div><strong>${escapeHtml(album)}</strong><span>${localizedCount('song', tracks.length)}</span></article>`).join('')}</div></section>` : ''}
      ${artists.length ? `<section class="search-section"><div class="section-title-row"><h2>本机艺术家</h2><span>${artists.length} 位</span></div><div class="search-artist-grid">${artists.slice(0, 8).map(([artist, tracks]) => `<article class="search-artist-result" data-play-id="${tracks[0].id}" tabindex="0" role="button" aria-label="播放艺术家 ${escapeHtml(artist)}">${artistAvatarMarkup(artist, tracks, true)}<div><strong>${escapeHtml(artist)}</strong><span>${tracks.length} 首匹配歌曲</span></div>${icon('play')}</article>`).join('')}</div></section>` : ''}
    </div>`;
  }

  function mediaCard(track) {
    return `<article class="media-card" data-play-id="${track.id}" tabindex="0" role="button" aria-label="播放 ${escapeHtml(track.title)}">
      <div class="media-card-cover" style="${coverStyle(track)}">
        ${track.coverUrl ? '' : `<span class="cover-letter">${escapeHtml(track.title.charAt(0).toUpperCase())}</span>`}
        <button class="card-play" aria-label="播放 ${escapeHtml(track.title)}">${icon('play')}</button>
      </div>
      <strong>${escapeHtml(track.title)}</strong><span>${escapeHtml(track.artist)}</span>
    </article>`;
  }

  function renderTrackPage(title, subtitle, sourceTracks) {
    const tracks = filterTracks(sourceTracks);
    pageContent.innerHTML = `<div class="page-view">
      <header class="page-header"><div><h1>${escapeHtml(title)}</h1><p>${escapeHtml(subtitle)} · ${tracks.length} 首</p></div>
      <div class="page-actions">
        <button class="accent-button" data-action="play-all" data-first-id="${tracks[0]?.id || ''}" ${tracks.length ? '' : 'disabled'}>${icon('play')} 播放全部</button>
        <button class="secondary-button" data-action="pick-files">${icon('plus')} 添加音乐</button>
        <button class="secondary-button icon-only" data-action="pick-folder" aria-label="添加音乐文件夹" title="添加音乐文件夹">${icon('folder')}</button>
        <button class="secondary-button icon-only" data-action="sort" aria-label="切换排序" title="切换排序">${icon('sort')}</button>
        <button class="secondary-button icon-only" data-action="search" aria-label="搜索音乐" title="搜索音乐">${icon('search')}</button>
      </div></header>
      ${tracks.length ? trackList(tracks) : emptyTrackResult(sourceTracks.length === 0)}
    </div>`;
  }

  function emptyTrackResult(noTracks) {
    if (state.query && !noTracks) {
      return `<div class="empty-state"><div class="empty-illustration">${icon('music')}</div><h2>没有找到匹配的音乐</h2><p>换一个歌名、艺术家或专辑关键词试试。</p></div>`;
    }
    return emptyInline();
  }

  function trackList(tracks) {
    return `<div class="list-surface">
      <div class="track-list-header"><span>#</span><span>标题</span><span>艺术家</span><span>专辑</span><span>时长</span><span></span></div>
      <div class="track-list">${tracks.map((track, index) => trackRow(track, index)).join('')}</div>
    </div>`;
  }

  function trackRow(track, index) {
    const favorite = state.favorites.has(track.id);
    return `<div class="track-row" data-track-id="${track.id}">
      <div class="track-index-cell"><span class="track-number">${String(index + 1).padStart(2, '0')}</span><button class="row-play" data-play-id="${track.id}" aria-label="播放 ${escapeHtml(track.title)}"><span class="row-play-icon">${icon('play')}</span><span class="row-pause-icon">${icon('pause')}</span></button></div>
      <div class="track-title-cell"><div class="row-cover" style="${coverStyle(track)}">${track.coverUrl ? '' : escapeHtml(track.title.charAt(0).toUpperCase())}</div><div class="title-stack"><strong>${escapeHtml(track.title)}</strong><span>${escapeHtml(formatBytes(track.size))}</span></div></div>
      <span class="track-cell">${escapeHtml(track.artist)}</span><span class="track-cell">${escapeHtml(track.album)}</span>
      <span class="duration-cell">${formatTime(Number(track.durationSeconds) || 0)}</span>
      <div class="row-actions"><button class="row-action" data-media-action="save" data-id="${escapeHtml(track.id)}" aria-label="加入歌单" title="加入歌单">${icon('plus')}</button><button class="row-action ${favorite ? 'favorite' : ''}" data-favorite-id="${track.id}" aria-label="${favorite ? '取消收藏' : '收藏'}">${icon('heart')}</button><button class="row-action" data-remove-id="${track.id}" aria-label="从音乐库移除" title="从音乐库移除">${icon('trash')}</button></div>
    </div>`;
  }

  function renderAlbums() {
    const groups = groupBy(state.tracks, track => track.album || '本地音乐');
    const entries = [...groups.entries()];
    pageContent.innerHTML = `<div class="page-view"><header class="page-header"><div><h1>专辑</h1><p>按所在文件夹整理 · ${entries.length} 张</p></div><div class="page-actions"><button class="accent-button" data-action="pick-files">${icon('plus')} 添加音乐</button></div></header>
      ${entries.length ? `<div class="album-grid">${entries.map(([album, tracks]) => `<article class="media-card" data-play-id="${tracks[0].id}" tabindex="0" role="button" aria-label="播放专辑 ${escapeHtml(album)}"><div class="media-card-cover" style="${coverStyle(tracks[0])}">${tracks[0].coverUrl ? '' : `<span class="cover-letter">${escapeHtml(album.charAt(0).toUpperCase())}</span>`}<button class="card-play" aria-label="播放专辑 ${escapeHtml(album)}">${icon('play')}</button></div><strong>${escapeHtml(album)}</strong><span>${localizedCount('song', tracks.length)}</span></article>`).join('')}</div>` : emptyInline()}
    </div>`;
  }

  function renderArtists() {
    const groups = groupBy(state.tracks, track => track.artist || '未知艺术家');
    const entries = [...groups.entries()];
    pageContent.innerHTML = `<div class="page-view"><header class="page-header"><div><h1>艺术家</h1><p>音乐库中的声音 · ${entries.length} 位</p></div><div class="page-actions"><button class="accent-button" data-action="pick-files">${icon('plus')} 添加音乐</button></div></header>
      ${entries.length ? `<div class="artist-grid">${entries.map(([artist, tracks]) => `<article class="artist-card" data-play-id="${tracks[0].id}" tabindex="0" role="button" aria-label="播放艺术家 ${escapeHtml(artist)}"><div class="artist-avatar-wrap">${artistAvatarMarkup(artist, tracks)}<button class="artist-avatar-edit" data-artist-upload="${escapeHtml(artist)}" aria-label="为 ${escapeHtml(artist)} 选择头像" title="选择本地头像">${icon('camera')}</button></div><strong>${escapeHtml(artist)}</strong><span>${tracks.length} 首歌曲 · 可更换头像</span></article>`).join('')}</div>` : emptyInline()}
    </div>`;
  }

  function providerSettingForKind(kind) {
    const [, providerId, key] = String(kind).split(':');
    const provider = onlineSettingsProviders.find(p => p.id === providerId);
    return provider && { provider, setting:provider.settings.find(s => s.key === key) };
  }

  const providerSettingsSave = { sequence:0, pending:null, timer:0 };
  const providerSettingDrafts = new Map();
  const providerSettingDefinition = setting => JSON.stringify([setting.kind, !!setting.required,
    setting.kind === 'choice' ? setting.choices.map(choice => choice.value) : []]);
  function saveProviderSetting(providerId, key, value) {
    const provider = onlineSettingsProviders.find(p => p.id === providerId);
    const setting = provider?.settings.find(s => s.key === key);
    if (!setting || providerSettingsSave.pending || typeof value !== 'string' || value.length > 2048) return;
    const requestId = ++providerSettingsSave.sequence;
    const definition = providerSettingDefinition(setting);
    providerSettingDrafts.set(providerId + ':' + key, {value,definition,error:''});
    providerSettingsSave.pending = {requestId,providerId,key,value,definition};
    nativePost('saveOnlineProviderSetting', {requestId,providerId,key,value});
    clearTimeout(providerSettingsSave.timer);
    providerSettingsSave.timer = setTimeout(() => {
      if (providerSettingsSave.pending?.requestId !== requestId) return;
      providerSettingsSave.pending = null;
      providerSettingDrafts.set(providerId + ':' + key, {value,definition,error:'保存尚未确认，请刷新设置后重试。'});
      showToast('保存尚未确认，请刷新设置后重试。');
      if (state.currentPage === 'settings') renderSettings();
    }, 15000);
    if (state.currentPage === 'settings') renderSettings();
  }

  function platformSettingsMarkup() {
    const accounts = onlineSettingsProviders.filter(p => p.capabilities.includes('Authentication'));
    const rows = onlineSettingsProviders.flatMap(p => p.settings.map(s => {
      const draft = providerSettingDrafts.get(p.id + ':' + s.key);
      const displayValue = draft?.value ?? s.value;
      const control = s.kind === 'choice'
        ? fluentSelectMarkup('pluginSetting:' + p.id + ':' + s.key, displayValue, s.choices.map(c => [c.value,c.label]), s.label)
        : `<div class="plugin-endpoint-control"><input class="setting-text-input" data-provider-setting-input="${escapeHtml(s.key)}" data-provider-id="${escapeHtml(p.id)}" aria-label="${escapeHtml(s.label)}" type="url" autocomplete="off" maxlength="2048" value="${escapeHtml(displayValue)}"><button class="secondary-button" data-action="save-provider-setting" data-provider-id="${escapeHtml(p.id)}" data-setting-key="${escapeHtml(s.key)}" ${providerSettingsSave.pending ? 'disabled' : ''}>${t('保存')}</button></div>`;
      return `<div class="setting-row" data-declared-setting="${escapeHtml(s.key)}"><div class="setting-label" data-i18n-skip><strong>${escapeHtml(p.name)} · ${escapeHtml(s.label)}</strong><span>${escapeHtml(s.description)}</span>${draft?.error ? `<span role="status">${escapeHtml(draft.error)}</span>` : ''}</div>${control}</div>`;
    })).join('');
    const present = accounts.length || rows || Object.keys(platformProviders).length;
    return `<section class="settings-card" data-settings-group="online"><div class="settings-card-header"><h2>${t('在线音乐')}</h2></div>
      ${accounts.map(onlinePlaylistProviderMarkup).join('')}${rows}
      ${Object.keys(platformProviders).length ? `<div class="setting-row"><div class="setting-label"><strong>默认在线来源</strong><span>仅列出已启用且支持搜索的插件。</span></div>${platformProviderSelectMarkup()}</div>` : ''}
      ${!present ? `<div class="platform-privacy-note"><p>暂无可用的在线设置。导入并启用插件后，按其声明的功能显示。</p><button class="secondary-button" data-action="manage-platform-plugins">${t('管理插件')}</button></div>` : ''}
      ${accounts.length ? '<div class="platform-privacy-note">账号功能由对应插件提供；停用会清除该插件的登录资料，不删除本地音乐与收藏。</div>' : ''}
    </section>`;
  }

  function lyricsCacheRowsMarkup() {
    const query = i18n?.lower(lyricsCacheQuery.trim()) ?? lyricsCacheQuery.trim().toLocaleLowerCase();
    const rows = query
      ? state.lyricsCacheIndex.filter(item => [item.title, item.artist, item.cacheSource]
          .some(value => (i18n?.lower(value) ?? String(value || '').toLocaleLowerCase()).includes(query)))
      : state.lyricsCacheIndex;
    if (!rows.length) {
      return `<div class="lyrics-cache-empty">${query ? '没有与搜索内容匹配的逐歌曲缓存' : '目前没有逐歌曲歌词缓存或指定歌词'}</div>`;
    }
    return rows.map(item => `<div class="lyrics-cache-row"><div><strong>${escapeHtml(item.title)}</strong><span>${escapeHtml(item.artist)} · ${item.hasOverride ? '指定本地歌词' : escapeHtml(String(item.cacheSource || '歌词缓存'))}</span></div><div>${item.hasOverride ? `<button class="secondary-button compact-action" data-cache-command="reset" data-track-id="${item.id}">恢复自动匹配</button>` : ''}${item.hasCache ? `<button class="secondary-button compact-action danger-text" data-cache-command="clear" data-track-id="${item.id}">清除缓存</button>` : ''}</div></div>`).join('');
  }

  function renderLyricsCacheDialog() {
    const list = $('#lyricsCacheDialogList');
    const count = $('#lyricsCacheDialogCount');
    if (list) list.innerHTML = lyricsCacheRowsMarkup();
    if (count) count.textContent = localizedCount('song', state.lyricsCacheIndex.length);
    if (list && !isMotionReduced()) {
      $$('.lyrics-cache-row', list).slice(0, 12).forEach((row, index) => {
        row.style.setProperty('--modal-row-index', String(index));
      });
    }
  }

  function toggleLyricsCacheDialog(open) {
    const dialog = $('#lyricsCacheDialog');
    if (!dialog) return;
    dialog.classList.toggle('open', open);
    dialog.setAttribute('aria-hidden', String(!open));
    dialog.inert = !open;
    if (open) {
      nativePost('requestLyricsCacheIndex');
      renderLyricsCacheDialog();
      requestAnimationFrame(() => $('#lyricsCacheSearch')?.focus());
    } else {
      lyricsCacheQuery = '';
      const input = $('#lyricsCacheSearch');
      if (input) input.value = '';
    }
  }

  const settingsCategories = [
    { id: 'appearance', title: '外观与个性化', description: '主题、强调色、窗口材质和列表密度', icon: 'artist' },
    { id: 'playback', title: '播放与快捷键', description: '启动行为、播放恢复和键盘操作', icon: 'play' },
    { id: 'online', title: '在线音乐', description: '按已启用插件提供账号、搜索与配置选项', icon: 'search' },
    { id: 'plugins', title: '平台插件', description: '查看安装状态、完整性与更新方式', icon: 'devices' },
    { id: 'fullscreen', title: '全屏播放器', description: '播放器布局、背景、歌词和过渡动画', icon: 'album' },
    { id: 'lyrics', title: '歌词', description: '来源优先级、缓存与桌面歌词', icon: 'music' },
    { id: 'audio', title: '音频设备', description: '输出后端、播放设备、声道和缓冲', icon: 'volume' },
    { id: 'windows', title: 'Windows 集成', description: '任务栏组件、托盘、媒体键和开机启动', icon: 'clock' },
    { id: 'lan', title: '局域网播放器', description: '浏览器配对、设备访问和本地音乐流', icon: 'devices' },
    { id: 'library', title: '本地音乐库', description: '音乐文件夹与艺术家头像显示', icon: 'folder' }
  ];

  const pluginInventory = { loaded: false, pending: false, error: false, items: [], issues: [], requestId: 0, timer: null };
  let advancedComponentsExpanded = false;
  const pluginManagement = { requestId: 0, pending: false, action: '', preview: null, results: [], error: '', notice: '', timer: null, focusId: '', cleanupFailures: new Map() };
  const pluginCompatibilityText = code => ({manifestUpgradeRequired:'此插件使用旧清单，不能在当前播放器中启用。请导入同一插件 ID 的新版包，确认后启用并重启；已有收藏、设置和账号数据会保留。',hostSdkIncompatible:'插件需要更新的播放器 SDK，请更新播放器后重试。',hostFeatureUnsupported:'播放器缺少插件必需的功能，请更新播放器或选择兼容的插件版本。',hostApiIncompatible:'插件接口版本与播放器不兼容，请选择匹配的版本。',manifestSchemaUnsupported:'播放器不支持此插件清单版本，请更新播放器或选择兼容包。'})[code] || '';
  const pluginImportError = code => t(pluginCompatibilityText(code) || ({ duplicatePlugin: '同批插件 ID 重复，请只保留一个版本。', batchLimit: '超过批次大小限制。', invalidPackage: '文件无效、已变化或不兼容，未导入。' })[code] || '文件无效、已变化或不兼容，未导入。');
  const pluginValidCount = () => pluginManagement.preview?.items.filter(row => row.preview && !row.error).length || 0;
  const pluginStateLabels = { verified: '校验通过', untrusted: '未批准或文件已变化', restartRequired: '重启后识别', unavailable: '文件缺失或冲突',
    incompatible: '与播放器不兼容', upgradeRequired: '需要升级插件', enabled: '已启用', disabled: '未启用', enablePending: '待启用 · 重启生效', disablePending: '已停用 · 重启后释放' };
  function pluginInventoryMarkup() {
    const p = pluginInventory;
    return `<div class="plugin-inventory-status" role="status">${p.pending ? escapeHtml(t('正在检查插件…')) : p.error ? escapeHtml(t('无法读取插件状态，请重试')) : escapeHtml(t('检查仅读取清单与文件，不登录账号或访问平台网络'))}</div>
      ${p.items.length ? `<ul class="plugin-inventory-list">${p.items.map(item => `<li class="setting-row plugin-inventory-row"><div class="setting-label" data-i18n-skip><strong>${escapeHtml(item.displayName)}</strong><span>${escapeHtml(item.providers.join(' · '))}</span><small>${escapeHtml(item.id)} · ${escapeHtml(item.version)}</small>${item.compatibilityIssue ? `<span class="plugin-compatibility-note">${escapeHtml(t(pluginCompatibilityText(item.compatibilityIssue)))}</span>` : ''} </div><div class="plugin-row-actions"><span class="plugin-state" data-state="${Object.hasOwn(pluginStateLabels, item.state) ? item.state : 'unavailable'}">${escapeHtml(t(pluginStateLabels[item.state] || pluginStateLabels.unavailable))}</span><button type="button" class="switch ${item.enabled ? 'on' : ''}" role="switch" data-plugin-enable="${escapeHtml(item.id)}" aria-label="${escapeHtml(t('启用插件'))} ${escapeHtml(item.displayName)}" aria-checked="${!!item.enabled}" ${pluginManagement.pending || p.pending || (!item.canEnable && !item.enabled) ? 'disabled' : ''}></button></div></li>`).join('')}</ul>` : p.loaded && !p.error ? `<div class="plugin-inventory-empty"><strong>${escapeHtml(t('尚未发现平台插件'))}</strong><p>${escapeHtml(t('本地音乐、歌单和播放不受影响；在线收藏条目会保留。'))}</p></div>` : ''}
      ${p.issues.length ? `<div class="plugin-inventory-warning" role="status">${escapeHtml(t('部分插件无法识别，请检查版本、重复安装或损坏文件。'))}</div>` : ''}`;
  }
  function updatePluginInventoryView() {
    const region = $('#pluginInventory');
    if (!region) return;
    region.innerHTML = pluginInventoryMarkup();
    region.setAttribute('aria-busy', String(pluginInventory.pending));
    const button = $('[data-action="refresh-plugins"]');
    if (button) button.disabled = pluginInventory.pending || pluginManagement.pending;
    if (!pluginInventory.pending && !pluginManagement.pending && pluginManagement.focusId) {
      $$('[data-plugin-enable]').find(b => b.dataset.pluginEnable === pluginManagement.focusId)?.focus({preventScroll:true});
      pluginManagement.focusId = '';
    }
  }
  function pluginImportMarkup() {
    const m = pluginManagement, p = m.preview;
    return `<div class="plugin-inventory-status" role="status">${escapeHtml(t(m.error || m.notice || (m.pending ? '正在处理插件…' : '导入后默认关闭；启用需重启，停用会立即停止新请求并清理登录数据。')))}</div>
      ${[...m.cleanupFailures].map(([id,name]) => `<div class="plugin-cleanup-row"><span role="status"><strong data-i18n-skip>${escapeHtml(name)}</strong>${escapeHtml(t('登录数据尚未完全清除，请重试。'))}</span><button type="button" class="secondary-button" data-action="retry-plugin-cleanup" data-cleanup-plugin="${escapeHtml(id)}" aria-label="${escapeHtml(t('重试清理登录数据'))} ${escapeHtml(name)}" ${m.pending || p ? 'disabled' : ''}>${escapeHtml(t('重试清理登录数据'))}</button></div>`).join('')}
      ${m.restartRequired || pluginInventory.items.some(i => ['enablePending','disablePending'].includes(i.state)) ? `<div class="plugin-restart-row"><span>${t('启用所需插件后，重启以应用更改。当前播放会停止。')}</span><button type="button" class="accent-button" data-action="restart-for-plugins" ${m.pending || p ? 'disabled' : ''}>${t('立即重启')}</button></div>` : ''}
      ${m.results.length ? `<ul class="plugin-batch-results">${m.results.map(row => `<li><span data-i18n-skip>${escapeHtml(row.fileName)}</span><span>${escapeHtml(row.error ? pluginImportError(row.error) : t('已导入 · 未启用'))}</span></li>`).join('')}</ul>` : ''}
      ${p ? `<section class="plugin-import-review" aria-label="${escapeHtml(t('确认导入插件'))}"><h3>${escapeHtml(t('确认导入插件'))} · ${pluginValidCount()} / ${p.items.length}</h3>
        <p>${escapeHtml(t('仅导入下方校验通过的插件；错误项不会安装。'))}</p>
        <ul class="plugin-batch-list">${p.items.map(row => `<li class="plugin-batch-item"><div class="plugin-batch-heading"><strong data-i18n-skip>${escapeHtml(row.fileName || row.preview?.displayName)}</strong><span>${escapeHtml(row.error ? pluginImportError(row.error) : t('待确认'))}</span></div>${row.preview ? `<div data-i18n-skip><p>${escapeHtml(row.preview.displayName)} · ${escapeHtml(row.preview.version)}</p><p>${escapeHtml(row.preview.id)} · ${escapeHtml(row.preview.providers.join(' · '))}</p></div><details><summary>${escapeHtml(t('查看声明能力和 SHA-256'))}</summary><p data-i18n-skip>${escapeHtml(row.preview.capabilities.join(' · '))}</p>${row.preview.hostRequirements ? `<p class="plugin-host-requirements">${escapeHtml(t('最低播放器 SDK'))}: <span data-i18n-skip>${escapeHtml(row.preview.hostRequirements.minimumHostSdkVersion)}</span><br>${escapeHtml(t('必需宿主功能'))}: <span data-i18n-skip>${escapeHtml(row.preview.hostRequirements.requiredFeatures.join(' · '))}</span></p>` : ''}<code class="plugin-package-hash">${escapeHtml(row.preview.sha256)}</code></details>` : ''}</li>`).join('')}</ul>
        ${p.items.some(row => row.preview?.credentialAliases?.length) ? `<section class="plugin-credential-review" aria-label="${escapeHtml(t('旧账号兼容访问'))}"><h4>${escapeHtml(t('旧账号兼容访问'))}</h4><p>${escapeHtml(t('以下插件请求访问列出的旧凭据地址，以保留登录并支持刷新和退出。这里只显示地址，不显示 Cookie 或令牌；不信任时请取消导入。'))}</p><ul>${p.items.filter(row => row.preview?.credentialAliases?.length).map(row => `<li><strong data-i18n-skip>${escapeHtml(row.preview.displayName)}</strong><ul>${row.preview.credentialAliases.map(a => `<li data-i18n-skip><code>${escapeHtml(a.key)}</code> → <code>${escapeHtml(a.scope)} / ${escapeHtml(a.legacyKey)}</code></li>`).join('')}</ul></li>`).join('')}</ul></section>` : ''}
        <label class="plugin-trust-choice"><input type="checkbox" id="pluginImportTrust" ${m.pending || !pluginValidCount() ? 'disabled' : ''}><span>${escapeHtml(t('我信任本批次所有有效插件的来源，了解它们将在播放器进程内运行。'))}${p.items.some(row => row.preview?.credentialAliases?.length) ? ` ${escapeHtml(t('同时批准上面列出的旧账号兼容访问。'))}` : ''}</span></label>
        <div class="plugin-import-actions"><button type="button" class="secondary-button" data-action="cancel-plugin-import" ${m.pending ? 'disabled' : ''}>${escapeHtml(t('取消'))}</button><button type="button" class="accent-button" data-action="confirm-plugin-import" disabled>${escapeHtml(t('确认导入'))}</button></div></section>` : ''}`;
  }
  function updatePluginManagementView() {
    const region = $('#pluginImportRegion');
    if (region) region.innerHTML = pluginImportMarkup();
    const picker = $('[data-action="import-plugin"]');
    if (picker) picker.disabled = pluginManagement.pending || !!pluginManagement.preview;
    updatePluginInventoryView();
  }
  function requestPluginManagement(action, payload = {}, files = null) {
    if (window.AuralisPlaybackComponents?.isBusy() || window.AuralisMediaTransportComponents?.isBusy()) { showToast(t('请先完成或取消当前插件导入。')); return; }
    if (pluginManagement.pending) return;
    pluginManagement.pending = true; pluginManagement.action = action;
    if (action === 'setPluginEnabled') pluginManagement.lastPluginId = payload.id;
    pluginManagement.error = ''; pluginManagement.notice = ''; pluginManagement.results = [];
    const requestId = ++pluginManagement.requestId;
    updatePluginManagementView();
    clearTimeout(pluginManagement.timer);
    // A native file picker may remain open while the user decides; don't permit overlapping requests.
    if (action !== 'pickPluginPackage') pluginManagement.timer = setTimeout(() => {
      if (pluginManagement.pending && pluginManagement.requestId === requestId) {
        pluginManagement.pending = false;
        pluginManagement.error = '操作未确认，请刷新状态后重试。';
        updatePluginManagementView(); requestPluginInventory();
      }
    }, 55000);
    try {
      if (files) window.chrome.webview.postMessageWithAdditionalObjects({ action, requestId }, files);
      else nativePost(action, { requestId, ...payload });
    } catch {
      clearTimeout(pluginManagement.timer); pluginManagement.pending = false;
      pluginManagement.error = '拖入不可用，请使用批量导入按钮选择文件。';
      updatePluginManagementView();
    }
  }

  // File objects travel through WebView2's native object channel, never as JSON paths or bytes.
  // Capture at window level so a drop cannot navigate the app to a local ZIP or affect playback.
  const pluginDropHint = document.createElement('div');
  pluginDropHint.className = 'plugin-drop-hint'; pluginDropHint.hidden = true;
  pluginDropHint.setAttribute('role', 'status'); document.body.append(pluginDropHint);
  let pluginDragDepth = 0;
  const clearPluginDrop = () => { pluginDragDepth = 0; pluginDropHint.hidden = true; };
  const isFileDrag = event => [...(event.dataTransfer?.types || [])].includes('Files');
  window.addEventListener('dragenter', event => {
    if (!isFileDrag(event)) return;
    event.preventDefault(); pluginDragDepth++;
    pluginDropHint.textContent = t(pluginManagement.pending || pluginManagement.preview
      ? '请先完成或取消当前插件导入。' : '松开以预览插件包，不会自动安装或启用');
    pluginDropHint.hidden = false;
  }, true);
  window.addEventListener('dragover', event => {
    if (!isFileDrag(event)) return;
    event.preventDefault(); event.dataTransfer.dropEffect = pluginManagement.pending || pluginManagement.preview ? 'none' : 'copy';
  }, true);
  window.addEventListener('dragleave', event => {
    if (isFileDrag(event) && --pluginDragDepth <= 0) clearPluginDrop();
  }, true);
  window.addEventListener('blur', clearPluginDrop);
  window.addEventListener('dragend', clearPluginDrop);
  window.addEventListener('keydown', event => { if (event.key === 'Escape') clearPluginDrop(); }, true);
  window.addEventListener('drop', event => {
    if (!isFileDrag(event)) return;
    event.preventDefault(); event.stopPropagation(); clearPluginDrop();
    if (pluginManagement.pending || pluginManagement.preview || window.AuralisPlaybackComponents?.isBusy() || window.AuralisMediaTransportComponents?.isBusy()) { showToast(t('请先完成或取消当前插件导入。')); return; }
    const files = [...event.dataTransfer.files];
    if (files.some(f => /\.auralis-transport\.zip$/i.test(f.name))) {
      showToast(t('传输组件请使用设置中的“导入传输组件”按钮；不要与平台插件混合导入。')); return;
    }
    if (files.some(f => /\.auralis-playback\.zip$/i.test(f.name))) {
      showToast(t('播放组件请使用设置中的“导入播放组件”按钮；不要与平台插件混合导入。')); return;
    }
    if (!files.length || files.length > 16 || !files.some(f => /\.(auralis-plugin|zip)$/i.test(f.name))) {
      showToast(t('请拖入 1–16 个 .auralis-plugin 或 ZIP 插件包。')); return;
    }
    if (typeof window.chrome?.webview?.postMessageWithAdditionalObjects !== 'function') {
      showToast(t('拖入不可用，请使用批量导入按钮选择文件。')); return;
    }
    toggleNowPlaying(false);
    if (state.currentPage !== 'settings') setPage('settings');
    navigateSettingsSection('plugins');
    requestPluginManagement('dropPluginPackages', {}, files);
  }, true);
  function requestPluginInventory() {
    if (pluginInventory.pending) return;
    pluginInventory.pending = true;
    pluginInventory.error = false;
    const requestId = ++pluginInventory.requestId;
    updatePluginInventoryView();
    clearTimeout(pluginInventory.timer);
    pluginInventory.timer = setTimeout(() => {
      if (pluginInventory.pending && pluginInventory.requestId === requestId) {
        pluginInventory.pending = false;
        pluginInventory.error = true;
        updatePluginInventoryView();
      }
    }, 20000);
    nativePost('requestPluginInventory', { requestId });
  }
  function renderPluginSettings() {
    pageContent.innerHTML = `<div class="page-view settings-page settings-detail-page">${settingsDetailHeaderMarkup()}
      <div class="settings-grid"><section class="settings-card plugin-settings-card"><div class="settings-card-header"><div><h2>${escapeHtml(t('已安装的插件'))}</h2><p>${escapeHtml(t('校验通过不代表已登录或平台服务可用'))}</p></div><button type="button" class="secondary-button" data-action="refresh-plugins" ${pluginInventory.pending ? 'disabled' : ''}>${escapeHtml(t('刷新'))}</button></div>
      <div id="pluginInventory" aria-busy="${pluginInventory.pending}">${pluginInventoryMarkup()}</div></section>
      <section class="settings-card plugin-settings-card"><div class="settings-card-header"><h2>${escapeHtml(t('安装与更新'))}</h2><button type="button" class="accent-button" data-action="import-plugin" ${pluginManagement.pending || pluginManagement.preview ? 'disabled' : ''}>${escapeHtml(t('批量导入插件'))}</button></div>
      <p class="plugin-inventory-status">${escapeHtml(t('可多选文件，或拖入应用窗口。每批最多 16 个包、256 MiB；导入后默认关闭。'))}</p>
      <div id="pluginImportRegion">${pluginImportMarkup()}</div>
      <div class="setting-row"><div class="setting-label"><strong>${escapeHtml(t('只安装你信任的插件'))}</strong><span>${escapeHtml(t('插件在播放器进程内运行，不是安全沙箱。SHA-256 仅用于核对文件完整性。'))}</span></div></div>
      <ol class="plugin-install-steps"><li>${escapeHtml(t('选择 .auralis-plugin 或兼容 ZIP，核对信息并确认信任。'))}</li><li>${escapeHtml(t('打开需要的插件开关，再从托盘退出并重启应用。'))}</li><li>${escapeHtml(t('停用会清除该插件的登录数据，不删除音乐或收藏；更新保留旧包。'))}</li></ol>
      <div class="setting-row"><div class="setting-label"><strong>${escapeHtml(t('用户插件文件夹'))}</strong><span>${escapeHtml(t('仅复制 DLL 不会获得批准；当前版本不提供热更新或自动下载。'))}</span></div><button type="button" class="secondary-button" data-action="open-plugin-folder">${escapeHtml(t('打开文件夹'))}</button></div></section></div></div>`;
    const advanced = document.createElement('details');
    advanced.id = 'advancedComponentsSettings'; advanced.className = 'plugin-advanced-components';
    advanced.open = advancedComponentsExpanded;
    advanced.innerHTML = `<summary><span><strong>${escapeHtml(t('高级组件'))}</strong><small>${escapeHtml(t('播放与媒体传输已内置，通常无需调整。仅在使用自定义组件时展开。'))}</small></span><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m5 7.5 5 5 5-5"/></svg></summary><div class="plugin-advanced-body"></div>`;
    advanced.addEventListener('toggle', () => { if (advanced.isConnected) advancedComponentsExpanded = advanced.open; });
    pageContent.querySelector('.settings-grid').append(advanced);
    const advancedBody = advanced.querySelector('.plugin-advanced-body');
    const playbackRegion = document.createElement('section');
    playbackRegion.id = 'playbackComponentsSettings'; playbackRegion.className = 'settings-card plugin-settings-card playback-components';
    advancedBody.append(playbackRegion);
    window.AuralisPlaybackComponents?.mount(playbackRegion, {t, escapeHtml, nativePost:(action, data) => { window.AuralisMediaTransportComponents?.refresh(); nativePost(action, data); },
      onAttention:revealAdvancedComponentAttention,
      restart:() => { persistSession(true); nativePost('restartForPlugins'); },
      isBlocked:() => pluginManagement.pending || !!pluginManagement.preview || window.AuralisMediaTransportComponents?.isBusy()});
    const transportRegion = document.createElement('section');
    transportRegion.id = 'transportComponentsSettings'; transportRegion.className = 'settings-card plugin-settings-card playback-components';
    advancedBody.append(transportRegion);
    window.AuralisMediaTransportComponents?.mount(transportRegion, {t, escapeHtml, nativePost:(action, data) => { window.AuralisPlaybackComponents?.refresh(); nativePost(action, data); },
      onAttention:revealAdvancedComponentAttention,
      restart:() => { persistSession(true); nativePost('restartForPlugins'); },
      isBlocked:() => pluginManagement.pending || !!pluginManagement.preview || window.AuralisPlaybackComponents?.isBusy()});
    revealAdvancedComponentAttention();
    if (!pluginInventory.loaded && !pluginInventory.pending && !pluginInventory.error) requestPluginInventory();
  }
  function revealAdvancedComponentAttention() {
    const advanced = $('#advancedComponentsSettings');
    if (advanced && (window.AuralisPlaybackComponents?.needsAttention?.() || window.AuralisMediaTransportComponents?.needsAttention?.())) {
      advanced.open = true; advancedComponentsExpanded = true;
    }
  }

  function settingsHomeMarkup() {
    const connectedCount = onlineSettingsProviders.filter(p => p.capabilities.includes('Authentication') &&
      state.platformConfiguration[p.authenticationKey]?.status === 'signedin').length;
    return `<div class="page-view settings-page settings-home-page"><header class="page-header"><div><h1>设置</h1><p>按功能分类，进入二级页面后再调整具体选项</p></div></header>
      <div class="settings-category-grid">
        ${settingsCategories.map((category, index) => `<button class="settings-category-card" type="button" data-settings-section="${category.id}" style="--settings-index:${index}"><span class="settings-category-icon">${icon(category.icon)}</span><span class="settings-category-copy"><strong>${escapeHtml(category.title)}</strong><small>${escapeHtml(category.description)}</small>${category.id === 'online' ? `<em>${connectedCount ? `${connectedCount} 个账号已连接` : (onlineSettingsProviders.length ? '按已启用插件显示功能' : '尚未启用在线插件')}</em>` : ''}</span><svg class="settings-category-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="m7.5 4.5 5.5 5.5-5.5 5.5"/></svg></button>`).join('')}
      </div>
      <section class="settings-home-about"><img class="about-mark" src="assets/auralis-icon.png" alt=""><div><strong>Auralis</strong></div><span data-runtime-build>${escapeHtml(runtimeBuild)}</span></section>
    </div>`;
  }

  function settingsDetailHeaderMarkup() {
    const category = settingsCategories.find(item => item.id === state.settingsSection) || settingsCategories[0];
    return `<header class="page-header settings-detail-header"><div class="settings-detail-title"><button type="button" class="settings-back-button" data-action="settings-home" aria-label="返回设置主页">←</button><div><h1>${escapeHtml(category.title)}</h1><p>${escapeHtml(category.description)}</p></div></div></header>`;
  }

  function fullscreenSettingsPreviewMarkup() {
    const styleLabel = state.playerStyle === 'record' ? '唱片与歌词' : '沉浸封面';
    return `<figure class="fullscreen-settings-preview" data-fullscreen-preview data-player-style="${state.playerStyle}" data-preview-background="${state.fullscreenBackground}" data-preview-transition="${state.coverTransition}" data-preview-fade="${state.immersiveFade ? 'on' : 'off'}" data-preview-adaptive="${state.lyricAdaptive ? 'on' : 'off'}" data-preview-blur="${state.lyricBlur ? 'on' : 'off'}" data-preview-spring="${state.lyricSpring ? 'on' : 'off'}" aria-labelledby="fullscreenSettingsPreviewCaption">
      <figcaption id="fullscreenSettingsPreviewCaption"><span>可视化预览</span><div><strong>海风与旋律</strong><small>Auralis · 设置示例，不播放音频</small></div><em data-preview-layout-label>${styleLabel}</em></figcaption>
      <div class="fullscreen-preview-canvas" aria-hidden="true">
        <div class="fullscreen-preview-backdrop"><i></i><i></i><i></i></div>
        <div class="fullscreen-preview-stage">
          <div class="fullscreen-preview-lyrics" data-i18n-skip>
            <span>听见窗外轻轻的风</span>
            <strong data-preview-current-lyric>让这一刻伴随音乐</strong>
            <span>海风与旋律</span>
          </div>
          <div class="fullscreen-preview-visual">
            <div class="fullscreen-preview-vinyl"><i></i></div>
            <div class="fullscreen-preview-cover-shell">
              <div class="fullscreen-preview-cover"></div>
            </div>
          </div>
        </div>
        <div class="fullscreen-preview-player">
          <div class="fullscreen-preview-track"><strong>海风与旋律</strong><span>Auralis</span></div>
          <div class="fullscreen-preview-controls">
            <span class="fullscreen-preview-skip"><svg viewBox="0 0 24 24"><path d="M6 5v14M18 6l-9 6 9 6Z"/></svg></span>
            <span class="fullscreen-preview-play"><span class="fullscreen-preview-play-glyph">${icon('play')}</span></span>
            <span class="fullscreen-preview-skip"><svg viewBox="0 0 24 24"><path d="M18 5v14M6 6l9 6-9 6Z"/></svg></span>
          </div>
          <span class="fullscreen-preview-time">0:43 / 4:01</span>
          <div class="fullscreen-preview-progress"><i></i></div>
        </div>
      </div>
    </figure>`;
  }

  function syncFullscreenSettingsPreview() {
    const preview = $('[data-fullscreen-preview]', pageContent);
    if (!preview) return;
    const previousStyle = preview.dataset.playerStyle;
    const previousTransition = preview.dataset.previewTransition;
    const previousSpring = preview.dataset.previewSpring;
    const previousAdaptive = preview.dataset.previewAdaptive;
    const previousBlur = preview.dataset.previewBlur;
    preview.dataset.playerStyle = state.playerStyle;
    preview.dataset.previewBackground = state.fullscreenBackground;
    preview.dataset.previewTransition = state.coverTransition;
    preview.dataset.previewFade = state.immersiveFade ? 'on' : 'off';
    preview.dataset.previewAdaptive = state.lyricAdaptive ? 'on' : 'off';
    preview.dataset.previewBlur = state.lyricBlur ? 'on' : 'off';
    preview.dataset.previewSpring = state.lyricSpring ? 'on' : 'off';
    preview.style.setProperty('--preview-lyric-offset', `${(Math.max(0, Math.min(1, state.lyricVertical)) - .5) * 72}px`);
    const layoutLabel = $('[data-preview-layout-label]', preview);
    if (layoutLabel) layoutLabel.textContent = state.playerStyle === 'record' ? '唱片与歌词' : '沉浸封面';
    if (previousStyle !== state.playerStyle || previousTransition !== state.coverTransition) {
      const coverMotionTarget = state.playerStyle === 'record'
        ? $('.fullscreen-preview-visual', preview)
        : $('.fullscreen-preview-cover-shell', preview);
      restartMotionClass(coverMotionTarget, 'preview-cover-change', 560);
    }
    if (previousSpring !== preview.dataset.previewSpring ||
        previousAdaptive !== preview.dataset.previewAdaptive ||
        previousBlur !== preview.dataset.previewBlur) {
      restartMotionClass($('[data-preview-current-lyric]', preview), 'preview-lyric-change', 460);
    }
  }

  function syncDesktopLyricsSettingsPreview() {
    const preview = $('[data-desktop-lyrics-preview]', pageContent);
    if (!preview) return;
    const options = state.desktopLyrics;
    preview.dataset.alignment = options.alignment;
    preview.dataset.doubleLine = options.doubleLine ? 'on' : 'off';
    preview.dataset.songInfo = options.alwaysShowSongInfo ? 'on' : 'off';
    preview.dataset.translation = options.showTranslation ? 'on' : 'off';
    preview.dataset.animate = options.animate ? 'on' : 'off';
    preview.dataset.locked = options.locked ? 'on' : 'off';
    preview.style.setProperty('--desktop-mask-alpha', options.showMask ? String(options.maskBrightness / 100) : '0');
    preview.style.setProperty('--desktop-preview-size', `${Math.max(18, Math.min(64, Number(options.fontSize) || 30))}px`);
    preview.style.setProperty('--desktop-preview-weight', String(Math.max(400, Math.min(800, Number(options.fontWeight) || 600))));
    preview.style.setProperty('--desktop-preview-active', options.activeColor || '#73BCFC');
    preview.style.setProperty('--desktop-preview-inactive', options.inactiveColor || 'rgba(32,33,36,0.58)');
    preview.style.setProperty('--desktop-preview-shadow', options.shadowColor || 'rgba(255,255,255,0.72)');
  }

  function musicFoldersMarkup() {
    if (!state.musicFolders.length) {
      return `<div class="music-folder-empty"><span class="music-folder-empty-icon">${icon('folder')}</span><div><strong>还没有持续监听的文件夹</strong><small>添加后，Auralis 会在启动时补扫，并自动发现新增、改名或删除的歌曲。</small></div></div>`;
    }

    return `<div class="music-folder-list" role="list" aria-label="正在监听的音乐文件夹">
      ${state.musicFolders.map(folder => `<div class="music-folder-item${folder.available ? '' : ' is-unavailable'}" role="listitem">
        <span class="music-folder-item-icon">${icon('folder')}</span>
        <div class="music-folder-copy"><strong>${escapeHtml(folder.name || folder.path)}</strong><small title="${escapeHtml(folder.path)}">${escapeHtml(folder.path)}</small><em>${folder.available ? `${Number(folder.trackCount || 0)} 首歌曲 · 正在监听` : '文件夹当前不可访问'}</em></div>
        <div class="music-folder-actions"><button type="button" class="secondary-button compact-action" data-action="rescan-music-folder" data-music-folder-id="${escapeHtml(folder.id)}" ${folder.available ? '' : 'disabled'}>重新扫描</button><button type="button" class="secondary-button compact-action danger-text" data-action="remove-music-folder" data-music-folder-id="${escapeHtml(folder.id)}">停止监听</button></div>
      </div>`).join('')}
    </div>`;
  }

  function audioEndpointLatencyMarkup() {
    const latency = state.audioDevices.endpointLatency || {};
    const hasNumericValue = value => value !== null && value !== undefined && value !== '' && Number.isFinite(Number(value));
    const estimated = hasNumericValue(latency.estimatedLatencyMilliseconds) ? Number(latency.estimatedLatencyMilliseconds) : null;
    const streamLatency = hasNumericValue(latency.streamLatencyMilliseconds) ? Number(latency.streamLatencyMilliseconds) : null;
    const enginePeriod = hasNumericValue(latency.enginePeriodMilliseconds) ? Number(latency.enginePeriodMilliseconds) : null;
    const hasEstimate = estimated !== null && estimated >= 0;
    const hasStreamLatency = streamLatency !== null && streamLatency >= 0;
    const hasEnginePeriod = enginePeriod !== null && enginePeriod >= 0;
    const statusText = {
      loading: '正在读取 Windows 音频路径…',
      unsupportedBackend: '仅“Windows 音频设备”后端可读取',
      deviceUnavailable: '所选设备当前不可用',
      probeUnavailable: 'Windows 未返回可用的延迟数值',
      transportUnreported: '驱动未报告蓝牙传输延迟',
      periodOnly: '只能读取音频引擎周期'
    }[latency.status] || 'Windows 未返回可用的延迟数值';
    const deviceName = String(latency.deviceName || (latency.status === 'unsupportedBackend' ? '兼容后端未报告终端' : 'Windows 当前默认音频终端'));
    const transport = latency.isBluetooth
      ? '<span class="audio-transport-badge bluetooth">蓝牙音频</span>'
      : '<span class="audio-transport-badge">Windows 终端</span>';
    return `<div role="row"><span role="cell">当前音频终端</span><strong role="cell" class="audio-device-name"><span>${escapeHtml(deviceName)}</span>${latency.status === 'estimated' || latency.status === 'periodOnly' || latency.status === 'transportUnreported' ? transport : ''}</strong></div>
      <div role="row"><span role="cell">Windows 共享路径估算</span><strong role="cell" class="audio-latency-value">${hasEstimate ? `约 ${estimated.toFixed(1)} ms <em>系统报告</em>` : escapeHtml(statusText)}</strong></div>
      <div role="row"><span role="cell">Windows 报告流延迟</span><strong role="cell">${latency.status === 'transportUnreported' ? '驱动未报告蓝牙传输部分' : hasStreamLatency ? `${streamLatency.toFixed(1)} ms` : '暂不可读'}</strong></div>
      <div role="row"><span role="cell">音频引擎周期</span><strong role="cell">${hasEnginePeriod ? `${enginePeriod.toFixed(1)} ms · Windows 共享模式` : '暂不可读'}</strong></div>`;
  }

  function audioEndpointLatencyNote() {
    const latency = state.audioDevices.endpointLatency || {};
    if (latency.isBluetooth) {
      return '蓝牙设备显示的是 Windows 为参考共享流报告的系统路径估算。驱动不保证完整计入无线传输、编解码、耳机内部 DSP 与实时抖动，因此不能把它当作真实端到端延迟，实际听感通常更高且会波动。';
    }
    return '延迟来自 Windows 共享模式参考流，不是对 LibVLC、设备内部处理或蓝牙无线链路的端到端实测；需要麦克风或硬件回环才能得到真实听感延迟。';
  }

  function activeAudioDeviceDescription() {
    if (!state.audioDevices.loaded) return '正在读取 Windows 音频设备…';
    const selectedId = state.audioOutput.outputDeviceId || state.audioDevices.activeDeviceId;
    const selectedModule = state.audioOutput.outputModule === 'auto' ? 'mmdevice' : state.audioOutput.outputModule;
    const device = state.audioDevices.devices.find(item =>
      String(item.moduleId || '').toLowerCase() === String(selectedModule || '').toLowerCase() &&
      String(item.id || '') === String(selectedId || ''));
    if (device?.displayName) return `当前设备：${String(device.displayName)}`;
    const diagnostics = state.audioDevices.endpointLatency || {};
    if (diagnostics.deviceName && (!selectedId || !diagnostics.endpointId || diagnostics.endpointId === selectedId)) {
      return `当前设备：${String(diagnostics.deviceName)}`;
    }
    if (selectedId) return '当前已选择一个 Windows 音频终端';
    return '使用 Windows 当前默认设备';
  }

  function filterSettingsCards() {
    $$('.settings-grid > .settings-card', pageContent).forEach(card => {
      card.hidden = card.dataset.settingsGroup !== state.settingsSection;
    });
  }

  function renderSettings(options = {}) {
    if (settingsTransitionInProgress && !options.force) {
      settingsRenderPending = true;
      return;
    }
    settingsRenderPending = false;
    if (state.settingsSection === 'plugins') {
      renderPluginSettings();
      return;
    }
    if (state.settingsSection === 'home') {
      pageContent.innerHTML = settingsHomeMarkup();
      requestPlatformConfiguration();
      return;
    }

    const lan = state.lanMusicSharing;
    const lanStatusLabel = lan.pending
      ? '正在应用…'
      : lan.running
        ? '正在运行'
        : lan.enabled
          ? '启动失败'
          : '已关闭';
    const lanExpiryLabel = lan.pairingExpiresAt
      ? new Date(lan.pairingExpiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      : '—';

    pageContent.innerHTML = `<div class="page-view settings-page settings-detail-page">${settingsDetailHeaderMarkup()}
      <div class="settings-grid">
        <section class="settings-card" data-settings-group="appearance"><div class="settings-card-header"><h2>外观</h2></div>
          <div class="setting-row"><div class="setting-label"><strong>${escapeHtml(t('language.label'))}</strong><span>${escapeHtml(t('language.description'))}</span></div>${fluentSelectMarkup('language', state.languagePreference, languageOptions(), t('language.label'))}</div>
          <div class="setting-row"><div class="setting-label"><strong>应用主题</strong><span>与全屏播放器使用相同的昼夜切换动画</span></div><div class="fullscreen-theme-selector settings-theme-selector" data-theme-selector role="group" aria-label="应用主题"><button type="button" class="fullscreen-theme-system" data-fullscreen-theme="system" aria-label="跟随系统主题" title="跟随系统主题"><svg viewBox="0 0 24 24"><rect x="3.5" y="4.5" width="17" height="12" rx="2"/><path d="M9 20h6M12 16.5V20"/></svg></button><button class="day-night-theme-toggle" type="button" data-day-night-toggle aria-label="切换到深色主题" title="切换到深色主题"><span class="day-night-sky" aria-hidden="true"><span class="day-night-stars"><i></i><i></i><i></i><i></i><i></i></span><span class="day-night-clouds"><i></i><i></i><i></i><i></i></span><span class="day-night-celestial"><i></i><i></i><i></i></span></span></button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>强调色</strong><span>选择播放控件和选中状态的颜色</span></div><div class="accent-swatches">${Object.entries(accentOptions).map(([name, option]) => `<button class="accent-swatch" data-accent="${name}" style="--swatch:${option[0]}" aria-label="${accentNames[name]}"></button>`).join('')}</div></div>
          <div class="setting-row"><div class="setting-label"><strong>窗口特效</strong><span>云母使用 Windows 系统材质；不支持时显示纯色背景</span></div><div class="segmented wide-segmented" data-setting="windowEffect"><button data-value="none">无</button><button data-value="mica">云母</button><button data-value="acrylic">亚克力</button><button data-value="custom">自定义图片</button><button data-value="song">歌曲背景</button></div></div>
          <div class="setting-row window-background-picker"><div class="setting-label"><strong>自定义窗口背景</strong><span>${state.customBackground ? '已选择本地图片' : '选择一张仅保存在本机的图片'}</span></div><button class="secondary-button" data-action="pick-window-background">选择图片</button></div>
          <div class="setting-row"><div class="setting-label"><strong>紧凑歌曲列表</strong><span>在同一页显示更多本地音乐</span></div><button class="switch" data-toggle="density" role="switch"></button></div>
        </section>

        <section class="settings-card" data-settings-group="playback"><div class="settings-card-header"><h2>播放</h2></div>
          <div class="setting-row"><div class="setting-label"><strong>打开后自动播放音乐</strong><span>启动应用后自动继续播放</span></div><button class="switch" data-toggle="autoPlayOnLaunch" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>重启后保存播放列表和当前音乐</strong><span>退出时记住歌曲、进度、音量和播放顺序</span></div><button class="switch" data-toggle="rememberPlayback" role="switch"></button></div>
        </section>

        ${platformSettingsMarkup()}

        <section class="settings-card fullscreen-settings-card" data-settings-group="fullscreen"><div class="settings-card-header"><h2>全屏播放器</h2><p>效果同时用于沉浸封面与唱片歌词布局</p></div>
          ${fullscreenSettingsPreviewMarkup()}
          <div class="setting-row"><div class="setting-label"><strong>播放器样式</strong><span>随时切换，两种样式都跟随应用主题</span></div><div class="segmented" data-setting="playerStyle"><button data-value="immersive">沉浸封面</button><button data-value="record">唱片与歌词</button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>背景效果</strong><span>动态模式让封面色彩缓慢流动</span></div><div class="segmented" data-setting="fullscreenBackground"><button data-value="static">静态</button><button data-value="dynamic">动态</button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>封面切换动画</strong><span>切换歌曲时使用所选过渡</span></div><div class="segmented wide-segmented" data-setting="coverTransition"><button data-value="fade">淡入淡出</button><button data-value="left">左边滑入滑出</button><button data-value="alternate">左右滑入滑出</button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>沉浸式播放栏</strong><span>鼠标移开时淡化中间与右侧控件</span></div><button class="switch" data-toggle="immersiveFade" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词文字大小</strong><span>全屏播放器歌词字号</span></div><label class="range-setting"><input type="range" min="18" max="48" step="1" value="${state.lyricFontSize}" data-range-setting="lyricFontSize"><output>${state.lyricFontSize}px</output></label></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词大小自适应</strong><span>小窗口下自动缩小，避免歌词拥挤</span></div><button class="switch" data-toggle="lyricAdaptive" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词字体</strong><span>选择适合中文歌词阅读的系统字体</span></div>${fluentSelectMarkup('lyricFont', state.lyricFont, lyricFontOptions, '歌词字体')}</div>
          <div class="setting-row"><div class="setting-label"><strong>歌词对齐位置</strong><span>调整当前歌词在可视区域中的垂直位置</span></div><label class="range-setting"><input type="range" min="0" max="1" step="0.05" value="${state.lyricVertical}" data-range-setting="lyricVertical"><output>${Math.round(state.lyricVertical * 100)}%</output></label></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词行模糊效果</strong><span>弱化已播放和即将播放的歌词</span></div><button class="switch" data-toggle="lyricBlur" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>物理弹簧动画</strong><span>当前歌词切换使用弹簧移动效果</span></div><button class="switch" data-toggle="lyricSpring" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>背景流动速度</strong><span>动态背景的移动速度</span></div><label class="range-setting"><input type="range" min="0.5" max="4" step="0.5" value="${state.backgroundSpeed}" data-range-setting="backgroundSpeed"><output>${state.backgroundSpeed.toFixed(1)}×</output></label></div>
          <div class="setting-row"><div class="setting-label"><strong>背景动画帧率</strong><span>性能不足时可降低此值</span></div><label class="range-setting"><input type="range" min="15" max="60" step="15" value="${state.backgroundFps}" data-range-setting="backgroundFps"><output>${state.backgroundFps} FPS</output></label></div>
        </section>

        <section class="settings-card" data-settings-group="playback"><div class="settings-card-header shortcut-header"><div><h2>快捷键</h2><p>点击按键后直接按下想使用的键</p></div><button class="secondary-button compact-action" data-action="reset-shortcuts">恢复默认</button></div>
          ${[
            ['playPause','播放 / 暂停'], ['previous','上一首'], ['next','下一首'],
            ['volumeUp','增大音量'], ['volumeDown','减小音量'], ['mute','静音'], ['fullscreen','全屏播放器']
          ].map(([key, label]) => `<div class="setting-row shortcut-row"><div class="setting-label"><strong>${label}</strong><span>点击右侧按键后重新录入</span></div><button class="shortcut-key" data-shortcut="${key}">${shortcutLabel(state.shortcuts[key])}<i>×</i></button></div>`).join('')}
        </section>

        <section class="settings-card" data-settings-group="lyrics"><div class="settings-card-header"><h2>歌词偏好</h2><p>本地歌词始终由你控制；在线匹配默认关闭</p></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词来源顺序</strong><span>指定歌词始终拥有最高优先级</span></div><div class="segmented wide-segmented" data-setting="lyricsPreference"><button data-value="localFirst">本地文件优先</button><button data-value="cacheFirst">已缓存优先</button><button data-value="localOnly">仅本地歌词</button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>歌词时间偏移</strong><span>正数让歌词稍后出现，负数让歌词提前</span></div><label class="range-setting"><input type="range" min="-5000" max="5000" step="100" value="${state.lyricsOffset}" data-range-setting="lyricsOffset"><output>${state.lyricsOffset > 0 ? '+' : ''}${state.lyricsOffset} ms</output></label></div>
          ${onlineSettingsProviders.some(p => p.capabilities.includes('LyricsLookup')) ? `<div class="setting-row"><div class="setting-label"><strong>在线歌词匹配</strong><span>开启后只发送标题、艺术家、专辑和时长；不上传音频、路径或封面</span></div><button class="switch" data-toggle="onlineLyricsEnabled" role="switch"></button></div>` : ''}
          <div class="setting-row"><div class="setting-label"><strong>逐歌曲歌词缓存</strong><span>${state.lyricsCacheIndex.length} 首歌曲 · 在独立窗口中搜索、恢复或清除单曲缓存</span></div><button class="secondary-button" data-action="open-lyrics-cache">管理缓存</button></div>
        </section>

        <section class="settings-card" data-settings-group="lyrics"><div class="settings-card-header"><h2>桌面歌词</h2><p>独立置顶悬浮窗，拖动位置会在本机保存</p></div>
          <div class="setting-row"><div class="setting-label"><strong>启用桌面歌词</strong><span>在其他窗口上方显示当前歌词</span></div><button class="switch" data-desktop-toggle="enabled" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>锁定桌面歌词</strong><span>锁定后鼠标可穿透歌词窗口</span></div><button class="switch" data-desktop-toggle="locked" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>字体大小</strong></div><label class="range-setting"><input type="range" min="18" max="64" step="1" value="${state.desktopLyrics.fontSize}" data-desktop-range="fontSize"><output>${state.desktopLyrics.fontSize}px</output></label></div>
          <div class="setting-row"><div class="setting-label"><strong>字重</strong></div><div class="segmented" data-desktop-setting="fontWeight">${[400,500,600,700,800].map(weight => `<button data-value="${weight}">${weight}</button>`).join('')}</div></div>
          <div class="setting-row color-setting-row"><div class="setting-label"><strong>主颜色</strong><span>当前歌词</span></div>${fluentColorMarkup('activeColor', state.desktopLyrics.activeColor, '主颜色')}</div>
          <div class="setting-row color-setting-row"><div class="setting-label"><strong>未播放颜色</strong><span>下一行歌词</span></div>${fluentColorMarkup('inactiveColor', state.desktopLyrics.inactiveColor, '未播放颜色')}</div>
          <div class="setting-row color-setting-row"><div class="setting-label"><strong>阴影颜色</strong></div>${fluentColorMarkup('shadowColor', state.desktopLyrics.shadowColor, '阴影颜色')}</div>
          <div class="setting-row"><div class="setting-label"><strong>对齐方式</strong></div><div class="segmented" data-desktop-setting="alignment"><button data-value="left">居左</button><button data-value="center">居中</button><button data-value="right">居右</button></div></div>
          <div class="setting-row"><div class="setting-label"><strong>背景遮罩</strong><span>歌词文字背后显示半透明遮罩</span></div><button class="switch" data-desktop-toggle="showMask" role="switch"></button></div>
          <div class="setting-row ${state.desktopLyrics.showMask ? '' : 'is-disabled'}" data-desktop-mask-brightness><div class="setting-label"><strong>背景遮罩亮度</strong><span>调节浅色遮罩的明亮程度，不改变歌词文字</span></div><label class="range-setting"><input type="range" min="10" max="90" step="1" value="${state.desktopLyrics.maskBrightness}" data-desktop-range="maskBrightness" aria-label="桌面歌词背景遮罩亮度" ${state.desktopLyrics.showMask ? '' : 'disabled'}><output>${Math.round(state.desktopLyrics.maskBrightness)}%</output></label></div>
          <div class="setting-row"><div class="setting-label"><strong>动画</strong><span>歌词切换时使用过渡动画</span></div><button class="switch" data-desktop-toggle="animate" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>显示翻译</strong><span>本地 LRC 含同时间翻译时一并显示</span></div><button class="switch" data-desktop-toggle="showTranslation" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>双行显示</strong><span>同时显示当前行与下一行</span></div><button class="switch" data-desktop-toggle="doubleLine" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>总是显示歌曲信息</strong><span>在歌词顶部保留歌曲标题与艺术家</span></div><button class="switch" data-desktop-toggle="alwaysShowSongInfo" role="switch"></button></div>
          <div class="desktop-lyrics-preview" data-desktop-lyrics-preview aria-label="桌面歌词样式预览">
            <div class="desktop-preview-song-info">海风与旋律&nbsp; · &nbsp;Auralis</div>
            <strong class="desktop-preview-current">让这一刻伴随音乐</strong>
            <span class="desktop-preview-translation">就向更远的海面出发</span>
            <small class="desktop-preview-next">海风与旋律</small>
            <em class="desktop-preview-lock" aria-hidden="true">${icon('lock')}</em>
          </div>
        </section>

        <section class="settings-card" data-settings-group="windows"><div class="settings-card-header"><h2>任务栏音乐体验</h2><p>默认保持关闭；启用后以 Windows 任务栏的材质和交互方式显示</p></div>
          <div class="setting-row"><div class="setting-label"><strong>启用任务栏音乐组件</strong><span>允许 Auralis 在 Windows 任务栏中显示当前歌曲与播放状态</span></div><button class="switch" data-toggle="taskbarWidgetEnabled" role="switch"></button></div>
          <div class="setting-row" data-taskbar-depends="widget"><div class="setting-label"><strong>播放时自动显示</strong><span>开始播放歌曲后，自动将音乐组件显示在任务栏中</span></div><button class="switch" data-toggle="taskbarAutoShowOnPlayback" role="switch"></button></div>
          <div class="setting-row" data-taskbar-depends="widget"><div class="setting-label"><strong>显示播放控制</strong><span>在任务栏组件中提供上一首、播放暂停和下一首</span></div><button class="switch" data-toggle="taskbarWidgetControls" role="switch"></button></div>
        </section>

        <section class="settings-card lan-sharing-card" data-settings-group="lan"><div class="settings-card-header"><div><h2>局域网播放器</h2><p>让同一可信私有网络中的浏览器独立播放这台电脑上的本地音乐</p></div><span class="lan-status-badge ${lan.running ? 'is-running' : lan.enabled ? 'is-error' : ''}" role="status">${escapeHtml(lanStatusLabel)}</span></div>
          <div class="setting-row"><div class="setting-label"><strong>启用浏览器播放</strong><span>默认关闭；关闭、退出应用或网络变化时会撤销访问会话</span></div><button class="switch" data-toggle="lanMusicSharingEnabled" role="switch" ${lan.pending ? 'disabled' : ''}></button></div>
          <div class="setting-row"><div class="setting-label"><strong>监听端口</strong><span>端口冲突时可改为 1024–65535 之间的其他值</span></div><label class="lan-port-field"><input type="number" min="1024" max="65535" step="1" value="${lan.port}" data-lan-port aria-label="局域网播放器端口" ${lan.pending || lan.running ? 'disabled' : ''}><span>TCP</span></label></div>
          <div class="lan-sharing-summary" aria-live="polite">
            <div><span>本地曲库</span><strong>${lan.trackCount} 首</strong></div>
            <div><span>已连接浏览器</span><strong>${lan.activeSessionCount} 个</strong></div>
            <div><span>当前访问链接有效至</span><strong>${escapeHtml(lanExpiryLabel)}</strong></div>
          </div>
          ${lan.baseUrls.length ? `<div class="lan-address-list"><span>可访问地址</span>${lan.baseUrls.map(url => `<code>${escapeHtml(url)}</code>`).join('')}</div>` : ''}
          ${lan.errorMessage ? `<div class="lan-sharing-message ${lan.running ? 'is-warning' : 'is-error'}" role="note">${escapeHtml(lan.errorMessage)}</div>` : ''}
          <div class="lan-sharing-actions"><button class="accent-button" data-action="copy-lan-sharing-url" ${lan.running ? '' : 'disabled'}>${icon('devices')} 复制配对链接</button><button class="secondary-button" data-action="open-lan-sharing-url" ${lan.running ? '' : 'disabled'}>在本机浏览器预览</button><button class="secondary-button" data-action="regenerate-lan-sharing-access" ${lan.running ? '' : 'disabled'}>生成新链接</button><button class="secondary-button" data-action="revoke-lan-sharing-sessions" ${lan.activeSessionCount ? '' : 'disabled'}>断开全部浏览器</button></div>
          ${lan.running && lan.pairingUrls.length ? `<div class="lan-pairing-manual"><button class="secondary-button" data-action="toggle-lan-pairing-link" aria-expanded="${lanPairingLinkVisible}" aria-controls="lanPairingLinkPanel">${lanPairingLinkVisible ? '收起配对链接' : '显示配对链接'}</button><div id="lanPairingLinkPanel" ${lanPairingLinkVisible ? '' : 'hidden'}><label for="lanPairingLinkText">完整配对链接（仅发送给可信设备）</label><textarea id="lanPairingLinkText" rows="3" readonly spellcheck="false" autocomplete="off">${escapeHtml(lan.pairingUrls[0])}</textarea><p>点击文本即可全选。链接包含一次性配对信息；配对后会更新，请勿公开分享。</p></div></div>` : ''}
          <div class="lan-security-note" role="note"><span aria-hidden="true">i</span><p>浏览器使用独立播放队列，不会抢占桌面播放器。音频只在局域网内通过 HTTP 传输，请勿在公共 Wi‑Fi、访客网络或端口映射环境中启用；在线平台歌曲与账号数据不会被共享。</p></div>
        </section>

        <section class="settings-card audio-device-card" data-settings-group="audio"><div class="settings-card-header"><div><h2>音频设备</h2><p>使用随 Auralis 提供的播放后端，只显示真正可用的能力</p></div><button class="secondary-button compact-action" data-action="refresh-audio-devices">刷新设备</button></div>
          <div class="setting-row"><div class="setting-label"><strong>输出后端</strong><span>默认交给 Windows；更换后端将从下一首歌起完整生效</span></div>${fluentSelectMarkup('audioModule', state.audioOutput.outputModule, fluentSelectOptions('audioModule'), '输出后端', 'audio-select')}</div>
          <div class="setting-row"><div class="setting-label"><strong>输出设备</strong><span>${escapeHtml(activeAudioDeviceDescription())}</span></div>${fluentSelectMarkup('audioDevice', state.audioOutput.outputDeviceId, fluentSelectOptions('audioDevice'), '输出设备', 'audio-select')}</div>
          <div class="setting-row"><div class="setting-label"><strong>输出声道</strong><span>默认保持原始立体声</span></div>${fluentSelectMarkup('audioChannel', state.audioOutput.channel, audioChannelOptions, '输出声道', 'audio-select')}</div>
          <div class="setting-row"><div class="setting-label"><strong>播放缓冲</strong><span>缓冲越大越抗抖动，但切歌和拖动反应会稍慢</span></div><label class="range-setting audio-buffer-range"><input type="range" min="100" max="5000" step="100" value="${state.audioOutput.bufferMilliseconds}" data-audio-buffer aria-label="播放缓冲"><output>${state.audioOutput.bufferMilliseconds} ms</output></label></div>
          <div class="audio-properties" role="table" aria-label="当前音频输出属性">
            <div role="row"><span role="cell">输出格式</span><strong role="cell">${escapeHtml(state.audioDevices.outputFormat)}</strong></div>
            <div role="row"><span role="cell">采样率</span><strong role="cell">${escapeHtml(state.audioDevices.sampleRateBehavior)}</strong></div>
            <div role="row"><span role="cell">DSD / 独占输出</span><strong role="cell">${escapeHtml(state.audioDevices.dsdBehavior)}</strong></div>
            ${audioEndpointLatencyMarkup()}
          </div>
          <div class="audio-latency-disclaimer" role="note"><span aria-hidden="true">i</span><p>${audioEndpointLatencyNote()}</p></div>
          <div class="audio-device-footer"><p>设备不支持当前音源格式时，Windows 音频引擎会安全协商转换；Auralis 不会伪装“位完美”、DSD 直通或精确的蓝牙端到端延迟。</p><button class="secondary-button" data-action="audio-compatibility-preset">使用兼容设置</button></div>
        </section>

        <section class="settings-card" data-settings-group="windows"><div class="settings-card-header"><h2>Windows</h2></div>
          <div class="setting-row"><div class="setting-label"><strong>开机自动启动</strong><span>登录 Windows 后自动运行 Auralis</span></div><button class="switch" data-toggle="startupEnabled" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>关闭后驻留系统托盘</strong><span>点击关闭只隐藏主窗口并继续播放；在托盘菜单选择“退出”才会完全关闭</span></div><button class="switch" data-toggle="trayEnabled" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>定时缩小到托盘</strong><span data-tray-timer-status>${state.trayMinimizeTimer.active ? `剩余 ${Math.ceil(state.trayMinimizeTimer.remainingSeconds / 60)} 分钟` : '到达时间后隐藏窗口，播放不会中断'}</span></div>${fluentSelectMarkup('trayTimer', String(state.trayMinimizeMinutes), trayTimerOptions, '定时缩小到托盘')}</div>
          <div class="setting-row"><div class="setting-label"><strong>系统媒体控制</strong><span>响应键盘播放、上一首和下一首媒体键</span></div><button class="switch" data-toggle="mediaKeys" role="switch"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>Windows 默认音乐播放器</strong><span>Auralis 只注册为候选应用，最终默认项由你在 Windows 设置中选择</span></div><button class="secondary-button" data-action="open-default-apps">在 Windows 中选择</button></div>
          <div class="setting-row"><div class="setting-label"><strong>应用日志</strong><span>日志保存在本机，并自动移除敏感信息</span></div><button class="secondary-button" data-action="open-application-logs">打开日志文件夹</button></div>
          <div class="setting-row"><div class="setting-label"><strong>动画效果</strong><span>默认跟随 Windows 的“显示动画”辅助功能设置</span></div>${fluentSelectMarkup('motion', state.motion, motionOptions, '动画效果')}</div>
        </section>

        <section class="settings-card music-folders-card" data-settings-group="library"><div class="settings-card-header"><div><h2>本地音乐库</h2><p>同时监听多个文件夹；在线搜索结果永远不会写入这里</p></div><div class="settings-card-header-actions"><button class="secondary-button" data-action="rescan-music-folders" ${state.musicFolders.length ? '' : 'disabled'}>全部重扫</button><button class="accent-button" data-action="pick-folder">${icon('plus')} 添加文件夹</button></div></div>
          ${musicFoldersMarkup()}
          <div class="music-folder-note">停止监听不会删除已经导入的歌曲，也不会移动或修改磁盘上的任何文件。</div>
          <div class="setting-row"><div class="setting-label"><strong>艺术家封面高斯模糊</strong><span>柔化从歌曲封面自动生成的艺术家封面；自定义头像保持清晰</span></div><button class="switch" data-toggle="artistCoverBlur" role="switch" aria-label="艺术家封面高斯模糊"></button></div>
          <div class="setting-row"><div class="setting-label"><strong>显示艺术家首字</strong><span>在艺术家封面或自定义头像上叠加艺术家首字</span></div><button class="switch" data-toggle="showArtistInitial" role="switch"></button></div>
        </section>
        <section class="settings-card" data-settings-group="library"><div class="setting-row about-row"><img class="about-mark" src="assets/auralis-icon.png" alt=""><div class="setting-label"><strong>Auralis</strong></div><span class="about-version" data-runtime-build>${escapeHtml(runtimeBuild)}</span></div></section>
      </div></div>`;
    filterSettingsCards();
    syncSettingsControls();
    if (!state.lyricsCacheLoaded) nativePost('requestLyricsCacheIndex');
    requestPlatformConfiguration();
  }

  function syncSettingsControls() {
    syncFullscreenThemeSelector();
    if (state.currentPage !== 'settings') return;
    $$('[data-setting="playerStyle"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.playerStyle));
    $$('[data-setting="windowEffect"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.windowEffect));
    $$('[data-setting="fullscreenBackground"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.fullscreenBackground));
    $$('[data-setting="coverTransition"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.coverTransition));
    $$('[data-setting="lyricsPreference"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.lyricsPreference));
    $$('[data-desktop-setting="fontWeight"] button').forEach(button => button.classList.toggle('active', Number(button.dataset.value) === state.desktopLyrics.fontWeight));
    $$('[data-desktop-setting="alignment"] button').forEach(button => button.classList.toggle('active', button.dataset.value === state.desktopLyrics.alignment));
    $$('[data-fluent-select]').forEach(trigger => {
      const options = fluentSelectOptions(trigger.dataset.fluentSelect);
      const selected = options.find(([value]) => value === fluentSelectValue(trigger.dataset.fluentSelect)) || options[0];
      const label = trigger.querySelector('span');
      if (label && selected) label.textContent = t(selected[1]);
    });
    $$('[data-accent]').forEach(button => button.classList.toggle('active', button.dataset.accent === state.accent));
    $('[data-toggle="density"]')?.classList.toggle('on', state.density === 'compact');
    $('[data-toggle="autoPlayOnLaunch"]')?.classList.toggle('on', state.autoPlayOnLaunch);
    $('[data-toggle="rememberPlayback"]')?.classList.toggle('on', state.rememberPlayback);
    $('[data-toggle="mediaKeys"]')?.classList.toggle('on', state.mediaKeys);
    $('[data-toggle="closeToTray"]')?.classList.toggle('on', state.closeToTray);
    $('[data-toggle="artistCoverBlur"]')?.classList.toggle('on', state.artistCoverBlur);
    $('[data-toggle="showArtistInitial"]')?.classList.toggle('on', state.showArtistInitial);
    $('[data-toggle="onlineLyricsEnabled"]')?.classList.toggle('on', state.onlineLyricsEnabled);
    $('[data-toggle="immersiveFade"]')?.classList.toggle('on', state.immersiveFade);
    $('[data-toggle="lyricAdaptive"]')?.classList.toggle('on', state.lyricAdaptive);
    $('[data-toggle="lyricBlur"]')?.classList.toggle('on', state.lyricBlur);
    $('[data-toggle="lyricSpring"]')?.classList.toggle('on', state.lyricSpring);
    $('[data-toggle="trayEnabled"]')?.classList.toggle('on', state.trayEnabled);
    $('[data-toggle="startupEnabled"]')?.classList.toggle('on', state.startupEnabled);
    $('[data-toggle="taskbarWidgetEnabled"]')?.classList.toggle('on', state.taskbarWidgetEnabled);
    $('[data-toggle="taskbarAutoShowOnPlayback"]')?.classList.toggle('on', state.taskbarAutoShowOnPlayback);
    $('[data-toggle="taskbarWidgetControls"]')?.classList.toggle('on', state.taskbarWidgetControls);
    $('[data-toggle="lanMusicSharingEnabled"]')?.classList.toggle('on', state.lanMusicSharing.enabled);
    $$('[data-desktop-toggle]').forEach(button => button.classList.toggle('on', !!state.desktopLyrics[button.dataset.desktopToggle]));
    const maskBrightnessRow = $('[data-desktop-mask-brightness]');
    const maskBrightnessInput = $('[data-desktop-range="maskBrightness"]');
    maskBrightnessRow?.classList.toggle('is-disabled', !state.desktopLyrics.showMask);
    if (maskBrightnessInput) {
      maskBrightnessInput.disabled = !state.desktopLyrics.showMask;
      maskBrightnessInput.setAttribute('aria-disabled', String(!state.desktopLyrics.showMask));
      maskBrightnessInput.value = String(state.desktopLyrics.maskBrightness);
      const output = maskBrightnessInput.parentElement?.querySelector('output');
      if (output) output.textContent = `${Math.round(state.desktopLyrics.maskBrightness)}%`;
    }
    syncDesktopLyricsSettingsPreview();
    $('[data-toggle="density"]')?.setAttribute('aria-checked', state.density === 'compact');
    $('[data-toggle="autoPlayOnLaunch"]')?.setAttribute('aria-checked', state.autoPlayOnLaunch);
    $('[data-toggle="rememberPlayback"]')?.setAttribute('aria-checked', state.rememberPlayback);
    $('[data-toggle="mediaKeys"]')?.setAttribute('aria-checked', state.mediaKeys);
    $('[data-toggle="closeToTray"]')?.setAttribute('aria-checked', state.closeToTray);
    $('[data-toggle="artistCoverBlur"]')?.setAttribute('aria-checked', state.artistCoverBlur);
    $('[data-toggle="showArtistInitial"]')?.setAttribute('aria-checked', state.showArtistInitial);
    $('[data-toggle="onlineLyricsEnabled"]')?.setAttribute('aria-checked', state.onlineLyricsEnabled);
    $('[data-toggle="immersiveFade"]')?.setAttribute('aria-checked', state.immersiveFade);
    $('[data-toggle="lyricAdaptive"]')?.setAttribute('aria-checked', state.lyricAdaptive);
    $('[data-toggle="lyricBlur"]')?.setAttribute('aria-checked', state.lyricBlur);
    $('[data-toggle="lyricSpring"]')?.setAttribute('aria-checked', state.lyricSpring);
    $('[data-toggle="trayEnabled"]')?.setAttribute('aria-checked', state.trayEnabled);
    $('[data-toggle="startupEnabled"]')?.setAttribute('aria-checked', state.startupEnabled);
    $('[data-toggle="taskbarWidgetEnabled"]')?.setAttribute('aria-checked', state.taskbarWidgetEnabled);
    $('[data-toggle="taskbarAutoShowOnPlayback"]')?.setAttribute('aria-checked', state.taskbarAutoShowOnPlayback);
    $('[data-toggle="taskbarWidgetControls"]')?.setAttribute('aria-checked', state.taskbarWidgetControls);
    $('[data-toggle="lanMusicSharingEnabled"]')?.setAttribute('aria-checked', state.lanMusicSharing.enabled);
    $$('[data-taskbar-depends]').forEach(row => {
      const enabled = state.taskbarWidgetEnabled;
      const control = $('.switch', row);
      row.classList.toggle('is-disabled', !enabled);
      if (control) {
        control.disabled = !enabled;
        control.setAttribute('aria-disabled', String(!enabled));
      }
    });
    $$('[data-desktop-toggle]').forEach(button => button.setAttribute('aria-checked', !!state.desktopLyrics[button.dataset.desktopToggle]));
    $$('[data-setting] button[data-value], [data-desktop-setting] button[data-value]').forEach(button => {
      button.setAttribute('aria-pressed', String(button.classList.contains('active')));
    });
    syncFullscreenSettingsPreview();
  }

  function filterTracks(tracks) {
    const query = i18n?.lower(state.query) ?? state.query.toLocaleLowerCase();
    const filtered = state.query
      ? tracks.filter(track => [track.title, track.artist, track.album, track.fileName].some(value => (i18n?.lower(value) ?? value?.toLocaleLowerCase())?.includes(query)))
      : [...tracks];
    const comparers = {
      title: (a, b) => i18n?.compare(a.title, b.title) ?? a.title.localeCompare(b.title),
      artist: (a, b) => i18n?.compare(a.artist, b.artist) ?? a.artist.localeCompare(b.artist),
      album: (a, b) => i18n?.compare(a.album, b.album) ?? a.album.localeCompare(b.album),
      added: (a, b) => new Date(a.dateAdded) - new Date(b.dateAdded)
    };
    return filtered.sort(comparers[state.sortMode] || comparers.added);
  }

  function recentTracks() {
    return state.recent.map(id => state.tracks.find(track => track.id === id)).filter(Boolean);
  }

  function groupBy(items, selector) {
    return items.reduce((map, item) => {
      const key = selector(item);
      map.set(key, [...(map.get(key) || []), item]);
      return map;
    }, new Map());
  }

  function persistSession(force = false) {
    if (!state.rememberPlayback || !state.currentTrackId || state.currentItemKind !== 'local') return;
    const now = Date.now();
    if (!force && now - state.lastSessionSave < 1500) return;
    state.lastSessionSave = now;
    const session = {
      trackId: state.currentTrackId,
      currentTime: state.currentTime,
      duration: state.duration,
      savedAt: now
    };
    state.session = session;
    localStorage.setItem('auralis:session', JSON.stringify(session));
  }

  function restorePlaybackSession() {
    if (!state.rememberPlayback || !state.session?.trackId) return;
    const index = state.tracks.findIndex(track => track.id === state.session.trackId);
    if (index < 0) {
      localStorage.removeItem('auralis:session');
      state.session = null;
      return;
    }

    const track = state.tracks[index];
    state.queueKind = 'local';
    state.currentItemKind = 'local';
    state.currentIndex = index;
    state.currentTrackId = track.id;
    state.currentTime = Math.max(0, Number(state.session.currentTime) || 0);
    state.duration = Number(track.durationSeconds) || Number(state.session.duration) || 0;
    state.isPlaying = state.autoPlayOnLaunch;
    // Start the native track transaction before requesting lyrics. The native side also guards
    // against stale results, but this ordering keeps playback/lyrics state deterministic.
    nativePost('loadTrack', { id: track.id, seconds: state.currentTime, autoplay: state.autoPlayOnLaunch });
    updateTrackDetails(track);
    playerBar.classList.remove('is-empty');
  }

  function setLibrary(tracks, append = false) {
    const normalized = tracks.map(track => ({ ...track, durationSeconds: Number(track.durationSeconds) || 0 }));
    if (append) {
      const positions = new Map(state.tracks.map((track, index) => [track.id, index]));
      normalized.forEach(track => {
        const position = positions.get(track.id);
        if (position === undefined) {
          positions.set(track.id, state.tracks.length);
          state.tracks.push(track);
        } else {
          state.tracks[position] = track;
        }
      });
    } else {
      state.tracks = normalized;
    }
    $('#songCountBadge').textContent = state.tracks.length;
    const availableTrackIds = new Set(state.tracks.map(track => track.id));
    state.favorites = new Set([...state.favorites].filter(id => availableTrackIds.has(id)));
    localStorage.setItem('auralis:favorites', JSON.stringify([...state.favorites]));
    if (!append && !state.currentTrackId) restorePlaybackSession();
    renderPage();
    renderQueue();
    if (!append) completeInitialUi();
  }

  function completeInitialUi() {
    if (initialUiRevealed) return;
    initialUiRevealed = true;
    clearTimeout(initialUiFallbackTimer);
    const reveal = () => {
      $('#app').classList.remove('is-loading');
      $('#splash').classList.add('is-leaving');
    };
    if (isMotionReduced()) reveal();
    else requestAnimationFrame(() => requestAnimationFrame(reveal));
  }

  function clearPendingPlatformPlayback() {
    const playback = state.platformPlayback;
    const hadPending = !!playback.pendingHandle;
    playback.pendingHandle = '';
    playback.pendingTrack = null;
    playback.pendingQueue = [];
    return hadPending;
  }

  function renderCurrentOnlineCollection() {
    if (state.currentPage === 'search') renderSearch();
    else if (state.currentPage === 'onlineCollection') renderOnlineCollection();




  }

  function playTrackById(id, forcePlay = true, mixedQueue = null) {
    requestedInlineVideo = '';
    const index = state.tracks.findIndex(track => track.id === id);
    if (index < 0) return;
    const cancelledOnlineRequest = clearPendingPlatformPlayback();
    if (state.currentItemKind === 'local' && state.currentTrackId === id) {
      if (cancelledOnlineRequest) {
        state.isPlaying = true;
        nativePost('playTrack', { id });
        syncPlaybackUi();
        renderQueue();
        renderCurrentOnlineCollection();
      } else if (forcePlay) {
        togglePlayback();
      }
      return;
    }
    state.queueKind = mixedQueue ? 'mixed' : 'local';
    if (mixedQueue) state.mixedQueue = [...mixedQueue];
    state.currentItemKind = 'local';
    state.streamQuality = null;
    state.currentIndex = mixedQueue ? mixedQueue.findIndex(t => t.id === id) : index;
    state.currentTrackId = id;
    const track = state.tracks[index];
    state.currentTime = 0;
    state.duration = Number(track.durationSeconds) || 0;
    state.isPlaying = true;
    nativePost('playTrack', { id });
    updateTrackDetails(track);
    addRecent(id);
    playerBar.classList.remove('is-empty');
    persistSession(true);
    syncPlaybackUi();
    pulseTransport();
    updateProgress();
    renderQueue();
    renderCurrentOnlineCollection();
  }

  function requestPlatformPlayback(track, queue = null, queueKind = 'online') {
    mediaHub?.ensureTrack(track);
    if (!track || track.kind !== 'online' || track.unavailable) return;
    if (requestedInlineVideo && requestedInlineVideo !== track.handle) requestedInlineVideo = '';
    if (state.platformPlayback.pendingHandle) {
      if (state.platformPlayback.pendingHandle === track.handle) return;
      clearPendingPlatformPlayback();
    }
    if (state.currentItemKind === 'online' && state.currentTrackId === track.id) {
      togglePlayback();
      return;
    }
    const sourceQueue = Array.isArray(queue) && queue.length
      ? queue.filter(item => item && !item.unavailable && (queueKind === 'mixed' || item.kind === 'online'))
      : [track];
    state.platformPlayback.pendingHandle = track.handle;
    state.platformPlayback.pendingTrack = track;
    state.platformPlayback.pendingQueue = sourceQueue;
    state.platformPlayback.pendingQueueKind = queueKind;
    renderCurrentOnlineCollection();
    renderQueue();
    nativePost('playPlatformResult', { handle: track.handle });
  }

  function playPlatformResultByHandle(handle) {
    const candidates = [
      ...(onlineCollection.detail?.tracks || []),
      ...state.platformSearch.items,
      ...state.onlineQueue
    ];
    const track = candidates.find(item => item.handle === handle);
    if (!track) {
      showToast('这个在线结果已经失效，请重新搜索');
      return;
    }
    const queue = (onlineCollection.detail?.tracks || []).some(item => item.handle === handle)
      ? onlineCollection.detail.tracks
      : state.platformSearch.items.some(item => item.handle === handle)
      ? state.platformSearch.items
      : state.onlineQueue;
    requestPlatformPlayback(track, queue);
  }

  function playPlatformQueueItemById(id, forcePlay = true) {
    const track = state.onlineQueue.find(item => item.id === id);
    if (!track) return;
    if (state.currentItemKind === 'online' && state.currentTrackId === id) {
      if (forcePlay) togglePlayback();
      return;
    }
    requestPlatformPlayback(track, state.onlineQueue);
  }

  function normalizeStreamQuality(value) {
    if (!value || typeof value !== 'object') return null;
    const bitrate = Number(value.bitrateKbps);
    const quality = {
      id: String(value.id || '').slice(0, 64),
      displayName: String(value.displayName || '').trim().slice(0, 96),
      bitrateKbps: Number.isFinite(bitrate) && bitrate > 0 ? Math.round(bitrate) : null,
      codec: String(value.codec || '').trim().slice(0, 24),
      isLossless: value.isLossless === true,
      sampleRateHz: Number.isFinite(Number(value.sampleRateHz)) && Number(value.sampleRateHz) > 0 ? Math.round(Number(value.sampleRateHz)) : null,
      bitsPerSample: Number.isFinite(Number(value.bitsPerSample)) && Number(value.bitsPerSample) > 0 ? Math.round(Number(value.bitsPerSample)) : null,
      channels: Number.isFinite(Number(value.channels)) && Number(value.channels) > 0 ? Math.round(Number(value.channels)) : null,
      isAverageBitrate: value.isAverageBitrate === true,
      requestedQualityId: String(value.requestedQualityId || '').trim().slice(0, 64),
      requestedQualityDisplayName: String(value.requestedQualityDisplayName || '').trim().slice(0, 96),
      usedFallback: value.usedFallback === true
    };
    return quality.id || quality.displayName || quality.codec || quality.bitrateKbps ? quality : null;
  }

  function streamQualityLabel(quality) {
    if (!quality) return '';
    const codec = quality.codec ? quality.codec.toUpperCase() : '';
    const bitrate = quality.bitrateKbps ? `${quality.bitrateKbps} kbps` : '';
    if (codec && bitrate) return `${codec} · ${quality.isAverageBitrate ? '≈ ' : ''}${bitrate}`;
    if (codec && quality.isLossless) return `${codec} · 无损`;
    if (bitrate) return bitrate;
    return quality.displayName || codec || '实际音质';
  }

  function syncStreamQuality() {
    const declared = state.currentItemKind === 'online' ? state.streamQuality : null;
    const observed = state.audioInformationTrackId === state.currentTrackId ? state.audioInformation : null;
    const quality = observed ? { ...declared, ...observed,
      codec: observed.isLossless ? observed.codec : (declared?.codec || observed.codec),
      bitrateKbps: observed.bitrateKbps || declared?.bitrateKbps,
      isLossless: observed.isLossless || declared?.isLossless,
      displayName: declared?.displayName || observed.codec,
      requestedQualityId: declared?.requestedQualityId,
      requestedQualityDisplayName: declared?.requestedQualityDisplayName,
      usedFallback: declared?.usedFallback } : declared;
    const actualLabel = streamQualityLabel(quality);
    $$('[data-stream-quality]').forEach(badge => {
      badge.hidden = !actualLabel;
      if (!actualLabel) {
        badge.textContent = '';
        badge.removeAttribute('title');
        badge.removeAttribute('aria-label');
        badge.classList.remove('is-fallback', 'quality-updated');
        delete badge.dataset.qualitySignature;
        return;
      }

      const requested = quality.requestedQualityDisplayName || quality.requestedQualityId;
      const details = [quality.isLossless ? t('无损') : '',
        quality.bitrateKbps ? `${quality.isAverageBitrate ? '≈ ' : ''}${quality.bitrateKbps} kbps` : t('码率未知'),
        quality.sampleRateHz ? `${quality.sampleRateHz / 1000} kHz` : '',
        quality.bitsPerSample ? `${quality.bitsPerSample} bit` : '',
        quality.channels ? `${quality.channels} ch` : '',
        quality.isAverageBitrate ? t('编码音频平均码率') : ''].filter(Boolean).join(' · ');
      const signature = `${actualLabel}|${details}|${quality.usedFallback}|${requested}`;
      badge.textContent = quality.usedFallback ? `已降至 ${actualLabel}` : actualLabel;
      badge.classList.toggle('is-fallback', quality.usedFallback);
      badge.title = quality.usedFallback
        ? `${requested ? `目标音质“${requested}”不可用，` : ''}已自动改用 ${actualLabel}`
        : `${t('当前音质')}：${quality.displayName || actualLabel} · ${details}`;
      badge.setAttribute('aria-label', badge.title);
      if (badge.dataset.qualitySignature !== signature) {
        badge.dataset.qualitySignature = signature;
        badge.classList.remove('quality-updated');
        void badge.offsetWidth;
        badge.classList.add('quality-updated');
      }
    });
  }

  function commitPlatformPlayback(handle, qualityValue = null) {
    const playback = state.platformPlayback;
    if (!playback.pendingTrack || playback.pendingHandle !== handle) return;
    const track = playback.pendingTrack;
    const queue = playback.pendingQueue.length ? [...playback.pendingQueue] : [track];
    state.onlineQueue = queue.filter(item => item.kind === 'online');
    state.queueKind = playback.pendingQueueKind === 'mixed' ? 'mixed' : 'online';
    if (state.queueKind === 'mixed') state.mixedQueue = queue;
    state.currentItemKind = 'online';
    state.currentIndex = Math.max(0, queue.findIndex(item => item.handle === handle));
    state.currentTrackId = track.id;
    state.streamQuality = normalizeStreamQuality(qualityValue);
    state.currentTime = 0;
    state.duration = Number(track.durationSeconds) || 0;
    state.isPlaying = true;
    playback.pendingHandle = '';
    playback.pendingTrack = null;
    playback.pendingQueue = [];
    updateTrackDetails(track);
    playerBar.classList.remove('is-empty');
    syncPlaybackUi();
    pulseTransport();
    updateProgress();
    renderQueue();
    renderCurrentOnlineCollection();
    if (requestedInlineVideo === handle) { requestedInlineVideo = ''; mediaHub?.setVideo(true); }
    if (state.streamQuality?.usedFallback) {
      const requested = state.streamQuality.requestedQualityDisplayName || state.streamQuality.requestedQualityId;
      showToast(`${requested ? `${requested}不可用，` : ''}已自动降至 ${streamQualityLabel(state.streamQuality)}`);
    }
  }

  function togglePlayback() {
    if (!state.currentTrackId) {
      if (state.tracks.length) playTrackById(state.tracks[0].id);
      else showToast('请先添加一些本地音乐');
      return;
    }
    state.isPlaying = !state.isPlaying;
    nativePost(state.isPlaying ? 'resumePlayback' : 'pausePlayback');
    syncPlaybackUi();
    pulseTransport();
  }

  let nextPlaybackPlan = null;
  let prefetchSignature = '';
  function plannedNextIndex(queue) {
    const signature = JSON.stringify([state.currentTrackId, state.shuffle, state.repeat, queue.map(t => t.id)]);
    if (nextPlaybackPlan?.signature === signature) return nextPlaybackPlan.index;
    let index = (state.currentIndex + 1) % queue.length;
    if (state.shuffle && queue.length > 1) {
      do { index = Math.floor(Math.random() * queue.length); } while (index === state.currentIndex);
    }
    if (state.repeat === 'one') index = state.currentIndex;
    else if (!state.shuffle && state.repeat === 'off' && state.currentIndex === queue.length - 1) index = -1;
    nextPlaybackPlan = { signature, index };
    return index;
  }

  function syncNextPrefetch() {
    if (state.platformPlayback.pendingHandle || !state.currentTrackId) return;
    const queue = activePlaybackQueue();
    const index = queue.length ? plannedNextIndex(queue) : -1;
    const track = queue[index];
    const handle = track?.kind === 'online' && track.id !== state.currentTrackId && !track.unavailable ? track.handle : '';
    // Let the foreground decoder establish its clock first; never prefetch an entire search page.
    if (handle && (!state.isPlaying || state.currentTime < 2)) return;
    const signature = `${state.currentTrackId}|${handle}`;
    if (signature === prefetchSignature) return;
    prefetchSignature = signature;
    nativePost('prefetchPlatformTrack', { currentId: state.currentTrackId, handle });
  }

  function restartCurrentPlayback() {
    state.currentTime = 0;
    state.isPlaying = true;
    nativePost('restartPlayback', { id: state.currentTrackId });
    updateProgress(true);
    syncPlaybackUi();
  }

  function playNext(direction = 1, automatic = false) {
    const queue = activePlaybackQueue();
    if (!queue.length || state.platformPlayback.pendingHandle) return;
    if (automatic && state.repeat === 'one') {
      restartCurrentPlayback();
      return;
    }
    let index;
    if (direction === 1 && (automatic || state.repeat !== 'one')) {
      index = plannedNextIndex(queue);
      if (index < 0 && !automatic) index = 0;
      if (index < 0) {
        state.isPlaying = false;
        syncPlaybackUi();
        return;
      }
    } else if (state.shuffle && queue.length > 1) {
      do { index = Math.floor(Math.random() * queue.length); } while (index === state.currentIndex);
    } else {
      index = (state.currentIndex + direction + queue.length) % queue.length;
      if (automatic && !state.shuffle && state.repeat === 'off' && state.currentIndex === queue.length - 1) {
        state.isPlaying = false;
        state.currentTime = 0;
        nativePost('pausePlayback');
        nativePost('seekPlayback', { seconds: 0 });
        updateProgress(true);
        syncPlaybackUi();
        return;
      }
    }
    if (automatic && queue[index].id === state.currentTrackId) {
      restartCurrentPlayback(); return;
    }
    if (state.queueKind === 'mixed') playMixedItem(queue[index], queue);
    else if (state.queueKind === 'online') playPlatformQueueItemById(queue[index].id);
    else playTrackById(queue[index].id);
  }

  function playPrevious() {
    if (state.currentTime > 3) {
      state.currentTime = 0;
      nativePost('seekPlayback', { seconds: 0 });
      updateProgress(true);
      return;
    }
    playNext(-1);
  }

  function renderLyricsStatus(title, detail, loading = false) {
    stopLyricsAnimation();
    lyricElements = [];
    const scroll = $('#lyricsScroll');
    scroll.innerHTML = `<div class="lyrics-status ${loading ? 'is-loading' : ''}"><strong>${escapeHtml(title)}</strong><span>${escapeHtml(detail)}</span></div>`;
    $('#lyricsSourceBadge').textContent = state.lyricsSource;
    $('#lyricsSyncBadge').textContent = loading ? '正在匹配' : '暂无同步';
    $('#nowPlayingOverlay').classList.remove('lyrics-ready');
    syncLyricsSourceSwitcher();
  }

  function syncLyricsSourceSwitcher() {
    const switcher = $('#lyricsSourceSwitcher');
    if (!switcher) return;
    const available = state.lyricsAvailability || {};
    const resolved = ['local', 'online'].includes(state.lyricsResolvedSource)
      ? state.lyricsResolvedSource
      : 'local';
    const active = ['local', 'online'].includes(state.lyricsSourceSelection)
      ? state.lyricsSourceSelection
      : resolved;
    const enabled = state.currentItemKind === 'local' && !!state.currentTrackId;

    switcher.classList.toggle('is-loading', state.lyricsLoading);
    switcher.setAttribute('aria-busy', String(state.lyricsLoading));
    $$('[data-lyrics-source]', switcher).forEach(button => {
      const source = button.dataset.lyricsSource;
      const selected = source === active;
      const hasSource = source === 'local'
        ? !!available.hasLocal
        : !!available.hasOnline || !!available.hasOnlineCache;
      button.classList.toggle('active', selected);
      button.classList.toggle('has-source', hasSource);
      button.classList.toggle('is-unknown', source === 'online' && !available.onlineChecked && !hasSource);
      button.setAttribute('aria-pressed', String(selected));
      button.disabled = !enabled;
      if (source === 'local') {
        button.title = available.hasLocal
          ? `使用本地歌词（${available.localSource || '已找到'}）`
          : '扫描同目录 LRC 与音频内嵌歌词';
      } else {
        button.title = hasSource
          ? `使用在线歌词（${available.onlineSource || '已有缓存'}）`
          : '为当前歌曲匹配在线歌词';
      }
    });
  }

  function requestLyricsForTrack(track, sourceSelection = 'auto') {
    const selected = ['local', 'online'].includes(sourceSelection) ? sourceSelection : 'auto';
    state.lyrics = [];
    state.lyricsTrackId = track.id;
    state.lyricsSourceSelection = selected;
    state.lyricsResolvedSource = selected === 'auto' ? 'none' : selected;
    state.lyricsAvailability = null;
    state.lyricsLoading = true;
    state.lyricsSource = selected === 'online' ? '在线歌词' : selected === 'local' ? '本地歌词' : '自动匹配';
    state.lyricsSynced = false;
    state.lyricsInstrumental = false;
    state.activeLyricIndex = -1;
    const detail = selected === 'local'
      ? '正在扫描同目录 LRC 与音频内嵌歌词'
      : selected === 'online'
        ? '正在通过已启用的歌词插件匹配；不会上传音频文件'
        : state.onlineLyricsEnabled
          ? '本地歌词 → 已缓存歌词 → 已启用的歌词插件'
          : '正在检查同目录 LRC 与音频内嵌歌词';
    renderLyricsStatus(
      '正在查找歌词',
      detail,
      true
    );
    nativePost('requestLyrics', {
      id: track.id,
      allowOnline: selected === 'online' || (selected === 'auto' && state.onlineLyricsEnabled && state.lyricsPreference !== 'localOnly'),
      preference: state.lyricsPreference,
      sourceSelection: selected
    });
  }

  function renderLyrics(payload) {
    if (!payload || payload.trackId !== state.currentTrackId) return;
    state.lyricsTrackId = payload.trackId;
    state.lyricsLoading = false;
    state.lyricsResolvedSource = ['local', 'online'].includes(payload.selectedSource)
      ? payload.selectedSource
      : 'none';
    state.lyricsAvailability = payload.availability && typeof payload.availability === 'object'
      ? payload.availability
      : null;
    if (state.lyricsSourceSelection !== 'auto' &&
        ['local', 'online'].includes(state.lyricsResolvedSource) &&
        state.lyricsResolvedSource !== state.lyricsSourceSelection &&
        Array.isArray(payload.lines) && payload.lines.length) {
      state.lyricsSourceSelection = state.lyricsResolvedSource;
    }
    const lyricsSource = String(payload.source || '歌词');
    state.lyricsSource = lyricsSource;
    state.lyricsSynced = !!payload.isSynced;
    state.lyricsInstrumental = !!payload.instrumental;
    state.lyrics = Array.isArray(payload.lines)
      ? payload.lines.filter(line => line && String(line.text || '').trim()).map(line => ({
          timeSeconds: line.timeSeconds !== null && line.timeSeconds !== undefined && Number.isFinite(Number(line.timeSeconds))
            ? Number(line.timeSeconds)
            : null,
          text: String(line.text).trim()
        }))
      : [];
    state.activeLyricIndex = -1;

    $('#lyricsSourceBadge').textContent = state.lyricsSource;
    $('#lyricsSyncBadge').textContent = state.lyricsInstrumental
      ? '纯音乐'
      : state.lyrics.length && !state.lyricsSynced ? '静态歌词' : '';
    syncLyricsSourceSwitcher();

    const overlay = $('#nowPlayingOverlay');
    overlay.classList.toggle('lyrics-ready', state.lyrics.length > 0 || state.lyricsInstrumental);
    if (!state.lyrics.length) {
      renderLyricsStatus(
        state.lyricsInstrumental ? '纯音乐，请欣赏' : '暂无可用歌词',
        payload.message || (state.onlineLyricsEnabled ? '没有找到匹配歌词' : '可在设置中启用在线歌词匹配')
      );
      return;
    }

    $('#lyricsScroll').innerHTML = `<div class="lyrics-lines ${state.lyricsSynced ? 'is-synced' : 'is-plain'}">${state.lyrics.map((line, index) => {
      const time = line.timeSeconds;
      const interactive = Number.isFinite(time);
      return `<button class="lyric-line" data-lyric-index="${index}" ${interactive ? `data-lyric-time="${time}"` : 'disabled'} data-text="${escapeHtml(line.text)}"><span>${escapeHtml(line.text)}</span></button>`;
    }).join('')}</div>`;
    lyricElements = $$('.lyric-line', $('#lyricsScroll'));
    observeLyricsClock();
    syncLyricsAnimation();
    updateLyricsAtTime(true);
  }

  function findActiveLyricIndex(seconds) {
    let low = 0;
    let high = state.lyrics.length - 1;
    let result = -1;
    while (low <= high) {
      const middle = (low + high) >> 1;
      const time = state.lyrics[middle].timeSeconds;
      if (!Number.isFinite(time) || time > seconds + .06) high = middle - 1;
      else {
        result = middle;
        low = middle + 1;
      }
    }
    return result;
  }

  function observeLyricsClock(reset = false) {
    lyricsClock.observe({ trackId: state.currentTrackId, seconds: state.currentTime,
      playing: state.isPlaying, rate: state.playbackRate }, performance.now(), reset);
    mediaHub?.clockUpdated(reset);
  }

  function stopLyricsAnimation() {
    cancelAnimationFrame(lyricsAnimationFrame);
    lyricsAnimationFrame = 0;
  }

  function canAnimateLyrics() {
    return state.isPlaying && !!state.currentTrackId && state.lyricsSynced &&
      lyricElements.length > 0 && !document.hidden && !isMotionReduced() &&
      $('#nowPlayingOverlay').classList.contains('open');
  }

  function syncLyricsAnimation() {
    if (!canAnimateLyrics()) { stopLyricsAnimation(); return; }
    if (lyricsAnimationFrame || !lyricsClock.isAdvancing(performance.now())) return;
    lyricsAnimationFrame = requestAnimationFrame(function frame(now) {
      lyricsAnimationFrame = 0;
      if (!canAnimateLyrics()) return;
      updateLyricsAtTime();
      if (lyricsClock.isAdvancing(now)) lyricsAnimationFrame = requestAnimationFrame(frame);
    });
  }

  function updateLyricsAtTime(force = false) {
    if (!state.lyricsSynced || !state.lyrics.length) return;
    const visualTime = canAnimateLyrics() ? lyricsClock.read(performance.now()) : state.currentTime;
    const lyricTime = Math.min(visualTime, state.duration || Infinity) - state.lyricsOffset / 1000;
    const activeIndex = findActiveLyricIndex(lyricTime);
    const changed = activeIndex !== state.activeLyricIndex;
    if (changed || force) {
      state.activeLyricIndex = activeIndex;
      lyricElements.forEach((line, index) => {
        line.classList.toggle('is-active', index === activeIndex);
        line.classList.toggle('is-past', index < activeIndex);
        line.classList.toggle('is-upcoming', index > activeIndex);
        if (index !== activeIndex) line.style.setProperty('--lyric-progress', index < activeIndex ? '100%' : '0%');
      });

      if (activeIndex >= 0 && $('#nowPlayingOverlay').classList.contains('open')) {
        const viewport = $('#lyricsViewport');
        const activeLine = $(`.lyric-line[data-lyric-index="${activeIndex}"]`, viewport);
        if (activeLine) {
          const targetTop = activeLine.offsetTop - viewport.clientHeight * .42 + activeLine.offsetHeight / 2;
          viewport.scrollTo({ top: Math.max(0, targetTop), behavior: isMotionReduced() ? 'auto' : 'smooth' });
        }
      }
    }

    if (activeIndex >= 0) {
      const start = state.lyrics[activeIndex].timeSeconds ?? lyricTime;
      const end = state.lyrics[activeIndex + 1]?.timeSeconds ?? Math.max(start + 4, state.duration);
      const progress = Math.max(0, Math.min(1, (lyricTime - start) / Math.max(.25, end - start)));
      lyricElements[activeIndex]?.style.setProperty('--lyric-progress', `${(progress * 100).toFixed(4)}%`);
    }
  }

  function updateTrackDetails(track, metadataOnly = false) {
    $('#miniTitle').textContent = track.title;
    $('#miniArtist').textContent = track.artist;
    $('#largeTitle').textContent = track.title;
    $('#largeArtist').textContent = track.artist;
    $('#stageTitle').textContent = track.title;
    $('#stageArtist').textContent = track.artist;
    const [a, b] = palettes[hashIndex(track.id)];
    const overlay = $('#nowPlayingOverlay');
    overlay.style.setProperty('--now-cover', track.coverUrl ? `url("${track.coverUrl}")` : 'none');
    overlay.style.setProperty('--record-a', a);
    overlay.style.setProperty('--record-b', b);
    root.style.setProperty('--app-song-background', track.coverUrl ? `url("${track.coverUrl}")` : 'none');
    const visual = $('.record-visual', overlay);
    visual.classList.remove('cover-enter-fade', 'cover-enter-left', 'cover-enter-right');
    void visual.offsetWidth;
    const transitionClass = state.coverTransition === 'left'
      ? 'cover-enter-left'
      : state.coverTransition === 'alternate'
        ? (state.currentIndex % 2 ? 'cover-enter-right' : 'cover-enter-left')
        : 'cover-enter-fade';
    visual.classList.add(transitionClass);
    [$('#miniCover'), $('#largeCover')].forEach(element => {
      element.style.setProperty('--cover-a', a);
      element.style.setProperty('--cover-b', b);
      element.style.backgroundImage = element.id === 'miniCover' && track.coverUrl ? `url("${track.coverUrl}")` : '';
      element.classList.toggle('has-cover', !!track.coverUrl);
    });
    const largeCoverImage = $('#largeCoverImage');
    // Do not leave the previous decoded bitmap visible while the new artwork loads.
    largeCoverImage.hidden = true;
    $('#largeCover').classList.remove('has-cover');
    const revealCover = () => {
      if (state.currentTrackId !== track.id || !largeCoverImage.naturalWidth) return;
      largeCoverImage.hidden = false;
      $('#largeCover').classList.add('has-cover');
    };
    largeCoverImage.onload = revealCover;
    largeCoverImage.onerror = () => { largeCoverImage.hidden = true; $('#largeCover').classList.remove('has-cover'); };
    if (track.coverUrl) {
      largeCoverImage.src = track.coverUrl;
      if (largeCoverImage.complete) revealCover();
    } else largeCoverImage.removeAttribute('src');
    largeCoverImage.alt = track.coverUrl ? `${track.title} 专辑封面` : '';
    if (metadataOnly) return; // Late artwork must not reset an already-playing lyric timeline.
    animateTrackChange();
    if (track.kind === 'online' || state.currentItemKind === 'online') {
      state.lyrics = [];
      state.lyricsTrackId = track.id;
      state.lyricsSourceSelection = 'auto';
      state.lyricsResolvedSource = 'none';
      state.lyricsAvailability = null;
      state.lyricsLoading = true;
      state.lyricsSource = platformDisplayName(track.providerId);
      state.lyricsSynced = false;
      state.lyricsInstrumental = false;
      state.activeLyricIndex = -1;
      renderLyricsStatus('正在获取平台歌词', '歌词只用于当前播放，不会写入本地歌词缓存。', true);
    } else {
      requestLyricsForTrack(track);
    }
  }

  function syncPlaybackUi() {
    mediaHub?.sync();
    observeLyricsClock();
    syncLyricsAnimation();
    const isPlaying = state.isPlaying && !!state.currentTrackId;
    document.body.classList.toggle('is-playing', isPlaying);
    $('#playButton').classList.toggle('is-playing', isPlaying);
    $('#overlayPlayButton').classList.toggle('is-playing', isPlaying);
    $('#playButton').setAttribute('aria-label', isPlaying ? '暂停' : '播放');
    $('#overlayPlayButton').setAttribute('aria-label', isPlaying ? '暂停' : '播放');
    syncPlaybackModeButtons();
    const localCurrent = state.currentItemKind === 'local';
    $('#favoriteCurrentButton').classList.toggle('active', localCurrent && state.favorites.has(state.currentTrackId));
    $('#favoriteCurrentButton').classList.remove('is-unavailable');
    $('#favoriteCurrentButton').disabled = !state.currentTrackId;
    $('#favoriteCurrentButton').title = localCurrent ? '我喜欢的' : '加入我的歌单';
    $('#lyricsOptionsButton').disabled = !localCurrent;
    $('#lyricsOptionsButton').classList.toggle('is-unavailable', !localCurrent);
    $('#desktopLyricsButton').classList.toggle('active', state.desktopLyrics.enabled);
    syncStreamQuality();
    syncLyricsSourceSwitcher();
    if (syncedTrackRowsRevision !== trackRowsRevision ||
        syncedTrackId !== state.currentTrackId ||
        syncedTrackPlaying !== isPlaying) {
      $$('.track-row.is-current, .track-row.is-playing').forEach(row => {
        row.classList.remove('is-current', 'is-playing');
      });
      if (state.currentTrackId) {
        const escapedTrackId = window.CSS?.escape
          ? window.CSS.escape(String(state.currentTrackId))
          : String(state.currentTrackId).replace(/["\\]/g, '\\$&');
        $$(`.track-row[data-track-id="${escapedTrackId}"]`).forEach(row => {
          row.classList.add('is-current');
          row.classList.toggle('is-playing', isPlaying);
        });
      }
      syncedTrackRowsRevision = trackRowsRevision;
      syncedTrackId = state.currentTrackId;
      syncedTrackPlaying = isPlaying;
    }
    $('#queueList')?.classList.toggle('is-paused', !isPlaying);
    syncVinylRotation();
  }

  function updateProgress(resetLyricsClock = false) {
    observeLyricsClock(resetLyricsClock);
    syncLyricsAnimation();
    const ratio = state.duration ? state.currentTime / state.duration : 0;
    const value = Math.round(ratio * 1000);
    [progressSlider, overlayProgressSlider].forEach(slider => {
      if (document.activeElement !== slider) slider.value = value;
      slider.style.setProperty('--range-value', `${ratio * 100}%`);
    });
    $('#currentTime').textContent = formatTime(state.currentTime);
    $('#overlayCurrentTime').textContent = formatTime(state.currentTime);
    $('#duration').textContent = formatTime(state.duration);
    $('#overlayDuration').textContent = formatTime(state.duration);
    updateLyricsAtTime();
  }

  function seekFromSlider(slider) {
    if (state.duration) {
      state.currentTime = (Number(slider.value) / 1000) * state.duration;
      nativePost('seekPlayback', { seconds: state.currentTime });
      updateProgress(true);
    }
  }

  function toggleFavorite(id) {
    if (!id || !state.tracks.some(track => track.id === id)) return;
    if (state.favorites.has(id)) state.favorites.delete(id);
    else state.favorites.add(id);
    localStorage.setItem('auralis:favorites', JSON.stringify([...state.favorites]));
    if (state.currentPage === 'favorites' || state.currentPage === 'songs') renderPage();
    syncPlaybackUi();
    showToast(state.favorites.has(id) ? '已添加到“我喜欢的”' : '已取消收藏');
  }

  function addRecent(id) {
    state.recent = [id, ...state.recent.filter(item => item !== id)].slice(0, 50);
    localStorage.setItem('auralis:recent', JSON.stringify(state.recent));
  }

  function removeTrack(id) {
    const track = state.tracks.find(item => item.id === id);
    if (!track) return;
    nativePost('removeTrack', { id });
    if (state.currentTrackId === id) {
      nativePost('pausePlayback');
      state.isPlaying = false;
      state.currentTime = 0;
      state.duration = 0;
      state.currentTrackId = null;
      state.currentIndex = -1;
      state.currentItemKind = null;
      state.queueKind = 'local';
      playerBar.classList.add('is-empty');
      $('#miniTitle').textContent = '还没有播放音乐';
      $('#miniArtist').textContent = '从音乐库中选择一首歌曲';
    }
    state.tracks = state.tracks.filter(item => item.id !== id);
    $('#songCountBadge').textContent = state.tracks.length;
    renderPage();
    renderQueue();
    showToast(`已从音乐库移除“${track.title}”（原文件不会被删除）`);
  }

  function renderQueue(force = false) {
    const container = $('#queueList');
    const panel = $('#queuePanel');
    if (!force && !panel.classList.contains('open')) {
      queueNeedsRender = true;
      return;
    }
    const queue = activePlaybackQueue();
    if (!queue.length) {
      container.innerHTML = '<div class="empty-state"><p>播放队列还是空的</p></div>';
      queueNeedsRender = false;
      return;
    }
    container.innerHTML = queue.map((track, index) => {
      const online = track.kind === 'online';
      const pending = online && state.platformPlayback.pendingHandle === track.handle;
      const action = state.queueKind === 'mixed' ? `data-media-action="mixed-queue" data-id="${escapeHtml(track.id)}"` : online ? `data-platform-queue-id="${escapeHtml(track.id)}"` : `data-play-id="${escapeHtml(track.id)}"`;
      const source = online ? ` · ${escapeHtml(platformDisplayName(track.providerId))}` : '';
      return `<button class="queue-item ${track.id === state.currentTrackId ? 'current' : ''} ${pending ? 'is-loading' : ''}" style="--queue-order:${Math.min(index, 12)}" ${action} ${track.unavailable ? 'disabled' : ''}><span class="queue-cover" style="${coverStyle(track)}">${track.coverUrl ? '' : escapeHtml(track.title.charAt(0).toUpperCase())}</span><span class="queue-text"><strong>${escapeHtml(track.title)}</strong><span>${escapeHtml(track.artist)}${source}</span></span>${pending ? '<span class="platform-spinner compact-spinner"></span>' : '<span class="playing-bars"><i></i><i></i><i></i></span>'}</button>`;
    }).join('');
    queueNeedsRender = false;
    syncPlaybackUi();
  }

  function toggleQueue(show) {
    const panel = $('#queuePanel');
    const open = show ?? !panel.classList.contains('open');
    const overPlayer = open && $('#nowPlayingOverlay').classList.contains('open');
    panel.classList.toggle('open', open);
    $('#queueScrim').classList.toggle('open', open);
    panel.setAttribute('aria-hidden', !open);
    document.body.classList.toggle('queue-over-player', overPlayer);
    $('#overlayQueueButton').classList.toggle('active', overPlayer);
    if (open && queueNeedsRender) renderQueue(true);
    mediaHub?.videoBounds();

    clearTimeout(queueAnimationTimer);
    panel.classList.remove('animate-items');
    if (open && !isMotionReduced()) {
      void panel.offsetWidth;
      panel.classList.add('animate-items');
      queueAnimationTimer = setTimeout(() => panel.classList.remove('animate-items'), 780);
    }
  }

  function toggleNowPlaying(show) {
    const open = show ?? !$('#nowPlayingOverlay').classList.contains('open');
    if (!open && document.body.classList.contains('queue-over-player')) toggleQueue(false);
    $('#nowPlayingOverlay').classList.toggle('open', open);
    $('#nowPlayingOverlay').setAttribute('aria-hidden', !open);
    mediaHub?.sync();
    if (open && $('#queuePanel').classList.contains('open')) {
      document.body.classList.add('queue-over-player');
      $('#overlayQueueButton').classList.add('active');
    }
    if (open) requestAnimationFrame(() => updateLyricsAtTime(true));
    syncLyricsAnimation();
    syncVinylRotation();
  }

  function syncPlaybackModeButtons() {
    const mode = state.shuffle ? 'shuffle' : state.repeat === 'one' ? 'one' : state.repeat === 'all' ? 'all' : 'off';
    const label = { off: '顺序播放', all: '列表循环', one: '单曲循环', shuffle: '随机播放' }[mode];
    const paths = {
      off: '<path d="M4 6h12M4 12h12M4 18h8m5-3 3 3-3 3m-3-3h6"/>',
      all: '<path d="m16 3 4 4-4 4M4 11V9a2 2 0 0 1 2-2h14M8 21l-4-4 4-4m12 0v2a2 2 0 0 1-2 2H4"/>',
      one: '<path d="m16 3 4 4-4 4M4 11V9a2 2 0 0 1 2-2h14M8 21l-4-4 4-4m12 0v2a2 2 0 0 1-2 2H4m6-6 2-1v4"/>',
      shuffle: '<path d="M3 6h3c5 0 7 12 12 12h3m-4-4 4 4-4 4M3 18h3c2 0 4-3 6-6s4-6 6-6h3m-4-4 4 4-4 4"/>'
    };
    [$('#shuffleButton'), $('#overlayRepeatButton')].forEach(button => {
      if (button.dataset.playbackMode !== mode) {
        button.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true">${paths[mode]}</svg>`;
        button.dataset.playbackMode = mode;
      }
      button.classList.toggle('active', mode !== 'off');
      button.title = t(label);
      button.setAttribute('aria-label', t(label));
    });
  }

  function cycleRepeatMode() {
    const mode = state.shuffle ? 'shuffle' : state.repeat;
    const next = { off: 'all', all: 'one', one: 'shuffle', shuffle: 'off' }[mode] || 'off';
    state.shuffle = next === 'shuffle';
    state.repeat = next === 'shuffle' ? 'off' : next;
    localStorage.setItem('auralis:shuffle', String(state.shuffle));
    localStorage.setItem('auralis:repeat', state.repeat);
    syncPlaybackUi();
    syncNextPrefetch();
    showToast({ off: '顺序播放', all: '列表循环', one: '单曲循环', shuffle: '随机播放' }[next]);
  }

  function cyclePlaybackRate() {
    const rates = [1, 1.25, 1.5, 2, .75];
    const current = rates.indexOf(state.playbackRate);
    state.playbackRate = rates[(current + 1) % rates.length];
    observeLyricsClock(true);
    syncLyricsAnimation();
    nativePost('setPlaybackRate', { value: state.playbackRate });
    localStorage.setItem('auralis:playback-rate', state.playbackRate);
    const label = `${state.playbackRate.toFixed(2)}x`;
    $('#speedButton').textContent = label;
    $('#overlaySpeedButton').textContent = label;
    showToast(`播放速度 ${label}`);
  }

  function showToast(message) {
    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;
    $('#toastRegion').append(toast);
    setTimeout(() => {
      toast.classList.add('leaving');
      setTimeout(() => toast.remove(), 220);
    }, 2400);
  }

  function setScanning(scanning) {
    state.scanning = scanning;
    $('.scanning-overlay')?.remove();
    if (!scanning) return;
    const overlay = document.createElement('div');
    overlay.className = 'scanning-overlay';
    overlay.innerHTML = '<div class="scanning-card"><div class="spinner"></div><strong>正在扫描音乐</strong><span>正在查找支持的本地音频文件…</span></div>';
    document.body.append(overlay);
  }

  function buildVisualizer() {
    const visualizer = $('#visualizer');
    if (!visualizer) return;
    let seed = 17;
    for (let index = 0; index < 58; index += 1) {
      seed = (seed * 9301 + 49297) % 233280;
      const height = 18 + (seed / 233280) * 58;
      const speed = 420 + ((index * 73) % 500);
      const bar = document.createElement('i');
      bar.style.setProperty('--bar-height', `${height}px`);
      bar.style.setProperty('--bar-speed', `${speed}ms`);
      bar.style.animationDelay = `${-(index * 37)}ms`;
      visualizer.append(bar);
    }
  }

  function openSearch() {
    if (state.currentPage !== 'search') setPage('search');
    $('.topbar').classList.add('open');
    setTimeout(() => {
      $('#searchInput').focus();
      $('#searchInput').select();
    }, isMotionReduced() ? 0 : 120);
  }

  function cycleSortMode() {
    const modes = ['added', 'title', 'artist', 'album'];
    state.sortMode = modes[(modes.indexOf(state.sortMode) + 1) % modes.length];
    localStorage.setItem('auralis:sort', state.sortMode);
    renderPage();
    showToast({ added: '按添加顺序排列', title: '按歌曲名称排列', artist: '按艺术家排列', album: '按专辑排列' }[state.sortMode]);
  }

  function refreshCurrentLyrics() {
    if (state.currentItemKind !== 'local') return;
    const track = state.tracks.find(item => item.id === state.currentTrackId);
    if (track) requestLyricsForTrack(track);
  }

  function persistDesktopLyrics() {
    const keyMap = {
      enabled: 'enabled', fontSize: 'font-size', fontWeight: 'font-weight', activeColor: 'active',
      inactiveColor: 'inactive', shadowColor: 'shadow', maskColor: 'mask', maskBrightness: 'mask-brightness', alignment: 'alignment',
      showMask: 'show-mask', animate: 'animate', wordHighlight: 'word-highlight',
      showTranslation: 'translation', doubleLine: 'double-line', alwaysShowSongInfo: 'song-info', locked: 'locked'
    };
    Object.entries(keyMap).forEach(([property, suffix]) => localStorage.setItem(`auralis:desktop-lyrics-${suffix}`, String(state.desktopLyrics[property])));
    nativePost('setDesktopLyricsOptions', { options: { ...state.desktopLyrics, offsetMilliseconds: state.lyricsOffset } });
    syncSettingsControls();
    syncPlaybackUi();
  }

  function selectPlatformProvider(providerId) {
    if (!platformProviders[providerId] || state.platformSearch.providerId === providerId) return;
    state.platformSearch.providerId = providerId;
    localStorage.setItem('auralis:platform-search-provider', providerId);
    state.platformSearch.items = [];
    state.platformSearch.error = '';
    if (state.query) schedulePlatformSearch();
    if (state.currentPage === 'search') {
      renderSearch();
      if (!isMotionReduced()) pageContent.querySelector('.platform-search-section')?.animate(
        [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }],
        { duration: 220, easing: 'cubic-bezier(.16,1,.3,1)' });
    }
  }

  pageContent.addEventListener('keydown', event => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    const platformRow = event.target.closest('[data-platform-play-row][role="button"]');
    if (platformRow && event.target === platformRow && platformRow.getAttribute('aria-disabled') !== 'true') {
      event.preventDefault();
      playPlatformResultByHandle(platformRow.dataset.platformPlayRow);
      return;
    }
    const card = event.target.closest('[data-play-id][role="button"]');
    if (!card || event.target !== card) return;
    event.preventDefault();
    playTrackById(card.dataset.playId);
  });

  function openOnlinePlaylist(providerId, handle) {
    if (!handle || !onlinePlaylistProviders.some(p => p.id === providerId)) return;
    Object.assign(onlineCollection, { providerId, pendingHandle: handle, detail: null, error: '', errorCode: '' });
    onlineCollection.pendingRequestId = ++onlineCollection.requestId;
    setPage('onlineCollection', {force:true});
    nativePost('requestOnlineCollection', { providerId, handle, requestId:onlineCollection.requestId });
  }

  pageContent.addEventListener('focusin', event => {
    if (event.target.id === 'lanPairingLinkText') event.target.select();
  });
  pageContent.addEventListener('change', event => {
    if (event.target.id === 'pluginImportTrust') {
      const button = $('[data-action="confirm-plugin-import"]');
      if (button) button.disabled = !event.target.checked || pluginManagement.pending || !pluginValidCount();
    }
  });

  pageContent.addEventListener('click', event => {
    const target = event.target.closest('button, [data-play-id], [data-page-link], [data-platform-play-row]');
    if (!target) return;
    if (target.dataset.onlineProviderFilter) {
      if (state.onlinePlaylistProvider === target.dataset.onlineProviderFilter) return;
      state.onlinePlaylistProvider = target.dataset.onlineProviderFilter;
      renderOnlinePlaylists();
      return;
    }
    if (target.dataset.action === 'restart-for-plugins') { persistSession(true); nativePost('restartForPlugins'); return; }
    if (target.dataset.action === 'retry-plugin-cleanup') {
      const id = target.dataset.cleanupPlugin;
      if (pluginManagement.cleanupFailures.has(id)) requestPluginManagement('setPluginEnabled', {id,enabled:false});
      return;
    }
    if (target.dataset.action === 'manage-platform-plugins') { setPage('settings'); navigateSettingsSection('plugins'); return; }
    if (target.dataset.action === 'configure-online-source') {
      if (onlineSettingsProviders.some(p => p.id === target.dataset.providerId && p.settings.length)) {
        setPage('settings'); navigateSettingsSection('online');
      }
      return;
    }
    if (['generic-platform-login','generic-platform-signout','generic-platform-refresh'].includes(target.dataset.action)) {
      const providerId = target.dataset.providerId || target.closest('[data-online-provider]')?.dataset.onlineProvider || onlineCollection.providerId;
      const provider = onlineSettingsProviders.find(p => p.id === providerId);
      if (provider) {
        if (target.dataset.action === 'generic-platform-refresh' && state.currentPage === 'onlineCollection') {
          // A configuration refresh produces new opaque collection handles, not a retry of this detail.
          // Return to the affected source and retire its stale entry points without changing accounts.
          Object.assign(onlineCollection, {pendingRequestId:0,pendingHandle:'',detail:null,error:'',errorCode:''});
          state.platformConfiguration[provider.playlistsKey] = [];
          state.onlinePlaylistProvider = providerId;
          onlinePlaylistReturnFocus = providerId;
          setPage('onlinePlaylists');
          showToast('已请求刷新歌单，请从列表重新选择。');
        }
        nativePost('manageOnlineAccount', {providerId, operation:target.dataset.action.split('-').at(-1)});
      }
      return;
    }
    if (target.dataset.action === 'save-provider-setting') {
      const input = target.closest('.setting-row')?.querySelector('[data-provider-setting-input]');
      if (input) saveProviderSetting(target.dataset.providerId, target.dataset.settingKey, input.value);
      return;
    }
    if (target.dataset.onlinePlaylistHandle) {
      openOnlinePlaylist(target.dataset.onlinePlaylistProvider, target.dataset.onlinePlaylistHandle);
      return;
    }
    if (target.dataset.fluentSelect) {
      openFluentSelect(target);
      return;
    }
    if (target.dataset.fluentColor) {
      openFluentColorPicker(target);
      return;
    }
    if (target.dataset.action === 'pick-files') nativePost('pickFiles');
    if (target.dataset.action === 'pick-folder') nativePost('pickFolder');
    if (target.dataset.action === 'rescan-music-folders') nativePost('rescanMusicFolder');
    if (target.dataset.action === 'rescan-music-folder') nativePost('rescanMusicFolder', { id: target.dataset.musicFolderId });
    if (target.dataset.action === 'remove-music-folder') nativePost('removeMusicFolder', { id: target.dataset.musicFolderId });
    if (target.dataset.action === 'pick-window-background') nativePost('pickWindowBackground');
    if (target.dataset.action === 'open-lyrics-cache') toggleLyricsCacheDialog(true);
    if (target.dataset.action === 'open-default-apps') nativePost('openDefaultAppsSettings');
    if (target.dataset.action === 'open-application-logs') nativePost('openApplicationLogs');
    if (target.dataset.action === 'open-plugin-folder') nativePost('openPluginFolder');
    if (target.dataset.action === 'refresh-plugins') requestPluginInventory();
    if (target.dataset.action === 'import-plugin') requestPluginManagement('pickPluginPackage');
    if (target.dataset.action === 'cancel-plugin-import') requestPluginManagement('cancelPluginImport');
    if (target.dataset.action === 'confirm-plugin-import' && $('#pluginImportTrust')?.checked && pluginManagement.preview)
      requestPluginManagement('confirmPluginImport', { token: pluginManagement.preview.token, trust: true });
    if (target.dataset.pluginEnable) {
      const item = pluginInventory.items.find(p => p.id === target.dataset.pluginEnable);
      if (item && (item.canEnable || item.enabled)) {
        pluginManagement.focusId = item.id;
        requestPluginManagement('setPluginEnabled', {id:item.id, enabled:!item.enabled});
      }
    }
    if (target.dataset.action === 'copy-lan-sharing-url') nativePost('copyLanMusicSharingUrl');
    if (target.dataset.action === 'toggle-lan-pairing-link') {
      lanPairingLinkVisible = !lanPairingLinkVisible;
      renderSettings();
      if (lanPairingLinkVisible) $('#lanPairingLinkText')?.focus();
      else pageContent.querySelector('[data-action="toggle-lan-pairing-link"]')?.focus();
      return;
    }
    if (target.dataset.action === 'open-lan-sharing-url') nativePost('openLanMusicSharingUrl');
    if (target.dataset.action === 'regenerate-lan-sharing-access') nativePost('regenerateLanMusicSharingAccess');
    if (target.dataset.action === 'revoke-lan-sharing-sessions') nativePost('revokeLanMusicSharingSessions');
    if (target.dataset.action === 'refresh-audio-devices') {
      markAudioEndpointLatencyLoading();
      nativePost('requestAudioDevices');
      showToast('正在刷新 Windows 音频设备…');
    }
    if (target.dataset.action === 'audio-compatibility-preset') {
      const hasDirectSound = state.audioDevices.modules.some(module => String(module.id).toLowerCase() === 'directsound');
      state.audioOutput = {
        outputModule: hasDirectSound ? 'directsound' : 'auto',
        outputDeviceId: '',
        channel: 'stereo',
        bufferMilliseconds: 1500
      };
      persistAudioOutput();
      renderSettings();
      showToast('已应用立体声与 1500 ms 稳定缓冲，下一首歌完整生效');
    }
    if (target.dataset.action === 'retry-platform-search') schedulePlatformSearch();
    if (target.dataset.action === 'platform-search-previous') showPlatformSearchPage(state.platformSearch.pageIndex - 1);
    if (target.dataset.action === 'platform-search-next') showPlatformSearchPage(state.platformSearch.pageIndex + 1);












    if (target.dataset.settingsSection) {
      navigateSettingsSection(target.dataset.settingsSection);
      return;
    }
    if (target.dataset.action === 'settings-home') {
      navigateSettingsSection('home');
      return;
    }
    if (target.dataset.platformPlayHandle) {
      playPlatformResultByHandle(target.dataset.platformPlayHandle);
      return;
    }
    if (target.dataset.platformMvHandle) {
      openPlatformMusicVideo(target.dataset.platformMvHandle);
      return;
    }
    if (target.dataset.platformPlayRow && target.getAttribute('aria-disabled') !== 'true') {
      playPlatformResultByHandle(target.dataset.platformPlayRow);
      return;
    }
    if (target.dataset.action === 'reset-shortcuts') {
      state.shortcuts = { ...defaultShortcuts };
      localStorage.setItem('auralis:shortcuts', JSON.stringify(state.shortcuts));
      renderSettings();
      showToast('快捷键已恢复默认');
    }
    if (target.dataset.shortcut) {
      state.recordingShortcut = target.dataset.shortcut;
      target.classList.add('recording');
      target.firstChild.textContent = '请按键…';
      return;
    }
    if (target.dataset.cacheCommand === 'clear') nativePost('clearLyricsCache', { id: target.dataset.trackId });
    if (target.dataset.cacheCommand === 'reset') nativePost('resetLyricsOverride', { id: target.dataset.trackId });
    if (target.dataset.action === 'play-all') {
      if (target.dataset.firstId) playTrackById(target.dataset.firstId);
    }
    if (target.dataset.action === 'sort') cycleSortMode();
    if (target.dataset.action === 'search') openSearch();
    if (target.dataset.action === 'clear-search') {
      cancelPlatformSearch(true);
      state.query = '';
      $('#searchInput').value = '';
      $('.search-box').classList.remove('query-active');
      renderSearch();
      $('#searchInput').focus();
    }
    if (target.dataset.artistUpload) {
      event.stopPropagation();
      nativePost('pickArtistImage', { artist: target.dataset.artistUpload });
      return;
    }
    if (target.dataset.playId) playTrackById(target.dataset.playId);
    if (target.dataset.favoriteId) toggleFavorite(target.dataset.favoriteId);
    if (target.dataset.removeId) removeTrack(target.dataset.removeId);
    if (target.dataset.pageLink) setPage(target.dataset.pageLink);
    const themeSelector = target.closest('[data-theme-selector]');
    if (themeSelector) {
      handleThemeSelectorClick(event);
      return;
    }
    if (target.closest('[data-setting="playerStyle"]')) applyPlayerStyle(target.dataset.value);
    if (target.closest('[data-setting="windowEffect"]')) {
      state.windowEffect = target.dataset.value;
      localStorage.setItem('auralis:window-effect', state.windowEffect);
      applyEffectPreferences();
      syncSettingsControls();
    }
    if (target.closest('[data-setting="fullscreenBackground"]')) {
      state.fullscreenBackground = target.dataset.value;
      localStorage.setItem('auralis:fullscreen-background', state.fullscreenBackground);
      applyEffectPreferences();
      syncSettingsControls();
    }
    if (target.closest('[data-setting="coverTransition"]')) {
      state.coverTransition = target.dataset.value;
      localStorage.setItem('auralis:cover-transition', state.coverTransition);
      applyEffectPreferences();
      syncSettingsControls();
    }
    if (target.closest('[data-setting="lyricsPreference"]')) {
      state.lyricsPreference = target.dataset.value;
      localStorage.setItem('auralis:lyrics-preference', state.lyricsPreference);
      refreshCurrentLyrics();
      syncSettingsControls();
    }
    if (target.closest('[data-desktop-setting="fontWeight"]')) {
      state.desktopLyrics.fontWeight = Number(target.dataset.value);
      persistDesktopLyrics();
    }
    if (target.closest('[data-desktop-setting="alignment"]')) {
      state.desktopLyrics.alignment = target.dataset.value;
      persistDesktopLyrics();
    }
    if (target.dataset.accent) applyAccent(target.dataset.accent);
    if (target.dataset.toggle === 'density') {
      state.density = state.density === 'compact' ? 'comfortable' : 'compact';
      localStorage.setItem('auralis:density', state.density);
      root.dataset.density = state.density;
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'autoPlayOnLaunch') {
      state.autoPlayOnLaunch = !state.autoPlayOnLaunch;
      if (state.autoPlayOnLaunch && !state.rememberPlayback) {
        state.rememberPlayback = true;
        localStorage.setItem('auralis:remember-playback', 'true');
      }
      localStorage.setItem('auralis:auto-play', String(state.autoPlayOnLaunch));
      persistSession(true);
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'rememberPlayback') {
      state.rememberPlayback = !state.rememberPlayback;
      localStorage.setItem('auralis:remember-playback', String(state.rememberPlayback));
      if (state.rememberPlayback) persistSession(true);
      else {
        state.autoPlayOnLaunch = false;
        localStorage.setItem('auralis:auto-play', 'false');
        localStorage.removeItem('auralis:session');
        state.session = null;
      }
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'mediaKeys') {
      state.mediaKeys = !state.mediaKeys;
      localStorage.setItem('auralis:media-keys', String(state.mediaKeys));
      nativePost('setMediaKeys', { enabled: state.mediaKeys });
      syncSettingsControls();
    }
    if (['taskbarWidgetEnabled', 'taskbarAutoShowOnPlayback', 'taskbarWidgetControls'].includes(target.dataset.toggle)) {
      const property = target.dataset.toggle;
      const storageSuffix = {
        taskbarWidgetEnabled: 'taskbar-widget-enabled',
        taskbarAutoShowOnPlayback: 'taskbar-auto-show-on-playback',
        taskbarWidgetControls: 'taskbar-widget-controls'
      }[property];
      state[property] = !state[property];
      localStorage.setItem(`auralis:${storageSuffix}`, String(state[property]));
      postTaskbarMediaOptions();
      syncSettingsControls();
      const toastText = {
        taskbarWidgetEnabled: state[property] ? '已启用 Auralis 任务栏音乐组件' : '已隐藏 Auralis 任务栏音乐组件',
        taskbarAutoShowOnPlayback: state[property] ? '开始播放后将自动显示任务栏音乐组件' : '播放不会再自动显示任务栏音乐组件',
        taskbarWidgetControls: state[property] ? '任务栏组件将显示播放控制' : '任务栏组件将只显示歌曲信息'
      }[property];
      showToast(toastText);
    }
    if (target.dataset.toggle === 'lanMusicSharingEnabled') {
      state.lanMusicSharing.enabled = !state.lanMusicSharing.enabled;
      state.lanMusicSharing.pending = true;
      nativePost('setLanMusicSharingOptions', {
        enabled: state.lanMusicSharing.enabled,
        port: state.lanMusicSharing.port
      });
      renderSettings();
    }
    if (target.dataset.toggle === 'showArtistInitial') {
      state.showArtistInitial = !state.showArtistInitial;
      localStorage.setItem('auralis:show-artist-initial', String(state.showArtistInitial));
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'artistCoverBlur') {
      state.artistCoverBlur = !state.artistCoverBlur;
      localStorage.setItem('auralis:artist-cover-blur', String(state.artistCoverBlur));
      applyEffectPreferences();
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'onlineLyricsEnabled') {
      state.onlineLyricsEnabled = !state.onlineLyricsEnabled;
      localStorage.setItem('auralis:online-lyrics', String(state.onlineLyricsEnabled));
      const track = state.currentItemKind === 'local'
        ? state.tracks.find(item => item.id === state.currentTrackId)
        : null;
      if (track) requestLyricsForTrack(track);
      showToast(state.onlineLyricsEnabled
        ? '已启用在线歌词：仅发送歌曲匹配所需元数据'
        : '已关闭在线歌词：之后只读取本地与已缓存歌词');
      renderSettings();
    }
    if (['immersiveFade', 'lyricAdaptive', 'lyricBlur', 'lyricSpring'].includes(target.dataset.toggle)) {
      const property = target.dataset.toggle;
      state[property] = !state[property];
      const suffix = { immersiveFade: 'immersive-fade', lyricAdaptive: 'lyric-adaptive', lyricBlur: 'lyric-blur', lyricSpring: 'lyric-spring' }[property];
      localStorage.setItem(`auralis:${suffix}`, String(state[property]));
      applyEffectPreferences();
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'trayEnabled') {
      state.trayEnabled = !state.trayEnabled;
      state.closeToTray = state.trayEnabled;
      localStorage.setItem('auralis:tray-enabled', String(state.trayEnabled));
      localStorage.setItem('auralis:close-to-tray', String(state.trayEnabled));
      nativePost('setTrayEnabled', { enabled: state.trayEnabled });
      showToast(state.trayEnabled
        ? '关闭主窗口后 Auralis 将驻留系统托盘'
        : '关闭主窗口将完全退出 Auralis');
      syncSettingsControls();
    }
    if (target.dataset.toggle === 'startupEnabled') {
      state.startupEnabled = !state.startupEnabled;
      localStorage.setItem('auralis:startup-enabled', String(state.startupEnabled));
      nativePost('setStartupEnabled', { enabled: state.startupEnabled });
      syncSettingsControls();
    }
    if (target.dataset.desktopToggle) {
      const property = target.dataset.desktopToggle;
      state.desktopLyrics[property] = !state.desktopLyrics[property];
      persistDesktopLyrics();
    }
  });

  pageContent.addEventListener('input', event => {
    const target = event.target;
    if (target.dataset.rangeSetting) {
      const property = target.dataset.rangeSetting;
      state[property] = Number(target.value);
      const suffix = { lyricFontSize: 'lyric-font-size', lyricVertical: 'lyric-vertical', backgroundSpeed: 'background-speed', backgroundFps: 'background-fps', lyricsOffset: 'lyrics-offset' }[property];
      localStorage.setItem(`auralis:${suffix}`, String(state[property]));
      const output = target.parentElement.querySelector('output');
      if (output) output.textContent = property === 'lyricFontSize' ? `${state[property]}px` : property === 'lyricVertical' ? `${Math.round(state[property] * 100)}%` : property === 'backgroundSpeed' ? `${state[property].toFixed(1)}×` : property === 'backgroundFps' ? `${state[property]} FPS` : `${state[property] > 0 ? '+' : ''}${state[property]} ms`;
      applyEffectPreferences();
      syncFullscreenSettingsPreview();
      if (property === 'lyricsOffset') {
        updateLyricsAtTime(true);
        nativePost('setDesktopLyricsOptions', { options: { ...state.desktopLyrics, offsetMilliseconds: state.lyricsOffset } });
      }
    }
    if (target.dataset.desktopRange) {
      const property = target.dataset.desktopRange;
      const rawValue = Number(target.value);
      const value = property === 'maskBrightness'
        ? Math.max(10, Math.min(90, rawValue))
        : rawValue;
      state.desktopLyrics[property] = value;
      const output = target.parentElement.querySelector('output');
      if (output) output.textContent = property === 'maskBrightness' ? `${Math.round(value)}%` : `${value}px`;
      syncDesktopLyricsSettingsPreview();
      persistDesktopLyrics();
    }
    if (target.dataset.audioBuffer !== undefined) {
      state.audioOutput.bufferMilliseconds = Math.max(100, Math.min(5000, Number(target.value) || 1000));
      const output = target.parentElement.querySelector('output');
      if (output) output.textContent = `${state.audioOutput.bufferMilliseconds} ms`;
      localStorage.setItem('auralis:audio-output', JSON.stringify(state.audioOutput));
      clearTimeout(audioBufferUpdateTimer);
      audioBufferUpdateTimer = setTimeout(() => {
        nativePost('setAudioOutputSettings', { settings: state.audioOutput, includeDiagnostics: false });
      }, 180);
    }
  });

  pageContent.addEventListener('change', event => {
    const target = event.target;
    if (target.dataset.lanPort === undefined) return;
    const port = Math.max(1024, Math.min(65535, Math.round(Number(target.value) || 43821)));
    state.lanMusicSharing.port = port;
    state.lanMusicSharing.pending = true;
    nativePost('setLanMusicSharingOptions', {
      enabled: state.lanMusicSharing.enabled,
      port
    });
    renderSettings();
  });

  document.addEventListener('click', event => {
    const option = event.target.closest('[data-fluent-option]');
    if (option && activeFluentFlyout?.contains(option) && activeFluentFlyoutAnchor) {
      applyFluentSelectValue(activeFluentFlyoutAnchor.dataset.fluentSelect, option.dataset.fluentOption);
      closeFluentFlyout({ restoreFocus: true });
      return;
    }
    const color = event.target.closest('[data-fluent-color-value]');
    if (color && activeFluentFlyout?.contains(color) && activeFluentFlyoutAnchor) {
      applyDesktopColor(activeFluentFlyoutAnchor.dataset.fluentColor, color.dataset.fluentColorValue);
      closeFluentFlyout({ restoreFocus: true });
      return;
    }
    if (activeFluentFlyout && !activeFluentFlyout.contains(event.target) && !activeFluentFlyoutAnchor?.contains(event.target)) {
      closeFluentFlyout();
    }
  });

  document.addEventListener('input', event => {
    const input = event.target.closest('.fluent-hex-field input');
    if (!input || !activeFluentFlyout?.contains(input) || !activeFluentFlyoutAnchor) return;
    const candidate = `#${input.value.replace(/[^0-9a-f]/gi, '').slice(0, 6).toUpperCase()}`;
    if (input.value !== candidate.slice(1)) input.value = candidate.slice(1);
    const valid = /^#[0-9A-F]{6}$/.test(candidate);
    input.closest('.fluent-hex-field')?.classList.toggle('invalid', !valid);
    if (!valid) return;
    activeFluentFlyout.querySelector('.fluent-color-preview')?.style.setProperty('--picker-color', candidate);
    applyDesktopColor(activeFluentFlyoutAnchor.dataset.fluentColor, candidate);
  });

  document.addEventListener('keydown', event => {
    if (!activeFluentFlyout) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      closeFluentFlyout({ restoreFocus: true });
    } else if (event.key === 'Enter' && event.target.matches('.fluent-hex-field input')) {
      event.preventDefault();
      if (!event.target.closest('.fluent-hex-field')?.classList.contains('invalid')) closeFluentFlyout({ restoreFocus: true });
    }
  });

  pageContent.addEventListener('scroll', () => closeFluentFlyout(), { passive: true });
  window.addEventListener('resize', () => closeFluentFlyout(), { passive: true });

  $('#lyricsScroll').addEventListener('click', event => {
    const line = event.target.closest('[data-lyric-time]');
    if (!line) return;
    const seconds = Number(line.dataset.lyricTime);
    if (!Number.isFinite(seconds)) return;
    state.currentTime = seconds;
    nativePost('seekPlayback', { seconds });
    updateProgress(true);
  });

  $$('.nav-item[data-page]').forEach(button => button.addEventListener('click', () => setPage(button.dataset.page)));
  $('#sidebarSearchButton').addEventListener('click', openSearch);
  $('#addTopButton').addEventListener('click', () => nativePost('pickFiles'));
  $('#themeQuickButton').addEventListener('click', () => applyTheme(root.dataset.theme === 'dark' ? 'light' : 'dark'));
  $('#fullscreenThemeSelector').addEventListener('click', handleThemeSelectorClick);

  $('#lyricsCacheDialog').addEventListener('click', event => {
    const target = event.target.closest('button, [data-cache-dialog-close]');
    if (!target) return;
    if (target.dataset.cacheDialogClose !== undefined || target.dataset.action === 'close-lyrics-cache') {
      toggleLyricsCacheDialog(false);
      return;
    }
    if (target.dataset.action === 'refresh-lyrics-cache') {
      nativePost('requestLyricsCacheIndex');
      return;
    }
    if (target.dataset.cacheCommand === 'clear') nativePost('clearLyricsCache', { id: target.dataset.trackId });
    if (target.dataset.cacheCommand === 'reset') nativePost('resetLyricsOverride', { id: target.dataset.trackId });
  });
  $('#lyricsCacheSearch').addEventListener('input', event => {
    lyricsCacheQuery = event.target.value;
    renderLyricsCacheDialog();
  });

  $('#searchInput').addEventListener('input', event => {
    state.query = event.target.value.trim();
    $('.search-box').classList.toggle('query-active', !!state.query);
    schedulePlatformSearch();
    if (state.currentPage !== 'search') setPage('search', { queryChanged: true });
    else renderSearch();
  });
  const searchHistory = window.AuralisSearchHistory.create(localStorage);
  function searchHistoryMarkup() {
    const items = searchHistory.all();
    return items.length ? `<section class="search-history"><header><h2>最近搜索</h2><button class="secondary-button" data-history-clear>清空历史</button></header><div class="search-history-items">${items.map((query,index) => `<button class="secondary-button" data-history-index="${index}" data-i18n-skip>${escapeHtml(query)}</button>`).join('')}</div></section>` : '';
  }
  $('#searchInput').addEventListener('blur', () => searchHistory.add(state.query));
  $('#searchInput').addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); searchHistory.add(state.query); }
  });
  pageContent.addEventListener('click', event => {
    if (event.target.closest('[data-history-clear]')) { searchHistory.clear(); renderSearch(); return; }
    const button = event.target.closest('[data-history-index]');
    if (!button) return;
    const query = searchHistory.all()[Number(button.dataset.historyIndex)];
    if (!query) return;
    searchHistory.add(query); $('#searchInput').value = query;
    $('#searchInput').dispatchEvent(new Event('input', {bubbles:true}));
    $('#searchInput').focus();
  });
  $('#clearSearchButton').addEventListener('click', () => {
    cancelPlatformSearch(true);
    state.query = '';
    $('#searchInput').value = '';
    $('.search-box').classList.remove('query-active');
    if (state.currentPage !== 'search') setPage('search');
    else renderSearch();
    $('#searchInput').focus();
  });

  $('#playButton').addEventListener('click', togglePlayback);
  $('#overlayPlayButton').addEventListener('click', togglePlayback);
  $('#nextButton').addEventListener('click', () => playNext(1));
  $('#overlayNextButton').addEventListener('click', () => playNext(1));
  $('#previousButton').addEventListener('click', playPrevious);
  $('#overlayPreviousButton').addEventListener('click', playPrevious);
  $('#favoriteCurrentButton').addEventListener('click', () => state.currentItemKind === 'online' ? mediaHub?.save(currentPlaybackItem()) : toggleFavorite(state.currentTrackId));
  $('#desktopLyricsButton').addEventListener('click', () => {
    state.desktopLyrics.enabled = !state.desktopLyrics.enabled;
    persistDesktopLyrics();
    syncPlaybackUi();
    showToast(state.desktopLyrics.enabled ? '已显示桌面歌词' : '已隐藏桌面歌词');
  });

  $('#lyricsSourceSwitcher').addEventListener('click', event => {
    const button = event.target.closest('[data-lyrics-source]');
    if (!button || button.disabled) return;
    const track = currentPlaybackItem();
    if (!track || state.currentItemKind !== 'local') {
      showToast('在线歌曲使用平台返回的歌词，不写入本地缓存');
      return;
    }
    const source = button.dataset.lyricsSource;
    if (!['local', 'online'].includes(source) || source === state.lyricsSourceSelection) return;
    requestLyricsForTrack(track, source);
    showToast(source === 'local'
      ? '已切换到本地歌词，仅扫描本机文件'
      : '正在为当前歌曲匹配在线歌词');
  });

  $('#shuffleButton').addEventListener('click', cycleRepeatMode);

  $('#overlayRepeatButton').addEventListener('click', cycleRepeatMode);
  $('#lyricsOptionsButton').addEventListener('click', event => {
    event.stopPropagation();
    const menu = $('#lyricsOptionsMenu');
    const open = !menu.classList.contains('open');
    menu.classList.toggle('open', open);
    menu.setAttribute('aria-hidden', String(!open));
  });
  $('#lyricsOptionsMenu').addEventListener('click', event => {
    const button = event.target.closest('[data-lyrics-command]');
    if (!button || !state.currentTrackId) return;
    if (button.dataset.lyricsCommand === 'pick') nativePost('pickLyricsFile', { id: state.currentTrackId });
    if (button.dataset.lyricsCommand === 'reset') nativePost('resetLyricsOverride', { id: state.currentTrackId });
    if (button.dataset.lyricsCommand === 'clear') nativePost('clearLyricsCache', { id: state.currentTrackId });
    $('#lyricsOptionsMenu').classList.remove('open');
    $('#lyricsOptionsMenu').setAttribute('aria-hidden', 'true');
  });
  document.addEventListener('click', event => {
    if (!event.target.closest('#lyricsOptionsMenu, #lyricsOptionsButton')) {
      $('#lyricsOptionsMenu').classList.remove('open');
      $('#lyricsOptionsMenu').setAttribute('aria-hidden', 'true');
    }
  });
  $('#speedButton').addEventListener('click', cyclePlaybackRate);
  $('#overlaySpeedButton').addEventListener('click', cyclePlaybackRate);

  [progressSlider, overlayProgressSlider].forEach(slider => {
    slider.addEventListener('input', () => {
      const percent = Number(slider.value) / 10;
      slider.style.setProperty('--range-value', `${percent}%`);
      seekFromSlider(slider);
    });
    slider.addEventListener('pointerdown', () => slider.classList.add('is-scrubbing'));
    slider.addEventListener('pointerup', () => slider.classList.remove('is-scrubbing'));
    slider.addEventListener('pointercancel', () => slider.classList.remove('is-scrubbing'));
  });

  volumeSlider.value = state.volume;
  volumeSlider.style.setProperty('--range-value', `${state.volume * 100}%`);
  overlayVolumeSlider.value = state.volume;
  overlayVolumeSlider.style.setProperty('--range-value', `${state.volume * 100}%`);
  [volumeSlider, overlayVolumeSlider].forEach(slider => slider.addEventListener('input', () => {
    state.volume = Number(slider.value);
    state.muted = false;
    volumeSlider.value = state.volume;
    overlayVolumeSlider.value = state.volume;
    volumeSlider.style.setProperty('--range-value', `${state.volume * 100}%`);
    overlayVolumeSlider.style.setProperty('--range-value', `${state.volume * 100}%`);
    localStorage.setItem('auralis:volume', state.volume);
    nativePost('setVolume', { value: state.volume });
  }));
  nativePost('setVolume', { value: state.volume });
  nativePost('setPlaybackRate', { value: state.playbackRate });
  $('#speedButton').textContent = `${state.playbackRate.toFixed(2)}x`;
  $('#overlaySpeedButton').textContent = `${state.playbackRate.toFixed(2)}x`;

  $('#queueButton').addEventListener('click', () => toggleQueue());
  $('#closeQueueButton').addEventListener('click', () => toggleQueue(false));
  $('#queueScrim').addEventListener('click', () => toggleQueue(false));
  $('#queueList').addEventListener('click', event => {
    const platformItem = event.target.closest('[data-platform-queue-id]');
    if (platformItem) {
      playPlatformQueueItemById(platformItem.dataset.platformQueueId);
      return;
    }
    const item = event.target.closest('[data-play-id]');
    if (item) playTrackById(item.dataset.playId);
  });
  $('#nowPlayingButton').addEventListener('click', () => toggleNowPlaying(true));
  $('#closeNowPlayingButton').addEventListener('click', () => toggleNowPlaying(false));
  $('#overlayQueueButton').addEventListener('click', () => toggleQueue());
  $('#fullscreenButton').addEventListener('click', () => nativePost('windowToggleFullscreen'));
  $('#pinButton').addEventListener('click', event => {
    event.currentTarget.classList.toggle('active');
    nativePost('windowToggleTopmost');
    showToast(event.currentTarget.classList.contains('active') ? '窗口已置顶' : '已取消窗口置顶');
  });
  $('#overlayMinimizeButton').addEventListener('click', () => nativePost('windowMinimize'));
  $('#overlayMaximizeButton').addEventListener('click', () => nativePost('windowToggleMaximize'));
  $('#overlayCloseWindowButton').addEventListener('click', () => nativePost('windowClose'));
  $('.immersive-toolbar').addEventListener('mousedown', event => {
    if (event.button === 0 && !event.target.closest('button')) nativePost('windowDrag');
  });
  $('#nowPlayingOverlay').addEventListener('pointermove', event => {
    const overlay = event.currentTarget;
    overlay.style.setProperty('--mouse-x', `${event.clientX}px`);
    overlay.style.setProperty('--mouse-y', `${event.clientY}px`);
  });

  $('#minimizeButton').addEventListener('click', () => nativePost('windowMinimize'));
  $('#maximizeButton').addEventListener('click', () => nativePost('windowToggleMaximize'));
  $('#closeButton').addEventListener('click', () => nativePost('windowClose'));
  $('#titlebar').addEventListener('mousedown', event => {
    if (event.button === 0 && !event.target.closest('[data-no-drag], button')) nativePost('windowDrag');
  });
  $('.title-drag-region').addEventListener('dblclick', () => nativePost('windowToggleMaximize'));

  function changeVolume(delta) {
    state.volume = Math.max(0, Math.min(1, state.volume + delta));
    state.muted = false;
    [volumeSlider, overlayVolumeSlider].forEach(slider => {
      slider.value = state.volume;
      slider.style.setProperty('--range-value', `${state.volume * 100}%`);
    });
    localStorage.setItem('auralis:volume', state.volume);
    nativePost('setVolume', { value: state.volume });
    showToast(`音量 ${Math.round(state.volume * 100)}%`);
  }

  document.addEventListener('keydown', event => {
    if (state.recordingShortcut) {
      event.preventDefault();
      event.stopPropagation();
      if (event.code === 'Escape') {
        state.recordingShortcut = null;
        renderSettings();
        return;
      }
      const duplicate = Object.entries(state.shortcuts).find(([key, code]) => code === event.code && key !== state.recordingShortcut);
      if (duplicate) state.shortcuts[duplicate[0]] = '';
      state.shortcuts[state.recordingShortcut] = event.code;
      state.recordingShortcut = null;
      localStorage.setItem('auralis:shortcuts', JSON.stringify(state.shortcuts));
      renderSettings();
      showToast('快捷键已更新');
      return;
    }

    // Space/Enter belong to the focused native control, including disclosure summaries.
    if (['Space', 'Enter'].includes(event.code) && event.target.closest?.('button, summary, [role="switch"]')) return;
    const typing = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);
    if (event.ctrlKey && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      openSearch();
    } else if (event.ctrlKey && event.key.toLowerCase() === 'o') {
      event.preventDefault();
      nativePost('pickFiles');
    } else if (event.code === state.shortcuts.playPause && !typing) {
      event.preventDefault();
      togglePlayback();
    } else if (event.code === state.shortcuts.next && !typing) {
      event.preventDefault();
      playNext(1);
    } else if (event.code === state.shortcuts.previous && !typing) {
      event.preventDefault();
      playPrevious();
    } else if (event.code === state.shortcuts.volumeUp && !typing) {
      event.preventDefault();
      changeVolume(.05);
    } else if (event.code === state.shortcuts.volumeDown && !typing) {
      event.preventDefault();
      changeVolume(-.05);
    } else if (event.code === state.shortcuts.mute && !typing) {
      state.muted = !state.muted;
      nativePost('setVolume', { value: state.muted ? 0 : state.volume });
      showToast(state.muted ? '已静音' : '已恢复音量');
    } else if (event.key === 'Escape') {
      if ($('#lyricsCacheDialog').classList.contains('open')) {
        event.preventDefault();
        toggleLyricsCacheDialog(false);
        return;
      }
      const lyricsMenu = $('#lyricsOptionsMenu');
      if (lyricsMenu.classList.contains('open')) {
        event.preventDefault();
        lyricsMenu.classList.remove('open');
        lyricsMenu.setAttribute('aria-hidden', 'true');
        return;
      }
      if ($('#queuePanel').classList.contains('open')) {
        event.preventDefault();
        toggleQueue(false);
        return;
      }
      if (state.fullscreen) {
        event.preventDefault();
        nativePost('windowToggleFullscreen');
        return;
      }
      if ($('#nowPlayingOverlay').classList.contains('open')) {
        event.preventDefault();
        toggleNowPlaying(false);
        return;
      }
      if (state.currentPage === 'search') {
        if (state.query) {
          state.query = '';
          cancelPlatformSearch(true);
          $('#searchInput').value = '';
          $('.search-box').classList.remove('query-active');
          renderSearch();
          $('#searchInput').focus();
        } else {
          setPage('songs');
        }
      } else {
        $('.topbar').classList.remove('open');
        $('#searchInput').blur();
      }
    } else if ((event.code === state.shortcuts.fullscreen && !typing) || event.key === 'F11') {
      event.preventDefault();
      if (!$('#nowPlayingOverlay').classList.contains('open')) toggleNowPlaying(true);
      nativePost('windowToggleFullscreen');
    }
  });

  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (state.theme === 'system') applyTheme('system');
  });

  function playMixedItem(track, queue) {
    if (!track || track.unavailable) return;
    if (track.id === state.currentTrackId) {
      state.queueKind = 'mixed'; state.mixedQueue = [...queue]; state.currentIndex = queue.findIndex(t => t.id === track.id);
      togglePlayback(); renderQueue(); return;
    }
    if (track.kind === 'online') requestPlatformPlayback(track, queue, 'mixed');
    else playTrackById(track.id, true, queue);
  }

  mediaHub = window.AuralisMediaHub.create({ state, escape: escapeHtml, post: nativePost,
    current: currentPlaybackItem, time: () => lyricsClock.read(performance.now()), advancing: () => lyricsClock.isAdvancing(performance.now()), reduced: isMotionReduced,
    normalize: normalizePlatformTrack, content: pageContent, formatTime, coverStyle,
    providerName: platformDisplayName, toast: showToast, play: playMixedItem,
    capabilities: currentPlatformCapabilities,
    metadataChanged: handle => {
      if (currentPlaybackItem()?.handle === handle) { updateTrackDetails(currentPlaybackItem(), true); syncPlaybackUi(); }
      renderQueue();
    },
    find: id => state.tracks.find(t => t.id === id) || findPlatformTrackByHandle(id) });

  let runtimeBuild = 'Preview 0.16.10';
  window.Auralis = {
    setWindowMaterialState(payload) { root.dataset.nativeMica = payload?.mica === true ? 'on' : 'off'; },
    setRuntimeBuild(label) {
      if (typeof label !== 'string' || label.length > 128) return;
      runtimeBuild = label;
      document.querySelectorAll('[data-runtime-build]').forEach(node => { node.textContent = label; });
    },
    setSavedPlaylists: payload => mediaHub.receiveLists(payload),
    setSavedTrackDetails: payload => mediaHub.receiveTrack(payload),
    setPlatformExtras: payload => mediaHub.receiveExtras(payload),
    setEmbeddedVideoState: payload => mediaHub.receiveVideo(payload),
    setUiLanguageState(payload) {
      applyUiLanguageState({
        preference: payload?.preference,
        resolvedLanguage: payload?.resolvedLanguage,
        notifyNative: false
      });
    },
    receiveLibrary(tracks) { setLibrary(tracks, false); },
    updateLibraryTracks(tracks) { setLibrary(Array.isArray(tracks) ? tracks : [], true); },
    removeLibraryTracks(ids) {
      const removed = new Set(Array.isArray(ids) ? ids : []);
      if (!removed.size) return;
      state.tracks = state.tracks.filter(track => !removed.has(track.id));
      state.favorites = new Set([...state.favorites].filter(id => !removed.has(id)));
      state.recent = state.recent.filter(id => !removed.has(id));
      localStorage.setItem('auralis:favorites', JSON.stringify([...state.favorites]));
      localStorage.setItem('auralis:recent', JSON.stringify(state.recent));
      $('#songCountBadge').textContent = state.tracks.length;
      state.mixedQueue = state.mixedQueue.filter(track => track.kind === 'online' || !removed.has(track.id));
      if (state.currentItemKind === 'local') state.currentIndex = activePlaybackQueue().findIndex(track => track.id === state.currentTrackId);
      renderPage();
      renderQueue();
    },
    setMusicFolders(folders) {
      state.musicFolders = Array.isArray(folders) ? folders : [];
      if (state.currentPage === 'settings' && state.settingsSection === 'library') renderSettings();
    },
    showLanPairingLink() {
      lanPairingLinkVisible = true;
      if (state.currentPage === 'settings' && state.settingsSection === 'lan') {
        renderSettings();
        $('#lanPairingLinkText')?.focus();
      }
    },
    setLanMusicSharingState(payload) {
      if (!payload?.running) lanPairingLinkVisible = false;
      state.lanMusicSharing = {
        enabled: !!payload?.enabled,
        running: !!payload?.running,
        pending: false,
        status: String(payload?.status || 'stopped'),
        port: Math.max(1024, Math.min(65535, Number(payload?.port) || 43821)),
        baseUrls: Array.isArray(payload?.baseUrls) ? payload.baseUrls.map(String) : [],
        pairingUrls: Array.isArray(payload?.pairingUrls) ? payload.pairingUrls.map(String) : [],
        pairingExpiresAt: payload?.pairingExpiresAt || null,
        trackCount: Math.max(0, Number(payload?.trackCount) || 0),
        activeSessionCount: Math.max(0, Number(payload?.activeSessionCount) || 0),
        errorCode: payload?.errorCode || null,
        errorMessage: payload?.errorMessage || null
      };
      if (state.currentPage === 'settings' && state.settingsSection === 'lan') renderSettings();
    },
    playLocalTrack(id) { playTrackById(id); },
    addTracks(tracks) {
      setLibrary(tracks, true);
      setScanning(false);
      showToast(`已添加 ${tracks.length} 首本地音乐`);
    },
    showToast,
    setScanning,
    setArtistImage(artist, url) {
      if (!artist || !url) return;
      state.artistImages[artist] = url;
      localStorage.setItem('auralis:artist-images', JSON.stringify(state.artistImages));
      if (state.currentPage === 'artists' || state.currentPage === 'search') renderPage();
      showToast(`已更新“${artist}”的本地头像`);
    },
    setWindowBackground(url) {
      state.customBackground = url || '';
      state.windowEffect = 'custom';
      localStorage.setItem('auralis:custom-background', state.customBackground);
      localStorage.setItem('auralis:window-effect', state.windowEffect);
      applyEffectPreferences();
      if (state.currentPage === 'settings') renderSettings();
      showToast('已应用本地窗口背景');
    },
    setLyricsCacheIndex(rows) {
      state.lyricsCacheIndex = Array.isArray(rows) ? rows : [];
      state.lyricsCacheLoaded = true;
      if (state.currentPage === 'settings') renderSettings();
      if ($('#lyricsCacheDialog').classList.contains('open')) renderLyricsCacheDialog();
    },
    setLyrics(payload) {
      renderLyrics(payload);
    },
    nativeDesktopLyricsLockChanged(locked) {
      state.desktopLyrics.locked = !!locked;
      syncSettingsControls();
      showToast(locked ? '桌面歌词已锁定' : '桌面歌词已解锁，可以拖动和调整大小');
    },
    setPlatformSearchResult(payload) {
      if (!payload || Number(payload.requestId) !== state.platformSearch.pendingRequestId) return;
      if (String(payload.query || '').trim() !== state.query.trim()) return;
      if (payload.providerId && String(payload.providerId).toLowerCase() !== state.platformSearch.providerId) return;
      if (String(payload.pageHandle || '') !== state.platformSearch.pendingPageHandle) return;
      const requestedPageIndex = state.platformSearch.pendingPageIndex;
      state.platformSearch.pending = false;
      state.platformSearch.pendingRequestId = 0;
      state.platformSearch.pendingPageHandle = '';
      state.platformSearch.error = platformErrorText(payload.error);
      state.platformSearch.errorCode = platformErrorCode(payload.error);
      const retryAfterSeconds = Math.max(0, Math.min(3600, Number(payload?.error?.retryAfterSeconds) || 0));
      state.platformSearch.retryAvailableAt = state.platformSearch.error
        ? Date.now() + retryAfterSeconds * 1000
        : 0;
      if (!state.platformSearch.error) {
        const items = (Array.isArray(payload.items) ? payload.items : [])
          .map(item => normalizePlatformTrack(item, payload.providerId || state.platformSearch.providerId))
          .filter(Boolean);
        const totalCountValue = payload.totalCount == null ? Number.NaN : Number(payload.totalCount);
        const page = {
          items,
          nextPageHandle: String(payload.nextPageHandle || ''),
          totalCount: Number.isFinite(totalCountValue) && totalCountValue >= 0 ? totalCountValue : null
        };
        state.platformSearch.pages = state.platformSearch.pages.slice(0, requestedPageIndex);
        state.platformSearch.pages[requestedPageIndex] = page;
        state.platformSearch.pageIndex = requestedPageIndex;
        state.platformSearch.items = items;
        state.platformSearch.nextPageHandle = page.nextPageHandle;
        state.platformSearch.totalCount = page.totalCount;
      } else if (!state.platformSearch.items.length) {
        state.platformSearch.pages = [];
        state.platformSearch.pageIndex = 0;
        state.platformSearch.totalCount = null;
      }
      renderCurrentOnlineCollection();
      schedulePlatformSearchRetryAvailability();
    },
    setPlatformPlaybackResult(payload) {
      const handle = String(payload?.handle || '');
      if (!handle || handle !== state.platformPlayback.pendingHandle) return;
      if (payload.success === true && !payload.error) {
        if (payload.item?.handle === handle) {
          const restored = normalizePlatformTrack(payload.item);
          if (restored) Object.assign(state.platformPlayback.pendingTrack, restored);
          mediaHub.receiveTrack({handle,item:payload.item});
        }
        commitPlatformPlayback(handle, payload.quality);
        return;
      }
      const message = platformErrorText(payload.error, '这首在线歌曲暂时无法播放') || '这首在线歌曲暂时无法播放';
      if (requestedInlineVideo === handle) requestedInlineVideo = '';
      clearPendingPlatformPlayback();
      renderCurrentOnlineCollection();
      renderQueue();
      showToast(message);
    },
    setPlatformVideoResult(payload) {
      const handle = String(payload?.handle || '');
      if (!handle || handle !== state.platformVideo.pendingHandle) return;
      const track = findPlatformTrackByHandle(handle);
      state.platformVideo.pendingHandle = '';
      renderCurrentOnlineCollection();
      if (payload.success === true && !payload.error) {
        showToast(`已在新窗口打开 ${platformDisplayName(track?.providerId)} 视频`);
        return;
      }
      showToast(platformErrorText(payload.error, '这个 MV 暂时无法播放') || '这个 MV 暂时无法播放');
    },
    setPluginInventory(payload) {
      if (!pluginInventory.pending || payload?.requestId !== pluginInventory.requestId) return;
      clearTimeout(pluginInventory.timer);
      pluginInventory.pending = false;
      pluginInventory.error = !!payload.error;
      if (!payload.error) {
        pluginInventory.loaded = true;
        pluginInventory.items = (Array.isArray(payload.items) ? payload.items : []).slice(0, 128).map(item => ({
          id: String(item.id || ''), displayName: String(item.displayName || ''), version: String(item.version || ''),
          providers: (Array.isArray(item.providers) ? item.providers : []).map(String), state: String(item.state || ''),
          enabled: item.enabled === true, active: item.active === true, canEnable: item.canEnable === true,
          compatibilityIssue: pluginCompatibilityText(item.compatibilityIssue) ? item.compatibilityIssue : ''
        }));
        pluginInventory.issues = Array.isArray(payload.issues) ? payload.issues : [];
      }
      updatePluginManagementView();
    },
    setPluginManagementResult(payload) {
      const m = pluginManagement;
      if (!m.pending || payload?.requestId !== m.requestId || payload.action !== m.action) return;
      clearTimeout(m.timer); m.pending = false;
      if (payload.error) m.error = '操作未完成或结果未确认。请检查包格式、完整性、兼容性和文件权限，并刷新状态。';
      else if (['pickPluginPackage', 'dropPluginPackages'].includes(m.action) && !payload.cancelled && (payload.batch || payload.preview)) {
        const batch = payload.batch || {token:payload.preview.token, items:[{fileName:'',preview:payload.preview}]};
        const normalize = p => p ? {id:String(p.id||''),displayName:String(p.displayName||''),version:String(p.version||''),sha256:String(p.sha256||''),providers:(p.providers||[]).map(String),capabilities:(p.capabilities||[]).map(String),
          credentialAliases:(Array.isArray(p.credentialAliases)?p.credentialAliases:[]).slice(0,16).map(a=>({key:String(a?.key||''),scope:String(a?.scope||''),legacyKey:String(a?.legacyKey||'')})),
          hostRequirements:p.hostRequirements ? {minimumHostSdkVersion:String(p.hostRequirements.minimumHostSdkVersion||''),requiredFeatures:(Array.isArray(p.hostRequirements.requiredFeatures)?p.hostRequirements.requiredFeatures:[]).slice(0,32).map(String)} : null} : null;
        m.preview = { token:String(batch.token||''), items:(Array.isArray(batch.items)?batch.items:[]).slice(0,16).map(row=>({fileName:String(row.fileName||''),preview:normalize(row.preview),error:row.error?String(row.error):null})) };
      } else if (m.action === 'confirmPluginImport') {
        m.preview = null;
        m.results = (Array.isArray(payload.results)?payload.results:[]).map(row=>({fileName:String(row.fileName||''),error:row.error?String(row.error):null}));
        m.notice = m.results.some(row=>row.error) ? '批次处理完成，请查看各文件结果。成功导入的插件默认关闭。' : '导入完成，默认未启用。请打开所需插件开关并重启应用。';
      }
      else if (m.action === 'cancelPluginImport') m.preview = null;
      else if (m.action === 'setPluginEnabled') m.notice = payload.credentialsCleared === true ? '已停用并清除该插件的登录数据，重启后释放已加载的组件。' : payload.credentialsCleared === false ? '插件已停用，但登录数据未完全清除。请再次执行停用以重试。' : '设置已保存，重启后生效。';
      if (!payload.error && ['setPluginEnabled','confirmPluginImport'].includes(m.action)) m.restartRequired = true;
      if (!payload.error && m.action === 'setPluginEnabled') {
        if (payload.credentialsCleared === false) m.cleanupFailures.set(m.lastPluginId, pluginInventory.items.find(i => i.id === m.lastPluginId)?.displayName || m.lastPluginId);
        else if (payload.credentialsCleared === true) m.cleanupFailures.delete(m.lastPluginId);
      }
      if (m.action !== 'pickPluginPackage') requestPluginInventory();
      updatePluginManagementView();
      if (m.preview && !payload.error) $('#pluginImportTrust')?.focus({preventScroll:false});
    },
    setPlaybackComponents(payload) { window.AuralisPlaybackComponents?.receive(payload); window.AuralisMediaTransportComponents?.refresh(); revealAdvancedComponentAttention(); },
    setMediaTransportComponents(payload) { window.AuralisMediaTransportComponents?.receive(payload); window.AuralisPlaybackComponents?.refresh(); revealAdvancedComponentAttention(); },
    setOnlineProviderSettingResult(payload) {
      const pending = providerSettingsSave.pending;
      if (!pending || payload?.requestId !== pending.requestId || payload?.providerId !== pending.providerId || payload?.key !== pending.key) return;
      clearTimeout(providerSettingsSave.timer);
      providerSettingsSave.pending = null;
      if (payload.error) providerSettingDrafts.set(pending.providerId + ':' + pending.key, {value:pending.value,definition:pending.definition,error:String(payload.error)});
      else providerSettingDrafts.delete(pending.providerId + ':' + pending.key);
      if (!payload.error) {
        const provider = onlineSettingsProviders.find(p => p.id === pending.providerId);
        const setting = provider?.settings.find(s => s.key === pending.key);
        if (setting) setting.value = pending.value;
        if (provider && Array.isArray(payload.settings)) { provider.settings = payload.settings; provider.configured = payload.configured === true; }
      }
      showToast(payload.error ? String(payload.error) : '插件设置已保存');
      if (state.currentPage === 'settings') renderSettings();
    },
    setPlatformConfiguration(payload) {
      const configuration = state.platformConfiguration;
      for (const key of Object.keys(configuration)) if (key.startsWith('provider:')) delete configuration[key];
      onlinePlaylistProviders.splice(0);
      onlineSettingsProviders.splice(0);
      for (const key of Object.keys(platformProviders)) delete platformProviders[key];
      for (const p of (Array.isArray(payload?.providers) ? payload.providers : [])) {
        if (!/^[a-z0-9][a-z0-9._-]{0,63}$/i.test(p.id || '')) continue;
        const id = String(p.id), name = String(p.name || id);
        const capabilities = Array.isArray(p.capabilities) ? p.capabilities : [];
        if (capabilities.includes('TrackSearch')) platformProviders[id] = name;
        const authenticationKey = 'provider:' + id + ':auth', playlistsKey = 'provider:' + id + ':playlists', errorKey = 'provider:' + id + ':error';
        configuration[authenticationKey] = {status: String(p.authentication?.status || 'signedout'),
          accountDisplayName:String(p.authentication?.accountDisplayName || ''), error:platformErrorText(p.error)};
        configuration[playlistsKey] = (Array.isArray(p.playlists) ? p.playlists : []).map(normalizeOnlineCollection).filter(Boolean);
        configuration[errorKey] = platformErrorText(p.error);
        const settings = (Array.isArray(p.settings) ? p.settings : []).filter(s => s && /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$/.test(s.key) && ['choice','endpoint'].includes(s.kind)).slice(0,16)
          .map(s => ({...s,label:String(s.label || s.key),description:String(s.description || ''),value:String(s.value || ''),choices:Array.isArray(s.choices) ? s.choices.slice(0,16) : []}));
        const entry = {id, name, capabilities, settings, configured:p.configured !== false, authenticationKey, playlistsKey, errorKey,
          loginAction:'generic-platform-login', signoutAction:'generic-platform-signout', refreshAction:'generic-platform-refresh'};
        onlineSettingsProviders.push(entry);
        if (capabilities.includes('PlaylistBrowse')) onlinePlaylistProviders.push(entry);
      }
      if (!platformProviders[state.platformSearch.providerId]) {
        cancelPlatformSearch(true);
        state.platformSearch.providerId = Object.keys(platformProviders)[0] || '';
      }
      if (!onlinePlaylistProviders.some(p => p.id === onlineCollection.providerId && canReadOnlineCollections(p))) {
        onlineCollection.pendingRequestId = 0; onlineCollection.pendingHandle = ''; onlineCollection.detail = null;
      }
      configuration.loaded = true;
      const settingDefinitions = new Map(onlineSettingsProviders.flatMap(p =>
        p.settings.map(s => [p.id + ':' + s.key, providerSettingDefinition(s)])));
      for (const [key, draft] of providerSettingDrafts) {
        if (settingDefinitions.get(key) !== draft.definition) providerSettingDrafts.delete(key);
      }
      const pendingSetting = providerSettingsSave.pending;
      if (pendingSetting && settingDefinitions.get(pendingSetting.providerId + ':' + pendingSetting.key) !== pendingSetting.definition) {
        clearTimeout(providerSettingsSave.timer);
        providerSettingsSave.pending = null;
      }
      configuration.pending = false;
      configuration.error = platformErrorText(payload?.error, '无法保存在线搜索配置');
      if (!platformProviderNeedsConfiguration(state.platformSearch.providerId)) {
        if (state.currentPage === 'search' && state.query.length >= 2) schedulePlatformSearch();
      } else if (platformProviderNeedsConfiguration(state.platformSearch.providerId)) {
        cancelPlatformSearch(true);
      }
      if (state.currentPage === 'settings') renderSettings();
      else if (state.currentPage === 'search') renderSearch();
      else if (state.currentPage === 'onlinePlaylists') renderOnlinePlaylists();
      else if (state.currentPage === 'onlineCollection') renderOnlineCollection();




      mediaHub?.sync();
    },
    setOnlineCollection(payload) {
      if (!onlineCollection.pendingRequestId || payload?.requestId !== onlineCollection.pendingRequestId || payload?.handle !== onlineCollection.pendingHandle ||
          payload?.providerId !== onlineCollection.providerId) return;
      const provider = onlinePlaylistProviders.find(p => p.id === payload.providerId);
      if (!provider || !canReadOnlineCollections(provider)) return;
      if (payload.detail && payload.detail.playlist?.handle !== payload.handle) return;
      onlineCollection.pendingRequestId = 0; onlineCollection.pendingHandle = '';
      onlineCollection.error = platformErrorText(payload.error);
      onlineCollection.errorCode = platformErrorCode(payload.error);
      if (onlineCollection.errorCode === 'authenticationrequired' && provider.capabilities.includes('Authentication')) {
        const configuration = state.platformConfiguration;
        configuration[provider.authenticationKey] = {status:'expired',accountDisplayName:'',error:onlineCollection.error};
        configuration[provider.playlistsKey] = [];
        configuration[provider.errorKey] = onlineCollection.error;
      }
      const playlist = normalizeOnlineCollection(payload.detail?.playlist);
      onlineCollection.detail = !onlineCollection.error && playlist ? {playlist,
        tracks:(payload.detail.tracks || []).map(i => normalizePlatformTrack(i, payload.providerId)).filter(Boolean)} : null;
      if (state.currentPage === 'onlineCollection') renderOnlineCollection();
    },
    setWindowState(maximized) {
      state.maximized = maximized;
      $('.window-actions').classList.toggle('is-maximized', maximized);
    },
    setDpiScale(scale) {
      state.dpiScale = Math.max(1, Number(scale) || 1);
      root.dataset.dpi = state.dpiScale >= 1.75 ? 'very-high' : state.dpiScale >= 1.25 ? 'high' : 'normal';
      root.style.setProperty('--native-dpi-scale', String(state.dpiScale));
    },
    setPlaybackVolume(value) {
      const nextVolume = Math.max(0, Math.min(1, Number(value) || 0));
      state.volume = nextVolume;
      state.muted = false;
      [volumeSlider, overlayVolumeSlider].forEach(slider => {
        slider.value = String(nextVolume);
        slider.style.setProperty('--range-value', `${nextVolume * 100}%`);
      });
      localStorage.setItem('auralis:volume', String(nextVolume));
    },
    setTrayMinimizeTimerState(payload) {
      const wasActive = state.trayMinimizeTimer.active;
      state.trayMinimizeTimer = {
        active: !!payload?.active,
        remainingSeconds: Math.max(0, Number(payload?.remainingSeconds) || 0)
      };
      if (wasActive && !state.trayMinimizeTimer.active) state.trayMinimizeMinutes = 0;
      const status = $('[data-tray-timer-status]');
      if (status) {
        const seconds = state.trayMinimizeTimer.remainingSeconds;
        status.textContent = state.trayMinimizeTimer.active
          ? `剩余 ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}，届时只隐藏窗口`
          : '到达时间后隐藏窗口，播放不会中断';
      }
      syncSettingsControls();
    },
    setAudioDevices(payload) {
      state.audioDevices.loaded = true;
      state.audioDevices.modules = Array.isArray(payload?.modules) ? payload.modules : [];
      state.audioDevices.devices = Array.isArray(payload?.devices) ? payload.devices : [];
      state.audioDevices.activeDeviceId = String(payload?.activeDeviceId || '');
      state.audioDevices.outputFormat = String(payload?.outputFormat || state.audioDevices.outputFormat);
      state.audioDevices.sampleRateBehavior = String(payload?.sampleRateBehavior || state.audioDevices.sampleRateBehavior);
      state.audioDevices.dsdBehavior = String(payload?.dsdBehavior || state.audioDevices.dsdBehavior);
      const endpointLatency = payload?.endpointLatency || {};
      const latencyNumber = value => value !== null && value !== undefined && value !== '' && Number.isFinite(Number(value))
        ? Number(value)
        : null;
      if (endpointLatency.status !== 'notRequested') {
        state.audioDevices.endpointLatency = {
          endpointId: String(endpointLatency.endpointId || ''),
          deviceName: String(endpointLatency.deviceName || ''),
          isBluetooth: !!endpointLatency.isBluetooth,
          estimatedLatencyMilliseconds: latencyNumber(endpointLatency.estimatedLatencyMilliseconds),
          streamLatencyMilliseconds: latencyNumber(endpointLatency.streamLatencyMilliseconds),
          enginePeriodMilliseconds: latencyNumber(endpointLatency.enginePeriodMilliseconds),
          status: String(endpointLatency.status || 'probeUnavailable')
        };
      }
      if (payload?.settings) {
        state.audioOutput = {
          outputModule: String(payload.settings.outputModule || 'auto'),
          outputDeviceId: String(payload.settings.outputDeviceId || ''),
          channel: audioChannelOptions.some(([value]) => value === payload.settings.channel) ? payload.settings.channel : 'stereo',
          bufferMilliseconds: Math.max(100, Math.min(5000, Number(payload.settings.bufferMilliseconds) || 1000))
        };
        localStorage.setItem('auralis:audio-output', JSON.stringify(state.audioOutput));
      }
      if (state.currentPage === 'settings' && state.settingsSection === 'audio') renderSettings();
      else syncSettingsControls();
    },
    setFullscreenState(fullscreen) {
      state.fullscreen = fullscreen;
      $('#nowPlayingOverlay').classList.toggle('native-fullscreen', fullscreen);
    },
    setPlaybackState(playback) {
      const current = currentPlaybackItem();
      if (playback.id && playback.id !== state.currentTrackId && playback.id !== current?.handle) return;
      if (Object.prototype.hasOwnProperty.call(playback, 'audioInformation')) {
        const observed = normalizeStreamQuality(playback.audioInformation);
        if (state.audioInformationTrackId !== state.currentTrackId || JSON.stringify(observed) !== JSON.stringify(state.audioInformation)) {
          state.audioInformation = observed;
          state.audioInformationTrackId = state.currentTrackId;
          syncStreamQuality();
        }
      }
      const playbackVisualChanged = state.isPlaying !== !!playback.isPlaying;
      state.isPlaying = !!playback.isPlaying;
      state.currentTime = Number(playback.currentTime) || 0;
      state.duration = Number(playback.duration) || 0;
      syncNextPrefetch();
      observeLyricsClock();
      if (state.currentItemKind === 'local') persistSession();
      if (document.hidden) return;
      if (playbackVisualChanged) syncPlaybackUi();
      updateProgress();
    },
    nativeEnded(id = null) {
      if (id && id !== state.currentTrackId) return;
      playNext(1, true);
    },
    mediaCommand(command) {
      if (command === 'playPause') togglePlayback();
      if (command === 'next') playNext(1);
      if (command === 'previous') playPrevious();
    }
  };

  window.addEventListener('beforeunload', () => {
    stopLyricsAnimation();
    cancelPlatformSearch(false);
    persistSession(true);
  });

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { stopLyricsAnimation(); return; }
    syncPlaybackUi();
    updateProgress();
  });

  reducedMotionMedia.addEventListener('change', () => {
    if (state.motion !== 'system') return;
    applyMotionPreference();
    syncVinylRotation();
    syncSettingsControls();
    postTaskbarMediaOptions();
  });

  applyPreferences();
  buildVisualizer();
  renderPage();
  renderQueue();
  requestPlatformConfiguration();
  nativePost('requestLanMusicSharingState');

  const uiTestParameters = new URLSearchParams(location.search);
  if (location.hostname === '127.0.0.1' && uiTestParameters.get('ui-test') === '1') {
    if (uiTestParameters.get('ui-test-lan') === '1') window.Auralis.setLanMusicSharingState({
      enabled: true, running: true, status: 'running', port: 43821, trackCount: 16,
      baseUrls: ['http://127.0.0.1:43821/'],
      pairingUrls: ['http://127.0.0.1:43821/#access=ui-test-not-a-real-pairing-token'],
      pairingExpiresAt: new Date(Date.now() + 600000).toISOString(), activeSessionCount: 0
    });
    const now = new Date().toISOString();
    const mockTracks = [
      { id: 'ui-test-1', title: '星河回声', artist: 'Auralis 测试歌手', album: '本地界面测试', fileName: '星河回声.flac', extension: 'FLAC', coverUrl: null, size: 28500000, durationSeconds: 180, dateAdded: now },
      { id: 'ui-test-2', title: '清晨的风', artist: '第二位歌手', album: '本地界面测试', fileName: '清晨的风.mp3', extension: 'MP3', coverUrl: null, size: 9200000, durationSeconds: 214, dateAdded: now },
      { id: 'ui-test-3', title: '无声轨迹', artist: 'Auralis 测试歌手', album: '另一张专辑', fileName: '无声轨迹.wav', extension: 'WAV', coverUrl: null, size: 41000000, durationSeconds: 156, dateAdded: now }
    ];
    setLibrary(mockTracks, false);
    playTrackById('ui-test-1');
    renderLyrics({
      trackId: 'ui-test-1',
      source: '本地 LRC · 界面测试',
      isSynced: true,
      instrumental: false,
      message: '同步歌词',
      lines: [
        { timeSeconds: 0, text: '灯光沿着窗边慢慢醒来' },
        { timeSeconds: 10, text: '我们把清晨写进旋律' },
        { timeSeconds: 20, text: '风穿过安静的街道' },
        { timeSeconds: 30, text: '每一步都靠近新的回声' },
        { timeSeconds: 40, text: '此刻星河落进眼睛' },
        { timeSeconds: 50, text: '让时间跟着节拍前行' },
        { timeSeconds: 60, text: '不必追赶远方的答案' },
        { timeSeconds: 70, text: '音乐会替我们记住今天' },
        { timeSeconds: 80, text: '下一段旅程正在开始' }
      ]
    });
    window.Auralis.setPlaybackState({ id: 'ui-test-1', isPlaying: true, currentTime: 42.5, duration: 180 });
    // Online fixtures use the same provider snapshot as Native; an empty core invents no accounts.
    window.Auralis.setPlatformConfiguration({ providers: [] });

    if (uiTestParameters.get('ui-test-theme') === 'dark') applyTheme('dark');
    if (uiTestParameters.get('ui-test-motion') === 'reduced') {
      state.motion = 'reduced';
      document.documentElement.dataset.motion = 'reduced';
    }

    if (uiTestParameters.get('ui-test-search') === '1') {
      window.Auralis.setPlatformConfiguration({providers:[{id:'fixture.search',name:'搜索测试来源',capabilities:['TrackSearch','VideoResolution'],playlists:[]}]});
      const requestId = ++state.platformSearch.requestId;
      const mockOnlineTracks = offset => Array.from({ length: 8 }, (_, index) => {
        const position = offset + index + 1;
        return {
          handle: `ui-search-${position}`,
          providerId: 'fixture.search',
          sourceName: '搜索测试来源',
          title: position === 1 ? '海风与旋律' : `海风与旋律 ${position}`,
          artist: position === 1 ? 'Auralis' : `在线测试歌手 ${position}`,
          album: position === 1 ? '自下而上生长' : '分页界面测试',
          durationSeconds: 201 + position * 8,
          availability: 'available',
          isPlayable: true,
          hasMusicVideo: position % 2 === 1
        };
      });
      state.query = '大海';
      state.currentPage = 'search';
      state.platformSearch.providerId = 'fixture.search';
      state.platformSearch.pending = true;
      state.platformSearch.pendingRequestId = requestId;
      state.platformSearch.pendingPageIndex = 0;
      state.platformSearch.pendingPageHandle = '';
      renderPage();
      window.Auralis.setPlatformSearchResult({
        requestId,
        query: state.query,
        providerId: 'fixture.search',
        pageHandle: null,
        nextPageHandle: 'ui-test-next-page',
        totalCount: 126,
        items: mockOnlineTracks(0)
      });
      state.platformSearch.pages[1] = {
        items: mockOnlineTracks(8).map(item => normalizePlatformTrack(item, 'fixture.search')).filter(Boolean),
        nextPageHandle: null,
        totalCount: 126
      };
    }

  }

  if (!initialUiRevealed) {
    initialUiFallbackTimer = setTimeout(completeInitialUi, isMotionReduced() ? 0 : 800);
  }
})();
