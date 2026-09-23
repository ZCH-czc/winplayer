/* CSS viewport dimensions already incorporate native DPI/zoom. Never multiply by DPR. */
(() => {
  const pageSize=(width,height)=>{
    const columns=Math.max(1,Math.min(8,Math.floor((Math.max(0,width)+20)/240)));
    const rows=Math.max(2,Math.min(4,Math.ceil(Math.max(0,height)/300)));
    return Math.max(6,Math.min(20,columns*rows));
  };
  const api={pageSize};
  if(typeof module!=='undefined')module.exports=api;
  if(typeof window!=='undefined')window.AuralisCataloguePagination=api;
})();
