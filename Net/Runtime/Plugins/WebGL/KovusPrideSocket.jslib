// Kovu's Pride (Web): WebSocket del navegador para el modo Online. El juego lo consulta en cada fotograma
// (KP_WSRecv) en lugar de recibir callbacks, así no depende de nombres de GameObject.
//   KP_WSOpen(url) → id · KP_WSState(id) → 0 conectando, 1 abierto, 3 cerrado · KP_WSSend · KP_WSRecv · KP_WSClose
mergeInto(LibraryManager.library, {
  KP_WSOpen: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    if (!window.kpSockets) window.kpSockets = { next: 1, all: {} };
    var id = window.kpSockets.next++;
    var s = { q: [], state: 0, ws: null };
    window.kpSockets.all[id] = s;
    try {
      s.ws = new WebSocket(url);
      s.ws.onopen = function () { s.state = 1; };
      s.ws.onmessage = function (e) { if (typeof e.data === 'string') s.q.push(e.data); };
      s.ws.onclose = function () { s.state = 3; };
      s.ws.onerror = function () { s.state = 3; };
    } catch (e) { s.state = 3; }
    return id;
  },

  KP_WSState: function (id) {
    var s = window.kpSockets && window.kpSockets.all[id];
    if (!s) return 3;
    return s.q.length > 0 && s.state === 3 ? 1 : s.state;
  },

  KP_WSSend: function (id, textPtr) {
    var s = window.kpSockets && window.kpSockets.all[id];
    if (!s || !s.ws || s.state !== 1) return;
    try { s.ws.send(UTF8ToString(textPtr)); } catch (e) { }
  },

  KP_WSRecv: function (id) {
    var s = window.kpSockets && window.kpSockets.all[id];
    if (!s || s.q.length === 0) return null;
    var str = s.q.shift();
    var size = lengthBytesUTF8(str) + 1;
    var buffer = _malloc(size);
    stringToUTF8(str, buffer, size);
    return buffer;
  },

  KP_WSClose: function (id) {
    var s = window.kpSockets && window.kpSockets.all[id];
    if (!s) return;
    try { if (s.ws) s.ws.close(1000, 'bye'); } catch (e) { }
    delete window.kpSockets.all[id];
  }
});
