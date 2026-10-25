using System.Text.Json;
using KickTranslator.Asr;
using KickTranslator.Config;
using KickTranslator.Models;
using KickTranslator.Translation;
using Microsoft.Extensions.Options;

namespace KickTranslator.Packs;

/// <summary>
/// İsteğe bağlı Japonca paketi: Japonca⇄Türkçe çeviri modelleri (ve zayıf donanımda çok dilli konuşma
/// tanıma modeli). Kendiliğinden inmez; kullanıcı eklenti penceresinden "İndir" derse iner, "Kaldır"
/// derse diskten silinir.
/// </summary>
public sealed class LanguagePacks(ModelDownloader models, WhisperEngine whisper, TranslatorRegistry translators,
                                  IOptions<TranslatorOptions> opts, ILogger<LanguagePacks> log)
{
    private readonly object _gate = new();
    private Task? _job;
    private double? _sizeMb;
    private DateTime _sizeCheckedAt = DateTime.MinValue;

    public string? Error { get; private set; }

    private string WhisperFile => whisper.ModelFor("ja");

    public bool Installed =>
        Directions.JapanesePackModels.All(models.HasMarian) && models.HasWhisper(WhisperFile);

    public string State =>
        _job is { IsCompleted: false } ? "downloading"
        : Installed ? "installed"
        : Error != null ? "failed"
        : "notinstalled";

    public void StartDownload()
    {
        lock (_gate)
        {
            if (_job is { IsCompleted: false }) return;
            Error = null;
            _job = Task.Run(async () =>
            {
                try
                {
                    log.LogInformation("Japonca paketi indiriliyor.");
                    foreach (var m in Directions.JapanesePackModels)
                        await models.EnsureMarianAsync(m, CancellationToken.None);
                    await models.EnsureWhisperAsync(WhisperFile, CancellationToken.None);
                    log.LogInformation("Japonca paketi hazır.");
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    log.LogWarning("Japonca paketi indirilemedi: {Msg}", ex.Message);
                }
            });
        }
    }

    public void Remove()
    {
        lock (_gate)
        {
            if (_job is { IsCompleted: false }) throw new InvalidOperationException("İndirme sürerken kaldırılamaz.");
            // tr-en modeli Türkçe→İngilizce için de kullanıldığından kalır
            foreach (var m in new[] { "ja-tr", "en-ja" })
            {
                translators.Unload(m);
                models.DeleteMarian(m);
            }
            Error = null;
            log.LogInformation("Japonca paketi kaldırıldı.");
        }
    }

    /// <summary>Paketin yaklaşık indirme boyutu (yayınlanan models.json'dan; bulunamazsa null).</summary>
    public async Task<double?> SizeMbAsync()
    {
        if (_sizeMb != null || DateTime.UtcNow - _sizeCheckedAt < TimeSpan.FromMinutes(10)) return _sizeMb;
        _sizeCheckedAt = DateTime.UtcNow;
        var baseUrl = opts.Value.TranslationModelsBaseUrl?.Trim();
        if (string.IsNullOrEmpty(baseUrl)) return null;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + "models.json"));
            long total = 0;
            foreach (var m in Directions.JapanesePackModels)
                if (!models.HasMarian(m) && doc.RootElement.TryGetProperty($"opus-mt-{m}.zip", out var e) &&
                    e.TryGetProperty("size", out var sz))
                    total += sz.GetInt64();
            _sizeMb = Math.Round(total / 1024.0 / 1024.0);
        }
        catch { /* boyut gösterilmez */ }
        return _sizeMb;
    }
}
