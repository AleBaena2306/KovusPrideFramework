// Kovu's Pride (Web): utilidades pequeñas del navegador.
//  - KP_Vibrate(ms): vibración en Android (Chrome). iPhone no permite vibrar desde la web: no hace nada.
mergeInto(LibraryManager.library, {
  KP_Vibrate: function (ms) {
    try {
      if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') navigator.vibrate(ms);
    } catch (e) { }
  }
});
