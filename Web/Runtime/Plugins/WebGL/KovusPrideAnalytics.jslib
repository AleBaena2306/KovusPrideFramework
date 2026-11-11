// Kovu's Pride (Web, copiado de MASSBOUND): puente de analítica entre el juego y la página que lo aloja.
// El juego llama a window.kpAnalytics(nombre, parametrosJson) con los mismos eventos que en Android.
// Si la página no define la función (o el jugador no ha dado consentimiento), no se envía nada.
mergeInto(LibraryManager.library, {
  KP_LogEvent: function (namePtr, jsonPtr) {
    try {
      if (typeof window === 'undefined' || typeof window.kpAnalytics !== 'function') return;
      window.kpAnalytics(UTF8ToString(namePtr), UTF8ToString(jsonPtr));
    } catch (e) {
      console.warn('[kpAnalytics] ' + e);
    }
  },

  // «Delete my data»: la página borra sus datos de analítica si define window.kpAnalyticsReset()
  KP_ResetAnalytics: function () {
    try {
      if (typeof window !== 'undefined' && typeof window.kpAnalyticsReset === 'function') window.kpAnalyticsReset();
    } catch (e) {
      console.warn('[kpAnalyticsReset] ' + e);
    }
  }
});
