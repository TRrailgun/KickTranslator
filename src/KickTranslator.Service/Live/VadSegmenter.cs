using KickTranslator.Audio;

namespace KickTranslator.Live;

/// <summary>
/// Silero VAD ile konuşmayı bulur ve altyazı parçalarına böler.
///  - Konuşma olmayan ses (müzik, oyun sesi, sessizlik) Whisper'a hiç gönderilmez; uydurma
///    altyazılar ("Thank you.", "♪") büyük ölçüde ortadan kalkar.
///  - Parça, konuşmadan sonra ~250 ms boşluk gelince kesilir. Çok kısa sözler ("yeah") bir sonraki
///    konuşmayla birleştirilir; ardından uzun boşluk gelirse tek başına gönderilir.
///  - Konuşma kesintisiz sürerse <c>maxSeconds</c>'ta, son 2 sn içindeki en düşük konuşma olasılığı
///    olan yerden (nefes arası) kesilir.
/// </summary>
public sealed class VadSegmenter : ISpeechSegmenter
{
    private const int Sr = AudioChunk.SampleRate;
    private const int F = SileroVad.FrameSamples;               // 32 ms
    private const float Threshold = 0.5f;                       // konuşma başladı
    private const float NegThreshold = 0.35f;                   // konuşma bitti (histerezis)
    private const int LongSilenceFrames = 25;                   // ~800 ms → kısa söz de tek başına gider
    private const int PadFrames = 3;                            // konuşmadan önce/sonra ~96 ms pay
    private const int MinSpeechFrames = 6;                      // ~190 ms'den kısa sesler gürültü sayılır

    private readonly SileroVad.Stream _vad;
    private readonly int _minFrames, _maxFrames;
    private readonly int _endSilenceFrames;   // bu kadar boşluk → parça biter (8 ≈ 256 ms)

    // Henüz çerçeveye tamamlanmamış örnekler
    private readonly List<float> _residue = new(F * 2);
    private double _residueStart;
    private double _expectedNext = double.NaN;

    // Konuşma dışındayken son birkaç çerçeve (konuşmanın başı kırpılmasın)
    private readonly Queue<(double Time, float[] Samples)> _pre = new();

    // Açık parça: çerçeveler, olasılıklar
    private readonly List<float[]> _frames = new();
    private readonly List<float> _probs = new();
    private double _segStart;
    private bool _inSegment;
    private int _speechFrames;
    private int _silenceRun;

    public string Kind => "vad";
    public double SpeechSeconds { get; private set; }

    public VadSegmenter(SileroVad.Stream vad, double minSeconds = 1.5, double maxSeconds = 6.0, int endSilenceFrames = 8)
    {
        _vad = vad;
        _endSilenceFrames = Math.Max(3, endSilenceFrames);
        _minFrames = Math.Max(1, (int)(minSeconds * Sr / F));
        _maxFrames = Math.Max(_minFrames + 4, (int)(maxSeconds * Sr / F));
    }

    public IEnumerable<AudioChunk> Push(double time, ReadOnlySpan<float> samples)
    {
        var result = new List<AudioChunk>();

        // Zaman sıçraması (sarma, duraklatma, yayın yeniden bağlanması) → her şeyi sıfırla
        if (!double.IsNaN(_expectedNext) && Math.Abs(time - _expectedNext) > 1.0)
            Reset();

        if (_residue.Count == 0) _residueStart = time;
        _residue.AddRange(samples.ToArray());
        _expectedNext = time + samples.Length / (double)Sr;

        while (_residue.Count >= F)
        {
            var frame = _residue.GetRange(0, F).ToArray();
            _residue.RemoveRange(0, F);
            var frameTime = _residueStart;
            _residueStart += F / (double)Sr;
            var p = _vad.Process(frame);
            OnFrame(frameTime, frame, p, result);
        }
        return result;
    }

    private void OnFrame(double t, float[] frame, float p, List<AudioChunk> output)
    {
        if (!_inSegment)
        {
            if (p >= Threshold)
            {
                _inSegment = true;
                _frames.Clear();
                _probs.Clear();
                _segStart = _pre.Count > 0 ? _pre.Peek().Time : t;
                foreach (var (_, s) in _pre) { _frames.Add(s); _probs.Add(0); }
                _pre.Clear();
                _frames.Add(frame);
                _probs.Add(p);
                _speechFrames = 1;
                _silenceRun = 0;
            }
            else
            {
                _pre.Enqueue((t, frame));
                while (_pre.Count > PadFrames) _pre.Dequeue();
            }
            return;
        }

        _frames.Add(frame);
        _probs.Add(p);
        if (p >= Threshold) { _speechFrames++; _silenceRun = 0; }
        else if (p < NegThreshold) _silenceRun++;

        // Konuşma bitti mi?
        if (_silenceRun >= _endSilenceFrames)
        {
            var speechLen = _frames.Count - _silenceRun;
            if (speechLen >= _minFrames || _silenceRun >= LongSilenceFrames)
            {
                Emit(_frames.Count - _silenceRun + PadFrames, output);
                CloseSegment();
            }
            return;
        }

        // Konuşma çok uzun sürdü: nefes arasından kes, kalan kısım açık parçada kalsın
        if (_frames.Count >= _maxFrames)
        {
            var from = Math.Max(_minFrames, _frames.Count - (2 * Sr / F));
            var best = _frames.Count - 1;
            for (var i = from; i < _frames.Count; i++)
                if (_probs[i] < _probs[best]) best = i;
            Emit(best + 1, output);
        }
    }

    /// <summary>Açık parçanın ilk <paramref name="count"/> çerçevesini gönderir ve parçadan çıkarır.</summary>
    private void Emit(int count, List<AudioChunk> output)
    {
        count = Math.Clamp(count, 1, _frames.Count);
        var speech = 0;
        for (var i = 0; i < count; i++) if (_probs[i] >= Threshold) speech++;

        if (speech >= MinSpeechFrames)
        {
            var samples = new float[count * F];
            for (var i = 0; i < count; i++) _frames[i].CopyTo(samples, i * F);
            output.Add(new AudioChunk(_segStart, samples));
            SpeechSeconds += count * F / (double)Sr;
        }

        _frames.RemoveRange(0, count);
        _probs.RemoveRange(0, count);
        _segStart += count * F / (double)Sr;
        _speechFrames = Math.Max(0, _speechFrames - speech);
    }

    private void CloseSegment()
    {
        // Kalan çerçeveler (sondaki boşluk) bir sonraki konuşmanın ön payı olur
        _pre.Clear();
        var start = _segStart;
        for (var i = 0; i < _frames.Count; i++)
        {
            _pre.Enqueue((start + i * F / (double)Sr, _frames[i]));
            while (_pre.Count > PadFrames) _pre.Dequeue();
        }
        _frames.Clear();
        _probs.Clear();
        _inSegment = false;
        _speechFrames = 0;
        _silenceRun = 0;
    }

    public AudioChunk? Peek(double minSeconds = 1.0)
    {
        if (!_inSegment) return null;
        var count = _frames.Count - _silenceRun;
        if (count * F < minSeconds * Sr || _speechFrames < MinSpeechFrames) return null;
        var samples = new float[count * F];
        for (var i = 0; i < count; i++) _frames[i].CopyTo(samples, i * F);
        return new AudioChunk(_segStart, samples);
    }

    private void Reset()
    {
        _residue.Clear();
        _pre.Clear();
        _frames.Clear();
        _probs.Clear();
        _inSegment = false;
        _speechFrames = 0;
        _silenceRun = 0;
        _expectedNext = double.NaN;
        _vad.Reset();
    }
}
