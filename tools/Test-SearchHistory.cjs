const {test} = require('node:test');
const assert = require('node:assert/strict');
const {create} = require('../Auralis/wwwroot/search-history.js');
const vm = require('node:vm');
const source = require('node:fs').readFileSync(require.resolve('../Auralis/wwwroot/app.js'), 'utf8');
function searchFixture() {
  const timers = new Map(), messages = [], renders = [];
  let sequence = 0;
  const context = vm.createContext({
    state: {query:'Example song', currentPage:'search', platformConfiguration:{loaded:true},
      platformSearch:{providerId:'arbitrary.source', requestId:0, pendingRequestId:0, pending:false, items:[], pages:[], pageSize:50}},
    platformProviders:{'arbitrary.source':'Example'}, platformSearchDebounceTimer:0, platformSearchRetryTimer:0,
    platformProviderNeedsConfiguration:()=>false, requestPlatformConfiguration:()=>{},
    nativePost:(action,payload)=>messages.push({action,...payload}),
    setTimeout:callback=>{timers.set(++sequence,callback);return sequence;},
    clearTimeout:id=>timers.delete(id),
    renderSearch:()=>renders.push(context.state.platformSearch.pending)
  });
  for (const name of ['cancelPlatformSearch','schedulePlatformSearch']) {
    const start=source.indexOf(`  function ${name}(`), end=source.indexOf('\n  function ',start+1);
    assert(start>=0&&end>start); vm.runInContext(source.slice(start,end),context);
  }
  return {context,messages,renders,flush(){const pending=[...timers.values()];timers.clear();pending.forEach(f=>f());}};
}
test('search marks debounce as loading, but sends only a dispatched request ID',()=>{
  const f=searchFixture();f.context.schedulePlatformSearch();
  assert.equal(f.context.state.platformSearch.pending,true);
  assert.deepEqual(f.renders,[true]);assert.equal(f.messages.length,0);
  assert.equal(f.context.state.platformSearch.pendingRequestId,0);
  f.flush();assert.equal(f.messages.length,1);assert.equal(f.messages[0].action,'platformSearchTracks');
  assert.equal(f.messages[0].providerId,'arbitrary.source');
  assert.equal(f.context.state.platformSearch.pendingRequestId,f.messages[0].requestId);
});
test('typing replaces scheduled query and cancelling debounce sends no fake cancellation',()=>{
  const f=searchFixture();f.context.schedulePlatformSearch();
  f.context.state.query='Replacement';f.context.schedulePlatformSearch();f.flush();
  assert.equal(f.messages.length,1);assert.equal(f.messages[0].query,'Replacement');
  f.context.cancelPlatformSearch(true);assert.equal(f.messages[1].action,'cancelPlatformSearch');
  f.context.schedulePlatformSearch();f.context.cancelPlatformSearch(true);f.flush();
  assert.equal(f.messages.length,2);assert.equal(f.context.state.platformSearch.pending,false);
});
test('missing plugin, short input or required settings never enter a stuck loading state',()=>{
  for(const change of [c=>c.platformProviders={},c=>c.state.query='x',c=>c.platformProviderNeedsConfiguration=()=>true]) {
    const f=searchFixture();change(f.context);f.context.schedulePlatformSearch();f.flush();
    assert.equal(f.context.state.platformSearch.pending,false);assert.equal(f.messages.length,0);
  }
});
test('search history: most recent ten, deduplication, reload, clear', () => {
  let saved = '[]'; const storage = {getItem:()=>saved,setItem:(_,v)=>saved=v};
  const history = create(storage);
  for (let i=0;i<12;i++) history.add(` query ${i} `);
  assert.equal(history.all().length,10); assert.equal(history.all()[0],'query 11');
  history.add('QUERY 5'); assert.equal(history.all()[0],'QUERY 5'); assert.equal(history.all().length,10);
  assert.deepEqual(create(storage).all(),history.all());
  history.clear(); assert.deepEqual(create(storage).all(),[]);
});
test('search history tolerates corrupt or unavailable storage', () => {
  for (const value of ['null','{}','invalid','[null,1," hi ","HI",""]']) {
    const history=create({getItem:()=>value,setItem:()=>{throw Error('full')}});
    history.add('test'); assert.equal(history.all()[0],'test');
    history.clear(); assert.deepEqual(history.all(),[]);
  }
});
