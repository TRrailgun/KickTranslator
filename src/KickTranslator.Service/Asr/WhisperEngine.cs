using System.Collections.Concurrent;
using KickTranslator.Config;
using KickTranslator.Hardware;
using KickTranslator.Models;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace KickTranslator.Asr;

/// <summary>
/// Whisper modellerini yönetir. Her model dosyası bir kez yüklenir; oturumlar kendi
/// processor'larını alır. Kaynak dile göre farklı model gerekebilir (APU'da en: small.en, tr: small).
/// </summary>
public sealed class WhisperEngine : IDisposable
{
    private readonly TranslatorOptions _opts;
    private readonly ModelDownloader _models;
    private readonly ILogger<WhisperEngine> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<WhisperFactory>>> _factories = new();

    public HardwarePlan Plan { get; }
    public string? LastError { get; private set; }
    public bool AnyLoaded => _factories.Values.Any(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully);

    public WhisperEngine(IOptions<TranslatorOptions> opts, ModelDownloader models, ILogger<WhisperEngine> log)
    {
        _opts = opts.Value;
        _models = models;
        _log = log;
        Plan = HardwareProbe.Decide(_opts);

        // whisper.cpp yerel kütüphanesi bu sırayla denenir; ilk yüklenebilen kullanılır.
        RuntimeOptions.RuntimeLibraryOrder = Plan.RuntimeOrder.ToList();
        _log.LogInformation("Donanım: {Desc} ({Device})", Plan.Description, Plan.Device);
    }

    public string ModelFor(string sourceLang) => HardwareProbe.WhisperModelFor(Plan, sourceLang, _opts);

    /// <summary>Uygulama açılırken varsayılan dil için modeli önceden hazırlar.</summary>
    public async Task WarmupAsync(string sourceLang, CancellationToken ct)
    {
        try { await GetFactoryAsync(sourceLang, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Ön yükleme başarısız: {Msg}", ex.Message);
        }
    }

    /// <param name="live">Canlı modda önceki metin bağlam olarak kullanılmaz (taslaklar tekrar döngüsüne sokmasın).</param>
    public async Task<WhisperProcessor> CreateProcessorAsync(string sourceLang, CancellationToken ct, bool live = false)
    {
        var factory = await GetFactoryAsync(sourceLang, ct);
        var b = factory.CreateBuilder()
            .WithLanguage(sourceLang)
            .WithThreads(Plan.Threads);
        if (live) b = b.WithNoContext();
        return b.Build();
    }

    private Task<WhisperFactory> GetFactoryAsync(string sourceLang, CancellationToken ct)
    {
        var file = ModelFor(sourceLang);
        var lazy = _factories.GetOrAdd(file, f => new Lazy<Task<WhisperFactory>>(() => LoadAsync(f)));
        var task = lazy.Value;
        if (task.IsFaulted) // başarısız yüklemeyi bir sonraki istekte yeniden dene
        {
            _factories.TryRemove(new KeyValuePair<string, Lazy<Task<WhisperFactory>>>(file, lazy));
            lazy = _factories.GetOrAdd(file, f => new Lazy<Task<WhisperFactory>>(() => LoadAsync(f)));
            task = lazy.Value;
        }
        return task.WaitAsync(ct);
    }

    private async Task<WhisperFactory> LoadAsync(string file)
    {
        try
        {
            var path = await _models.EnsureWhisperAsync(file, CancellationToken.None);
            _log.LogInformation("Whisper modeli yükleniyor: {File}", file);
            var f = WhisperFactory.FromPath(path);
            LastError = null;
            return f;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Whisper modeli yüklenemedi: {File}", file);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var l in _factories.Values)
            if (l.IsValueCreated && l.Value.IsCompletedSuccessfully) l.Value.Result.Dispose();
    }
}
