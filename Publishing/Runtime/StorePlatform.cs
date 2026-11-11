using System;
using System.Collections;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#endif

namespace KovusPride.Publishing
{
    // Dónde se está jugando. Lo decide la build: el menú de publicación compila cada plataforma con su símbolo
    // (KP_STORE_CRAZYGAMES, KP_STORE_PLAYGAMA, KP_STORE_GAMEDISTRIBUTION, KP_STORE_ITCH). Sin símbolo, es el portfolio.
    public enum Store { Portfolio, CrazyGames, Playgama, GameDistribution, Itch }

    // Capa común de plataforma. El juego llama siempre a esto y nunca a un SDK concreto:
    //   StorePlatform.Init()                    al arrancar
    //   StorePlatform.GameReady()               cuando el jugador ya puede jugar (menú visible)
    //   StorePlatform.GameplayStart() / Stop()  al empezar o reanudar una partida / al pausar, perder o ir al menú
    //   StorePlatform.Happytime()               en un logro grande (récord), con moderación
    //   StorePlatform.ShowInterstitial(...)     en una pausa natural (fin de partida); la capa decide si toca
    //   StorePlatform.ShowRewarded(...)         solo cuando el jugador lo pide (continuar, etc.)
    //   StorePlatform.LoadString / SaveString   guardado (Playgama exige su almacenamiento; el resto, PlayerPrefs)
    //   StorePlatform.SubmitScore(board, n)     puntuación para la clasificación de la plataforma, si la tiene
    // Reglas que se cumplen aquí dentro, en todas las plataformas, para que ningún juego las pueda romper:
    //   · Nunca un intersticial durante la partida.
    //   · Entre intersticiales, como mínimo InterstitialMinSeconds (CrazyGames corta a los 3 minutos), y ninguno en
    //     los primeros FirstInterstitialSeconds. En Playgama no: su SDK decide la frecuencia y prohíbe temporizadores
    //     propios, así que allí se le pide uno en cada pausa natural.
    //   · Mientras hay un anuncio, el audio está en pausa.
    //   · Sin SDK o sin anuncios (CrazyGames en Basic Launch, itch.io, bloqueadores), todo responde «no» y el
    //     juego sigue igual, sin esperas.
    // El código de cada SDK está en el WebGL Template de cada plataforma (kp-platform.js); aquí solo se habla
    // con window.KPPlatform a través de KovusPridePlatform.jslib.
    public static class StorePlatform
    {
        public static Store Current
        {
            get
            {
#if KP_STORE_CRAZYGAMES
                return Store.CrazyGames;
#elif KP_STORE_PLAYGAMA
                return Store.Playgama;
#elif KP_STORE_GAMEDISTRIBUTION
                return Store.GameDistribution;
#elif KP_STORE_ITCH
                return Store.Itch;
#else
                return Store.Portfolio;
#endif
            }
        }

        public static bool IsPortal => Current != Store.Portfolio;

        // Inicio de sesión con Google u otras cuentas propias: solo en el portfolio. Los portales lo prohíben
        // (CrazyGames: «No external login options») y además Google solo admite el dominio del portfolio.
        public static bool AllowsExternalLogin => Current == Store.Portfolio;

        // Enlaces que abren otras webs (privacidad, portfolio): los portales los suelen rechazar
        public static bool AllowsExternalLinks => Current == Store.Portfolio || Current == Store.Itch;

        // Servidor propio (modos online, rankings propios). Playgama exige que todo vaya dentro del juego y además
        // reparte el juego a webs que bloquean las conexiones externas: allí, de momento, nada de servidor propio.
        public static bool AllowsOwnServer => Current != Store.Playgama;

        // Multijugador con servidor propio (duelos y carreras por WebSocket). En CrazyGames, además, el juego le dice a
        // su SDK en qué sala está cada jugador (UpdateRoom/LeftRoom) para amigos, invitaciones y entrada directa.
        public static bool AllowsOwnMultiplayer => Current != Store.Playgama;

        // Tecla Esc: en los portales el navegador la reserva para salir de pantalla completa (Playgama 1.5.2)
        public static bool AllowsEscapeKey => !IsPortal;

        // El SDK de la plataforma regula los intersticiales (frecuencia y primer anuncio)
        public static bool PlatformPacesAds => Current == Store.Playgama;

        // Idioma que da la plataforma (ISO 639-1, p. ej. «es»); vacío si no lo da
        public static string Language { get; private set; } = "";

        public static float InterstitialMinSeconds = 180f;
        public static float FirstInterstitialSeconds = 90f;

        public static bool Ready { get; private set; }
        public static bool InterstitialAvailable { get; private set; }
        public static bool RewardedAvailable { get; private set; }
        public static bool AdShowing { get; private set; }
        public static bool GameplayActive { get; private set; }
        // Nombre del jugador en la plataforma (CrazyGames, Playgama) si ha iniciado sesión allí; si no, vacío
        public static string PlayerName { get; private set; } = "";
        // La plataforma tiene cuentas propias a las que se puede invitar a entrar desde el juego (CrazyGames)
        public static bool AccountAvailable { get; private set; }
        // Sin cuenta en la plataforma (invitado): se le puede invitar a iniciar sesión (ShowAuthPrompt)
        public static bool IsGuest { get; private set; }
        public static event Action<bool> PauseRequested;   // la plataforma pide pausar (true) o reanudar (false)
        public static event Action<bool> MuteRequested;    // la plataforma pide silenciar (true) o no (false)
        public static event Action OnReady;

        private static bool initialized, gameReadySent, platformMuted, platformPaused;
        private static float lastAdTime = -9999f, sessionStart;
        private static int nextAdId = 1, pendingId;
        private static Action<bool> pendingDone;
        private static bool pendingRewarded;

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void EventCallback(int kind, IntPtr json);
        [DllImport("__Internal")] private static extern void KPP_Init(EventCallback cb);
        [DllImport("__Internal")] private static extern void KPP_Message(string name);
        [DllImport("__Internal")] private static extern void KPP_ShowAd(int id, string type, string placement);
        [DllImport("__Internal")] private static extern string KPP_StorageGet(string key);
        [DllImport("__Internal")] private static extern void KPP_StorageSet(string key, string value);
        [DllImport("__Internal")] private static extern void KPP_SetScore(string board, double score);
        [DllImport("__Internal")] private static extern void KPP_Call(int id, string method, string args);

        [MonoPInvokeCallback(typeof(EventCallback))]
        private static void OnJsEvent(int kind, IntPtr json)
        {
            string s = json == IntPtr.Zero ? "{}" : Marshal.PtrToStringUTF8(json);
            try { HandleEvent(kind, s); } catch (Exception e) { Debug.LogWarning("[Platform] " + e.Message); }
        }
#endif

        // Mensajes de window.KPPlatform: 1 listo, 2 fin de anuncio, 3 pausa, 4 silencio, 5 empieza un anuncio,
        // 6 respuesta de una llamada (Call), 7 evento de la plataforma (PlatformEvent)
        [Serializable] private class JsEvent
        {
            public string store; public int ads, rewarded, id, ok, on, mp, instant, acct, guest; public string user, lang, lb, pid, data, name, invite;
        }

        private static void HandleEvent(int kind, string json)
        {
            var e = JsonUtility.FromJson<JsEvent>(json) ?? new JsEvent();
            switch (kind)
            {
                case 1:
                    Ready = true;
                    InterstitialAvailable = e.ads == 1;
                    RewardedAvailable = e.rewarded == 1;
                    PlayerName = string.IsNullOrEmpty(e.user) ? "" : e.user;
                    AccountAvailable = e.acct == 1;
                    IsGuest = e.guest == 1;
                    Language = string.IsNullOrEmpty(e.lang) ? "" : e.lang;
                    LeaderboardType = string.IsNullOrEmpty(e.lb) ? "" : e.lb;
                    MultiplayerAvailable = e.mp == 1;
                    PlayerId = string.IsNullOrEmpty(e.pid) ? "" : e.pid;
                    IsInstantMultiplayer = e.instant == 1;
                    InviteParamsJson = string.IsNullOrEmpty(e.invite) ? "" : e.invite;
                    Debug.Log($"[Platform] {Current} listo · intersticiales {InterstitialAvailable} · bonificados {RewardedAvailable}" +
                              (PlayerName != "" ? $" · jugador {PlayerName}" : ""));
                    OnReady?.Invoke();
                    break;
                case 2:
                    if (e.id == pendingId) FinishAd(e.ok == 1);
                    break;
                case 3:
                    platformPaused = e.on == 1;
                    ApplyAudio();
                    PauseRequested?.Invoke(platformPaused);
                    break;
                case 4:
                    platformMuted = e.on == 1;
                    ApplyAudio();
                    MuteRequested?.Invoke(platformMuted);
                    break;
                case 5:
                    ApplyAudio();
                    break;
                case 6:
                    if (calls.TryGetValue(e.id, out var cb)) { calls.Remove(e.id); cb?.Invoke(e.ok == 1, e.data ?? ""); }
                    break;
                case 7:
                    PlatformEvent?.Invoke(e.name ?? "", e.data ?? "");
                    break;
            }
        }

        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            sessionStart = Time.realtimeSinceStartup;
            Debug.Log($"[Platform] Plataforma: {Current}");
#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsPortal)
            {
                try { KPP_Init(OnJsEvent); }
                catch (Exception e) { Debug.LogWarning("[Platform] " + e.Message); HandleEvent(1, "{}"); }
                return;
            }
#endif
            // Portfolio y Editor: sin SDK de portal. En el Editor, con un símbolo de portal activo, se simulan
            // los anuncios para poder probar el flujo.
            bool simulate = Application.isEditor && IsPortal;
            HandleEvent(1, simulate ? "{\"ads\":1,\"rewarded\":1}" : "{}");
        }

        private static void Message(string name)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!IsPortal) return;
            try { KPP_Message(name); } catch (Exception) { }
#else
            if (Application.isEditor && IsPortal) Debug.Log("[Platform] " + name);
#endif
        }

        public static void GameReady()
        {
            if (gameReadySent) return;
            gameReadySent = true;
            Message("loading_stopped");
            Message("game_ready");
        }

        public static void GameplayStart()
        {
            if (GameplayActive) return;
            GameplayActive = true;
            Message("gameplay_started");
        }

        public static void GameplayStop()
        {
            if (!GameplayActive) return;
            GameplayActive = false;
            Message("gameplay_stopped");
        }

        public static void Happytime() => Message("happytime");

        // ¿Se podría enseñar un intersticial ahora mismo? (para no ofrecer nada que luego no salga)
        public static bool CanShowInterstitial =>
            Ready && InterstitialAvailable && !AdShowing && !GameplayActive &&
            (PlatformPacesAds ||
             (Time.realtimeSinceStartup - sessionStart >= FirstInterstitialSeconds &&
              Time.realtimeSinceStartup - lastAdTime >= InterstitialMinSeconds));

        // done siempre se llama (también si no toca anuncio), así el juego puede seguir en el mismo sitio
        // skipOwnPacing: el juego pide el anuncio aunque no hayan pasado los minutos propios entre intersticiales
        // (el SDK de cada portal sigue mandando sobre su propia frecuencia). Por ejemplo, al perder cerca del récord.
        public static void ShowInterstitial(string placement, Action done, bool skipOwnPacing = false)
        {
            bool can = skipOwnPacing ? Ready && InterstitialAvailable && !AdShowing && !GameplayActive : CanShowInterstitial;
            if (!can) { done?.Invoke(); return; }
            StartAd("interstitial", placement, _ => done?.Invoke());
        }

        // done(true) = el jugador ha visto el anuncio entero y gana la recompensa
        public static void ShowRewarded(string placement, Action<bool> done)
        {
            if (!Ready || !RewardedAvailable || AdShowing) { done?.Invoke(false); return; }
            StartAd("rewarded", placement, done);
        }

        private static void StartAd(string type, string placement, Action<bool> done)
        {
            AdShowing = true;
            pendingRewarded = type == "rewarded";
            pendingDone = done;
            pendingId = nextAdId++;
            ApplyAudio();
#if UNITY_WEBGL && !UNITY_EDITOR
            try { KPP_ShowAd(pendingId, type, placement ?? ""); }
            catch (Exception e) { Debug.LogWarning("[Platform] " + e.Message); FinishAd(false); }
#else
            Runner.Get().StartCoroutine(FakeAd(type, placement, pendingId));
#endif
        }

        private static IEnumerator FakeAd(string type, string placement, int id)
        {
            Debug.Log($"[Platform] (Editor) Simulando anuncio {type} «{placement}» en {Current}…");
            yield return new WaitForSecondsRealtime(1f);
            if (id == pendingId) FinishAd(true);
        }

        private static void FinishAd(bool ok)
        {
            if (!AdShowing) return;
            AdShowing = false;
            lastAdTime = Time.realtimeSinceStartup;
            ApplyAudio();
            var cb = pendingDone; pendingDone = null;
            pendingId = 0;
            cb?.Invoke(ok);   // en un bonificado, ok = visto entero; en un intersticial da igual
        }

        private static void ApplyAudio() => AudioListener.pause = AdShowing || platformMuted || platformPaused;

        // ---------------- Guardado ----------------
        // Playgama: bridge.storage (leído antes de arrancar Unity, ver kp-platform.js). Resto: PlayerPrefs.
        private static bool UsesPlatformStorage => Current == Store.Playgama;

        public static string LoadString(string key, string fallback = "")
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (UsesPlatformStorage)
            {
                try { return KPP_StorageGet(key) ?? fallback; }
                catch (Exception e) { Debug.LogWarning("[Platform] " + e.Message); return fallback; }
            }
#endif
            return PlayerPrefs.GetString(key, fallback);
        }

        // Llamar solo cuando cambia algo que importa (fin de partida, ajustes): Playgama lo exige así
        public static void SaveString(string key, string value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (UsesPlatformStorage)
            {
                try { KPP_StorageSet(key, value ?? ""); }
                catch (Exception e) { Debug.LogWarning("[Platform] " + e.Message); }
                return;
            }
#endif
            PlayerPrefs.SetString(key, value ?? "");
            PlayerPrefs.Save();
        }

        // ---------------- Llamadas a la plataforma ----------------
        // Clasificación en el juego («in_game»: la plataforma guarda, el juego pinta la tabla), «native» (la pinta la
        // plataforma) o vacío (no hay). Multijugador: salas de la plataforma (Playgama), si el juego lo activa.
        public static string LeaderboardType { get; private set; } = "";
        public static bool HasInGameLeaderboard => LeaderboardType == "in_game";
        public static bool MultiplayerAvailable { get; private set; }
        public static string PlayerId { get; private set; } = "";

        // Salas de la plataforma cuando el juego usa su propio servidor (CrazyGames): la plataforma solo necesita saber
        // en qué sala está el jugador para los amigos, las invitaciones y la entrada directa desde su página.
        public static bool IsInstantMultiplayer { get; private set; }   // abrir directamente en una sala privada
        public static string InviteParamsJson { get; private set; } = "";   // el juego se abrió desde una invitación
        public static bool HasRoomPresence => Current == Store.CrazyGames;

        public static void UpdateRoom(string roomId, bool joinable, string inviteParamsJson)
        {
            if (!HasRoomPresence) return;
            Call("room.update", "{\"roomId\":\"" + (roomId ?? "").Replace("\"", "") + "\",\"isJoinable\":" + (joinable ? "true" : "false") +
                                ",\"inviteParams\":" + (string.IsNullOrEmpty(inviteParamsJson) ? "{}" : inviteParamsJson) + "}", null);
        }

        public static void LeftRoom()
        {
            if (HasRoomPresence) Call("room.left", "{}", null);
        }

        // Eventos de la plataforma (nombre, json): mp.joined, mp.left, mp.host, mp.message, mp.roomLeft
        public static event Action<string, string> PlatformEvent;

        private static readonly System.Collections.Generic.Dictionary<int, Action<bool, string>> calls =
            new System.Collections.Generic.Dictionary<int, Action<bool, string>>();
        private static int nextCall = 1;

        // Llamada asíncrona a la plataforma; done(ok, json) llega siempre (sin plataforma, ok = false al momento)
        public static void Call(string method, string argsJson, Action<bool, string> done)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsPortal)
            {
                int id = nextCall++;
                calls[id] = done;
                try { KPP_Call(id, method, string.IsNullOrEmpty(argsJson) ? "{}" : argsJson); return; }
                catch (Exception e) { calls.Remove(id); Debug.LogWarning("[Platform] " + e.Message); }
            }
#endif
            done?.Invoke(false, "{\"error\":\"not_supported\"}");
        }

        // ---------------- Cuenta de la plataforma ----------------
        [Serializable] private class AuthReply { public string user; }

        // Inicio de sesión de la propia plataforma (CrazyGames: showAuthPrompt; Playgama: player.authorize). Los portales
        // no permiten cuentas externas como Google. done(true) si el jugador ha iniciado sesión; desde entonces
        // PlayerName es el nombre de su cuenta (si la plataforma lo da).
        public static void ShowAuthPrompt(Action<bool> done)
        {
            Call("user.auth", "{}", (ok, json) =>
            {
                string user = "";
                if (ok) { try { user = JsonUtility.FromJson<AuthReply>(json)?.user ?? ""; } catch (Exception) { } }
                if (!string.IsNullOrEmpty(user)) PlayerName = user;
                if (ok) IsGuest = false;
                done?.Invoke(ok);
            });
        }

        // ---------------- Clasificación ----------------
        public static void SubmitScore(string board, double score)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!IsPortal) return;
            try { KPP_SetScore(board, score); } catch (Exception) { }
#else
            if (Application.isEditor && IsPortal) Debug.Log($"[Platform] Puntuación {board}: {score}");
#endif
        }

        // Para las corrutinas del modo simulado del Editor
        private class Runner : MonoBehaviour
        {
            private static Runner instance;
            public static Runner Get()
            {
                if (instance != null) return instance;
                var go = new GameObject("KovusPridePlatform");
                UnityEngine.Object.DontDestroyOnLoad(go);
                instance = go.AddComponent<Runner>();
                return instance;
            }
        }
    }
}
