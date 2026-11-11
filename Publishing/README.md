# Publishing (`KovusPride.Publishing`)

Publicación en portales web sin que el juego conozca ningún SDK.

## Runtime: `StorePlatform`
`Init`, `GameReady`, `GameplayStart/Stop`, `Happytime`, `ShowInterstitial`, `ShowRewarded`, `LoadString/SaveString`, `SubmitScore`, `Call`/`PlatformEvent`, `UpdateRoom`/`LeftRoom`, `PlayerName`, `Language` y las propiedades `IsPortal`, `RewardedAvailable`, `AllowsEscapeKey`, `AllowsOwnServer`, etc. (ver comentarios en `StorePlatform.cs`).
La plataforma la fija el símbolo de compilación (`KP_STORE_CRAZYGAMES`, `KP_STORE_PLAYGAMA`, `KP_STORE_GAMEDISTRIBUTION`, `KP_STORE_ITCH`); sin símbolo es el portfolio.

## Editor: menús del juego
```csharp
using KovusPride.Publishing;
using UnityEditor;

public static class TrickshotPublish
{
    private const string Game = "Trickshot Defense";
    [MenuItem("TRICKSHOT/Build/CrazyGames", priority = 100)] static void CrazyGames() => KovusPridePublish.Build(Store.CrazyGames, Game);
    [MenuItem("TRICKSHOT/Build/Playgama", priority = 101)] static void Playgama() => KovusPridePublish.Build(Store.Playgama, Game);
    [MenuItem("TRICKSHOT/Build/Playgama · subir la última build", priority = 111)] static void PlaygamaUpload() => KovusPridePublish.UploadLast(Store.Playgama, Game);
    [MenuItem("TRICKSHOT/Build/GameDistribution", priority = 102)] static void GameDistribution() => KovusPridePublish.Build(Store.GameDistribution, Game);
    [MenuItem("TRICKSHOT/Build/itch.io", priority = 103)] static void Itch() => KovusPridePublish.Build(Store.Itch, Game);
    [MenuItem("TRICKSHOT/Build/Todos los portales", priority = 120)] static void All() => KovusPridePublish.BuildAll(Game);
}
```

## Configuración: `<proyecto>/Publishing/publish.json`
`gameDistributionGameId`, `itchTarget`, `butlerPath`, `interstitialMinSeconds`, `storageKeys`, `playgamaApplicationId`, `playgamaPublicToken`, `playgamaLeaderboards`, `playgamaMultiplayer`. Las builds salen en `Builds/Publish/<plataforma>/` y se anotan en `Publishing/history.md`; los rechazos de QA, en `Publishing/rejections.md`.
