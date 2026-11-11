using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Kovu's Pride · Build web para el portfolio (launcher en alejandrobaenaportfolio.com).
// Compila las dos versiones web y las PUBLICA en el portfolio:
//   <portfolio>/content/games/<slug>/web/mobile  (texturas ASTC, móviles y tabletas)
//   <portfolio>/content/games/<slug>/web/pc      (texturas DXT, ordenadores)
// Cada carpeta lleva un play.json con los nombres de los archivos, que lee la página /play/<slug>.
// <portfolio> es el clon del repositorio AleBaena2306/portfolio (por defecto Documentos\Portfolio\portfolio).
// Al terminar hace git pull, commit y push de esa carpeta: Cloudflare publica la web en un minuto.
//
// Cada juego declara sus menús con una clase pequeña en su carpeta Editor, por ejemplo:
//   static readonly WebGame Game = new WebGame("trickshot-defense", "Trickshot Defense");
//   [MenuItem("TRICKSHOT/Build/Web (móvil + PC)")] static void Web() => KovusPrideWebBuild.BuildAndPublish(Game);
// (ver README del módulo Web).
namespace KovusPride.Web.Editor
{
public sealed class WebGame
{
    public readonly string Slug;   // carpeta en content/games y ruta /play/<slug>
    public readonly string Name;   // nombre visible en los diálogos y en el mensaje del commit
    public WebGame(string slug, string name) { Slug = slug; Name = name; }
}

public static class KovusPrideWebBuild
{
    private const string PortfolioPathKey = "KovusPrideWebBuild.PortfolioPath";
    private static string GameSlug = "";
    private static string Title = "Build web";
    private static string GameName = "";

    private static void Use(WebGame game)
    {
        GameSlug = game.Slug;
        GameName = game.Name;
        Title = game.Name + " · Build web";
    }

    public static void BuildAndPublish(WebGame game) { Use(game); Run(publish: true); }

    public static void BuildOnly(WebGame game) { Use(game); Run(publish: false); }

    // Prueba rápida: solo la versión de PC, optimizada para compilar rápido, en <proyecto>/Builds/WebTest (no se publica)
    public static void QuickTest(WebGame game)
    {
        Use(game);
        if (!ScriptsUpToDate()) return;
        if (!EnsureWebPlatform()) return;
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "WebTest"));
        if (!BuildVariant("pc", WebGLTextureSubtarget.DXT, outDir, quick: true)) return;
        Debug.Log($"[WebBuild] Prueba lista en {outDir}. Para abrirla: en esa carpeta, «python -m http.server 8000» y http://localhost:8000");
        EditorUtility.RevealInFinder(Path.Combine(outDir, "index.html"));
    }

    public static void PublishOnly(WebGame game)
    {
        Use(game);
        string portfolio = GetPortfolioPath();
        if (string.IsNullOrEmpty(portfolio)) return;
        Publish(portfolio, PlayerSettings.bundleVersion);
    }

    [MenuItem("Kovu's Pride/Web/Cambiar carpeta del portfolio…")]
    public static void ChangePortfolioPath()
    {
        EditorPrefs.DeleteKey(PortfolioPathKey);
        GetPortfolioPath(askIfMissing: true);
    }

    // Si los scripts han cambiado en disco (por ejemplo, tras un pull con Unity abierto) y el Editor aún no los ha
    // recompilado, la build falla con «script class layout is incompatible». Antes de compilar se refrescan los
    // assets: si hay que recompilar, se avisa y se para; al terminar la compilación basta con volver a pulsar.
    private static bool ScriptsUpToDate()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        if (EditorApplication.isCompiling)
        {
            Debug.LogWarning("[WebBuild] Había cambios en los scripts y Unity los está recompilando. Cuando termine, vuelve a lanzar la build.");
            EditorUtility.DisplayDialog(Title, "Unity está recompilando los scripts que han cambiado.\n\nCuando termine (sin la rueda de carga abajo a la derecha), vuelve a lanzar la build.", "OK");
            return false;
        }
        if (EditorUtility.scriptCompilationFailed)
        {
            Debug.LogError("[WebBuild] Hay errores de compilación en la consola: corrígelos antes de compilar la web.");
            EditorUtility.DisplayDialog(Title, "Hay errores de compilación en la consola. Corrígelos antes de compilar la web.", "OK");
            return false;
        }
        return true;
    }

    private static void Run(bool publish)
    {
        if (!ScriptsUpToDate()) return;
        string portfolio = GetPortfolioPath();
        if (string.IsNullOrEmpty(portfolio)) return;
        if (!EnsureWebPlatform()) return;

        // Antes de compilar, el portfolio al día (por si se cambió desde otro sitio)
        if (publish && IsGitRepo(portfolio)) Git(portfolio, "pull --rebase --autostash", "Actualizando el portfolio…", logOnly: true);

        string root = Path.Combine(portfolio, "content", "games", GameSlug, "web");
        bool mobile = BuildVariant("mobile", WebGLTextureSubtarget.ASTC, Path.Combine(root, "mobile"));
        bool pc = mobile && BuildVariant("pc", WebGLTextureSubtarget.DXT, Path.Combine(root, "pc"));
        if (!mobile || !pc) return;

        Debug.Log($"[WebBuild] Builds listas en {root}.");
        if (publish) Publish(portfolio, PlayerSettings.bundleVersion);
        else EditorUtility.RevealInFinder(root);
    }

    private static bool EnsureWebPlatform()
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL) return true;
        bool ok = EditorUtility.DisplayDialog(Title,
            "Hay que cambiar la plataforma a Web. Unity reimportará los assets y puede tardar varios minutos.\n\n" +
            "Al terminar, la plataforma se queda en Web: para la build de Android, vuelve a Android en Build Profiles.",
            "Continuar", "Cancelar");
        if (!ok) return false;
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
        {
            Debug.LogError("[WebBuild] No se pudo cambiar la plataforma a Web. ¿Está instalado el módulo Web Build Support?");
            return false;
        }
        return true;
    }

    // ---------------- Carpeta del portfolio ----------------

    private static string DefaultPortfolioPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Portfolio", "portfolio");

    private static string GetPortfolioPath(bool askIfMissing = true)
    {
        string path = EditorPrefs.GetString(PortfolioPathKey, "");
        // La carpeta buena es el clon de git; si la guardada no lo es y existe el clon por defecto, se usa ese
        if (IsPortfolio(path) && IsGitRepo(path)) return path;
        if (IsPortfolio(DefaultPortfolioPath) && IsGitRepo(DefaultPortfolioPath))
        {
            EditorPrefs.SetString(PortfolioPathKey, DefaultPortfolioPath);
            return DefaultPortfolioPath;
        }
        if (IsPortfolio(path)) return path;   // sin git: compila, pero no podrá publicar
        if (!askIfMissing) return null;

        path = EditorUtility.OpenFolderPanel("Carpeta del repositorio del portfolio (la que contiene build.js)", "", "");
        if (string.IsNullOrEmpty(path)) return null;
        if (!IsPortfolio(path))
        {
            EditorUtility.DisplayDialog(Title,
                "Esa carpeta no parece el repositorio del portfolio: no contiene build.js ni content/games.", "Vale");
            return null;
        }
        EditorPrefs.SetString(PortfolioPathKey, path);
        return path;
    }

    private static bool IsPortfolio(string path) =>
        !string.IsNullOrEmpty(path) &&
        File.Exists(Path.Combine(path, "build.js")) &&
        Directory.Exists(Path.Combine(path, "content", "games"));

    private static bool IsGitRepo(string path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(Path.Combine(path, ".git"));

    // ---------------- Compilación ----------------

    // quick: para pruebas. Compila para tardar poco (código menos optimizado y compresión rápida)
    private static bool BuildVariant(string variant, WebGLTextureSubtarget textures, string outDir, bool quick = false)
    {
        EditorUserBuildSettings.webGLBuildSubtarget = textures;
        // IL2CPP «Faster (smaller) builds»: menos código C++ generado, compila bastante antes y el .wasm pesa menos.
        // En juegos casuales la diferencia de rendimiento no se nota. Solo afecta a la build web.
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
        // Optimización del código web: en pruebas, la más rápida de compilar; al publicar, la de por defecto
        EditorUserBuildSettings.SetPlatformSettings(BuildPipeline.GetBuildTargetName(BuildTarget.WebGL), "CodeOptimization", quick ? "BuildTimes" : "RuntimeSpeed");
        PlayerSettings.WebGL.compressionFormat = quick ? WebGLCompressionFormat.Gzip : WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = true;  // funciona aunque el servidor no envíe Content-Encoding
        PlayerSettings.WebGL.dataCaching = true;              // segunda carga instantánea
        PlayerSettings.WebGL.nameFilesAsHashes = false;
        PlayerSettings.WebGL.template = "APPLICATION:Minimal";

        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        Debug.Log($"[WebBuild] Compilando versión {variant} ({textures}) en {outDir}…");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[WebBuild] La versión {variant} ha fallado ({report.summary.result}, {report.summary.totalErrors} errores). Revisa la consola.");
            return false;
        }

        CleanOutput(outDir);
        WriteManifest(outDir, variant);
        Debug.Log($"[WebBuild] Versión {variant} lista: {report.summary.totalSize / (1024f * 1024f):0.0} MB.");
        return true;
    }

    // Fuera lo que la web no necesita: la configuración de Firebase para escritorio (la web usa la suya).
    // Y dentro lo que solo usa la web: <proyecto>/WebAssets/StreamingAssets (p. ej. el vídeo del logo en MP4,
    // porque en el navegador no se pueden reproducir los VideoClip importados). Así no ocupa en Android.
    private static void CleanOutput(string outDir)
    {
        string streaming = Path.Combine(outDir, "StreamingAssets");
        string desktopConfig = Path.Combine(streaming, "google-services-desktop.json");
        if (File.Exists(desktopConfig)) File.Delete(desktopConfig);

        string webOnly = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "WebAssets", "StreamingAssets"));
        if (!Directory.Exists(webOnly)) return;
        foreach (string src in Directory.GetFiles(webOnly, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(streaming, src.Substring(webOnly.Length).TrimStart('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
        }
    }

    // play.json: los nombres exactos de los archivos de Build/, para que la web no tenga que adivinarlos
    private static void WriteManifest(string outDir, string variant)
    {
        string buildDir = Path.Combine(outDir, "Build");
        string Find(string suffix) =>
            Directory.GetFiles(buildDir)
                .Select(Path.GetFileName)
                .FirstOrDefault(f => f.Contains(suffix)) ?? "";

        string json =
            "{\n" +
            $"  \"variant\": \"{variant}\",\n" +
            $"  \"version\": \"{PlayerSettings.bundleVersion}\",\n" +
            $"  \"loader\": \"Build/{Find(".loader.js")}\",\n" +
            $"  \"framework\": \"Build/{Find(".framework.js")}\",\n" +
            $"  \"data\": \"Build/{Find(".data")}\",\n" +
            $"  \"code\": \"Build/{Find(".wasm")}\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(outDir, "play.json"), json);
    }

    // ---------------- Publicación (git) ----------------

    private static void Publish(string portfolio, string version)
    {
        if (!IsGitRepo(portfolio))
        {
            EditorUtility.DisplayDialog(GameName + " · Publicar",
                "La carpeta del portfolio no es un repositorio de git, así que no se puede publicar desde aquí.\n\n" +
                "Usa Kovu's Pride > Web > Cambiar carpeta del portfolio… y elige el clon de AleBaena2306/portfolio.", "Vale");
            return;
        }
        if (FindGit() == null)
        {
            EditorUtility.DisplayDialog(GameName + " · Publicar",
                "No se ha encontrado git en este ordenador. Instala Git for Windows (git-scm.com) o GitHub Desktop y vuelve a intentarlo con " +
                "<JUEGO> > Build > Web: publicar la última build.", "Vale");
            return;
        }

        string gamePath = $"content/games/{GameSlug}/web";
        try
        {
            if (!Git(portfolio, $"add -A -- \"{gamePath}\"", "Preparando los archivos…")) return;

            var status = RunGit(portfolio, $"status --porcelain -- \"{gamePath}\"");
            if (status.code == 0 && string.IsNullOrWhiteSpace(status.output))
            {
                EditorUtility.DisplayDialog(GameName + " · Publicar", "No hay cambios que publicar: la web ya tiene esta build.", "Vale");
                return;
            }

            string message = $"{GameName} web {version} ({DateTime.Now:yyyy-MM-dd HH:mm})";
            if (!Git(portfolio, $"commit -m \"{message}\" -- \"{gamePath}\"", "Guardando la build…")) return;
            if (!Git(portfolio, "pull --rebase --autostash", "Sincronizando con GitHub…")) return;
            if (!Git(portfolio, "push", "Subiendo a GitHub (puede tardar)…", timeoutSeconds: 900)) return;

            Debug.Log($"[WebBuild] Publicado: {message}. Cloudflare actualiza la web en un minuto (alejandrobaenaportfolio.com/play/{GameSlug}).");
            EditorUtility.DisplayDialog(GameName + " · Publicar",
                $"Build {version} subida a GitHub.\n\nCloudflare la publica en alejandrobaenaportfolio.com/play/{GameSlug} en un minuto aproximadamente.", "Vale");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // Ejecuta git y avisa si falla. logOnly: el fallo solo se registra (no interrumpe)
    private static bool Git(string repo, string args, string progress, int timeoutSeconds = 300, bool logOnly = false)
    {
        EditorUtility.DisplayProgressBar(GameName + " · Publicar", progress, 0.5f);
        var r = RunGit(repo, args, timeoutSeconds);
        EditorUtility.ClearProgressBar();
        if (r.code == 0)
        {
            if (!string.IsNullOrWhiteSpace(r.output)) Debug.Log($"[WebBuild] git {args}\n{r.output}");
            return true;
        }

        Debug.LogWarning($"[WebBuild] git {args} ha fallado ({r.code}):\n{r.output}");
        if (!logOnly)
            EditorUtility.DisplayDialog(GameName + " · Publicar",
                $"Ha fallado «git {args.Split(' ')[0]}». La build está en la carpeta del portfolio, pero no se ha publicado.\n\n" +
                $"{Trim(r.output, 600)}\n\nCuando esté resuelto: <JUEGO> > Build > Web: publicar la última build.", "Vale");
        return false;
    }

    private static (int code, string output) RunGit(string repo, string args, int timeoutSeconds = 300)
    {
        string git = FindGit();
        if (git == null) return (-1, "git no encontrado");
        try
        {
            var psi = new ProcessStartInfo(git, args)
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutSeconds * 1000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return (-2, $"git {args}: tiempo agotado ({timeoutSeconds} s)");
                }
                p.WaitForExit();
                return (p.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
            }
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    private static string cachedGit;

    // git del PATH, el de Git for Windows o el que trae GitHub Desktop
    private static string FindGit()
    {
        if (cachedGit != null) return cachedGit;
        var candidates = new System.Collections.Generic.List<string> { "git" };
        candidates.Add(@"C:\Program Files\Git\cmd\git.exe");
        candidates.Add(@"C:\Program Files (x86)\Git\cmd\git.exe");
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubDesktop");
        if (Directory.Exists(desktop))
            foreach (var app in Directory.GetDirectories(desktop, "app-*").OrderByDescending(d => d))
                candidates.Add(Path.Combine(app, "resources", "app", "git", "cmd", "git.exe"));

        foreach (var c in candidates)
        {
            if (c != "git" && !File.Exists(c)) continue;
            try
            {
                var psi = new ProcessStartInfo(c, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(10000);
                    if (p.HasExited && p.ExitCode == 0) return cachedGit = c;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
}
}
