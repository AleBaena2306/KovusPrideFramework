# Changelog

## [0.1.0] · 2026-11-11
Primera versión, extraída de Colorify.
### Añadido
- **Publishing**: `StorePlatform` (capa común de portales), `KovusPridePublish` (builds por portal con comprobaciones, historial y zip), `KovusPridePlaygama` (subida a Playgama por su MCP con OAuth), adaptador `kp-platform.js` y plantilla `KovusPridePortal` (se copia a `Assets/WebGLTemplates` al compilar).
- **Web**: `KovusPrideWebBuild` (build web móvil + PC para el portfolio, `play.json`, publicación por git; los menús los declara cada juego con `WebGame`), `Analytics`, `Ads` (con `Ads.TestAds`), `Haptics` y los puentes `KovusPrideAnalytics`, `KovusPrideWeb`, `KovusPrideExtras` y `KovusPrideInput`.
- **Net**: `KpSocket` y `KovusPrideSocket.jslib`.
### Cambios respecto a Colorify
- Namespaces `KovusPride.Web` y `KovusPride.Net` (antes `Colorify` y `Colorify.Online`).
- `Ads` registra el placement real en `ad_reward_earned` (antes siempre «continue» fuera de los portales).
- La ruta del adaptador y de la plantilla apuntan al paquete (`Packages/com.kovuspride.framework`).
