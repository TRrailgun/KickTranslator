using System.Collections.Concurrent;
using KickTranslator.Config;
using KickTranslator.Models;

namespace KickTranslator.Translation;

/// <summary>Her çeviri yönü için (en-tr, tr-en) tek bir Marian örneği; ilk ihtiyaçta indirilir/yüklenir.</summary>
public sealed class TranslatorRegistry : IDisposable
{
    private readonly ModelDownloader _models;
    private readonly ILogger<TranslatorRegistry> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<MarianTranslator>>> _items = new();

    public ConcurrentDictionary<string, string> Errors { get; } = new();

    public TranslatorRegistry(ModelDownloader models, ILogger<TranslatorRegistry> log)
    {
        _models = models;
        _log = log;
    }

    public bool IsLoaded(string direction) =>
        _items.TryGetValue(direction, out var l) && l.IsValueCreated && l.Value.IsCompletedSuccessfully;

    /// <summary>Çevirmeni getirir; model yoksa ve indirilemezse null döner (altyazı kaynak dilde kalır).</summary>
    public async Task<MarianTranslator?> GetAsync(string direction, CancellationToken ct)
    {
        direction = Directions.Normalize(direction);
        var lazy = _items.GetOrAdd(direction, d => new Lazy<Task<MarianTranslator>>(() => LoadAsync(d)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Sonraki denemede tekrar indirmeyi denesin
            _items.TryRemove(new KeyValuePair<string, Lazy<Task<MarianTranslator>>>(direction, lazy));
            return null;
        }
    }

    private async Task<MarianTranslator> LoadAsync(string direction)
    {
        try
        {
            var dir = await _models.EnsureMarianAsync(direction, CancellationToken.None);
            var t = await Task.Run(() => new MarianTranslator(dir));
            Errors.TryRemove(direction, out _);
            _log.LogInformation("Çeviri modeli yüklendi: {Dir}", direction);
            return t;
        }
        catch (Exception ex)
        {
            Errors[direction] = ex.Message;
            _log.LogWarning("Çeviri modeli ({Dir}) yüklenemedi: {Msg}", direction, ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var l in _items.Values)
            if (l.IsValueCreated && l.Value.IsCompletedSuccessfully) l.Value.Result.Dispose();
    }
}
