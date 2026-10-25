using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KickTranslator.Live;

/// <summary>
/// Silero VAD v5 (MIT lisansı, ~2 MB, Assets\silero_vad.onnx). Sesin içinde insan konuşması olup
/// olmadığını 32 ms'lik çerçevelerle söyler; müzik ve oyun sesini konuşmadan ayırır. CPU'da çok hızlıdır.
/// Model bir kez yüklenir; her tarayıcı bağlantısı kendi <see cref="Stream"/> durumunu taşır.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int FrameSamples = 512;   // 16 kHz'de 32 ms
    private const int ContextSamples = 64;

    private readonly InferenceSession? _session;
    private readonly ILogger<SileroVad> _log;

    public string? Error { get; }
    public bool Available => _session != null;

    public SileroVad(ILogger<SileroVad> log)
    {
        _log = log;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        try
        {
            var so = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            _session = new InferenceSession(path, so);
            _log.LogInformation("Konuşma algılama (Silero VAD) hazır.");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _log.LogWarning("Silero VAD yüklenemedi, ses seviyesine bakan eski yöntem kullanılacak: {Msg}", ex.Message);
        }
    }

    public Stream CreateStream() =>
        _session != null ? new Stream(_session) : throw new InvalidOperationException("VAD yok.");

    public void Dispose() => _session?.Dispose();

    /// <summary>Tek bir ses akışının VAD durumu (thread-safe değildir).</summary>
    public sealed class Stream
    {
        private readonly InferenceSession _session;
        private float[] _state = new float[2 * 1 * 128];
        private readonly float[] _input = new float[ContextSamples + FrameSamples];
        private static readonly int[] InputShape = { 1, ContextSamples + FrameSamples };
        private static readonly int[] StateShape = { 2, 1, 128 };

        internal Stream(InferenceSession session) => _session = session;

        public void Reset()
        {
            Array.Clear(_state);
            Array.Clear(_input);
        }

        /// <summary>512 örneklik çerçevenin konuşma olasılığı (0–1).</summary>
        public float Process(ReadOnlySpan<float> frame)
        {
            if (frame.Length != FrameSamples) throw new ArgumentException("Çerçeve 512 örnek olmalı.");

            // Girdi = önceki çerçevenin son 64 örneği + bu çerçeve
            Array.Copy(_input, FrameSamples, _input, 0, ContextSamples);
            frame.CopyTo(_input.AsSpan(ContextSamples));

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>((float[])_input.Clone(), InputShape)),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, StateShape)),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>())),
            };
            using var results = _session.Run(inputs);
            float prob = 0;
            foreach (var r in results)
            {
                if (r.Name == "output") prob = r.AsEnumerable<float>().First();
                else if (r.Name == "stateN") _state = r.AsEnumerable<float>().ToArray();
            }
            return prob;
        }
    }
}
