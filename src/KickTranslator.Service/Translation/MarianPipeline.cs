namespace KickTranslator.Translation;

/// <summary>
/// Bir çeviri yönü için bir ya da daha fazla OPUS-MT modelini sırayla uygular
/// (ör. Türkçe→Japonca = Türkçe→İngilizce→Japonca).
/// </summary>
public sealed class MarianPipeline(IReadOnlyList<MarianTranslator> steps)
{
    public string Translate(string text)
    {
        foreach (var s in steps) text = s.Translate(text);
        return text;
    }
}
