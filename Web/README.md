# Web (`KovusPride.Web`)

## Runtime
- `Analytics.Log(nombre, (clave, valor)...)`: en la web llama a `window.kpAnalytics(nombre, json)` si la página lo define; en el Editor, a la consola.
- `Ads.Create(parent)` y `Ads.Instance.ShowRewarded(placement, ok => ...)`: H5 Games Ads en el portfolio (fijar `Ads.TestAds` antes de `Create`) y el SDK del portal mediante `StorePlatform` en los portales. El Editor simula el anuncio.
- `Haptics.Pulse(ms)`: vibración en Android (navegador o app).
- Puentes `.jslib`: `KovusPrideAnalytics`, `KovusPrideWeb` (cuentas del portfolio y anuncios), `KovusPrideExtras` (vibración) y `KovusPrideInput` (detección de móvil, prompt, abrir enlaces).

## Editor: build para el portfolio
```csharp
using KovusPride.Web.Editor;
using UnityEditor;

public static class TrickshotWebBuild
{
    static readonly WebGame Game = new WebGame("trickshot-defense", "Trickshot Defense");
    [MenuItem("TRICKSHOT/Build/Web (móvil + PC)")] static void Web() => KovusPrideWebBuild.BuildAndPublish(Game);
    [MenuItem("TRICKSHOT/Build/Web: solo compilar (sin publicar)")] static void Only() => KovusPrideWebBuild.BuildOnly(Game);
    [MenuItem("TRICKSHOT/Build/Web: prueba rápida (solo PC, sin publicar)")] static void Quick() => KovusPrideWebBuild.QuickTest(Game);
    [MenuItem("TRICKSHOT/Build/Web: publicar la última build")] static void Publish() => KovusPrideWebBuild.PublishOnly(Game);
}
```
Compila `content/games/<slug>/web/mobile` (ASTC) y `.../pc` (DXT) con su `play.json`, y hace pull, commit y push del portfolio. La carpeta del portfolio se cambia en *Kovu's Pride > Web > Cambiar carpeta del portfolio…*.
