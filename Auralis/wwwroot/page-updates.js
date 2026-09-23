/* Generic, bounded visible-page polling. No platform IDs, endpoints or account material. */
window.AuralisPageUpdates = (() => {
  const validAction=a=>/^page-[a-f0-9]{32}$/.test(a?.handle||'');
  const valid=u=>u==null||validAction(u.check)&&validAction(u.reload)&&/^[A-F0-9]{64}$/.test(u.fingerprint||'')&&
    Number.isInteger(u.intervalSeconds)&&u.intervalSeconds>=60&&u.intervalSeconds<=900;
  function create(api){
    let config=null,timer=0,failed=false,fresh=null;
    function pause(){clearTimeout(timer);timer=0;}
    function schedule(){pause();if(!config||failed||fresh||document.hidden||!navigator.onLine||!api.visible())return;
      timer=setTimeout(()=>{timer=0;if(api.idle()&&!document.hidden&&navigator.onLine&&api.visible())api.check(config.check.handle);else schedule();},config.intervalSeconds*1000);
    }
    function configure(value){pause();config=value;fresh=null;failed=false;api.notice(null);schedule();}
    function receive(value){
      if(!config||!value||!valid(value)){fail();return;}
      // Keep the displayed revision. A newer check must not silently acknowledge unseen content.
      if(value.fingerprint!==config.fingerprint){fresh=value;api.notice({kind:'new',reload:()=>api.reload(fresh.reload.handle)});pause();}
      else {config={...value,fingerprint:config.fingerprint};schedule();}
    }
    function fail(){pause();failed=true;api.notice({kind:'error',reload:()=>api.reload(config?.reload.handle)});}
    return {configure,pause,schedule,receive,fail,clear:()=>{pause();config=null;fresh=null;failed=false;}};
  }
  return {create,valid};
})();
