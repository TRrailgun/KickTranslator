namespace KickTranslator.Translation;

/// <summary>Bir altyazı satırını çevirir. <paramref name="context"/>: önceki satırlar (kaynak dilde, eskiden yeniye).</summary>
public interface ISubtitleTranslator
{
    string Name { get; }
    Task<string?> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken ct);
}

/// <summary>OPUS-MT: hızlı, CPU'da çalışır, bağlam kullanmaz.</summary>
public sealed class MarianSubtitleTranslator(MarianTranslator m) : ISubtitleTranslator
{
    public string Name => "opus";
    public MarianTranslator Model => m;

    public Task<string?> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken ct) =>
        Task.Run(() => (string?)m.Translate(text), ct);
}
