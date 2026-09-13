(() => {
  'use strict';
  const kinds = {install:'首次安装',update:'更新插件',recovery:'重新导入旧版',reinstall:'重新导入相同版本',unknown:'旧版本信息不可用'};
  const groups = {providers:'提供方标识',capabilities:'声明能力',pages:'页面入口',settings:'设置项',features:'必需宿主功能',credentials:'旧账号兼容访问',settingAliases:'旧设置兼容访问',artworkDomains:'图片域名范围'};
  const normalize = value => {
    if (!value || !Object.hasOwn(kinds, value.kind)) return null;
    const text = x => typeof x === 'string' ? x.slice(0,512) : '';
    return {kind:value.kind,previousVersion:text(value.previousVersion),previousPayloadSha256:text(value.previousPayloadSha256),
      previousVerified:value.previousVerified === true,hostSdkVersion:text(value.hostSdkVersion),accessReviewRequired:value.accessReviewRequired === true,
      changes:(Array.isArray(value.changes)?value.changes:[]).filter(c=>c && Object.hasOwn(groups,c.kind)).slice(0,8).map(c=>({kind:c.kind,
        added:(Array.isArray(c.added)?c.added:[]).slice(0,512).map(text),removed:(Array.isArray(c.removed)?c.removed:[]).slice(0,512).map(text)}))};
  };
  function markup(review, {t,escapeHtml:e,candidateVersion}) {
    if (!review) return '';
    const label = text => e(t(text));
    return `<section class="plugin-update-review" aria-label="${label('版本与声明变化')}">
      <h4>${label(kinds[review.kind])}</h4>
      <dl><div><dt>${label('当前选择版本')}</dt><dd data-i18n-skip>${e(review.previousVersion || '—')}</dd></div>
      <div><dt>${label('待导入版本')}</dt><dd data-i18n-skip>${e(candidateVersion || '—')}</dd></div>
      <div><dt>${label('当前播放器 SDK')}</dt><dd data-i18n-skip>${e(review.hostSdkVersion)}</dd></div></dl>
      <p>${label('清单兼容检查通过；尚未测试登录、网络或播放功能。')}</p>
      ${review.kind === 'recovery' ? `<p class="plugin-review-warning">${label('将重新导入所选旧版包。不会自动恢复账号或设置，也不会热替换运行中的插件。')}</p>` : ''}
      ${review.kind !== 'install' && !review.previousVerified ? `<p class="plugin-review-warning">${label('旧版本未通过完整性确认；差异仅供参考，不能作为已验证的恢复基线。')}</p>` : ''}
      ${review.accessReviewRequired ? `<p class="plugin-review-warning">${label('访问声明发生变化，请核对以下新增与移除范围后再确认信任。')}</p>` : ''}
      <details class="plugin-review-differences" ${review.accessReviewRequired?'open':''}><summary>${label('版本与声明变化')}</summary>
        ${review.changes.length ? review.changes.map(c=>`<div class="plugin-review-change"><h5>${label(groups[c.kind])}</h5>${['added','removed'].map(key=>c[key].length?`<div><strong>${label(key==='added'?'新增':'移除')}</strong><ul>${c[key].map(s=>`<li data-i18n-skip>${e(s)}</li>`).join('')}</ul></div>`:'').join('')}</div>`).join('') : `<p>${label('未发现所列声明的增删；仍需重新确认信任。')}</p>`}
        <p>${label('这里只比较声明标识和类型，不代表全部代码、权限或设置语义的差异。收藏引用会保留，不自动迁移或删除。')}</p>
        ${review.previousPayloadSha256 ? `<p>${label('旧版内容摘要（不是压缩包摘要）')}</p><code class="plugin-package-hash" data-i18n-skip>${e(review.previousPayloadSha256)}</code>` : ''}
      </details>
      <p>${label('确认后默认关闭；请明确启用并重启。正在播放的会话不会在导入时切换。')}</p>
    </section>`;
  }
  window.AuralisPluginUpdateReview = Object.freeze({normalize,markup});
})();
