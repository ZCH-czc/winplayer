/* Core-owned, read-only controls. All plugin labels are plain text. Never persists input. */
window.AuralisPageQuery = (() => {
  const key = s => typeof s==='string' && /^[a-zA-Z0-9._-]{1,64}$/.test(s);
  const text = (s,n) => typeof s==='string' && s.length<=n && !/[\u0000-\u001f\u007f-\u009f]/.test(s);
  const valid = q => q && /^page-[a-f0-9]{32}$/.test(q.submit?.handle||'') &&
    text(q.submit.label,100) && q.submit.label.length>0 &&
    Array.isArray(q.fields) && q.fields.length>0 && q.fields.length<=4 &&
    new Set(q.fields.map(f=>f?.key)).size===q.fields.length &&
    q.fields.every(f=>f && key(f.key) && text(f.label,100) && f.label.length>0 &&
      text(f.placeholder,200) && Number.isInteger(f.maxLength) && f.maxLength>=1 && f.maxLength<=256 &&
      Number.isInteger(f.minLength) && f.minLength>=0 && f.minLength<=f.maxLength &&
      text(f.value,f.maxLength) && Array.isArray(f.options) &&
      (f.kind==='text' && f.options.length===0 || f.kind==='choice' && f.minLength===0 &&
       f.options.length>0 && f.options.length<=16 && new Set(f.options.map(o=>o?.value)).size===f.options.length &&
       f.options.every(o=>o && text(o.label,100) && o.label.length>0 && text(o.value,Math.min(64,f.maxLength)) && o.value.length>0) &&
       f.options.some(o=>o.value===f.value)));
  function create(query,submit,edited) {
    const form=document.createElement('form');form.className='plugin-page-query';form.noValidate=true;
    const controls=new Map(),textInputs=new Map();let composing=false;
    query.fields.forEach((field,index)=>{
      const group=document.createElement(field.kind==='choice'?'fieldset':'div');
      group.className='plugin-query-field';
      const label=document.createElement(field.kind==='choice'?'legend':'label');label.textContent=field.label;
      const id='pluginQuery-'+index;if(field.kind==='text')label.htmlFor=id;group.append(label);
      if(field.kind==='text'){
        const input=document.createElement('input');input.type='text';input.id=id;input.name=id;
        input.value=field.value;input.placeholder=field.placeholder;input.maxLength=field.maxLength;
        input.minLength=field.minLength;input.required=field.minLength>0;input.autocomplete='off';
        input.spellcheck=false;input.className='setting-text-input plugin-query-text';
        input.addEventListener('input',()=>{input.setCustomValidity('');edited();});
        group.append(input);controls.set(field.key,()=>input.value);textInputs.set(field.key,input);
      }else{
        const choices=document.createElement('div');choices.className='plugin-query-choices';
        field.options.forEach((option,i)=>{
          const item=document.createElement('label'),radio=document.createElement('input'),caption=document.createElement('span');
          radio.type='radio';radio.name=id;radio.value=option.value;radio.checked=field.value===option.value;
          radio.id=id+'-'+i;caption.textContent=option.label;
          radio.addEventListener('change',edited);item.append(radio,caption);choices.append(item);
        });
        group.append(choices);controls.set(field.key,()=>[...choices.querySelectorAll('input')].find(n=>n.checked)?.value||'');
      }
      form.append(group);
    });
    const button=document.createElement('button');button.type='submit';button.className='accent-button plugin-query-submit';
    button.textContent=query.submit.label;form.append(button);
    form.addEventListener('compositionstart',()=>composing=true);
    form.addEventListener('compositionend',()=>composing=false);
    form.addEventListener('keydown',e=>{
      if(e.key==='Enter' && (composing||e.isComposing||e.keyCode===229))e.preventDefault();
      // Typing must not trigger the player's shortcuts (including Space).
      if(e.key!=='Escape')e.stopPropagation();
    });
    form.addEventListener('submit',e=>{
      e.preventDefault();if(composing)return;
      const values=Object.fromEntries([...controls].map(([k,get])=>[k,get()]));
      for(const field of query.fields){
        const value=values[field.key];
        if(!text(value,field.maxLength)||value.length<field.minLength ||
          field.kind==='choice'&&!field.options.some(o=>o.value===value)){
          const input=textInputs.get(field.key);
          if(input instanceof HTMLInputElement){
            input.setCustomValidity(window.AuralisI18n?.t('查询条件无效，请检查输入后重试。')||'Please check your query.');
            input.reportValidity();
          }return;
        }
      }
      submit(query.submit.handle,values);
    });
    return form;
  }
  return {valid,create};
})();
