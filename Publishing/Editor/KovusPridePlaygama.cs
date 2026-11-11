using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

// Kovu's Pride · Subida a Playgama desde Unity, sin pasar por su consola.
//
// Playgama no tiene API de subida pública: la única vía es su servidor MCP (https://developer.playgama.com/api/mcp),
// que autoriza con OAuth. Este script hace de cliente MCP mínimo:
//   1. Conectar (una vez): abre el navegador, el desarrollador pulsa «Permitir» en la consola de Playgama y Unity
//      guarda el permiso en EditorPrefs (de este ordenador; nunca en el repositorio). Se renueva solo; si no se usa
//      en 30 días, Playgama pide conectar otra vez.
//   2. Subir: start_archive_upload → PUT del zip → confirm_archive_upload → espera al análisis (get_archive_status).
//   3. Publicar en la sandbox (página de prueba pública) solo si el desarrollador lo confirma en un diálogo: Playgama
//      exige un «sí» explícito para cada publicación.
// El id del juego está en Publishing/publish.json → playgamaApplicationId.
public static class KovusPridePlaygama
{
    private const string McpUrl = "https://developer.playgama.com/api/mcp";
    private const string ProtocolVersion = "2025-06-18";
    private const int CallbackPort = 38251;
    private static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";
    private const string Prefs = "KovusPride.Playgama.";
    private const string Title = "Playgama";

    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    private static bool busy;

    // ---------------- Menús ----------------

    [MenuItem("Kovu's Pride/Playgama/Conectar cuenta", priority = 1)]
    public static async void ConnectMenu()
    {
        if (busy) return;
        busy = true;
        try
        {
            await Connect();
            EditorUtility.DisplayDialog(Title, "Unity ya está conectado a su cuenta de Playgama.\n\nLas builds de Playgama se podrán subir desde el menú de cada juego.", "Vale");
        }
        catch (Exception e) { Report(e); }
        finally { busy = false; EditorUtility.ClearProgressBar(); }
    }

    [MenuItem("Kovu's Pride/Playgama/Desconectar", priority = 2)]
    public static void Disconnect()
    {
        foreach (var k in new[] { "access", "refresh", "expires", "client", "token_endpoint", "auth_endpoint", "resource" })
            EditorPrefs.DeleteKey(Prefs + k);
        EditorUtility.DisplayDialog(Title, "Desconectado. También puede revocar el acceso en la página MCP de la consola de Playgama.", "Vale");
    }

    public static bool IsConnected => !string.IsNullOrEmpty(EditorPrefs.GetString(Prefs + "refresh", "")) ||
                                      !string.IsNullOrEmpty(EditorPrefs.GetString(Prefs + "access", ""));

    // ---------------- Subida ----------------

    // Sube el zip al juego applicationId, espera al análisis y ofrece publicarlo en la sandbox. Devuelve el texto
    // para el historial (o lanza la excepción con el motivo).
    public static async void Upload(string applicationId, string zipPath, string archiveName, Action<string> log = null)
    {
        if (busy) { EditorUtility.DisplayDialog(Title, "Ya hay una subida en marcha.", "Vale"); return; }
        busy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(applicationId)) throw new Exception("Falta playgamaApplicationId en Publishing/publish.json.");
            if (!File.Exists(zipPath)) throw new Exception("No encuentro el zip:\n" + zipPath);
            await EnsureToken();

            long size = new FileInfo(zipPath).Length;
            Progress("Pidiendo la subida…", 0.05f);
            var start = await CallTool("start_archive_upload", new Dictionary<string, object>
                { { "applicationId", applicationId }, { "name", archiveName }, { "contentSize", size } });
            string archiveId = Str(start, "archiveId") ?? Str(start, "id") ?? Str(start, "mediaId");
            string uploadUrl = Str(start, "uploadUrl");
            if (string.IsNullOrEmpty(archiveId) || string.IsNullOrEmpty(uploadUrl)) throw new Exception("Playgama no ha devuelto la dirección de subida.\n" + MiniJson.Write(start));

            Progress($"Subiendo {size / 1048576f:0.0} MB…", 0.2f);
            using (var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl))
            {
                var content = new ByteArrayContent(File.ReadAllBytes(zipPath));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                if (start.TryGetValue("headers", out var hv) && hv is Dictionary<string, object> hdrs)
                    foreach (var kv in hdrs)
                    {
                        if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) content.Headers.ContentType = MediaTypeHeaderValue.Parse(kv.Value.ToString());
                        else if (!kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value.ToString());
                    }
                req.Content = content;
                var res = await http.SendAsync(req);
                if (!res.IsSuccessStatusCode) throw new Exception($"La subida del zip ha fallado ({(int)res.StatusCode}).\n{await res.Content.ReadAsStringAsync()}");
            }

            Progress("Confirmando…", 0.5f);
            await CallTool("confirm_archive_upload", new Dictionary<string, object> { { "applicationId", applicationId }, { "archiveId", archiveId } });

            // Análisis: desempaquetar y comprobar la build (SDK de Bridge incluido)
            Dictionary<string, object> status = null;
            var until = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < until)
            {
                status = await CallTool("get_archive_status", new Dictionary<string, object> { { "applicationId", applicationId }, { "archiveId", archiveId } });
                string processing = Str(status, "processing");
                string st = Str(Obj(status, "state"), "status");
                if (processing == "FAILED" || (processing == "DONE" && st != null && st != "CHECKING")) break;
                if (EditorUtility.DisplayCancelableProgressBar(Title, $"Playgama está analizando la build… ({processing ?? "?"} · {st ?? "?"})", 0.7f))
                    throw new Exception("Has dejado de esperar. La build ya está subida: el resultado del análisis aparecerá en la consola de Playgama.");
                await Task.Delay(4000);
            }
            EditorUtility.ClearProgressBar();
            if (status == null) throw new Exception("No he podido leer el estado de la build.");

            string proc = Str(status, "processing");
            var state = Obj(status, "state");
            string result = Str(state, "status");
            if (proc == "FAILED") throw new Exception("Playgama no ha podido desempaquetar el zip:\n" + (Str(status, "errorMessage") ?? "sin detalle"));
            if (result != "PASSED")
            {
                string msg = Str(state, "message") ?? "sin detalle";
                log?.Invoke($"Playgama: análisis {result} · {msg}");
                EditorUtility.DisplayDialog(Title, $"La build está subida, pero el análisis de Playgama dice {result}:\n\n{msg}", "Vale");
                return;
            }
            string bridge = Str(status, "bridgeSdk");
            log?.Invoke($"SUBIDA a Playgama · archivo {archiveId} · análisis PASSED · Bridge {bridge}");

            // Publicar en la página de prueba: pública y sin moderación, así que se pregunta siempre
            if (!EditorUtility.DisplayDialog(Title,
                    "Build subida y analizada sin problemas.\n\n¿Publicarla en la página de prueba de Playgama?\nEs pública: la puede jugar cualquiera con el enlace.",
                    "Publicar", "Ahora no"))
                return;

            var check = await CallTool("get_sandbox_state", new Dictionary<string, object> { { "applicationId", applicationId }, { "archiveId", archiveId } });
            var pub = Obj(check, "publish");
            if (pub != null && pub.TryGetValue("allowed", out var allowed) && allowed is bool b && !b)
                throw new Exception("Playgama no deja publicarla:\n" + (Str(pub, "message") ?? Str(pub, "reason") ?? "sin detalle"));

            Progress("Publicando en la página de prueba…", 0.85f);
            var publish = await CallTool("publish_sandbox", new Dictionary<string, object> { { "applicationId", applicationId }, { "archiveId", archiveId } });
            string url = Str(publish, "url");
            string releaseOk = await WaitPublished(applicationId, archiveId);
            EditorUtility.ClearProgressBar();
            log?.Invoke($"Playgama: publicada en la sandbox ({releaseOk}) {url}");
            if (EditorUtility.DisplayDialog(Title, $"Publicación: {releaseOk}.\n\n{url}", "Abrir", "Cerrar") && !string.IsNullOrEmpty(url))
                Application.OpenURL(url);
        }
        catch (Exception e) { Report(e); }
        finally { busy = false; EditorUtility.ClearProgressBar(); }
    }

    private static async Task<string> WaitPublished(string applicationId, string archiveId)
    {
        var until = DateTime.UtcNow.AddMinutes(8);
        while (DateTime.UtcNow < until)
        {
            var pubs = await CallTool("get_publications", new Dictionary<string, object> { { "applicationId", applicationId } });
            foreach (var item in Items(pubs))
            {
                if (Str(item, "platform") != "PLAYGAMA_SANDBOX") continue;
                var arch = Obj(item, "archive");
                string id = Str(arch, "id") ?? Str(arch, "archiveId") ?? Str(item, "archiveId");
                if (id != null && id != archiveId) continue;
                string st = Str(item, "status");
                if (st == "PUBLISHED" || st == "FAILED") return st == "PUBLISHED" ? "lista" : "ha fallado";
            }
            if (EditorUtility.DisplayCancelableProgressBar(Title, "Publicando en la página de prueba…", 0.9f)) return "en curso";
            await Task.Delay(5000);
        }
        return "en curso";
    }

    // La lista de publicaciones puede venir como array o dentro de un objeto
    private static IEnumerable<Dictionary<string, object>> Items(object o)
    {
        if (o is List<object> l) return l.OfType<Dictionary<string, object>>();
        if (o is Dictionary<string, object> d)
            foreach (var kv in d)
                if (kv.Value is List<object> list) return list.OfType<Dictionary<string, object>>();
        return Enumerable.Empty<Dictionary<string, object>>();
    }

    // ---------------- MCP ----------------

    private static int rpcId;
    private static string sessionId;

    private static async Task<Dictionary<string, object>> CallTool(string name, Dictionary<string, object> args)
    {
        var result = await Rpc("tools/call", new Dictionary<string, object> { { "name", name }, { "arguments", args } });
        var r = result as Dictionary<string, object> ?? new Dictionary<string, object>();
        string text = null;
        if (r.TryGetValue("content", out var c) && c is List<object> parts)
            foreach (var p in parts.OfType<Dictionary<string, object>>())
                if (Str(p, "type") == "text") { text = Str(p, "text"); break; }
        bool isError = r.TryGetValue("isError", out var ie) && ie is bool eb && eb;
        if (isError) throw new Exception($"Playgama ({name}): {text ?? MiniJson.Write(r)}");
        if (r.TryGetValue("structuredContent", out var sc) && sc is Dictionary<string, object> sd) return sd;
        if (text != null)
        {
            var parsed = MiniJson.Parse(text);
            if (parsed is Dictionary<string, object> pd) return pd;
            if (parsed is List<object> pl) return new Dictionary<string, object> { { "list", pl } };
            return new Dictionary<string, object> { { "text", text } };
        }
        return r;
    }

    private static async Task<object> Rpc(string method, Dictionary<string, object> prms, bool retried = false)
    {
        if (sessionId == null) await Initialize();
        var body = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", ++rpcId }, { "method", method }, { "params", prms } };
        var (code, text, _) = await Post(MiniJson.Write(body), auth: true);
        if (code == 401 && !retried) { await Refresh(force: true); sessionId = null; return await Rpc(method, prms, true); }
        if (code < 200 || code >= 300) throw new Exception($"Playgama respondió {code} a {method}.\n{Trim(text)}");
        var msg = ReadRpc(text);
        if (msg.TryGetValue("error", out var err)) throw new Exception($"Playgama ({method}): {MiniJson.Write(err)}");
        return msg.TryGetValue("result", out var res) ? res : null;
    }

    private static async Task Initialize()
    {
        var body = new Dictionary<string, object>
        {
            { "jsonrpc", "2.0" }, { "id", ++rpcId }, { "method", "initialize" },
            { "params", new Dictionary<string, object>
                {
                    { "protocolVersion", ProtocolVersion },
                    { "capabilities", new Dictionary<string, object>() },
                    { "clientInfo", new Dictionary<string, object> { { "name", "kovuspride-unity" }, { "version", "1.0" } } },
                } },
        };
        var (code, text, sid) = await Post(MiniJson.Write(body), auth: true);
        if (code == 401) { await Refresh(force: true); (code, text, sid) = await Post(MiniJson.Write(body), auth: true); }
        if (code < 200 || code >= 300) throw new Exception($"Playgama no acepta la conexión ({code}). Pruebe «Kovu's Pride > Playgama > Conectar cuenta».\n{Trim(text)}");
        sessionId = sid ?? "";
        // Aviso de inicializado (sin respuesta); en un servidor sin sesión no hace falta, pero no estorba
        await Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", auth: true);
    }

    private static async Task<(int code, string text, string session)> Post(string json, bool auth)
    {
        using (var req = new HttpRequestMessage(HttpMethod.Post, McpUrl))
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            if (!string.IsNullOrEmpty(sessionId)) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            if (auth)
            {
                string token = EditorPrefs.GetString(Prefs + "access", "");
                if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var res = await http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            string sid = res.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.FirstOrDefault() : null;
            lastWwwAuthenticate = res.Headers.WwwAuthenticate.Count > 0 ? string.Join(", ", res.Headers.WwwAuthenticate.Select(h => h.ToString())) : null;
            return ((int)res.StatusCode, text, sid);
        }
    }

    // Respuesta en JSON o en Server-Sent Events («data: {…}»): el mensaje con resultado o error
    private static Dictionary<string, object> ReadRpc(string text)
    {
        text = text ?? "";
        var candidates = new List<string>();
        if (text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[")) candidates.Add(text);
        else
            foreach (var line in text.Split('\n'))
                if (line.StartsWith("data:")) candidates.Add(line.Substring(5).Trim());
        foreach (var c in candidates)
        {
            var o = MiniJson.Parse(c);
            if (o is List<object> batch) o = batch.OfType<Dictionary<string, object>>().FirstOrDefault(m => m.ContainsKey("result") || m.ContainsKey("error"));
            if (o is Dictionary<string, object> d && (d.ContainsKey("result") || d.ContainsKey("error"))) return d;
        }
        throw new Exception("Respuesta de Playgama que no entiendo:\n" + Trim(text));
    }

    // ---------------- OAuth ----------------

    private static string lastWwwAuthenticate;

    private static async Task EnsureToken()
    {
        if (!IsConnected) { await Connect(); return; }
        if (DateTime.UtcNow.Ticks > EditorPrefs.GetString(Prefs + "expires", "0").ToLongOr(0)) await Refresh(force: false);
    }

    private static async Task Refresh(bool force)
    {
        string refresh = EditorPrefs.GetString(Prefs + "refresh", "");
        string tokenEndpoint = EditorPrefs.GetString(Prefs + "token_endpoint", "");
        string client = EditorPrefs.GetString(Prefs + "client", "");
        if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(tokenEndpoint))
        {
            if (!force && !string.IsNullOrEmpty(EditorPrefs.GetString(Prefs + "access", ""))) return;
            await Connect();
            return;
        }
        var form = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" }, { "refresh_token", refresh }, { "client_id", client },
            { "resource", EditorPrefs.GetString(Prefs + "resource", McpUrl) },
        };
        var res = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            // Permiso caducado o revocado: hay que volver a conectar
            EditorPrefs.DeleteKey(Prefs + "refresh");
            EditorPrefs.DeleteKey(Prefs + "access");
            await Connect();
            return;
        }
        SaveTokens(MiniJson.Parse(text) as Dictionary<string, object>);
    }

    private static async Task Connect()
    {
        Progress("Buscando la autorización de Playgama…", 0.1f);
        // 1. Metadatos del recurso protegido (RFC 9728): el servidor lo indica en WWW-Authenticate al pedir sin token
        sessionId = null;
        EditorPrefs.DeleteKey(Prefs + "access");
        var (code, _, _) = await Post("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + ProtocolVersion +
                                       "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"kovuspride-unity\",\"version\":\"1.0\"}}}", auth: false);
        string metaUrl = null;
        if (lastWwwAuthenticate != null)
        {
            int i = lastWwwAuthenticate.IndexOf("resource_metadata=\"", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { i += "resource_metadata=\"".Length; int j = lastWwwAuthenticate.IndexOf('"', i); if (j > i) metaUrl = lastWwwAuthenticate.Substring(i, j - i); }
        }
        var uri = new Uri(McpUrl);
        string origin = uri.GetLeftPart(UriPartial.Authority);
        var resourceMeta = await GetJson(metaUrl, origin + "/.well-known/oauth-protected-resource" + uri.AbsolutePath, origin + "/.well-known/oauth-protected-resource");
        string resource = Str(resourceMeta, "resource") ?? McpUrl;
        string issuer = (resourceMeta != null && resourceMeta.TryGetValue("authorization_servers", out var asv) && asv is List<object> asl && asl.Count > 0)
            ? asl[0].ToString() : origin;
        var issuerUri = new Uri(issuer);
        string iOrigin = issuerUri.GetLeftPart(UriPartial.Authority), iPath = issuerUri.AbsolutePath.TrimEnd('/');
        var asMeta = await GetJson(null,
            iOrigin + "/.well-known/oauth-authorization-server" + iPath,
            iOrigin + "/.well-known/openid-configuration" + iPath,
            issuer.TrimEnd('/') + "/.well-known/oauth-authorization-server",
            issuer.TrimEnd('/') + "/.well-known/openid-configuration");
        if (asMeta == null) throw new Exception($"No encuentro el servidor de autorización de Playgama ({issuer}). Código inicial: {code}.");
        string authEndpoint = Str(asMeta, "authorization_endpoint");
        string tokenEndpoint = Str(asMeta, "token_endpoint");
        string regEndpoint = Str(asMeta, "registration_endpoint");
        if (authEndpoint == null || tokenEndpoint == null) throw new Exception("El servidor de autorización de Playgama no indica sus direcciones.");

        // 2. Registro dinámico del cliente (RFC 7591), una vez por ordenador
        string client = EditorPrefs.GetString(Prefs + "client", "");
        if (string.IsNullOrEmpty(client))
        {
            if (regEndpoint == null) throw new Exception("Playgama no permite registrar el cliente de Unity (sin registration_endpoint).");
            var reg = new Dictionary<string, object>
            {
                { "client_name", "Kovu's Pride · Unity" },
                { "redirect_uris", new List<object> { RedirectUri } },
                { "grant_types", new List<object> { "authorization_code", "refresh_token" } },
                { "response_types", new List<object> { "code" } },
                { "token_endpoint_auth_method", "none" },
            };
            var res = await http.PostAsync(regEndpoint, new StringContent(MiniJson.Write(reg), Encoding.UTF8, "application/json"));
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception($"Playgama no ha aceptado el registro del cliente ({(int)res.StatusCode}).\n{Trim(text)}");
            client = Str(MiniJson.Parse(text) as Dictionary<string, object>, "client_id");
            if (string.IsNullOrEmpty(client)) throw new Exception("Playgama no ha devuelto el client_id.");
            EditorPrefs.SetString(Prefs + "client", client);
        }

        // 3. Autorización en el navegador con PKCE y respuesta en 127.0.0.1
        string verifier = Base64Url(RandomBytes(32));
        string challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomBytes(16));
        string scope = (resourceMeta != null && resourceMeta.TryGetValue("scopes_supported", out var sv) && sv is List<object> sl && sl.Count > 0)
            ? string.Join(" ", sl.Select(x => x.ToString())) : null;
        string url = authEndpoint + (authEndpoint.Contains("?") ? "&" : "?") +
                     "response_type=code&client_id=" + Uri.EscapeDataString(client) +
                     "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                     "&code_challenge=" + challenge + "&code_challenge_method=S256" +
                     "&state=" + state + "&resource=" + Uri.EscapeDataString(resource) +
                     (scope != null ? "&scope=" + Uri.EscapeDataString(scope) : "");

        string authCode;
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}/callback/");
            try { listener.Start(); }
            catch (Exception e) { throw new Exception($"No puedo abrir el puerto {CallbackPort} para recibir la autorización: {e.Message}"); }
            Application.OpenURL(url);
            EditorUtility.ClearProgressBar();
            var wait = listener.GetContextAsync();
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!wait.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("No ha llegado la autorización en 5 minutos. Vuelva a intentarlo.");
                if (EditorUtility.DisplayCancelableProgressBar(Title, "Pulse «Permitir» en la consola de Playgama que se ha abierto en el navegador…", 0.3f))
                    throw new Exception("Conexión cancelada.");
                await Task.Delay(300);
            }
            var ctx = wait.Result;
            var q = ParseQuery(ctx.Request.Url.Query);
            bool good = q.TryGetValue("code", out authCode) && q.TryGetValue("state", out var gotState) && gotState == state;
            string page = good
                ? "<html><body style='font-family:sans-serif;padding:40px'><h2>Unity ya está conectado a Playgama.</h2>Puede cerrar esta pestaña y volver a Unity.</body></html>"
                : "<html><body style='font-family:sans-serif;padding:40px'><h2>No se ha podido conectar.</h2>" + WebUtility.HtmlEncode(q.TryGetValue("error_description", out var ed) ? ed : q.TryGetValue("error", out var er) ? er : "") + "</body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            listener.Stop();
            if (!good) throw new Exception("Playgama no ha dado el permiso" + (q.TryGetValue("error", out var e2) ? $" ({e2})." : "."));
        }

        // 4. Código → tokens
        Progress("Guardando el permiso…", 0.8f);
        var form = new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" }, { "code", authCode }, { "redirect_uri", RedirectUri },
            { "client_id", client }, { "code_verifier", verifier }, { "resource", resource },
        };
        var tr = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        string tt = await tr.Content.ReadAsStringAsync();
        if (!tr.IsSuccessStatusCode) throw new Exception($"Playgama no ha entregado el permiso ({(int)tr.StatusCode}).\n{Trim(tt)}");
        EditorPrefs.SetString(Prefs + "token_endpoint", tokenEndpoint);
        EditorPrefs.SetString(Prefs + "auth_endpoint", authEndpoint);
        EditorPrefs.SetString(Prefs + "resource", resource);
        SaveTokens(MiniJson.Parse(tt) as Dictionary<string, object>);
        sessionId = null;
    }

    private static void SaveTokens(Dictionary<string, object> t)
    {
        if (t == null || Str(t, "access_token") == null) throw new Exception("Respuesta de tokens no válida.");
        EditorPrefs.SetString(Prefs + "access", Str(t, "access_token"));
        if (Str(t, "refresh_token") != null) EditorPrefs.SetString(Prefs + "refresh", Str(t, "refresh_token"));
        double secs = t.TryGetValue("expires_in", out var e) && e is double d ? d : 3600;
        EditorPrefs.SetString(Prefs + "expires", DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 60)).Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<Dictionary<string, object>> GetJson(params string[] urls)
    {
        foreach (var u in urls)
        {
            if (string.IsNullOrEmpty(u)) continue;
            try
            {
                var res = await http.GetAsync(u);
                if (!res.IsSuccessStatusCode) continue;
                if (MiniJson.Parse(await res.Content.ReadAsStringAsync()) is Dictionary<string, object> d) return d;
            }
            catch (Exception) { }
        }
        return null;
    }

    // ---------------- Utilidades ----------------

    private static void Progress(string text, float p) => EditorUtility.DisplayProgressBar(Title, text, p);

    private static void Report(Exception e)
    {
        EditorUtility.ClearProgressBar();
        Debug.LogError("[Playgama] " + e);
        EditorUtility.DisplayDialog(Title, e.Message, "Vale");
    }

    private static string Str(Dictionary<string, object> d, string k) =>
        d != null && d.TryGetValue(k, out var v) && v != null ? (v is double dv ? dv.ToString(CultureInfo.InvariantCulture) : v.ToString()) : null;

    private static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) =>
        d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

    private static string Trim(string s) => s == null ? "" : s.Length > 600 ? s.Substring(0, 600) + "…" : s;

    private static long ToLongOr(this string s, long def) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static byte[] RandomBytes(int n) { var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in (q ?? "").TrimStart('?').Split('&'))
        {
            if (part.Length == 0) continue;
            int i = part.IndexOf('=');
            string k = Uri.UnescapeDataString(i < 0 ? part : part.Substring(0, i));
            string v = i < 0 ? "" : Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    // JSON mínimo: objetos → Dictionary<string, object>, listas → List<object>, números → double
    public static class MiniJson
    {
        public static object Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            int i = 0;
            try { return Value(s, ref i); } catch (Exception) { return null; }
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); string k = (string)Value(s, ref i); Ws(s, ref i); i++;   // :
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; return d;   // }
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; return l;   // ]
                }
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        i++;
                        char e = s[i];
                        if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                        else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : e);
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                i++; return sb.ToString();
            }
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        public static string Write(object o)
        {
            var sb = new StringBuilder(); W(o, sb); return sb.ToString();
        }

        private static void W(object o, StringBuilder sb)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string str)
            {
                sb.Append('"');
                foreach (char ch in str)
                {
                    if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                    else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                }
                sb.Append('"');
                return;
            }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is Dictionary<string, object> d)
            {
                sb.Append('{'); bool first = true;
                foreach (var kv in d) { if (!first) sb.Append(','); first = false; W(kv.Key, sb); sb.Append(':'); W(kv.Value, sb); }
                sb.Append('}');
                return;
            }
            if (o is System.Collections.IEnumerable list)
            {
                sb.Append('['); bool f = true;
                foreach (var x in list) { if (!f) sb.Append(','); f = false; W(x, sb); }
                sb.Append(']');
                return;
            }
            if (o is IFormattable n) { sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); return; }
            W(o.ToString(), sb);
        }
    }
}
