(() => {
  'use strict';
  const state = { requestId: 0, operation: '', pending: false, loaded: false, inventory: null, preview: null,
    trusted: false, error: '', notice: '', timer: null, leaving: false, focus: '' };
  let host = null, env = null, observer = null;
  const busy = () => state.pending || !!state.preview;
  const blocked = () => !!env?.isBlocked?.();
  const escape = value => env.escapeHtml(String(value ?? ''));
  const text = value => escape(env.t(value));
  const disabled = value => value ? 'disabled' : '';
  function render() {
    if (!host?.isConnected || !env) return;
    const s = state, data = s.inventory, preview = s.preview;
    const locked = s.pending || !!preview || blocked(), invalid = !!data?.stateError;
    host.setAttribute('aria-busy', String(s.pending));
    host.innerHTML = `<div class="settings-card-header"><div><h2>${text('播放组件')}</h2><p>${text('选择音视频解码组件；更改在重启后生效。')}</p></div>
      <div class="plugin-row-actions"><button type="button" class="secondary-button" data-pb-action="list" data-pb-focus="refresh" ${disabled(s.pending)}>${text('刷新')}</button>
      <button type="button" class="accent-button" data-pb-action="pick" data-pb-focus="pick" ${disabled(locked || invalid)}>${text('导入播放组件')}</button></div></div>
      <p class="plugin-inventory-status" role="status">${s.pending ? text('正在处理播放组件…') : text('支持 .auralis-playback.zip；导入后默认关闭，不影响当前播放。')}</p>
      ${s.error || invalid ? `<p class="plugin-inventory-status" role="alert">${text(s.error || '安装记录不可用，请检查文件权限并刷新。')}</p>` : ''}
      ${s.notice ? `<p class="plugin-inventory-status" role="status">${text(s.notice)}</p>` : ''}
      ${data ? `<div class="setting-row"><div class="setting-label"><strong>${text('当前播放组件')}</strong><span data-i18n-skip>${escape(data.current?.displayName)} · ${escape(data.current?.version)}</span></div><span class="plugin-state">${text(data.current?.bundled ? '随包默认' : '外置组件')}</span></div>
      ${data.current?.fellBack ? `<p class="plugin-inventory-status" role="status">${text('外置组件不可用，已回退随包默认。')}</p>` : ''}
      <div class="setting-row"><div class="setting-label"><strong>${text('使用随包默认')}</strong><span>${text('保留离线基础播放，不需要导入组件。')}</span></div><button type="button" class="secondary-button" data-pb-action="default" data-pb-focus="default" ${disabled(locked || invalid || data.selectedId === null)}>${text(data.selectedId === null ? '已选择' : '设为下次启动')}</button></div>
      ${data.items.length ? `<ul class="plugin-inventory-list">${data.items.map(item => `<li class="setting-row plugin-inventory-row"><div class="setting-label"><strong data-i18n-skip>${escape(item.displayName)}</strong><small data-i18n-skip>${escape(item.id)} · ${escape(item.version)}</small>${!item.available ? `<span>${text('文件不可用或组件不兼容')}</span>` : ''}</div><div class="plugin-row-actions"><button type="button" class="secondary-button" data-pb-action="select" data-pb-id="${escape(item.id)}" data-pb-focus="select-${escape(item.id)}" ${disabled(locked || invalid || !item.enabled || !item.available || item.selected)}>${text(item.selected ? '已选择' : '设为下次启动')}</button><button type="button" class="switch ${item.enabled ? 'on' : ''}" role="switch" aria-label="${text('启用播放组件')} ${escape(item.displayName)}" aria-checked="${!!item.enabled}" data-pb-action="enable" data-pb-id="${escape(item.id)}" data-pb-focus="enable-${escape(item.id)}" ${disabled(locked || invalid || (!item.enabled && !item.available))}></button></div></li>`).join('')}</ul>` : `<div class="plugin-inventory-empty"><strong>${text('尚未导入播放组件')}</strong><p>${text('保留离线基础播放，不需要导入组件。')}</p></div>`}
      ${data.restartRequired ? `<div class="plugin-import-actions"><span>${text('更改已保存，重启后使用新的播放组件。当前播放会停止。')}</span><button type="button" class="accent-button" data-pb-action="restart" ${disabled(locked)}>${text('立即重启')}</button></div>` : ''}` : ''}
      ${preview ? `<section class="plugin-import-review" aria-label="${text('确认导入播放组件')}"><h3>${text('确认导入播放组件')}</h3><p data-i18n-skip>${escape(preview.displayName)} · ${escape(preview.version)}</p><p data-i18n-skip>${escape(preview.id)}</p>
      <details><summary>${text('查看声明能力和 SHA-256')}</summary><p data-i18n-skip>${escape(preview.capabilities)} · ${escape(preview.fileCount)} · ${escape((Number(preview.payloadBytes || 0) / 1048576).toFixed(1))} MiB</p><code class="plugin-package-hash">${escape(preview.manifestSha256)}</code><code class="plugin-package-hash">${escape(preview.archiveSha256)}</code></details>
      <label class="plugin-trust-choice"><input type="checkbox" data-pb-trust ${s.trusted ? 'checked' : ''} ${disabled(s.pending)}><span>${text('我信任此播放组件的来源，了解它将在播放器进程内运行，不是安全沙箱。')}</span></label>
      <div class="plugin-import-actions"><button type="button" class="secondary-button" data-pb-action="cancel" ${disabled(s.pending)}>${text('取消')}</button><button type="button" class="accent-button" data-pb-action="confirm" ${disabled(s.pending || !s.trusted || !/^[a-f0-9]{32}$/.test(preview.token || ''))}>${text('确认导入')}</button></div></section>` : ''}`;
    if (needsAttention()) env.onAttention?.();
    if (!s.pending && s.focus) {
      [...host.querySelectorAll('[data-pb-focus]')].find(node => node.dataset.pbFocus === s.focus)?.focus({preventScroll:true});
      s.focus = '';
    }
  }
  function request(operation, extra = {}) {
    if (!env || state.pending) return;
    if (blocked() && !['cancel', 'list'].includes(operation)) { state.error = '请先完成或取消当前插件导入。'; render(); return; }
    state.pending = true; state.operation = operation; state.error = ''; state.notice = '';
    const requestId = ++state.requestId;
    clearTimeout(state.timer);
    if (operation !== 'pick') state.timer = setTimeout(() => {
      if (state.pending && state.requestId === requestId) {
        state.pending = false; state.error = '操作未确认，请刷新状态后重试。'; render();
      }
    }, 55000);
    render();
    try { env.nativePost('managePlaybackComponents', { requestId, operation, ...extra }); }
    catch { clearTimeout(state.timer); state.pending = false; state.error = '操作未确认，请刷新状态后重试。'; render(); }
  }
  function receive(payload) {
    if (!state.pending || payload?.requestId !== state.requestId || payload.operation !== state.operation) return;
    clearTimeout(state.timer); state.pending = false;
    if (payload.inventory) {
      state.loaded = true;
      state.inventory = { ...payload.inventory, items: (Array.isArray(payload.inventory.items) ? payload.inventory.items : []).slice(0, 64) };
    }
    if (Object.hasOwn(payload, 'preview')) {
      if (state.preview?.token !== payload.preview?.token) state.trusted = false;
      state.preview = payload.preview || null;
    }
    if (payload.error) state.error = payload.error === 'busy' ? '请先完成或取消当前插件导入。' : '操作未完成或结果未确认。请检查包格式、完整性、兼容性和文件权限，并刷新状态。';
    else if (['confirm', 'enable', 'select'].includes(payload.operation)) state.notice = '设置已保存，重启后生效。';
    render();
    if (state.leaving && state.preview && payload.operation !== 'cancel') request('cancel');
    else if (!payload.error && payload.operation === 'pick' && state.preview) host?.querySelector('[data-pb-trust]')?.focus();
  }
  function leave() {
    if (state.leaving) return;
    state.leaving = true;
    if (state.preview && !state.pending) request('cancel');
  }
  function mount(element, context) {
    host = element; env = context; state.leaving = false;
    host.onclick = event => {
      const button = event.target.closest('[data-pb-action]');
      if (!button || button.disabled || state.pending) return;
      const action = button.dataset.pbAction, id = button.dataset.pbId;
      state.focus = button.dataset.pbFocus || '';
      if (action === 'restart') { if (!busy() && !blocked()) env.restart(); return; }
      if (action === 'confirm') { if (state.trusted && state.preview) request('confirm', {token:state.preview.token,trust:true}); return; }
      if (action === 'enable') { const item = state.inventory?.items.find(row => row.id === id); if (item) request('enable', {id, enabled:!item.enabled}); return; }
      if (action === 'default' || action === 'select') { request('select', {id:action === 'default' ? null : id}); return; }
      request(action);
    };
    host.onchange = event => {
      if (event.target.matches('[data-pb-trust]')) {
        state.trusted = event.target.checked;
        const confirm = host.querySelector('[data-pb-action="confirm"]');
        if (confirm) confirm.disabled = !state.trusted || state.pending || !/^[a-f0-9]{32}$/.test(state.preview?.token || '');
      }
    };
    if (!observer && document.getElementById('pageContent')) {
      observer = new MutationObserver(() => { if (host && !host.isConnected) leave(); });
      observer.observe(document.getElementById('pageContent'), {childList:true});
    }
    render();
    if (!state.loaded && !state.pending && !state.error) request('list');
  }
  window.addEventListener('beforeunload', leave);
  const needsAttention = () => !!(state.preview || state.error || state.inventory?.stateError || state.inventory?.restartRequired || state.inventory?.current?.fellBack);
  window.AuralisPlaybackComponents = Object.freeze({ mount, receive, leave, refresh:render, isBusy:busy, needsAttention });
})();
