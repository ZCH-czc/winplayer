/* Local-only, explicit committed queries. Never provider cookies or search results. */
(() => {
  function create(storage) {
    const key = 'auralis:search-history';
    const normalize = value => typeof value === 'string' ? value.trim().slice(0,200) : '';
    let queries = [];
    try {
      const value = JSON.parse(storage.getItem(key) || '[]');
      if (Array.isArray(value)) for (const item of value) {
        const text = normalize(item);
        if (text && !queries.some(q => q.toLowerCase() === text.toLowerCase())) queries.push(text);
        if (queries.length === 10) break;
      }
    } catch (_) { /* A disabled/full storage must never break search. */ }
    function persist() { try { storage.setItem(key, JSON.stringify(queries)); } catch (_) {} }
    return {
      all: () => [...queries],
      add(value) { const text = normalize(value); if (!text) return; queries = [text, ...queries.filter(q => q.toLowerCase() !== text.toLowerCase())].slice(0,10); persist(); },
      clear() { queries = []; persist(); }
    };
  }
  if (typeof module === 'object') module.exports = {create};
  else window.AuralisSearchHistory = {create};
})();
