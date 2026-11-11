// Kovu's Pride (Web): entrada de texto y ventanas.
//  - KP_IsMobileBrowser(): 1 en móviles y tabletas (pantalla táctil sin ratón).
//  - KP_Prompt(titulo, valor): pide un texto con el cuadro del navegador (en el móvil abre su teclado).
//    Devuelve el texto, o "\u0001" si se cancela.
//  - KP_OpenOnRelease(url): abre url en otra pestaña al soltar el dedo o el ratón. Los navegadores solo dejan
//    abrir ventanas dentro de un toque, y Unity procesa los botones un fotograma después; por eso el juego
//    lo llama al pulsar (pointer down) y la pestaña se abre al soltar, dentro del mismo gesto.
mergeInto(LibraryManager.library, {
  KP_IsMobileBrowser: function () {
    try {
      var coarse = window.matchMedia && window.matchMedia('(pointer: coarse)').matches;
      var ua = /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent || '');
      var iPadOS = navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
      return (coarse || ua || iPadOS) ? 1 : 0;
    } catch (e) { return 0; }
  },

  KP_Prompt: function (titlePtr, valuePtr) {
    var r = null;
    try { r = window.prompt(UTF8ToString(titlePtr), UTF8ToString(valuePtr)); } catch (e) { r = null; }
    var str = r === null ? '\u0001' : String(r);
    var size = lengthBytesUTF8(str) + 1;
    var buffer = _malloc(size);
    stringToUTF8(str, buffer, size);
    return buffer;
  },

  KP_OpenOnRelease: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var done = false;
    var open = function () {
      if (done) return;
      done = true;
      ['pointerup', 'touchend', 'mouseup'].forEach(function (t) { document.removeEventListener(t, open, true); });
      try { window.open(url, '_blank'); } catch (e) { }   // si el navegador lo bloquea, el juego enseña el enlace
    };
    ['pointerup', 'touchend', 'mouseup'].forEach(function (t) { document.addEventListener(t, open, true); });
    setTimeout(function () { if (!done) { done = true; ['pointerup', 'touchend', 'mouseup'].forEach(function (t) { document.removeEventListener(t, open, true); }); } }, 4000);
  }
});
