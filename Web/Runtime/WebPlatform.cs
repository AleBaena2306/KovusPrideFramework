using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#endif

namespace KovusPride.Web
{
    // Analítica: eventos estándar (game_start / game_end) y los propios del juego.
    // Web: puente KP_LogEvent → window.kpAnalytics(nombre, json), que define la página del portfolio
    //      (si no existe todavía, no se envía nada). Editor: se escriben en la consola.
    // Android (si el juego va a Google Play): pendiente de una implementación con Firebase (módulo Analytics).
    public static class Analytics
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void KP_LogEvent(string name, string json);
#endif
        public static bool LogToConsole = true;

        public static void Log(string name, params (string key, object value)[] parameters)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(parameters[i].key).Append("\":");
                object v = parameters[i].value;
                if (v == null) sb.Append("null");
                else if (v is bool) sb.Append((bool)v ? "1" : "0");   // GA4 no tiene booleanos: 1/0
                else if (v is int || v is long) sb.Append(Convert.ToInt64(v));
                else if (v is float || v is double) sb.Append(Convert.ToDouble(v).ToString("0.###", CultureInfo.InvariantCulture));
                else sb.Append('"').Append(v.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            string json = sb.Append('}').ToString();
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                KP_LogEvent(name, json);
#endif
                if (LogToConsole && (Application.isEditor || Debug.isDebugBuild)) Debug.Log($"[Analytics] {name} {json}");
            }
            catch (Exception e) { Debug.LogWarning("[Analytics] " + e.Message); }
        }
    }

    // Anuncios bonificados. Web: Google H5 Games Ads (AdSense) mediante KovusPrideWeb.jslib; el ID de editor
    // lo define la página del portfolio (window.KP_ADSENSE_CLIENT). Editor: se simula un anuncio de 1 s.
    public class Ads : MonoBehaviour
    {
        public static Ads Instance { get; private set; }
        // Anuncios de prueba de H5 Games Ads en el portfolio (mientras AdSense no apruebe la cuenta). Fijarlo antes de Create.
        public static bool TestAds = true;
        private string currentPlacement = "";
        // En los portales manda la capa de publicación (el SDK de CrazyGames, Playgama o GameDistribution)
        public bool IsReady
        {
            get => KovusPride.Publishing.StorePlatform.IsPortal ? KovusPride.Publishing.StorePlatform.RewardedAvailable : ownReady;
            private set => ownReady = value;
        }
        private bool ownReady;
        public bool IsShowing { get; private set; }
        private Action<bool> onDone;

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void IntCallback(int value);
        [DllImport("__Internal")] private static extern void KP_AdsInit(string client, int test, IntCallback onReady);
        [DllImport("__Internal")] private static extern void KP_AdsShowRewarded(string placement, IntCallback onDone);

        [MonoPInvokeCallback(typeof(IntCallback))]
        private static void OnInit(int ok) { if (Instance != null) Instance.IsReady = ok == 1; }

        [MonoPInvokeCallback(typeof(IntCallback))]
        private static void OnShown(int result) { if (Instance != null) Instance.Finish(result == 1); }
#endif

        public static Ads Create(Transform parent)
        {
            if (Instance != null) return Instance;
            var go = new GameObject("Ads");
            go.transform.SetParent(parent, false);
            Instance = go.AddComponent<Ads>();
            return Instance;
        }

        private void Start()
        {
            if (KovusPride.Publishing.StorePlatform.IsPortal) return;   // sin AdSense fuera del portfolio
#if UNITY_WEBGL && !UNITY_EDITOR
            // El ID de editor lo pone la página (window.KP_ADSENSE_CLIENT, definido en build.js del portfolio):
            // no se repite aquí para que viva en un solo sitio
            try { KP_AdsInit("", TestAds ? 1 : 0, OnInit); }
            catch (Exception e) { Debug.LogWarning("[Ads] " + e.Message); }
#else
            // Solo el Editor simula anuncios. En Android todavía no hay AdMob: sin anuncio, no se ofrece continuar
            IsReady = Application.isEditor;
#endif
        }

        // done(true) = el jugador vio el anuncio y gana la recompensa
        public void ShowRewarded(string placement, Action<bool> done)
        {
            if (!IsReady || IsShowing) { done?.Invoke(false); return; }
            if (KovusPride.Publishing.StorePlatform.IsPortal)
            {
                IsShowing = true;
                Analytics.Log("ad_rewarded_show", ("placement", placement));
                KovusPride.Publishing.StorePlatform.ShowRewarded(placement, ok =>
                {
                    IsShowing = false;
                    if (ok) Analytics.Log("ad_reward_earned", ("placement", placement));
                    done?.Invoke(ok);
                });
                return;
            }
            IsShowing = true;
            onDone = done;
            currentPlacement = placement;
            AudioListener.pause = true;
            Analytics.Log("ad_rewarded_show", ("placement", placement));
#if UNITY_WEBGL && !UNITY_EDITOR
            try { KP_AdsShowRewarded(placement, OnShown); }
            catch (Exception e) { Debug.LogWarning("[Ads] " + e.Message); Finish(false); }
#else
            StartCoroutine(FakeAd(placement));
#endif
        }

        private IEnumerator FakeAd(string placement)
        {
            Debug.Log($"[Ads] (Editor) Simulando anuncio bonificado «{placement}»…");
            yield return new WaitForSecondsRealtime(1f);
            Finish(true);
        }

        private void Finish(bool rewarded)
        {
            if (!IsShowing) return;
            IsShowing = false;
            AudioListener.pause = false;
            if (rewarded) Analytics.Log("ad_reward_earned", ("placement", currentPlacement));
            var cb = onDone; onDone = null;
            cb?.Invoke(rewarded);
        }
    }

    public static class Haptics
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void KP_Vibrate(int ms);
#endif
        public static bool Enabled = true;

        // Solo hace algo en Android (navegador o app). iPhone no permite vibrar desde la web.
        public static void Pulse(int ms)
        {
            if (!Enabled) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            try { KP_Vibrate(ms); } catch (Exception) { }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (ms >= 30) Handheld.Vibrate();
#endif
        }
    }
}
