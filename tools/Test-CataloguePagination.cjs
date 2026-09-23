const {test}=require('node:test'),assert=require('node:assert/strict');
const {pageSize}=require('../Auralis/wwwroot/catalogue-pagination.js');
test('viewport batch is bounded and uses CSS dimensions, not device pixel density',()=>{
  assert.equal(pageSize(420,500),6);
  assert.equal(pageSize(1000,800),12);
  assert.equal(pageSize(2200,1400),20);
  for(const width of [0,400,800,1200,1600,3200])for(const height of [0,400,800,1600]){
    const size=pageSize(width,height);assert(size>=6&&size<=20&&Number.isInteger(size));
  }
  assert(pageSize(900,600)<=pageSize(1800,1200),'higher scale means fewer CSS pixels and less eager loading');
});
