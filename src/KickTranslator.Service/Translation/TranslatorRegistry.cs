using System.Collections.Concurrent;
using KickTranslator.Config;
using KickTranslator.Models;

namespace KickTranslator.Translation;

/// <summary>
/// OPUS-MT modellerini (opus-mt-en-tr, opus-mt-ja-tr...) bir kez yükler ve her çeviri yönü için
/// gereken zinciri kurar. İngilizce/Türkçe modelleri ilk ihtiyaçta indirilir; Japonca modelleri ise
/// sadece kullanıcı Japonca paketini indirdiyse kullanılır.
/// </summary>
public sealed class TranslatorRegistry : IDisposable
{
    private readonly ModelDownloader _models;
    private readonly ILogger<TranslatorRegistry> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<MarianTranslator>>> _items = new();

    /// <summary>Yön → neden kullanılamadığı (eklentide gösterilir).</summary>
    public ConcurrentDictionary<string, string> Errors { get; } = new();

    public TranslatorRegistry(ModelDownloader models, ILogger<TranslatorRegistry> log)
    {
        _models = models;
        _log = log;
    }

    public bool IsLoaded(string direction) =>
        Directions.ModelChain(direction).All(m =>
            _items.TryGetValue(m, out var l) && l.IsValueCreated && l.Value.IsCompletedSuccessfully);

    /// <summary>Yönün çevirmenini getirir; kullanılamıyorsa null döner (altyazı kaynak dilde kalır).</summary>
    public async Task<MarianPipeline?> GetAsync(string direction, CancellationToken ct)
    {
        direction = Directions.Normalize(direction);
        var chain = Directions.ModelChain(direction);

        if (Directions.NeedsJapanesePack(direction) && !chain.All(_models.HasMarian))
        {
            Errors[direction] = "Japonca paketi indirilmemiş (eklenti simgesinden indirebilirsiniz)";
            return null;
        }

        var steps = new List<MarianTranslator>();
        foreach (var model in chain)
        {
            var lazy = _items.GetOrAdd(model, m => new Lazy<Task<MarianTranslator>>(() => LoadAsync(m)));
            try
            {
                steps.Add(await lazy.Value.WaitAsync(ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Sonraki denemede tekrar indirmeyi denesin
                _items.TryRemove(new KeyValuePair<string, Lazy<Task<MarianTranslator>>>(model, lazy));
                Errors[direction] = ex.Message;
                return null;
            }
        }
        Errors.TryRemove(direction, out _);
        return new MarianPipeline(steps);
    }

    /// <summary>Paket kaldırılırken: modeli bellekten çıkarır ki klasörü silinebilsin.</summary>
    public void Unload(string model)
    {
        if (_items.TryRemove(model, out var l) && l.IsValueCreated && l.Value.IsCompletedSuccessfully)
            l.Value.Result.Dispose();
    }

    private async Task<MarianTranslator> LoadAsync(string model)
    {
        try
        {
            var dir = await _models.EnsureMarianAsync(model, CancellationToken.None);
            var t = await Task.Run(() => new MarianTranslator(dir));
            _log.LogInformation("Çeviri modeli yüklendi: {Model}", model);
            return t;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Çeviri modeli ({Model}) yüklenemedi: {Msg}", model, ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var l in _items.Values)
            if (l.IsValueCreated && l.Value.IsCompletedSuccessfully) l.Value.Result.Dispose();
    }
}
