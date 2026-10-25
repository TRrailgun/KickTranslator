using KickTranslator.Audio;

namespace KickTranslator.Live;

/// <summary>
/// Canlı yayında tarayıcıdan gelen 16 kHz sesi konuşma parçalarına böler.
/// Parça; en az MinSeconds uzunlukta olup ardından ~240 ms sessizlik gelince,
/// ya da MaxSeconds'a ulaşınca (en sessiz noktadan) kesilir.
/// </summary>
public sealed class LiveSegmenter : ISpeechSegmenter
{
    public string Kind => "energy";
    public double SpeechSeconds { get; private set; }

    private const int Sr = HlsAudioReader.SampleRate;
    private const int Frame = Sr * 30 / 1000;      // 30 ms
    private const int SilenceFrames = 8;           // 240 ms

    private readonly List<float> _buf = new(Sr * 10);
    private readonly int _min, _max;
    private double _bufStart;
    private double _expectedNext = double.NaN;

    public LiveSegmenter(double minSeconds = 1.5, double maxSeconds = 6.0)
    {
        _min = (int)(minSeconds * Sr);
        _max = (int)(maxSeconds * Sr);
    }

    /// <param name="time">İlk örneğin video zamanı (saniye).</param>
    public IEnumerable<AudioChunk> Push(double time, ReadOnlySpan<float> samples)
    {
        var result = new List<AudioChunk>();

        // Zaman sıçraması (sarma / duraklatma sonrası) → eski tamponu at.
        if (_buf.Count > 0 && Math.Abs(time - _expectedNext) > 1.0)
            _buf.Clear();

        if (_buf.Count == 0) _bufStart = time;
        _buf.AddRange(samples.ToArray());
        _expectedNext = time + samples.Length / (double)Sr;

        int cut;
        while ((cut = FindCut()) > 0)
        {
            result.Add(new AudioChunk(_bufStart, _buf.GetRange(0, cut).ToArray()));
            SpeechSeconds += cut / (double)Sr;
            _buf.RemoveRange(0, cut);
            _bufStart += cut / (double)Sr;
        }
        return result;
    }

    /// <summary>
    /// Henüz bitmemiş (kesilmemiş) konuşmanın kopyası — taslak altyazı için.
    /// En az minSeconds ses yoksa veya tampon sessizse null döner.
    /// </summary>
    public AudioChunk? Peek(double minSeconds = 1.0)
    {
        if (_buf.Count < minSeconds * Sr) return null;
        var copy = _buf.ToArray();
        return Rms(copy) < 0.003 ? null : new AudioChunk(_bufStart, copy);
    }

    public void Reset()
    {
        _buf.Clear();
        _expectedNext = double.NaN;
    }

    private int FindCut()
    {
        var n = _buf.Count;
        if (n < _min) return 0;

        var frames = n / Frame;
        var rms = new double[frames];
        double sum = 0;
        for (var f = 0; f < frames; f++)
        {
            double e = 0;
            for (var i = f * Frame; i < (f + 1) * Frame; i++) e += _buf[i] * _buf[i];
            rms[f] = Math.Sqrt(e / Frame);
            sum += rms[f];
        }
        var thr = Math.Max(0.004, 0.25 * (sum / Math.Max(1, frames)));

        // _min'den sonra gelen ilk yeterince uzun sessizlik
        var run = 0;
        for (var f = _min / Frame; f < frames; f++)
        {
            run = rms[f] < thr ? run + 1 : 0;
            if (run >= SilenceFrames)
                return (f - SilenceFrames / 2) * Frame;
        }

        if (n < _max) return 0;

        // Sınır aşıldı: son 2 sn içindeki en sessiz çerçeveden kes
        var from = Math.Max(0, frames - (2 * Sr / Frame));
        var best = frames - 1;
        for (var f = from; f < frames; f++)
            if (rms[f] < rms[best]) best = f;
        return Math.Max(Frame, best * Frame + Frame / 2);
    }

    public static double Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        double e = 0;
        foreach (var v in s) e += v * v;
        return Math.Sqrt(e / s.Length);
    }
}
