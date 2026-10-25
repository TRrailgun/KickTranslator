using KickTranslator.Audio;

namespace KickTranslator.Live;

/// <summary>Tarayıcıdan gelen 16 kHz sesi altyazı parçalarına böler.</summary>
public interface ISpeechSegmenter
{
    /// <summary>Kısa adı (günlük için).</summary>
    string Kind { get; }

    /// <param name="time">İlk örneğin video zamanı (saniye).</param>
    IEnumerable<AudioChunk> Push(double time, ReadOnlySpan<float> samples);

    /// <summary>Henüz bitmemiş konuşmanın kopyası (taslak altyazı için); yoksa null.</summary>
    AudioChunk? Peek(double minSeconds = 1.0);

    /// <summary>Şu ana kadar konuşma olarak algılanan toplam süre (saniye).</summary>
    double SpeechSeconds { get; }
}
