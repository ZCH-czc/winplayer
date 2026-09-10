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
