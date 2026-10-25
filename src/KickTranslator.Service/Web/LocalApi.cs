using System.Diagnostics;
using KickTranslator.Asr;
using KickTranslator.Config;
using KickTranslator.Live;
using KickTranslator.Llm;
using KickTranslator.Models;
using KickTranslator.Packs;
using KickTranslator.Sessions;
using KickTranslator.Translation;
using Microsoft.Extensions.Options;

namespace KickTranslator.Web;

/// <summary>
/// Sadece bu bilgisayardan (127.0.0.1) erişilen yerel uç noktalar.
/// Eklenti ve kurulum sayfası bunları kullanır; başka web siteleri Origin kontrolüyle engellenir.
/// </summary>
public static class LocalApi
{
    public static void Map(WebApplication app)
    {
        var port = new Uri(app.Services.GetRequiredService<IOptions<TranslatorOptions>>().Value.Listen).Port;
        var allowedHosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };

        // Origin kontrolü: Kick sayfası (sadece /ws), eklenti, yerel kurulum sayfası.
        app.Use(async (ctx, next) =>
        {
            // DNS rebinding'e karşı: başka bir alan adı 127.0.0.1'e yönlendirilse bile Host başlığı tutmaz
            var host = ctx.Request.Host.Value ?? "";
            if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            // Sayfalar başka sitelerin çerçevesi içinde açılamasın (clickjacking), tür tahmini yapılmasın
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

            var origin = ctx.Request.Headers.Origin.ToString();
            var isWs = ctx.Request.Path.StartsWithSegments("/ws");
            if (!IsAllowedOrigin(origin, allowKick: isWs))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });

        app.MapGet("/", () => Results.Redirect("/setup"));
        app.MapGet("/setup", () => Results.Content(SetupPage.Html, "text/html; charset=utf-8"));

        app.MapGet("/health", async (WhisperEngine w, TranslatorRegistry tr, ModelDownloader md, SileroVad vad, LlamaServerHost llm,
                                   LanguagePacks packs) =>
        {
            return Results.Json(new
            {
                ok = true,
                version = AppInfo.Version,
                asr = new
                {
                    device = w.Plan.Device,
                    description = w.Plan.Description,
                    ready = w.AnyLoaded,
                    error = w.LastError,
                    model = w.ModelFor("en"),
                    vad = vad.Available,
                },
                translation = new
                {
                    enTr = tr.IsLoaded(Directions.EnTr),
                    trEn = tr.IsLoaded(Directions.TrEn),
                    jaTr = tr.IsLoaded(Directions.JaTr),
                    trJa = tr.IsLoaded(Directions.TrJa),
                    errors = tr.Errors,
                    llm = new
                    {
                        state = llm.State.ToString().ToLowerInvariant(), // notstarted | downloading | starting | ready | failed
                        error = llm.Error,
                        recommended = w.Plan.Fast,
                        installed = llm.IsInstalled,
                    },
                },
                packs = new
                {
                    ja = new { state = packs.State, error = packs.Error, sizeMb = await packs.SizeMbAsync() },
                },
                downloads = md.ProgressText,
                extensionPath = ExtensionPath(),
            });
        });

        // ---------- İsteğe bağlı dil paketleri (eklenti penceresinden) ----------
        app.MapPost("/packs/ja/download", (LanguagePacks packs) =>
        {
            packs.StartDownload();
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/packs/ja/remove", (LanguagePacks packs) =>
        {
            try { packs.Remove(); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        // Kaliteli çeviri modeli: sadece kullanıcı isterse iner (~2.5 GB)
        app.MapPost("/llm/download", (LlamaServerHost llm) =>
        {
            _ = llm.EnsureStartedAsync(allowDownload: true);
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/llm/remove", (LlamaServerHost llm) =>
        {
            try { llm.Remove(); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        // ---------- Kurulum yardımcıları ----------
        app.MapPost("/setup/open-extension-folder", () =>
        {
            var p = ExtensionPath();
            if (p == null) return Results.NotFound();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/setup/open-chrome-extensions", () =>
        {
            foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
            {
                try
                {
                    var page = exe == "msedge.exe" ? "edge://extensions" : "chrome://extensions";
                    Process.Start(new ProcessStartInfo(exe, page) { UseShellExecute = true });
                    return Results.Ok(new { ok = true, browser = exe });
                }
                catch { /* sıradakini dene */ }
            }
            return Results.Ok(new { ok = false });
        });

        // ---------- Çeviri bağlantısı ----------
        app.Map("/ws", async (HttpContext ctx, WhisperEngine w, TranslatorRegistry tr, SileroVad vad, LlamaServerHost llm,
                              IOptions<TranslatorOptions> opts, ILoggerFactory lf) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new TranslationSession(ws, w, tr, vad, llm, opts.Value, lf.CreateLogger("Session"));
            await session.RunAsync(ctx.RequestAborted);
        });
    }

    /// <summary>Eklenti kimliği: extension\manifest.json içindeki "key" alanından türetilir.</summary>
    private const string ExtensionId = "hianelcgfmofeanljigdbppihlpdcdle";

    private static bool IsAllowedOrigin(string origin, bool allowKick)
    {
        if (string.IsNullOrEmpty(origin)) return true; // tarayıcı dışı yerel istemci
        // Sadece bu uygulamanın eklentisi (kimliği manifest.json'daki "key" ile sabit). Chrome ve Edge aynı kimliği kullanır.
        if (origin.Equals("chrome-extension://" + ExtensionId, StringComparison.OrdinalIgnoreCase)) return true;
        if (origin.Equals("http://127.0.0.1:5757", StringComparison.OrdinalIgnoreCase)) return true;
        if (origin.Equals("http://localhost:5757", StringComparison.OrdinalIgnoreCase)) return true;
        // Sadece şifreli (https) Kick sayfaları; http alt alan adları ağda araya girilerek taklit edilebilir
        if (allowKick && origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      && (origin.Equals("https://kick.com", StringComparison.OrdinalIgnoreCase)
                          || origin.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>Kurulumda exe'nin yanındaki "extension" klasörü; geliştirmede depo kökündeki klasör.</summary>
    public static string? ExtensionPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "extension");
            if (File.Exists(Path.Combine(p, "manifest.json"))) return p;
        }
        return null;
    }
}
