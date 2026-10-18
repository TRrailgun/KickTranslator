using System.Diagnostics;
using System.Globalization;
using KickTranslator.Asr;
using KickTranslator.Config;
using KickTranslator.Logging;
using KickTranslator.Models;
using KickTranslator.Translation;
using KickTranslator.Tray;
using KickTranslator.Web;

namespace KickTranslator;

internal static class Program
{
    private const string MutexName = @"Local\KickCeviri.Service";

    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        var openSetup = args.Contains("--setup");

        // Tek örnek: zaten çalışıyorsa sadece kurulum sayfasını aç ve çık.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            if (openSetup) OpenUrl("http://127.0.0.1:5757/setup");
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Windows açılışında çalışma dizini System32 olur; ayarları exe'nin yanından oku.
            ContentRootPath = AppContext.BaseDirectory,
        });

        var opts = builder.Configuration.GetSection("Translator").Get<TranslatorOptions>() ?? new TranslatorOptions();
        builder.Services.Configure<TranslatorOptions>(builder.Configuration.GetSection("Translator"));

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        // Visual Studio'dan çalıştırırken açılan konsol penceresinde günlükleri göster
        if (builder.Environment.IsDevelopment() || Debugger.IsAttached)
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.AddProvider(new FileLoggerProvider(opts.LogsDir));

        builder.Services.AddSingleton<ModelDownloader>();
        builder.Services.AddSingleton<WhisperEngine>();
        builder.Services.AddSingleton<TranslatorRegistry>();
        builder.Services.AddHostedService<Warmup>();

        builder.WebHost.UseUrls(opts.Listen);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        LocalApi.Map(app);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            MessageBox.Show(
                $"Kick Çeviri başlatılamadı: {opts.Listen} adresi başka bir uygulama tarafından kullanılıyor.\n\n{ex.Message}",
                "Kick Çeviri", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        app.Logger.LogInformation("Kick Çeviri başladı: {Url} ({Version})", opts.Listen, AppInfo.Version);
        if (openSetup) OpenUrl("http://127.0.0.1:5757/setup");

        using (var tray = new TrayApp(app.Services))
            Application.Run(tray);

        app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return 0;
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* tarayıcı açılamadı */ }
    }
}

internal static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

/// <summary>Açılışta modelleri arka planda hazırlar ki ilk video beklemesin.</summary>
internal sealed class Warmup(WhisperEngine whisper, TranslatorRegistry translators) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1500, stoppingToken);
        await Task.WhenAll(
            whisper.WarmupAsync("en", stoppingToken),
            translators.GetAsync(Directions.EnTr, stoppingToken));
    }
}
