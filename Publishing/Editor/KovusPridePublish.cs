using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using KovusPride.Publishing;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Kovu's Pride · Publicación en portales web. Cada juego declara sus menús (p. ej. <JUEGO> > Build > CrazyGames)
// y llama a KovusPridePublish.Build. Una build por plataforma, sin tocar nada a mano:
//   1. Comprueba que los scripts están compilados y que la plataforma es Web (si no, ofrece cambiarla).
//   2. Compila con la plantilla KovusPridePortal y el símbolo KP_STORE_<PLATAFORMA> (solo para esa build; no cambia
//      los ajustes del proyecto ni obliga a recompilar el Editor).
//   3. Añade el SDK de la plataforma al index.html y mete dentro el adaptador kp-platform.js (y la configuración de Playgama).
//   4. Comprueba los requisitos de la plataforma (tamaño, número de archivos, descarga inicial, rutas relativas).
//      Si algo incumple un requisito obligatorio, no genera el zip.
//   5. Genera el zip en <proyecto>/Builds/Publish/<plataforma>/ y apunta el resultado en Publishing/history.md.
//   6. Sube o prepara la subida: itch.io con butler (si está instalado); Playgama la sube Claude por MCP; en
//      CrazyGames y GameDistribution abre la página de subida y la carpeta del zip.
// La configuración del juego está en <proyecto>/Publishing/publish.json (se crea con valores por defecto).
// Los rechazos de cada portal se apuntan en Publishing/rejections.md para ir mejorando estas comprobaciones.
public static class KovusPridePublish
{
    [Serializable]
    public class Settings
    {
        public string gameName = "";
        public string gameDistributionGameId = "";   // «Game ID» del panel de GameDistribution (pestaña Upload)
        public string itchTarget = "";               // usuario/juego:html5, p. ej. alebaena/colorify:html5
        public string butlerPath = "";               // vacío = buscar «butler» en el PATH
        public float interstitialMinSeconds = 180f;
        // Claves de guardado que el juego usa con StorePlatform.LoadString/SaveString. En Playgama se leen con su SDK
        // antes de arrancar Unity (lo exige: el estado del jugador se restaura antes de enseñar el juego).
        public string[] storageKeys = new string[0];
        // Playgama: id del juego en su consola (subida automática) y token público de Bridge SaaS (clasificaciones y
        // multijugador; está en la ficha del juego, Bridge SaaS). El token es público: va dentro del juego.
        public string playgamaApplicationId = "";
        public string playgamaPublicToken = "";
        public string[] playgamaLeaderboards = new string[0];   // ids creados en la consola, p. ej. «best»
        // Multijugador de Playgama (salas en sus servidores). Solo se puede activar cuando el juego está en su
        // catálogo (la página de prueba no admite juegos multijugador).
        public bool playgamaMultiplayer = false;
    }

    private class Profile
    {
        public Store store;
        public string name, define, folder, uploadUrl, notes;
        public string head;                          // lo que se añade al <head> del index.html
        public WebGLCompressionFormat compression = WebGLCompressionFormat.Brotli;
        public bool decompressionFallback = true;    // funciona aunque el servidor no envíe Content-Encoding
        public long maxTotalBytes = 250L << 20, maxInitialBytes = 50L << 20, warnInitialBytes = 20L << 20;
        public int maxFiles = 1500;
        public bool dataCaching = true;              // caché de la build en IndexedDB del navegador
        public bool uploadFolder;                    // el portal pide los archivos sueltos, no el zip (CrazyGames)
    }

    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string PublishingDir => Path.Combine(ProjectRoot, "Publishing");
    private static string SettingsPath => Path.Combine(PublishingDir, "publish.json");
    private const string TemplateName = "PROJECT:KovusPridePortal";
    // El adaptador y la plantilla viven en el paquete del framework (com.kovuspride.framework). Unity solo lee las
    // plantillas web de Assets/WebGLTemplates, así que la plantilla se copia ahí la primera vez que hace falta.
    private const string PackageRoot = "Packages/com.kovuspride.framework";
    private static string AdapterPath => Path.GetFullPath(Path.Combine(PackageRoot, "Publishing", "Templates~", "kp-platform.js"));
    private static string PackageTemplateDir => Path.GetFullPath(Path.Combine(PackageRoot, "Publishing", "WebGLTemplates~", "KovusPridePortal"));
    private static string ProjectTemplateDir => Path.Combine(Application.dataPath, "WebGLTemplates", "KovusPridePortal");

    // Copia la plantilla del paquete a Assets/WebGLTemplates/KovusPridePortal (sin pisar una versión ya modificada
    // en el juego, salvo que se pida). Devuelve false si no se pudo.
    [MenuItem("Kovu's Pride/Publishing/Instalar o actualizar la plantilla web de los portales")]
    public static void InstallTemplateMenu() => InstallTemplate(overwrite: true);

    public static bool InstallTemplate(bool overwrite = false)
    {
        if (!Directory.Exists(PackageTemplateDir)) { Debug.LogError("[Publish] Falta la plantilla en el paquete: " + PackageTemplateDir); return false; }
        if (Directory.Exists(ProjectTemplateDir) && !overwrite) return true;
        Directory.CreateDirectory(ProjectTemplateDir);
        foreach (string src in Directory.GetFiles(PackageTemplateDir, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(ProjectTemplateDir, src.Substring(PackageTemplateDir.Length).TrimStart('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
        }
        AssetDatabase.Refresh();
        Debug.Log("[Publish] Plantilla KovusPridePortal copiada a Assets/WebGLTemplates.");
        return true;
    }

    private static Profile GetProfile(Store store, Settings s)
    {
        switch (store)
        {
            case Store.CrazyGames:
                return new Profile
                {
                    store = store, name = "CrazyGames", define = "KP_STORE_CRAZYGAMES", folder = "crazygames",
                    uploadUrl = "https://developer.crazygames.com/",
                    head = "<script src=\"https://sdk.crazygames.com/crazygames-sdk-v3.js\"></script>\n  <script>window.KP_STORE = 'crazygames';</script>",
                    notes = "En el formulario, súbalo como juego HTML5 (no Unity): con Unity, CrazyGames ignora nuestro index.html y se perdería el adaptador del SDK. No acepta el zip: arrastre index.html y la carpeta Build. Basic Launch: sin anuncios y sin inicio de sesión externo.",
                    uploadFolder = true,
                };
            case Store.Playgama:
                return new Profile
                {
                    store = store, name = "Playgama", define = "KP_STORE_PLAYGAMA", folder = "playgama",
                    uploadUrl = "https://developer.playgama.com/",
                    // El SDK tiene que ser el primer script del <head>, cargado normal (ni async ni desde código)
                    head = "<script src=\"https://bridge.playgama.com/v2/stable/playgama-bridge.js\"></script>\n  <script>window.KP_STORE = 'playgama';</script>",
                    // Playgama prohíbe guardar fuera de su SDK (IndexedDB incluido): sin caché de la build
                    dataCaching = false,
                    maxTotalBytes = 300L << 20,
                    notes = "Claude sube el zip con el servidor MCP de Playgama (start_archive_upload → confirm_archive_upload) y lo publica en la sandbox.",
                };
            case Store.GameDistribution:
                string id = (s.gameDistributionGameId ?? "").Trim().Replace("'", "");
                return new Profile
                {
                    store = store, name = "GameDistribution", define = "KP_STORE_GAMEDISTRIBUTION", folder = "gamedistribution",
                    uploadUrl = "https://developer.gamedistribution.com/",
                    head = "<script>\n    window.KP_STORE = 'gamedistribution';\n    window.__kpGdQueue = [];\n" +
                           "    window['GD_OPTIONS'] = { gameId: '" + id + "', onEvent: function (ev) {\n" +
                           "      if (window.__kpGdEvent) window.__kpGdEvent(ev); else window.__kpGdQueue.push(ev); } };\n" +
                           "  </script>\n  <script src=\"https://html5.api.gamedistribution.com/main.min.js\"></script>",
                    notes = "Los anuncios se prueban desde la pestaña Upload del panel de GameDistribution (iframe de prueba).",
                };
            default:
                return new Profile
                {
                    store = Store.Itch, name = "itch.io", define = "KP_STORE_ITCH", folder = "itch",
                    uploadUrl = "https://itch.io/game/new",
                    head = "<script>window.KP_STORE = 'itch';</script>",
                    maxFiles = 1000, maxTotalBytes = 500L << 20, maxInitialBytes = 500L << 20, warnInitialBytes = 50L << 20,
                    notes = "En la página del juego en itch.io: Kind of project = HTML y marcar el canal como «This file will be played in the browser» (solo la primera vez).",
                };
        }
    }

    // ---------------- Menú ----------------

    public static void BuildAll(string gameName)
    {
        foreach (Store s in new[] { Store.CrazyGames, Store.Playgama, Store.GameDistribution, Store.Itch })
            if (!Build(s, gameName, askAtEnd: false)) return;
        EditorUtility.RevealInFinder(Path.Combine(ProjectRoot, "Builds", "Publish"));
    }

    public static bool Build(Store store, string gameName, bool askAtEnd = true)
    {
        Settings settings = LoadSettings(gameName);
        Profile p = GetProfile(store, settings);
        string title = $"{gameName} · Build {p.name}";

        if (!ScriptsUpToDate(title)) return false;
        if (!EnsureWebPlatform(title)) return false;
        if (!File.Exists(AdapterPath)) { Fail(title, "Falta kp-platform.js en el paquete del framework (" + AdapterPath + ")."); return false; }
        if (!InstallTemplate()) { Fail(title, "No se pudo instalar la plantilla web KovusPridePortal."); return false; }
        if (store == Store.GameDistribution && string.IsNullOrWhiteSpace(settings.gameDistributionGameId))
        {
            Fail(title, "Falta el Game ID de GameDistribution.\n\nCópialo del panel de GameDistribution (pestaña Upload) en Publishing/publish.json → gameDistributionGameId.");
            EditorUtility.RevealInFinder(SettingsPath);
            return false;
        }

        string version = PlayerSettings.bundleVersion;
        string outDir = Path.Combine(ProjectRoot, "Builds", "Publish", p.folder, "web");
        string zipPath = Path.Combine(ProjectRoot, "Builds", "Publish", p.folder, $"{Slug(gameName)}-{p.folder}-{version}.zip");

        var snapshot = new SettingsSnapshot();
        BuildReport report;
        try
        {
            PlayerSettings.WebGL.template = TemplateName;
            PlayerSettings.WebGL.compressionFormat = p.compression;
            PlayerSettings.WebGL.decompressionFallback = p.decompressionFallback;
            PlayerSettings.WebGL.dataCaching = p.dataCaching;
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
            EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;
            // Descarga más pequeña (11/11/2026: un 29 % de los jugadores de Playgama se iba durante la carga de 12,7 MB).
            // Stripping alto del código de Unity y de los paquetes; el código del juego lo protege link.xml.
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.High);
            PlayerSettings.stripEngineCode = true;
            SettingsSnapshot.SetWasmCodeOptimization("DiskSizeLTO", "DiskSize");

            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(sc => sc.enabled).Select(sc => sc.path).ToArray(),
                locationPathName = outDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { p.define },
            };
            Debug.Log($"[Publish] Compilando {gameName} {version} para {p.name} en {outDir}…");
            report = BuildPipeline.BuildPlayer(options);
        }
        finally { snapshot.Restore(); }

        if (report.summary.result == BuildResult.Cancelled)
        {
            Log(p, version, "CANCELADA", 0, 0, 0, "compilación cancelada en Unity");
            Fail(title, "La compilación se ha cancelado. Vuelve a lanzarla cuando quieras.");
            return false;
        }
        if (report.summary.result != BuildResult.Succeeded)
        {
            Log(p, version, "ERROR DE COMPILACIÓN", 0, 0, 0, $"{report.summary.totalErrors} errores en la consola");
            Fail(title, $"La compilación ha fallado ({report.summary.totalErrors} errores). Revisa la consola.");
            return false;
        }

        // Plantilla: SDK de la plataforma, adaptador y configuración
        string index = Path.Combine(outDir, "index.html");
        string html = File.ReadAllText(index);
        if (!html.Contains("<!-- KP_STORE_HEAD -->")) { Fail(title, "El index.html no viene de la plantilla KovusPridePortal."); return false; }
        string keys = string.Join(", ", (settings.storageKeys ?? new string[0])
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => "'" + k.Trim().Replace("'", "") + "'"));
        string head = p.head + "\n  <script>window.KP_STORAGE_KEYS = [" + keys + "];</script>\n  " + OptionsScript(store, settings);
        html = html.Replace("<!-- KP_STORE_HEAD -->", head);
        // El adaptador va dentro del index.html: un archivo menos que el portal pueda perder (CrazyGames devolvió 404
        // con kp-platform.js como archivo aparte)
        string adapter = File.ReadAllText(AdapterPath).Replace("</script", "<\\/script");
        if (!html.Contains("<script src=\"kp-platform.js\"></script>")) { Fail(title, "La plantilla no carga kp-platform.js."); return false; }
        html = html.Replace("<script src=\"kp-platform.js\"></script>", "<script>\n" + adapter + "\n</script>");
        File.WriteAllText(index, html, new UTF8Encoding(false));
        if (store == Store.Playgama)
            // Sin minimumDelayBetweenInterstitial: Playgama pide dejar los valores por defecto de cada plataforma.
            // La clasificación «best» la usan las plataformas de Playgama que la tienen (MSN, YouTube, Lagged…).
            File.WriteAllText(Path.Combine(outDir, "playgama-bridge-config.json"), PlaygamaConfig(settings), new UTF8Encoding(false));

        // Comprobaciones
        var problems = new List<string>();
        var warnings = new List<string>();
        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
        long total = files.Sum(f => new FileInfo(f).Length);
        long initial = files.Where(f => f.Replace('\\', '/').Contains("/Build/")).Sum(f => new FileInfo(f).Length);
        if (files.Length > p.maxFiles) problems.Add($"{files.Length} archivos (máximo {p.maxFiles}).");
        if (total > p.maxTotalBytes) problems.Add($"Tamaño total {Mb(total)} (máximo {Mb(p.maxTotalBytes)}).");
        if (initial > p.maxInitialBytes) problems.Add($"Descarga inicial {Mb(initial)} (máximo {Mb(p.maxInitialBytes)}).");
        else if (initial > p.warnInitialBytes) warnings.Add($"Descarga inicial {Mb(initial)}: por encima de {Mb(p.warnInitialBytes)} (en CrazyGames, sin portada móvil).");
        string final = File.ReadAllText(index);
        foreach (var src in new[] { "src=\"/", "href=\"/", "url(/" })
            if (final.Contains(src)) problems.Add($"Ruta absoluta en index.html ({src}…): deben ser relativas.");
        if (!Directory.Exists(Path.Combine(outDir, "Build"))) problems.Add("No hay carpeta Build/.");
        if (store == Store.Playgama)
        {
            // Requisitos de Playgama que se pueden comprobar sobre la build (wiki: Required steps y 1.3, 5.1)
            int firstScript = final.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
            int tagEnd = firstScript < 0 ? -1 : final.IndexOf('>', firstScript);
            if (tagEnd < 0 || !final.Substring(firstScript, tagEnd - firstScript).Contains("playgama-bridge.js"))
                problems.Add("El SDK de Playgama no es el primer script del <head>.");
            if (!File.Exists(Path.Combine(outDir, "playgama-bridge-config.json"))) problems.Add("Falta playgama-bridge-config.json junto al index.html.");
            if (!p.decompressionFallback) problems.Add("Playgama exige Decompression Fallback (5.1.1).");
            if (files.Any(f => Path.GetRelativePath(outDir, f).Any(ch => ch > 127)))
                problems.Add("Hay nombres de archivo con caracteres no latinos (1.3.2).");
            if ((settings.storageKeys ?? new string[0]).Length == 0)
                warnings.Add("publish.json no tiene storageKeys: en Playgama el guardado no se leerá al arrancar.");
        }

        if (problems.Count > 0)
        {
            Log(p, version, "NO CUMPLE", total, initial, files.Length, string.Join(" ", problems));
            Fail(title, "La build no cumple los requisitos de " + p.name + ":\n\n· " + string.Join("\n· ", problems) +
                        "\n\nNo se ha generado el zip.");
            return false;
        }

        if (File.Exists(zipPath)) File.Delete(zipPath);
        Zip(outDir, zipPath);
        Log(p, version, "LISTA", total, initial, files.Length, string.Join(" ", warnings));
        Debug.Log($"[Publish] {p.name} lista: {zipPath} · {Mb(total)} · descarga inicial {Mb(initial)} · {files.Length} archivos." +
                  (warnings.Count > 0 ? "\n· " + string.Join("\n· ", warnings) : ""));

        Upload(p, settings, outDir, zipPath, version, askAtEnd);
        return true;
    }

    // playgama-bridge-config.json: clasificaciones (las de la consola, en SaaS para Playgama y su herramienta de
    // pruebas; en el resto de plataformas de su red, las nativas) y el token público de Bridge SaaS
    private static string PlaygamaConfig(Settings s)
    {
        var boards = (s.playgamaLeaderboards ?? new string[0]).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
        var sb = new StringBuilder("{\n");
        if (!string.IsNullOrWhiteSpace(s.playgamaPublicToken))
        {
            sb.Append("  \"saas\": {\n    \"publicToken\": \"").Append(s.playgamaPublicToken.Trim()).Append("\"");
            if (boards.Count > 0) sb.Append(",\n    \"leaderboards\": { \"platforms\": [\"playgama\", \"qa_tool\"] }");
            sb.Append("\n  }").Append(boards.Count > 0 ? "," : "").Append("\n");
        }
        if (boards.Count > 0)
        {
            sb.Append("  \"leaderboards\": [\n");
            for (int i = 0; i < boards.Count; i++)
                sb.Append("    { \"id\": \"").Append(boards[i]).Append("\"").Append(i == 0 ? ", \"isMain\": true" : "").Append(" }").Append(i < boards.Count - 1 ? "," : "").Append("\n");
            sb.Append("  ]\n");
        }
        return sb.Append("}\n").ToString();
    }

    // Opciones de la plataforma que lee el juego en el arranque (window.KP_OPTIONS)
    private static string OptionsScript(Store store, Settings s)
    {
        bool mp = store == Store.Playgama && s.playgamaMultiplayer && !string.IsNullOrWhiteSpace(s.playgamaPublicToken);
        return "<script>window.KP_OPTIONS = { multiplayer: " + (mp ? "true" : "false") + " };</script>";
    }

    // ---------------- Subida ----------------

    private static void Upload(Profile p, Settings s, string outDir, string zipPath, string version, bool ask)
    {
        if (p.store == Store.Playgama && !string.IsNullOrWhiteSpace(s.playgamaApplicationId))
        {
            if (!ask || EditorUtility.DisplayDialog("Playgama", $"¿Subir la versión {version} a Playgama ahora?\n\n" +
                    (KovusPridePlaygama.IsConnected ? "" : "La primera vez se abre el navegador para conectar su cuenta de Playgama.\n\n") +
                    "Después de subirla, Unity le preguntará si la publica en la página de prueba.", "Subir", "Ahora no"))
            {
                KovusPridePlaygama.Upload(s.playgamaApplicationId, zipPath, Path.GetFileNameWithoutExtension(zipPath),
                    detail => Log(p, version, "PLAYGAMA", 0, 0, 0, detail));
                return;
            }
            EditorUtility.RevealInFinder(zipPath);
            return;
        }

        if (p.store == Store.Itch && !string.IsNullOrWhiteSpace(s.itchTarget))
        {
            string butler = FindButler(s.butlerPath);
            if (butler != null)
            {
                if (!ask || EditorUtility.DisplayDialog("itch.io", $"¿Subir la versión {version} a {s.itchTarget} con butler?", "Subir", "Ahora no"))
                {
                    int code = Run(butler, $"push \"{outDir}\" {s.itchTarget} --userversion {version}", out string output);
                    Debug.Log("[Publish] butler: " + output);
                    if (code == 0) { Log(p, version, "SUBIDA a itch.io", 0, 0, 0, s.itchTarget); return; }
                    Debug.LogError("[Publish] butler ha fallado. Si es la primera vez, ejecuta «butler login» en una terminal.");
                }
                EditorUtility.RevealInFinder(zipPath);
                return;
            }
            Debug.LogWarning("[Publish] No encuentro butler. Instálalo desde https://itchio.itch.io/butler y ejecuta «butler login» una vez.");
        }

        string show = p.uploadFolder ? Path.Combine(outDir, "index.html") : zipPath;
        EditorUtility.RevealInFinder(show);
        if (ask && EditorUtility.DisplayDialog($"{p.name} · lista para subir",
                $"{(p.uploadFolder ? "Archivos preparados en:\n" + outDir : "Zip preparado:\n" + zipPath)}\n\n{p.notes}\n\n¿Abrir el portal de {p.name}?", "Abrir", "Cerrar"))
            Application.OpenURL(p.uploadUrl);
    }

    // Sube a Playgama la última build generada (sin volver a compilar)
    public static void UploadLast(Store store, string gameName)
    {
        var s = LoadSettings(gameName);
        var p = GetProfile(store, s);
        string dir = Path.Combine(ProjectRoot, "Builds", "Publish", p.folder);
        var zip = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
        if (zip == null) { EditorUtility.DisplayDialog(p.name, $"No hay ninguna build de {p.name}. Compílela primero.", "Vale"); return; }
        string version = PlayerSettings.bundleVersion;
        Upload(p, s, Path.Combine(dir, "web"), zip.FullName, version, true);
    }

    private static string FindButler(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        try { return Run("butler", "--version", out _) == 0 ? "butler" : null; }
        catch (Exception) { return null; }
    }

    private static int Run(string exe, string args, out string output)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = ProjectRoot,
        };
        using (var proc = Process.Start(psi))
        {
            string o = proc.StandardOutput.ReadToEnd(), e = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            output = (o + "\n" + e).Trim();
            return proc.ExitCode;
        }
    }

    // ---------------- Utilidades ----------------

    private static Settings LoadSettings(string gameName)
    {
        Directory.CreateDirectory(PublishingDir);
        Settings s = null;
        if (File.Exists(SettingsPath))
            try { s = JsonUtility.FromJson<Settings>(File.ReadAllText(SettingsPath)); } catch (Exception) { }
        if (s == null)
        {
            s = new Settings { gameName = gameName };
            File.WriteAllText(SettingsPath, JsonUtility.ToJson(s, true), new UTF8Encoding(false));
            Debug.Log("[Publish] Creado " + SettingsPath + " (rellena el Game ID de GameDistribution y el destino de itch.io).");
        }
        if (string.IsNullOrEmpty(s.gameName)) s.gameName = gameName;
        string rejections = Path.Combine(PublishingDir, "rejections.md");
        if (!File.Exists(rejections))
            File.WriteAllText(rejections,
                "# Rechazos y avisos de los portales\n\n" +
                "Una línea por rechazo o aviso de QA: fecha · plataforma · versión · qué dijeron · qué se cambió en el sistema.\n" +
                "Cada caso nuevo se convierte en una comprobación de KovusPridePublish para que no se repita.\n\n", new UTF8Encoding(false));
        return s;
    }

    private static void Log(Profile p, string version, string result, long total, long initial, int files, string detail)
    {
        string path = Path.Combine(PublishingDir, "history.md");
        if (!File.Exists(path)) File.WriteAllText(path, "# Historial de builds para portales\n\n", new UTF8Encoding(false));
        string line = $"- {DateTime.Now:yyyy-MM-dd HH:mm} · {p.name} · {version} · {result}" +
                      (total > 0 ? $" · {Mb(total)}, inicial {Mb(initial)}, {files} archivos" : "") +
                      (string.IsNullOrEmpty(detail) ? "" : " · " + detail) + "\n";
        File.AppendAllText(path, line, new UTF8Encoding(false));
    }

    // Zip con rutas «/» (algunos portales no aceptan las «\» de Windows) y el index.html en la raíz.
    // Escritor propio sin compresión (método «store»): los archivos grandes de Unity ya van comprimidos, y así no
    // depende de System.IO.Compression, que según la configuración del Editor no está disponible.
    private static void Zip(string dir, string zipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
        var central = new MemoryStream();
        int count = 0;
        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                byte[] data = File.ReadAllBytes(f);
                uint crc = Crc32(data);
                uint offset = (uint)fs.Position;
                DateTime t = File.GetLastWriteTime(f);
                ushort dosTime = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
                ushort dosDate = (ushort)(((Math.Max(t.Year, 1980) - 1980) << 9) | (t.Month << 5) | t.Day);

                w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0);
                w.Write(dosTime); w.Write(dosDate); w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length);
                w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write(nameBytes); w.Write(data);

                var c = new BinaryWriter(central);
                c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0x0800); c.Write((ushort)0);
                c.Write(dosTime); c.Write(dosDate); c.Write(crc); c.Write((uint)data.Length); c.Write((uint)data.Length);
                c.Write((ushort)nameBytes.Length); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0);
                c.Write(0u); c.Write(offset); c.Write(nameBytes);
                count++;
            }
            uint centralOffset = (uint)fs.Position;
            central.Position = 0;
            central.CopyTo(fs);
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)count); w.Write((ushort)count);
            w.Write((uint)central.Length); w.Write(centralOffset); w.Write((ushort)0);
        }
    }

    private static uint[] crcTable;
    private static uint Crc32(byte[] data)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                crcTable[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static bool ScriptsUpToDate(string title)
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        if (EditorApplication.isCompiling)
        {
            EditorUtility.DisplayDialog(title, "Unity está recompilando los scripts que han cambiado.\n\nCuando termine, vuelve a lanzar la build.", "OK");
            return false;
        }
        if (EditorUtility.scriptCompilationFailed)
        {
            EditorUtility.DisplayDialog(title, "Hay errores de compilación en la consola. Corrígelos antes de compilar.", "OK");
            return false;
        }
        return true;
    }

    private static bool EnsureWebPlatform(string title)
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL) return true;
        if (!EditorUtility.DisplayDialog(title, "Hay que cambiar la plataforma a Web (reimporta los assets y tarda un poco).", "Cambiar", "Cancelar"))
            return false;
        return EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
    }

    private static void Fail(string title, string message)
    {
        Debug.LogError("[Publish] " + message);
        EditorUtility.DisplayDialog(title, message, "OK");
    }

    private static string Mb(long bytes) => (bytes / (1024f * 1024f)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    private static string Slug(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Ajustes del proyecto que se cambian para la build y se dejan como estaban al acabar
    private class SettingsSnapshot
    {
        private readonly string template = PlayerSettings.WebGL.template;
        private readonly WebGLCompressionFormat compression = PlayerSettings.WebGL.compressionFormat;
        private readonly bool fallback = PlayerSettings.WebGL.decompressionFallback;
        private readonly bool caching = PlayerSettings.WebGL.dataCaching;
        private readonly bool hashes = PlayerSettings.WebGL.nameFilesAsHashes;
        private readonly WebGLTextureSubtarget textures = EditorUserBuildSettings.webGLBuildSubtarget;
        private readonly Il2CppCodeGeneration codegen = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.WebGL);
        private readonly ManagedStrippingLevel stripping = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL);
        private readonly bool stripEngine = PlayerSettings.stripEngineCode;
        private readonly object codeOptimization = GetWasmCodeOptimization();

        // Optimización del WebAssembly (Unity 6: Build Settings > Web > Code Optimization). Se toca por reflexión para
        // no depender del nombre exacto del tipo en cada versión de Unity; si no existe, se deja como esté.
        private static System.Reflection.PropertyInfo WasmCodeOptimizationProperty()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("UnityEditor.WebGL.UserBuildSettings"); } catch { }
                var prop = t?.GetProperty("codeOptimization", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum) return prop;
            }
            return null;
        }

        private static object GetWasmCodeOptimization()
        {
            try { return WasmCodeOptimizationProperty()?.GetValue(null); } catch { return null; }
        }

        public static void SetWasmCodeOptimization(params string[] preferred)
        {
            var prop = WasmCodeOptimizationProperty();
            if (prop == null) { Debug.Log("[Publish] Code Optimization: no disponible en esta versión de Unity"); return; }
            var names = Enum.GetNames(prop.PropertyType);
            foreach (var name in preferred)
            {
                if (!names.Contains(name)) continue;
                try { prop.SetValue(null, Enum.Parse(prop.PropertyType, name)); Debug.Log("[Publish] Code Optimization: " + name); return; }
                catch (Exception e) { Debug.LogWarning("[Publish] Code Optimization: " + e.Message); }
            }
            Debug.Log("[Publish] Code Optimization: ninguna de " + string.Join(", ", preferred) + " existe (" + string.Join(", ", names) + ")");
        }

        public void Restore()
        {
            PlayerSettings.WebGL.template = template;
            PlayerSettings.WebGL.compressionFormat = compression;
            PlayerSettings.WebGL.decompressionFallback = fallback;
            PlayerSettings.WebGL.dataCaching = caching;
            PlayerSettings.WebGL.nameFilesAsHashes = hashes;
            EditorUserBuildSettings.webGLBuildSubtarget = textures;
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, codegen);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, stripping);
            PlayerSettings.stripEngineCode = stripEngine;
            try { if (codeOptimization != null) WasmCodeOptimizationProperty()?.SetValue(null, codeOptimization); } catch { }
        }
    }
}
