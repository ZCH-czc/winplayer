(() => {
  'use strict';
  const buttons=[...document.querySelectorAll('[data-color]')];
  const group=document.getElementById('accent-picker'),caption=document.getElementById('accent-caption');
  const stage=document.getElementById('customize-stage');
  const names={en:{blue:'Blue',teal:'Teal',violet:'Violet',coral:'Coral',amber:'Amber'},'zh-CN':{blue:'蓝色',teal:'青色',violet:'紫色',coral:'珊瑚红',amber:'琥珀橙'}};
  let accent='blue';
  const language=()=>document.documentElement.lang==='zh-CN'?'zh-CN':'en';
  function render(){
    const lang=language();group.setAttribute('aria-label',lang==='en'?'Choose an accent color':'选择强调色');
    for(const button of buttons){const selected=button.dataset.color===accent;button.setAttribute('aria-checked',String(selected));button.tabIndex=selected?0:-1;button.setAttribute('aria-label',names[lang][button.dataset.color]);button.title=names[lang][button.dataset.color];}
    caption.textContent=(lang==='en'?'Selected accent: ':'当前强调色：')+names[lang][accent];
    window.AuralisGallery.show(stage,`assets/gallery/customize-${accent}-${lang==='en'?'en-US':lang}.png`,caption.textContent);
  }
  for(const button of buttons){
    button.addEventListener('click',()=>{accent=button.dataset.color;render();});
    button.addEventListener('keydown',event=>{
      if(!['ArrowRight','ArrowDown','ArrowLeft','ArrowUp','Home','End'].includes(event.key))return;
      event.preventDefault();const index=buttons.indexOf(button);
      const next=event.key==='Home'?0:event.key==='End'?buttons.length-1:(index+(['ArrowRight','ArrowDown'].includes(event.key)?1:-1)+buttons.length)%buttons.length;
      buttons[next].click();buttons[next].focus();
    });
  }
  window.addEventListener('auralis-language-change',render);
  render();
})();
