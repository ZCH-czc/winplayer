/* Provider-neutral UI. Only opaque native handles and public display text are accepted here. */
window.AuralisMediaHub = (() => {
  function create(api) {
    const $ = selector => document.querySelector(selector);
    const esc = api.escape;
    const state = api.state;
    const t = text => window.AuralisI18n?.t(text) || text;
    let lists = [], selected = '', saveTarget = null, requestId = 0, trackKey = '', panelKind = '';
    const pending = new Map();
    const metadataPending = new Map(), metadataAttempted = new Set(), metadataForced = new Set();
    let metadataObserver = null, metadataQueue = [];
    function pumpMetadata() {
      while (metadataPending.size < 2 && metadataQueue.length) {
        const handle = metadataQueue.shift();
        const forced=metadataForced.delete(handle);
        if ((!forced && state.currentPage !== 'savedPlaylists') || metadataAttempted.has(handle)) continue;
        metadataAttempted.add(handle);
        metadataPending.set(handle, setTimeout(() => receiveTrack({handle}), 22000));
        api.post('hydrateSavedTrack', {handle});
      }
    }
    function ensureTrack(track) {
      if (!track?.handle?.startsWith('saved-') || track.coverUrl || metadataAttempted.has(track.handle)) return;
      metadataForced.add(track.handle);
      if (!metadataQueue.includes(track.handle)) metadataQueue.unshift(track.handle);
      pumpMetadata();
    }
    function observeMetadata() {
      metadataObserver?.disconnect(); metadataQueue = [];
      metadataObserver = new IntersectionObserver(entries => {
        const list = lists.find(p => p.id === selected);
        for (const entry of entries) if (entry.isIntersecting) {
          const track = list?.entries[Number(entry.target.dataset.index)]?.track;
          if (track?.handle?.startsWith('saved-') && !track.coverUrl && !metadataAttempted.has(track.handle) && !metadataQueue.includes(track.handle)) metadataQueue.push(track.handle);
        }
        pumpMetadata();
      }, {root:api.content, rootMargin:'100px'});
      api.content.querySelectorAll('.saved-track-play').forEach(row=>metadataObserver.observe(row));
    }
    function receiveTrack(payload) {
      const handle = payload?.handle;
      clearTimeout(metadataPending.get(handle)); metadataPending.delete(handle);
      const item = payload?.item && api.normalize(payload.item);
      if (item && item.handle === handle) {
        const tracks = [...lists.flatMap(p=>p.entries.map(e=>e.track)), ...state.mixedQueue, ...state.onlineQueue, ...(state.platformPlayback?.pendingQueue || []), state.platformPlayback?.pendingTrack].filter(Boolean);
        for (const track of tracks) if (track.handle === handle) Object.assign(track,item);
        // Patch artwork in place: a late metadata result must not rebuild the list,
        // steal keyboard focus, move its scroll position or restart row animations.
        const list = lists.find(p=>p.id===selected);
        for (const row of api.content.querySelectorAll('.saved-track-play')) {
          const track=list?.entries[Number(row.dataset.index)]?.track;
          if (track?.handle !== handle) continue;
          const cover=row.querySelector('.queue-cover');
          cover.setAttribute('style', api.coverStyle(track).replaceAll('&quot;','"'));
          cover.textContent=track.coverUrl?'':'♪';
          row.querySelector('strong').textContent=track.title;
          row.lastElementChild.textContent=api.formatTime(Number(track.durationSeconds)||0);
        }
        lastListSignature=JSON.stringify(lists);
        api.metadataChanged?.(handle);
      }
      pumpMetadata();
    }
    let parts = [], commentTrack = null, followsPlayback = false;
    let danmaku = [], danmakuTrack = '', lastTime = -1, frame = 0, danmakuIndex = 0;
    let closeAnimation = null, returnFocus = null, listMutation = null, lastListSignature = '', lastRenderedList = '';
    let boundsFrame = 0, boundsUntil = 0, lastBounds = '', lastOverlayOpen = false;
    let videoEnabled = false, videoPending = false, videoRequest = 0;
    let videoExit = null;
    let frameTimer = 0, frameAbort = null, pictureGeneration = 0;
    let coverVideo = localStorage.getItem('auralis:cover-video') === 'true';
    let showDanmaku = localStorage.getItem('auralis:danmaku') === 'true';
    const dialog = document.createElement('dialog');
    dialog.className = 'media-hub-dialog';
    dialog.setAttribute('aria-labelledby', 'mediaDialogTitle');
    document.body.append(dialog);
    const creatorFeed = window.AuralisCreatorFeed.create(api, (track,follow) => openComments(track,follow));
    const pageDiscussion = window.AuralisPageDiscussion.create(api,()=>++requestId,{
      beforeOpen:()=>{if(dialog.open&&panelKind==='comments'){closeDialog(true);returnFocus=null;}}
    });
    const drawerDiscussion = window.AuralisPageDiscussion.create(api,()=>++requestId,{
      beforeOpen:()=>pageDiscussion.close(),
      active:subject=>dialog.open&&!closeAnimation&&panelKind==='comments'&&
        (!followsPlayback||($('#nowPlayingOverlay').classList.contains('open')&&api.current()?.handle===subject.handle)),
      canAutoLoad:()=>dialog.open&&!closeAnimation,
      heading:()=>dialog.querySelector('#mediaDialogTitle'),
      invalidated:()=>closeDialog(true)
    });
    const pluginPages = window.AuralisPluginPages.create(api, (track,follow) => openComments(track,follow),pageDiscussion);
    const creatorSearch = window.AuralisCreatorSearch.create(api, item => {
      const entry=pluginPages.creator(item);
      if(entry?.acceptsCreatorContext)pluginPages.open(item,entry);else creatorFeed.open(item);
    });
    const creatorAvailable = track => !!pluginPages.creator(track) || hasCapability(track,'CreatorFeed') || hasCapability(track,'CreatorProfile');
    const openCreator = track => pluginPages.creator(track) ? pluginPages.open(track,pluginPages.creator(track)) : creatorFeed.open(track);
    $('#nowPlayingOverlay').insertAdjacentHTML('beforeend', '<div id="mediaTools" class="media-tools" hidden><button class="secondary-button" data-media-action="save-current">加入歌单</button><button class="secondary-button" data-media-action="parts" hidden>分 P</button><button class="secondary-button" data-media-action="comments">评论</button><button class="secondary-button" data-media-action="options">播放扩展</button></div><div id="danmakuLayer" class="danmaku-layer" aria-hidden="true"></div><section id="embeddedVideoPage" class="embedded-video-page" hidden><header><button class="secondary-button" data-media-action="audio">← 返回音频</button><span id="embeddedVideoStatus" role="status">正在加载视频，音频继续播放…</span></header><div id="embeddedVideoSurface"></div></section>');
    const videoCanvas = document.createElement('canvas');
    $('#mediaTools').insertAdjacentHTML('afterbegin','<button class="secondary-button" data-media-action="creator" hidden>作者主页</button>');
    videoCanvas.setAttribute('aria-label', '视频画面');
    $('#embeddedVideoSurface').append(videoCanvas);
    $('#embeddedVideoSurface').insertAdjacentHTML('beforeend', '<div class="video-awaiting-picture"><span>视频画面准备中；暂停时可点击播放继续</span></div>');
    $('#embeddedVideoPage header').insertAdjacentHTML('beforeend', '<button class="secondary-button" data-media-action="video-retry" hidden>重试视频</button>');
    const qualityControl=document.createElement('label');qualityControl.className='video-quality-control';qualityControl.hidden=true;
    const qualityLabel=document.createElement('span'),qualitySelect=document.createElement('select');
    let automaticVideoQuality=true;
    qualitySelect.id='videoQuality';qualityControl.append(qualityLabel,qualitySelect);$('#embeddedVideoPage header').append(qualityControl);
    qualitySelect.addEventListener('change',()=>setVideo(true,qualitySelect.value||null));
    function stopPictures(clear = false) {
      pictureGeneration++; clearTimeout(frameTimer); frameTimer = 0;
      frameAbort?.abort(); frameAbort = null;
      if (clear) {
        videoCanvas.getContext('2d').clearRect(0, 0, videoCanvas.width, videoCanvas.height);
        $('#embeddedVideoSurface').classList.add('awaiting-picture');
      }
    }
    function startPictures() {
      if (frameTimer || frameAbort || !videoEnabled || document.hidden) return;
      const generation = pictureGeneration, handle = api.current()?.handle, id = videoRequest;
      async function nextPicture() {
        frameTimer = 0;
        const started = performance.now();
        if (generation !== pictureGeneration || !videoEnabled || document.hidden) return;
        const controller = new AbortController(); frameAbort = controller;
        let picture;
        try {
          const response = await fetch(`https://video.auralis.local/${encodeURIComponent(handle)}/${id}?frame=${performance.now()}`, {signal:controller.signal, cache:'no-store', credentials:'omit'});
          if (!response.ok) throw new Error('Frame not ready');
          picture = await createImageBitmap(await response.blob());
          if (generation !== pictureGeneration || handle !== api.current()?.handle || id !== videoRequest) return;
          if (videoCanvas.width !== picture.width || videoCanvas.height !== picture.height) {
            videoCanvas.width = picture.width; videoCanvas.height = picture.height;
          }
          videoCanvas.getContext('2d', {alpha:false}).drawImage(picture, 0, 0);
          $('#embeddedVideoSurface').classList.remove('awaiting-picture');
        } catch (_) { /* Decoder readiness/errors are reported by Native, not a noisy per-frame toast. */ }
        finally {
          picture?.close();
          if (frameAbort === controller) frameAbort = null;
          if (generation === pictureGeneration && videoEnabled && !document.hidden)
            frameTimer = setTimeout(nextPicture, state.isPlaying ? Math.max(0, 33-(performance.now()-started)) : 250);
        }
      }
      void nextPicture();
    }
    function openDialog(title, body) {
      const wasOpen = dialog.open, scroll = dialog.querySelector('.media-dialog-body')?.scrollTop || 0;
      const focused = dialog.contains(document.activeElement) ? document.activeElement : null;
      const focusId = focused?.id, focusAction = focused?.dataset.mediaAction;
      const draft = $('#savedPlaylistName')?.value, selection = $('#savedPlaylistName')?.selectionStart;
      closeAnimation?.cancel(); closeAnimation = null;
      dialog.classList.remove('is-closing');
      if (!wasOpen) {
        returnFocus = document.activeElement;
        dialog.innerHTML = '<header><h2 id="mediaDialogTitle"></h2><button class="icon-button" data-media-action="close" aria-label="关闭"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18"/></svg></button></header><p class="media-dialog-context media-context" data-i18n-skip hidden></p><div class="media-dialog-body"></div>';
      }
      if (panelKind !== 'comments') drawerDiscussion.close();
      dialog.classList.toggle('is-comments', panelKind === 'comments');
      dialog.querySelector('.media-dialog-context').hidden = true;
      dialog.querySelector('.media-dialog-body').classList.toggle('discussion-surface',panelKind==='comments');
      $('#mediaDialogTitle').textContent = title;
      dialog.querySelector('.media-dialog-body').innerHTML = body;
      if (draft !== undefined && $('#savedPlaylistName')) { $('#savedPlaylistName').value = draft; }
      if (!wasOpen) dialog.showModal();
      else {
        const target = focusId ? document.getElementById(focusId) : [...dialog.querySelectorAll('[data-media-action]')].find(n => n.dataset.mediaAction === focusAction);
        (target || (focused && dialog.querySelector('[data-media-action="close"]')))?.focus({preventScroll:true});
        if (focusId === 'savedPlaylistName' && selection != null) $('#savedPlaylistName')?.setSelectionRange(selection, selection);
        dialog.querySelector('.media-dialog-body').scrollTop = scroll;
      }
      videoBounds(); startDanmaku();
    }
    function closeDialog(immediate = false) {
      if (immediate) { closeAnimation?.cancel(); closeAnimation = null; }
      if (!dialog.open || closeAnimation) return;
      // Cancel at the start, not the end, of dismissal. A response arriving during
      // the closing animation must not cancel that animation and reopen the panel.
      if (panelKind && pending.delete(panelKind)) api.post('cancelPlatformExtras', {kind:panelKind});
      drawerDiscussion.close(true);
      if (immediate || api.reduced()) { dialog.close(); return; }
      dialog.classList.add('is-closing');
      closeAnimation = dialog.animate([{opacity:1,transform:'none'},{opacity:0,transform:panelKind === 'comments' ? 'translateX(28px)' : 'translateY(6px)'}], {duration:140,easing:'ease-in'});
      const animation = closeAnimation;
      animation.finished.then(() => { if (closeAnimation === animation) { closeAnimation = null; dialog.close(); animation.cancel(); } }).catch(() => {});
    }
    dialog.addEventListener('cancel', event => { event.preventDefault(); if(panelKind==='comments'&&drawerDiscussion.back())return; closeDialog(); });
    dialog.addEventListener('close', () => {
      if (dialog.open) return; // A queued close event may arrive after a rapid reopen.
      if (panelKind && pending.delete(panelKind)) api.post('cancelPlatformExtras', {kind:panelKind});
      saveTarget = null; panelKind = ''; listMutation = null;
      drawerDiscussion.close();
      if (returnFocus?.isConnected) returnFocus.focus({preventScroll:true});
      returnFocus = null; videoBounds(); startDanmaku();
    });
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const box = dialog.getBoundingClientRect();
      if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) closeDialog();
    });
    // Native dialog owns Escape/Tab and text editing; global playback shortcuts must not close
    // the fullscreen surface or start playback behind a modal.
    document.addEventListener('keydown', event => { if (dialog.open) event.stopImmediatePropagation(); }, true);
    const hasCapability = (track, capability) => track?.kind === 'online' && api.capabilities(track).includes(capability);
    const canRequestVideo = track => !!track?.hasMusicVideo && hasCapability(track, 'VideoResolution');
    const canRequestExtras = (track, kind) => hasCapability(track, kind === 'comments' ? 'Comments' : 'MediaExtras');
    function request(kind, pageHandle) {
      if (pending.has(kind)) return;
      const track = api.current();
      if (!canRequestExtras(track, kind)) return;
      const id = ++requestId;
      pending.set(kind, { id, handle: track.handle, pageHandle });
      api.post('requestPlatformExtras', { handle: track.handle, kind, requestId: id, pageHandle });
    }
    function receiveExtras(payload) {
      if (drawerDiscussion.receive(payload) || pageDiscussion.receive(payload)) return;
      if (creatorFeed.receive(payload)) return;
      const current = pending.get(payload?.kind);
      if (!current || current.id !== payload.requestId || current.handle !== payload.handle || api.current()?.handle !== payload.handle) return;
      if (!canRequestExtras(api.current(), payload.kind)) return;
      pending.delete(payload.kind);
      if (payload.error) {
        if (payload.kind === 'danmaku') { danmakuTrack = payload.handle; api.toast(payload.error.message || '此平台暂不提供弹幕'); }
        else if (dialog.open && panelKind === payload.kind) {
          openDialog('分 P', `<p role="status">${esc(payload.error.message || '此平台暂不支持这项功能')}</p><button class="secondary-button" data-media-action="retry-extras">重试</button>`);
        }
        return;
      }
      if (payload.kind === 'danmaku') {
        danmaku = (payload.items || []).filter(d => Number.isFinite(d.seconds) && d.seconds >= 0 && typeof d.text === 'string').slice(0, 10000).sort((a,b) => a.seconds-b.seconds);
        danmakuTrack = payload.handle;
        clearDanmaku(); startDanmaku();
      } else if (payload.kind === 'parts') {
        parts = (payload.items || []).map(t => api.normalize(t, api.current()?.providerId)).filter(Boolean);
        if (dialog.open && panelKind === 'parts') openDialog('分 P', parts.length ? `<div class="media-parts">${parts.map((p,i) => `<button class="media-list-item" data-media-action="part" data-index="${i}"><strong>${esc(p.title)}</strong><span>${api.formatTime(p.durationSeconds)}</span></button>`).join('')}</div>` : '<p>这个视频没有可切换的分 P。</p>');
      }
    }
    function openComments(track, follow = false) {
      if (!canRequestExtras(track, 'comments')) return;
      drawerDiscussion.close();
      panelKind = 'comments'; commentTrack = track; followsPlayback = follow;
      openDialog(t('评论'), '');
      drawerDiscussion.open(dialog.querySelector('.media-dialog-body'),track);
    }
    function save(track) {
      if (!track) return;
      saveTarget = track;
      api.post('requestSavedPlaylists');
      showPicker();
    }
    function showPicker() {
      openDialog('加入我的歌单', `<p>${esc(saveTarget?.title || '')}</p><div class="media-parts">${lists.map(p => `<button class="media-list-item" data-media-action="add" data-id="${esc(p.id)}"><strong>${esc(p.name)}</strong><span>${p.entries.length} 首</span></button>`).join('')}</div><form id="newSavedPlaylistForm"><label for="savedPlaylistName">新建歌单</label><div class="media-form-row"><input id="savedPlaylistName" name="name" maxlength="100" required placeholder="歌单名称"><button class="accent-button" type="submit">创建</button></div></form>`);
      for (const button of dialog.querySelectorAll('button:not([data-media-action="close"])')) button.disabled = !!listMutation;
    }
    function receiveLists(payload) {
      const completedMutation = listMutation && payload.action === listMutation.action && payload.requestId === listMutation.id;
      if (payload?.error) {
        if (completedMutation) { listMutation = null; if (dialog.open && $('#newSavedPlaylistForm')) showPicker(); }
        // Late failures from a closed/replaced form must not interrupt its successor.
        if (completedMutation || !['createSavedPlaylist','addToSavedPlaylist'].includes(payload.action)) api.toast(payload.error.message);
        return;
      }
      if (completedMutation) listMutation = null;
      if (payload.action === 'requestSavedPlaylists') metadataAttempted.clear();
      lists = (payload.items || []).map(p => ({ ...p, entries: (p.entries || []).map(e => ({ ...e, track: e.track.kind === 'online' ? api.normalize(e.track) : { ...e.track, kind: 'local', unavailable: e.track.isPlayable === false } })).filter(e => e.track) }));
      if (completedMutation && payload.action === 'createSavedPlaylist' && lists.length) selected = lists[lists.length-1].id;
      if (saveTarget && completedMutation && payload.action === 'addToSavedPlaylist') { closeDialog(); api.toast('已加入歌单'); }
      else if (dialog.open && $('#newSavedPlaylistForm')) {
        if (completedMutation && payload.action === 'createSavedPlaylist' && !saveTarget) closeDialog();
        else { showPicker(); if (completedMutation && $('#savedPlaylistName')) $('#savedPlaylistName').value = ''; }
      }
      if (state.currentPage === 'savedPlaylists') render();
    }
    function render() {
      const list = lists.find(p => p.id === selected) || lists[0];
      selected = list?.id || '';
      const signature = JSON.stringify(lists);
      if ($('.saved-playlists-page') && signature === lastListSignature && selected === lastRenderedList) { syncSavedRows(); observeMetadata(); return; }
      const previousRows = new Set([...api.content.querySelectorAll('.saved-track-row [data-media-action="remove"]')].map(n => n.dataset.id));
      const changedList = lastRenderedList !== selected;
      const focus = api.content.contains(document.activeElement) ? document.activeElement : null;
      const focusAction = focus?.dataset.mediaAction, focusId = focus?.dataset.id;
      lastListSignature = signature; lastRenderedList = selected;
      api.content.innerHTML = `<div class="page-view saved-playlists-page"><header class="page-header"><div><h1>我的歌单</h1><p>本地文件与多平台曲目 · 在线曲目按平台权限播放</p></div><button class="accent-button" data-media-action="new">新建歌单</button></header><nav class="saved-playlist-tabs" aria-label="我的歌单">${lists.map(p => `<button class="secondary-button ${p.id === selected ? 'active' : ''}" data-media-action="select" data-id="${esc(p.id)}" aria-pressed="${p.id === selected}">${esc(p.name)} · ${p.entries.length}</button>`).join('')}</nav>${list ? `<div class="section-title-row"><h2>${esc(list.name)}</h2><button class="accent-button" data-media-action="saved-play" data-index="0" ${list.entries.some(e => !e.track.unavailable) ? '' : 'disabled'}>播放全部</button></div><div class="saved-track-list">${list.entries.map((e,i) => `<div class="saved-track-row ${e.track.id === state.currentTrackId ? 'current' : ''}"><button class="saved-track-play" data-media-action="saved-play" data-index="${i}" ${e.track.unavailable ? 'disabled' : ''}><span class="queue-cover" style="${api.coverStyle(e.track)}">${e.track.coverUrl ? '' : '♪'}</span><span><strong>${esc(e.track.title)}</strong><small>${esc(e.track.artist)} · ${esc(e.track.kind === 'online' ? api.providerName(e.track.providerId) : '本地')}${e.track.unavailable ? ' · 文件不可用' : ''}</small></span><span>${api.formatTime(Number(e.track.durationSeconds)||0)}</span></button><button class="row-action" data-media-action="remove" data-id="${esc(e.entryId)}" aria-label="从歌单移除">✕</button></div>`).join('') || '<p class="media-empty">从歌曲列表的“加入歌单”按钮添加音乐。</p>'}</div>` : '<p class="media-empty">创建你的第一个歌单，把喜欢的音乐放在一起。</p>'}</div>`;
      if (focusAction) {
        const target = [...api.content.querySelectorAll('[data-media-action]')].find(n => n.dataset.mediaAction === focusAction && n.dataset.id === focusId);
        (target || api.content.querySelector('.saved-playlist-tabs .active') || api.content.querySelector('[data-media-action="new"]'))?.focus({preventScroll:true});
      }
      if (!api.reduced()) [...api.content.querySelectorAll('.saved-track-row')].filter(row => changedList || !previousRows.has(row.querySelector('[data-media-action="remove"]').dataset.id)).slice(0,12).forEach((row,index) => {
        row.animate([{opacity:0,transform:'translateY(5px)'},{opacity:1,transform:'none'}], {duration:200,delay:index*18,easing:'cubic-bezier(.2,.8,.2,1)',fill:'backwards'});
      });
      syncSavedRows();
      observeMetadata();
    }
    function syncSavedRows() {
      const list = lists.find(p => p.id === selected);
      for (const row of api.content.querySelectorAll('.saved-track-row')) {
        const button = row.querySelector('.saved-track-play');
        const current = list?.entries[Number(button.dataset.index)]?.track.id === state.currentTrackId;
        row.classList.toggle('current', current); button.setAttribute('aria-current', current ? 'true' : 'false');
      }
    }
    function setVideo(enabled,qualityHandle=undefined) {
      const track = api.current();
      if ((track?.id || '') !== trackKey) sync();
      if (enabled && !canRequestVideo(track)) return;
      if (enabled && (videoPending || videoEnabled && qualityHandle===undefined)) return;
      const switching=enabled&&videoEnabled;
      automaticVideoQuality=!qualityHandle;
      videoPending = enabled; videoEnabled = false; videoRequest++;
      stopPictures(enabled&&!switching);
      qualitySelect.disabled=enabled;
      if(!enabled)qualityControl.hidden=true;
      $('[data-media-action="video-retry"]').hidden = true;
      if (enabled) {
        videoExit?.cancel(); videoExit = null;
        $('#embeddedVideoPage').hidden = false;
        $('#embeddedVideoPage').classList.add('is-loading');
      } else hideVideoPage();
      $('#embeddedVideoStatus').textContent = '正在加载视频，音频继续播放…';
      api.post('setEmbeddedVideo', { handle: track?.handle, enabled, requestId: videoRequest, ...(qualityHandle?{qualityHandle}:{}) });
      sync(); videoBounds();
      if (enabled&&!switching) $('[data-media-action="audio"]').focus({preventScroll:true});
      else if (!enabled && $('#nowPlayingOverlay').classList.contains('open')) $('#largeCover').focus({preventScroll:true});
    }
    function receiveVideo(payload) {
      if (payload?.requestId !== videoRequest || payload.handle !== api.current()?.handle) return;
      videoPending = false; videoEnabled = payload.enabled === true;
      qualitySelect.replaceChildren();
      const automatic=document.createElement('option');automatic.value='';automatic.textContent=t('自动画质');qualitySelect.append(automatic);
      const qualities=Array.isArray(payload.qualities)?payload.qualities.filter(q=>/^vq-[a-f0-9]{32}$/.test(q?.handle)&&typeof q.label==='string').slice(0,24):[];
      for(const q of qualities){const option=document.createElement('option');option.value=q.handle;option.textContent=q.label;qualitySelect.append(option);}
      const actual=qualities.find(q=>q.handle===payload.selectedQualityHandle);
      if(actual)automatic.textContent=t('自动画质')+' · '+actual.label;
      qualitySelect.value=automaticVideoQuality?'':payload.selectedQualityHandle||'';
      qualityLabel.textContent=t('画质');qualitySelect.setAttribute('aria-label',t('画质'));
      qualityControl.hidden=!qualities.length;qualitySelect.disabled=false;
      $('#embeddedVideoPage').classList.remove('is-loading');
      if (videoEnabled) $('#embeddedVideoPage').hidden = false;
      else if (!payload.error) hideVideoPage();
      $('#embeddedVideoStatus').textContent = payload.error?.message || api.current()?.title || '';
      $('[data-media-action="video-retry"]').hidden = !payload.error;
      if (payload.error) $('#embeddedVideoPage').hidden = false;
      $('.video-awaiting-picture span').textContent = payload.error ? '视频暂不可用，音频继续播放。可重试视频或返回音频。' : '视频画面准备中；暂停时可点击播放继续';
      if (videoEnabled) startPictures(); else stopPictures();
      if (payload.error) api.toast(payload.error.message);
      videoBounds(); sync();
    }
    function hideVideoPage(immediate = false) {
      const page = $('#embeddedVideoPage');
      if (page.hidden || videoExit) return;
      page.classList.remove('is-loading');
      if (immediate || api.reduced()) { page.hidden = true; return; }
      // Retain the last picture for the finite exit animation, without another media player.
      const animation = page.animate([{opacity:1,transform:'none'}, {opacity:0,transform:'translateY(8px) scale(.99)'}],
        {duration:160,easing:'cubic-bezier(.4,0,1,1)',fill:'forwards'});
      videoExit = animation;
      animation.finished.then(() => {
        if (videoExit !== animation) return;
        page.hidden = true; animation.cancel(); videoExit = null;
      }).catch(() => {});
    }
    function videoBounds() {
      cancelAnimationFrame(boundsFrame); boundsFrame = 0;
      // Transforms do not trigger ResizeObserver. Track only the finite transition interval,
      // then publish final geometry; never leave a child HWND at an intermediate rectangle.
      boundsUntil = performance.now() + 700;
      projectVideoBounds();
    }
    function projectVideoBounds() {
      boundsFrame = 0;
      const box = $('#embeddedVideoSurface').getBoundingClientRect();
      const moving = [$('#nowPlayingOverlay'), $('#embeddedVideoPage'), $('#queuePanel')].some(n => n.getAnimations().some(a => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().endTime)));
      const visible = videoEnabled && $('#nowPlayingOverlay').classList.contains('open') && !document.hidden;
      const payload = { handle:api.current()?.handle, requestId:videoRequest, x:Math.round(box.x*100)/100, y:Math.round(box.y*100)/100, width:Math.round(box.width*100)/100, height:Math.round(box.height*100)/100, visible };
      const key = JSON.stringify(payload);
      if (key !== lastBounds) { lastBounds = key; api.post('setEmbeddedVideoBounds', payload); }
      if (moving && videoEnabled && !document.hidden && performance.now() < boundsUntil) boundsFrame = requestAnimationFrame(projectVideoBounds);
    }
    new ResizeObserver(videoBounds).observe($('#embeddedVideoSurface'));
    window.addEventListener('resize', videoBounds);
    document.addEventListener('visibilitychange', () => { if (document.hidden) stopPictures(); else startPictures(); videoBounds(); startDanmaku(); });
    function clearDanmaku() {
      $('#danmakuLayer').replaceChildren(); lastTime = -1;
      const now = api.time();
      let low = 0, high = danmaku.length;
      while (low < high) { const mid = (low+high) >>> 1; if (danmaku[mid].seconds <= now) low = mid+1; else high = mid; }
      danmakuIndex = low;
    }
    function startDanmaku() {
      const layer = $('#danmakuLayer');
      const parent = videoEnabled ? $('#embeddedVideoSurface') : $('#nowPlayingOverlay');
      if (layer.parentElement !== parent) { parent.append(layer); clearDanmaku(); }
      layer.classList.toggle('on-video', videoEnabled);
      const active = showDanmaku && api.capabilities(api.current()).includes('MediaExtras') && $('#nowPlayingOverlay').classList.contains('open') && !document.hidden && !videoPending;
      layer.hidden = !active;
      if (!active || !state.isPlaying || !api.advancing()) { cancelAnimationFrame(frame); frame = 0; return; }
      if (frame) return;
      function tick() {
        frame = 0;
        const now = api.time();
        if (lastTime < 0 || now < lastTime || now-lastTime > 1) clearDanmaku();
        const reduced = api.reduced(), duration = reduced ? 3 : 8;
        // Positions and expiry derive from the media clock, not wall-clock CSS animation.
        // Pausing/stalling freezes existing text; seeking discards old lanes deterministically.
        for (const span of [...layer.children]) {
          const elapsed = now - Number(span.dataset.started);
          if (elapsed >= duration || elapsed < 0) { span.remove(); continue; }
          span.classList.toggle('danmaku-static', reduced);
          span.style.transform = reduced ? 'translateX(-50%)' : `translateX(${layer.clientWidth-(layer.clientWidth+span.offsetWidth)*elapsed/duration}px)`;
        }
        while (danmakuIndex < danmaku.length && danmaku[danmakuIndex].seconds <= now) {
          const d = danmaku[danmakuIndex++];
          if (lastTime < 0 || d.seconds <= lastTime || now-d.seconds > .3 || layer.childElementCount >= 6) continue;
          const span = document.createElement('span'); span.textContent = d.text.slice(0,200);
          const occupied = new Set([...layer.children].map(n => Number(n.dataset.lane)));
          const lane = [0,1,2,3,4,5].find(n => !occupied.has(n));
          span.dataset.started = d.seconds; span.dataset.lane = lane;
          span.style.top = `${lane*34}px`; span.classList.toggle('danmaku-static', reduced);
          span.style.transform = reduced ? 'translateX(-50%)' : `translateX(${layer.clientWidth}px)`;
          layer.append(span);
        }
        lastTime = now;
        if (api.advancing() && !api.reduced()) frame = requestAnimationFrame(tick);
      }
      frame = requestAnimationFrame(tick);
    }
    let pageEntrySignature = '';
    function syncPluginPageEntries() {
      const track=api.current(), entries=pluginPages.entries(track).filter(e=>e.placement==='media');
      const signature=JSON.stringify([track?.handle,document.documentElement.lang,entries]);
      if(signature===pageEntrySignature)return;
      pageEntrySignature=signature;
      document.querySelector('.plugin-page-entries')?.remove();
      const group=document.createElement('span');group.className='plugin-page-entries';
      if(entries.length){
        const button=document.createElement('button');button.className='secondary-button';
        button.textContent=entries.length===1?pluginPages.label(entries[0]):t('插件页面');button.dataset.i18nSkip='';
        button.addEventListener('click',()=>pluginPages.choose(track));group.append(button);
      }
      $('#mediaTools').append(group);
    }
    function sync() {
      pageDiscussion.sync();
      creatorFeed.sync();
      creatorSearch.sync();
      pluginPages.sync();
      syncPluginPageEntries();
      const track = api.current(), open = $('#nowPlayingOverlay').classList.contains('open');
      if ((track?.id || '') !== trackKey) {
        trackKey = track?.id || '';
        for (const kind of ['parts', 'danmaku']) { pending.delete(kind); api.post('cancelPlatformExtras', {kind}); }
        danmaku = []; danmakuTrack = ''; parts = []; clearDanmaku();
        const followComments = dialog.open && !closeAnimation && panelKind === 'comments' && followsPlayback;
        if (dialog.open && panelKind !== 'comments') closeDialog(true);
        videoExit?.cancel(); videoExit = null;
        videoEnabled = videoPending = false; videoRequest++; hideVideoPage(true);
        qualityControl.hidden=true;qualitySelect.replaceChildren();
        stopPictures(true);
        $('#embeddedVideoStatus').textContent = '';
        videoBounds();
        if (followComments) {
          if (canRequestExtras(track, 'comments')) openComments(track, true);
          else closeDialog(true);
        }
      }
      // Configuration can revoke a capability while the same track remains audible. Retire
      // only its extension requests/UI; an already active audio/video lease is not stopped.
      for (const kind of ['parts', 'danmaku']) {
        if (!canRequestExtras(track, kind) && pending.delete(kind))
          api.post('cancelPlatformExtras', {kind});
      }
      const extrasAvailable = hasCapability(track, 'MediaExtras'), videoAvailable = canRequestVideo(track);
      qualitySelect.disabled=videoPending||!videoAvailable;
      if (!extrasAvailable) { parts = []; danmaku = []; danmakuTrack = ''; clearDanmaku(); }
      drawerDiscussion.sync();
      if (dialog.open && ((panelKind === 'comments' && !canRequestExtras(commentTrack, 'comments')) ||
          (panelKind === 'parts' && !extrasAvailable) ||
          (dialog.querySelector('#coverVideoOption') && !videoAvailable) ||
          (dialog.querySelector('#danmakuOption') && !extrasAvailable))) closeDialog(true);
      if (videoPending && !videoAvailable) { setVideo(false); return; }
      $('#mediaTools').hidden = track?.kind !== 'online';
      $('#nowPlayingOverlay').classList.toggle('has-media-tools', track?.kind === 'online');
      $('[data-media-action="parts"]').hidden = !api.capabilities(track).includes('MediaExtras');
      $('[data-media-action="comments"]').hidden = !api.capabilities(track).includes('Comments');
      $('#mediaTools [data-media-action="creator"]').hidden = !creatorAvailable(track);
      for(const artist of [$('#stageArtist'),$('#largeArtist')].filter(Boolean)) {
        const enabled=creatorAvailable(track);
        artist.classList.toggle('creator-display-link',enabled);artist.tabIndex=enabled?0:-1;
        if(enabled){artist.setAttribute('role','button');artist.dataset.mediaAction='creator';artist.title=t('作者主页');}
        else{artist.removeAttribute('role');delete artist.dataset.mediaAction;artist.removeAttribute('title');}
      }
      $('[data-media-action="options"]').hidden = !videoAvailable && !extrasAvailable;
      if (!videoAvailable) $('[data-media-action="video-retry"]').hidden = true;
      const cover = $('#largeCover');
      const clickable = coverVideo && videoAvailable;
      cover.setAttribute('role', clickable ? 'button' : 'img'); cover.tabIndex = clickable ? 0 : -1;
      cover.setAttribute('aria-label', clickable ? '在全屏播放器内播放视频' : '专辑封面');
      cover.classList.toggle('cover-video-enabled', !!clickable);
      syncSavedRows();
      if (lastOverlayOpen !== open) { lastOverlayOpen = open; videoBounds(); }
      if (!open && (videoEnabled || videoPending)) setVideo(false);
      if (open && showDanmaku && api.capabilities(track).includes('MediaExtras') && danmakuTrack !== track.handle && !pending.has('danmaku')) request('danmaku');
      startDanmaku();
    }
    $('#largeCover').addEventListener('click', () => { if (coverVideo) setVideo(true); });
    for(const artist of [$('#stageArtist'),$('#largeArtist')].filter(Boolean)) artist.addEventListener('keydown',e=>{
      if(artist.dataset.mediaAction==='creator' && (e.key==='Enter'||e.key===' ')){e.preventDefault();e.stopPropagation();openCreator(api.current());}
    });
    $('#largeCover').addEventListener('keydown', e => { if (coverVideo && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); e.stopPropagation(); setVideo(true); } });
    function mutateList(action, payload) {
      listMutation = {action, id:++requestId};
      api.post(action, {...payload, requestId:listMutation.id}); showPicker();
    }
    document.addEventListener('submit', e => {
      if (e.target.id !== 'newSavedPlaylistForm') return;
      e.preventDefault(); if (listMutation) return;
      mutateList('createSavedPlaylist', { name: new FormData(e.target).get('name') });
    });
    document.addEventListener('change', e => {
      if (e.target.id === 'coverVideoOption') { coverVideo = e.target.checked; localStorage.setItem('auralis:cover-video', coverVideo); if (!coverVideo && (videoEnabled || videoPending)) setVideo(false); sync(); }
      if (e.target.id === 'danmakuOption') { showDanmaku = e.target.checked; localStorage.setItem('auralis:danmaku', showDanmaku); clearDanmaku(); sync(); }
    });
    document.addEventListener('click', e => {
      const button = e.target.closest('[data-media-action]'); if (!button || button.disabled) return;
      e.preventDefault(); e.stopImmediatePropagation();
      const action = button.dataset.mediaAction;
      if (action === 'creator' || action === 'track-creator') openCreator(action==='creator' ? api.current() : api.find(button.dataset.id));
      if (action === 'close') closeDialog();
      if (action === 'save-current') save(api.current());
      if (action === 'save') save(api.find(button.dataset.id));
      if (action === 'add' && saveTarget && !listMutation) mutateList('addToSavedPlaylist', { playlistId: button.dataset.id, ...(saveTarget.kind === 'online' ? { handle: saveTarget.handle } : { localId: saveTarget.id }) });
      if (action === 'new') { saveTarget = null; showPicker(); }
      if (action === 'select') { selected = button.dataset.id; render(); }
      if (action === 'remove') api.post('removeFromSavedPlaylist', { playlistId: selected, entryId: button.dataset.id });
      if (action === 'saved-play') {
        const list = lists.find(p => p.id === selected); const item = list?.entries[Number(button.dataset.index)]?.track;
        const queue = list?.entries.map(e => e.track).filter(t => !t.unavailable) || [];
        if (item && !item.unavailable) api.play(item, queue); else if (queue[0]) api.play(queue[0], queue);
      }
      if (action === 'mixed-queue') { const track = state.mixedQueue.find(t => t.id === button.dataset.id); if (track) api.play(track, state.mixedQueue); }
      if (action === 'parts' || action === 'comments' || action === 'track-comments') {
        if (action !== 'parts') {
          const track = action === 'track-comments' ? api.find(button.dataset.id) : api.current();
          if (track?.kind === 'online') openComments(track, action === 'comments');
          return;
        }
        if (!canRequestExtras(api.current(), 'parts')) return;
        panelKind = 'parts';
        openDialog('分 P', '<p role="status">正在读取…</p>'); request(panelKind);
      }
      if (action === 'retry-extras') { button.disabled = true; request(panelKind); }
      if (action === 'part') { const item = parts[Number(button.dataset.index)]; if (item) { closeDialog(true); api.play(item, parts); } }
      if (action === 'audio') setVideo(false);
      if (action === 'video-retry') setVideo(true);
      if (action === 'options') {
        const track = api.current(), capabilities = api.capabilities(track);
        const rows = (canRequestVideo(track) ? `<label class="media-option"><span><strong>封面点击切换视频</strong><small>内嵌在全屏播放器中</small></span><input id="coverVideoOption" role="switch" type="checkbox" ${coverVideo ? 'checked' : ''}></label>` : '')
          + (capabilities.includes('MediaExtras') ? `<label class="media-option"><span><strong>弹幕（音频与视频）</strong><small>随播放进度显示；减少动态效果时使用静态文字</small></span><input id="danmakuOption" role="switch" type="checkbox" ${showDanmaku ? 'checked' : ''}></label>` : '');
        if (!rows) return;
        panelKind = ''; openDialog('播放扩展', rows); startDanmaku();
      }
    }, true);
    return { render, sync, renderCreator:creatorFeed.render, renderPluginPage:pluginPages.render, renderCreatorSearch:creatorSearch.mount, receiveCreatorSearch:creatorSearch.receive, receiveLists, receiveExtras, receivePage:pluginPages.receive, receiveVideo, save, setVideo, videoBounds,
      clockUpdated: reset => { if (reset) clearDanmaku(); startDanmaku(); },
      refresh: () => api.post('requestSavedPlaylists'),
      ensureTrack, receiveTrack, allTracks: () => lists.flatMap(p => p.entries.map(e => e.track)), parts: () => parts };
  }
  return { create };
})();
