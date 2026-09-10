// Presentation sandbox: app preferences and native messages never leave this frame.
(() => {
  'use strict';
  function memoryStorage(initial = {}) {
    const values = new Map(Object.entries(initial));
    return {getItem:key=>values.get(String(key))??null, setItem:(key,value)=>values.set(String(key),String(value)), removeItem:key=>values.delete(String(key)), clear:()=>values.clear(), key:index=>[...values.keys()][index]??null, get length(){return values.size;}};
  }
  const customization=location.pathname.endsWith('/customize.html');
  Object.defineProperty(window,'localStorage',{value:memoryStorage({
    'auralis:theme':customization?'light':'dark','auralis:player-style':'record','auralis:motion':customization?'reduced':'system',
    'auralis:fullscreen-background':customization?'static':'dynamic',
    'auralis:accent':'blue','auralis:immersive-fade':'false','auralis:online-lyrics-enabled':'false'
  })});
  Object.defineProperty(window,'sessionStorage',{value:memoryStorage()});
  const chrome = window.chrome || {};
  chrome.webview = Object.freeze({postMessage() { /* No native host, media, account or file access. */ }});
  if (!window.chrome) window.chrome = chrome;
})();
