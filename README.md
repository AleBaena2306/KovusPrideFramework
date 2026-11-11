# Kovu's Pride Framework

Código reutilizable de los juegos de Kovu's Pride (Unity 6000.2, URP). Solo código: sin arte, audio, escenas ni prefabs.

## Instalación
Package Manager → «+» → *Install package from git URL…*:

```
https://github.com/AleBaena2306/KovusPrideFramework.git#v0.1.0
```

Fije siempre una versión (`#vX.Y.Z`). Cada versión se publica como una rama `vX.Y.Z` que no se vuelve a mover (las etiquetas de git no se pueden subir desde las sesiones de Claude); `main` es la versión en desarrollo. Un juego publicado no se actualiza salvo que se decida.

## Módulos
| Módulo | Assembly | Qué hace |
| --- | --- | --- |
| Publishing | `KovusPride.Publishing` (+ `.Editor`) | Capa común `StorePlatform` para portales (CrazyGames, Playgama, GameDistribution, itch.io), builds por portal con comprobaciones previas, subida automática a Playgama, plantilla web `KovusPridePortal` |
| Web | `KovusPride.Web` (+ `.Editor`) | Build web para el portfolio (móvil + PC, `play.json`, publicación por git), analítica web (`KP_LogEvent`), anuncios bonificados (H5 Games Ads / portales), vibración y utilidades del navegador |
| Net | `KovusPride.Net` | `KpSocket`: WebSocket de texto consultado desde el hilo principal (navegador y .NET) |

Cada módulo tiene su README con la API y un ejemplo.

## Uso en un juego nuevo
1. Instalar el paquete por URL (arriba).
2. Crear en `Assets/<Juego>/Editor/` los menús del juego (ver `Web/README.md` y `Publishing/README.md`).
3. Crear `<proyecto>/Publishing/publish.json` (ver `Publishing/README.md`).
4. La primera build de portal copia la plantilla `KovusPridePortal` a `Assets/WebGLTemplates` (o: *Kovu's Pride > Publishing > Instalar o actualizar la plantilla web*).

## Reglas
- Sin referencias a ningún juego concreto. Lo específico de cada juego (menús, nombre, slug, parámetros) vive en el juego.
- Versionado semántico. Un cambio que rompa la API sube la versión MAJOR. Cada versión se anota en `CHANGELOG.md` y se publica como rama `vX.Y.Z`.
- Un sistema pasa al framework cuando ya se ha usado en un juego y es razonable reutilizarlo.

## Origen
v0.1.0 se extrae de Colorify (sistema de publicación, build web y puentes de JavaScript, que a su vez venían de MASSBOUND). Colorify y MASSBOUND conservan su propia copia hasta que se migren.

© Kovu's Pride. Todos los derechos reservados.
