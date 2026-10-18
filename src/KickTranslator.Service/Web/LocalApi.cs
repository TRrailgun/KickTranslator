using System.Diagnostics;
using KickTranslator.Asr;
using KickTranslator.Config;
using KickTranslator.Models;
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
        // Origin kontrolü: Kick sayfası (sadece /ws), eklenti, yerel kurulum sayfası.
        app.Use(async (ctx, next) =>
        {
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

        app.MapGet("/health", (WhisperEngine w, TranslatorRegistry tr, ModelDownloader md) =>
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
                },
                translation = new
                {
                    enTr = tr.IsLoaded(Directions.EnTr),
                    trEn = tr.IsLoaded(Directions.TrEn),
                    errors = tr.Errors,
                },
                downloads = md.ProgressText,
                extensionPath = ExtensionPath(),
            });
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
        app.Map("/ws", async (HttpContext ctx, WhisperEngine w, TranslatorRegistry tr,
                              IOptions<TranslatorOptions> opts, ILoggerFactory lf) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new TranslationSession(ws, w, tr, opts.Value, lf.CreateLogger("Session"));
            await session.RunAsync(ctx.RequestAborted);
        });
    }

    private static bool IsAllowedOrigin(string origin, bool allowKick)
    {
        if (string.IsNullOrEmpty(origin)) return true; // tarayıcı dışı yerel istemci
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return true;
        if (origin.Equals("http://127.0.0.1:5757", StringComparison.OrdinalIgnoreCase)) return true;
        if (origin.Equals("http://localhost:5757", StringComparison.OrdinalIgnoreCase)) return true;
        if (allowKick && (origin.Equals("https://kick.com", StringComparison.OrdinalIgnoreCase)
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
