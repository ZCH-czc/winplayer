'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync(require.resolve('../Auralis/wwwroot/app.js'), 'utf8');
const start = source.indexOf('  function renderOnlinePlaylists()');
const end = source.indexOf('\n  function ', start + 1);

function render(previous, next, reduced = false, populated = true) {
  const animations = [];
  let focused = false;
  const pageContent = {
    innerHTML: '',
    querySelector(selector) {
      if (selector === '.online-playlists-page') return { dataset: { provider: previous } };
      if (selector === '.online-provider-content') return { animate: (frames, options) => animations.push({ frames, options }) };
      if (selector.startsWith('[data-online-provider-filter=')) return { focus() { focused = true; } };
      return null;
    },
    querySelectorAll() { return []; }
  };
  const providers = [{ id: 'tx', name: 'QQ', authenticationKey: 'txAuth', playlistsKey: 'txList', errorKey: 'txError', capabilities:['Authentication'] },
    { id: 'bili', name: 'Bilibili', authenticationKey: 'biliAuth', playlistsKey: 'biliList', errorKey: 'biliError', capabilities:['Authentication'] }];
  const context = vm.createContext({
    pageContent, onlinePlaylistProviders: providers, onlinePlaylistReturnFocus: '',
    publicPlaylistSourceMarkup: () => '<section></section>',
    state: { onlinePlaylistProvider: next, platformConfiguration: { loaded: true,
      txAuth: { status: 'signedin' }, biliAuth: { status: 'signedin' },
      txList: populated ? [{ handle: 'one', title: 'test' }] : [], biliList: [] } },
    document: { activeElement: { dataset: { onlineProviderFilter: next } } },
    t: x => x, escapeHtml: x => x, icon: () => '', isMotionReduced: () => reduced,
    canReadOnlineCollections: () => true,
    onlinePlaylistProviderMarkup: () => '<section></section>', onlinePlaylistCardMarkup: () => '<button></button>'
  });
  vm.runInContext(source.slice(start, end) + '\nrenderOnlinePlaylists();', context);
  return { animations, focused, html: pageContent.innerHTML };
}

test('platform content switches forward and backward while keeping filter focus', () => {
  const forward = render('tx', 'bili');
  assert.equal(forward.animations.length, 1);
  assert.equal(forward.animations[0].frames[0].transform, 'translateX(12px)');
  assert.equal(forward.animations[0].options.duration, 240);
  assert.equal(forward.focused, true);
  assert.equal(render('bili', 'tx').animations[0].frames[0].transform, 'translateX(-12px)');
});
test('empty platforms still transition, same-platform refresh and reduced motion do not', () => {
  assert.equal(render('tx', 'bili', false, false).animations.length, 1);
  assert.equal(render('bili', 'bili').animations.length, 0);
  assert.equal(render('tx', 'bili', true).animations.length, 0);
  assert.equal(render(undefined, 'all').animations.length, 0);
});
