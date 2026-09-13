'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const source=require('node:fs').readFileSync(require.resolve('../Auralis/wwwroot/app.js'),'utf8');
const begin=source.indexOf('  let nextPlaybackPlan = null;');
const end=source.indexOf('  function playPrevious()',begin);
function fixture(options={}) {
  const messages=[], played=[];
  const queue=[{id:'a',kind:'online',handle:'a'}, {id:'b',kind:'online',handle:'b'}, {id:'c',kind:'online',handle:'c'}];
  const state={currentTrackId:'a',currentIndex:0,currentTime:3,isPlaying:true,shuffle:false,repeat:'all',queueKind:'online',platformPlayback:{pendingHandle:''},...options};
  const context=vm.createContext({state,activePlaybackQueue:()=>queue,nativePost:(action,payload)=>messages.push({action,...payload}),
    updateProgress:()=>{},syncPlaybackUi:()=>{},playPlatformQueueItemById:id=>played.push(id),playTrackById:id=>played.push(id),playMixedItem:item=>played.push(item.id)});
  vm.runInContext(source.slice(begin,end),context);
  return {state,queue,messages,played,run:code=>vm.runInContext(code,context)};
}
test('one next-track request, deferred until startup has advanced',()=>{
  const f=fixture({currentTime:.5}); f.run('syncNextPrefetch()'); assert.equal(f.messages.length,0);
  f.state.currentTime=3;f.run('syncNextPrefetch();syncNextPrefetch()');
  assert.equal(f.messages.length,1);assert.equal(f.messages[0].handle,'b');
  f.run('playNext(1,true)');assert.deepEqual(f.played,['b']);
});
test('shuffle consumes the same choice that was prefetched',()=>{
  const f=fixture({shuffle:true});f.run('syncNextPrefetch();playNext(1,true)');
  assert.notEqual(f.messages[0].handle,'a');assert.equal(f.played[0],f.messages[0].handle);
});
test('repeat one reopens the decoder without seeking an ended input',()=>{
  const f=fixture();f.run('syncNextPrefetch()');f.state.repeat='one';f.run('syncNextPrefetch();playNext(1,true)');
  assert.equal(f.messages[1].handle,'');assert.equal(f.messages[2].action,'restartPlayback');assert.equal(f.messages[2].id,'a');
  assert.equal(f.state.currentTime,0);assert.equal(f.played.length,0);
  assert(!f.messages.some(m=>m.action==='seekPlayback'||m.action==='resumePlayback'));
});
test('repeat off at end stops; explicit next can wrap',()=>{
  const f=fixture({currentTrackId:'c',currentIndex:2,repeat:'off'});f.run('syncNextPrefetch();playNext(1,true)');
  assert.equal(f.messages[0].handle,'');assert.equal(f.state.isPlaying,false);assert.equal(f.played.length,0);
  f.run('playNext(1,false)');assert.deepEqual(f.played,['a']);
});
test('queue edits replace speculative target; local/unavailable next never invokes provider',()=>{
  const f=fixture();f.run('syncNextPrefetch()');f.queue.splice(1,1);f.run('syncNextPrefetch()');
  assert.deepEqual(f.messages.map(m=>m.handle),['b','c']);
  f.queue[1].unavailable=true;f.run('syncNextPrefetch()');assert.equal(f.messages.at(-1).handle,'');
  f.queue[1]={id:'local',kind:'local'};f.run('syncNextPrefetch()');assert.equal(f.messages.length,3);
});
test('pending replacement suppresses prefetch and auto-advance',()=>{
  const f=fixture({platformPlayback:{pendingHandle:'c'}});f.run('syncNextPrefetch();playNext(1,true)');
  assert.equal(f.messages.length,0);assert.equal(f.played.length,0);
});

test('pause or pending replacement cancels an existing speculative target',()=>{
  for(const change of [f=>f.state.isPlaying=false,f=>f.state.platformPlayback.pendingHandle='c']){
    const f=fixture();f.run('syncNextPrefetch()');change(f);f.run('syncNextPrefetch();syncNextPrefetch()');
    assert.deepEqual(f.messages.map(m=>m.handle),['b','']);
    f.state.isPlaying=true;f.state.platformPlayback.pendingHandle='';f.run('syncNextPrefetch()');
    assert.equal(f.messages.at(-1).handle,'b');
  }
});

test('decoder ended or matching next request preserves prepared successor',()=>{
  const f=fixture();f.run('syncNextPrefetch()');
  f.state.duration=120;f.state.currentTime=120;f.state.isPlaying=false;f.run('syncNextPrefetch()');
  f.state.platformPlayback.pendingHandle='b';f.run('syncNextPrefetch()');
  assert.deepEqual(f.messages.map(m=>m.handle),['b']);
});
const selection=source.slice(source.indexOf('  function selectPageMedia('),source.indexOf('  function playMixedItem('));
function pageSelection(options={}) {
  const local={id:'local',kind:'local'},work={id:'work',handle:'work',kind:'online'},calls=[],toasts=[];
  const state={tracks:[local],currentTrackId:'local',currentIndex:0,currentTime:42,isPlaying:true,queueKind:'local',
    mixedQueue:[],onlineQueue:[],platformPlayback:{pendingHandle:'',pendingQueue:[]},...options};
  const context=vm.createContext({state,currentPlatformCapabilities:()=>['StreamResolution'],
    activePlaybackQueue:()=>state.queueKind==='mixed'?state.mixedQueue:state.queueKind==='online'?state.onlineQueue:state.tracks,
    requestPlatformPlayback:(...args)=>calls.push(args),renderQueue:()=>{},syncNextPrefetch:()=>{},
    showToast:text=>toasts.push(text),work});
  vm.runInContext(selection,context);
  return {state,calls,toasts,run:code=>vm.runInContext(code,context)};
}
test('page enqueue preserves local playback, appends once and never starts a lease',()=>{
  const f=pageSelection();f.run('selectPageMedia(work);selectPageMedia(work)');
  assert.equal(f.state.currentTrackId,'local');assert.equal(f.state.currentTime,42);assert.equal(f.state.isPlaying,true);
  assert.deepEqual(Array.from(f.state.mixedQueue,t=>t.id),['local','work']);assert.equal(f.calls.length,0);
  assert.equal(f.state.tracks.length,1,'online media never enters local library');
});
test('page enqueue with empty playback does not implicitly enqueue local library',()=>{
  const f=pageSelection({currentTrackId:'',isPlaying:false});f.run('selectPageMedia(work)');
  assert.deepEqual(Array.from(f.state.mixedQueue,t=>t.id),['work']);assert.equal(f.calls.length,0);assert.equal(f.state.currentTrackId,'');
});
test('page play uses existing pending playback commit, failure cannot replace current queue',()=>{
  const f=pageSelection();f.run('selectPageMedia(work,true)');
  assert.equal(f.calls.length,1);assert.equal(f.calls[0][2],'mixed');assert.equal(f.state.queueKind,'local');
  assert.equal(f.state.currentTrackId,'local');assert.equal(f.state.currentTime,42);
});
test('enqueue survives an in-flight replacement and unavailable media stays inert',()=>{
  const f=pageSelection({platformPlayback:{pendingHandle:'next',pendingQueue:[{id:'next',kind:'online'}]}});
  f.run('selectPageMedia(work);selectPageMedia({...work,id:"blocked",unavailable:true})');
  assert.deepEqual(Array.from(f.state.platformPlayback.pendingQueue,t=>t.id),['next','work']);
  assert.equal(f.state.platformPlayback.pendingQueueKind,'mixed');assert.equal(f.state.mixedQueue.length,2);assert.equal(f.calls.length,0);
});
