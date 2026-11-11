// Kovu's Pride · Publicación: puente entre StorePlatform.cs y window.KPPlatform (kp-platform.js, que va en el
// WebGL Template de cada plataforma). En el portfolio no existe window.KPPlatform y todo queda en nada.
// Eventos hacia C#: cb(tipo, json) · 1 listo · 2 fin de anuncio · 3 pausa · 4 silencio · 5 empieza un anuncio
// · 6 respuesta de KPP_Call · 7 evento de la plataforma (salas de multijugador)
var KovusPridePlatformLib = {

  KPP_Init: function (cb) {
    var K = window.__kpp = window.__kpp || {};
    K.emit = function (kind, obj) {
      try {
        var s = JSON.stringify(obj || {});
        var n = lengthBytesUTF8(s) + 1, p = _malloc(n);
        stringToUTF8(s, p, n);
        {{{ makeDynCall('vii', 'cb') }}}(kind, p);
        _free(p);
      } catch (e) { console.warn('[KPPlatform] emit', e); }
    };
    if (!window.KPPlatform) { K.emit(1, { store: 'none', ads: 0, rewarded: 0 }); return; }
    try { window.KPPlatform.init(K.emit); }
    catch (e) { console.warn('[KPPlatform] init', e); K.emit(1, { store: 'error', ads: 0, rewarded: 0 }); }
  },

  KPP_Message: function (namePtr) {
    try { if (window.KPPlatform) window.KPPlatform.message(UTF8ToString(namePtr)); }
    catch (e) { console.warn('[KPPlatform] message', e); }
  },

  KPP_ShowAd: function (id, typePtr, placementPtr) {
    var K = window.__kpp || {};
    var type = UTF8ToString(typePtr), placement = UTF8ToString(placementPtr);
    try {
      if (window.KPPlatform) { window.KPPlatform.showAd(id, type, placement); return; }
    } catch (e) { console.warn('[KPPlatform] showAd', e); }
    if (K.emit) K.emit(2, { id: id, ok: 0 });
  },

  // Guardado de la plataforma (Playgama). Devuelve null si no hay valor o la plataforma no guarda.
  KPP_StorageGet: function (keyPtr) {
    var v = null;
    try { if (window.KPPlatform && window.KPPlatform.storageGet) v = window.KPPlatform.storageGet(UTF8ToString(keyPtr)); }
    catch (e) { console.warn('[KPPlatform] storageGet', e); }
    if (v === null || v === undefined) return 0;
    var n = lengthBytesUTF8(v) + 1, p = _malloc(n);
    stringToUTF8(v, p, n);
    return p;
  },

  KPP_StorageSet: function (keyPtr, valuePtr) {
    try { if (window.KPPlatform && window.KPPlatform.storageSet) window.KPPlatform.storageSet(UTF8ToString(keyPtr), UTF8ToString(valuePtr)); }
    catch (e) { console.warn('[KPPlatform] storageSet', e); }
  },

  // Llamada asíncrona: la respuesta llega como evento 6 {id, ok, data}
  KPP_Call: function (id, methodPtr, argsPtr) {
    var method = UTF8ToString(methodPtr), args = UTF8ToString(argsPtr);
    try {
      if (window.KPPlatform && window.KPPlatform.call) { window.KPPlatform.call(id, method, args); return; }
    } catch (e) { console.warn('[KPPlatform] call', e); }
    if (window.__kpp && window.__kpp.emit) window.__kpp.emit(6, { id: id, ok: 0, data: '{"error":"not_supported"}' });
  },

  KPP_SetScore: function (boardPtr, score) {
    try { if (window.KPPlatform && window.KPPlatform.setScore) window.KPPlatform.setScore(UTF8ToString(boardPtr), score); }
    catch (e) { }
  },
};

mergeInto(LibraryManager.library, KovusPridePlatformLib);
