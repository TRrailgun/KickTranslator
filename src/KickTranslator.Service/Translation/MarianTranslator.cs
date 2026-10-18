using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace KickTranslator.Translation;

/// <summary>
/// Opus-MT (Marian) çevirmen (en→tr veya tr→en). ONNX Runtime ile CPU'da çalışır;
/// int8 sıkıştırılmış model APU'lu sistemlerde de hızlıdır.
///
/// Beklenen klasör içeriği (tools\build_models.py üretir):
///   encoder_model.onnx, decoder_model.onnx, source.spm, vocab.json, config.json
/// </summary>
public sealed class MarianTranslator : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceTokenizer _spm;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _idToToken;
    private readonly HashSet<int> _special = new();
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly object _runLock = new();
    private readonly int _eos, _pad, _unk, _decoderStart;

    public MarianTranslator(string dir)
    {
        string F(string name)
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) throw new FileNotFoundException($"Çeviri modeli dosyası yok: {p}");
            return p;
        }

        var so = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        _encoder = new InferenceSession(F("encoder_model.onnx"), so);
        _decoder = new InferenceSession(F("decoder_model.onnx"), so);

        if (!_decoder.InputMetadata.ContainsKey("encoder_hidden_states"))
            throw new InvalidOperationException("decoder_model.onnx beklenen girdileri içermiyor; modeli build_models.py ile yeniden üretin.");

        using (var s = File.OpenRead(F("source.spm")))
            _spm = SentencePieceTokenizer.Create(s); // <s> ve </s> Translate() içinde atlanıyor

        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(F("vocab.json")))
                 ?? throw new InvalidOperationException("vocab.json okunamadı.");
        _idToToken = new string[_vocab.Values.Max() + 1];
        foreach (var (tok, id) in _vocab) _idToToken[id] = tok;

        using var cfg = JsonDocument.Parse(File.ReadAllText(F("config.json")));
        int Cfg(string key, int fallback) =>
            cfg.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

        _eos = Cfg("eos_token_id", _vocab.GetValueOrDefault("</s>", 0));
        _pad = Cfg("pad_token_id", _vocab.GetValueOrDefault("<pad>", _idToToken.Length - 1));
        _decoderStart = Cfg("decoder_start_token_id", _pad);
        _unk = _vocab.GetValueOrDefault("<unk>", 1);
        _special.UnionWith(new[] { _eos, _pad, _unk });
    }

    public string Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        text = text.Trim();
        if (_cache.TryGetValue(text, out var hit)) return hit;
        lock (_runLock)
        {
            var r = TranslateCore(text);
            if (_cache.Count < 20000) _cache[text] = r;
            return r;
        }
    }

    private string TranslateCore(string text)
    {

        // 1) Kaynak cümleyi parçalara ayır ve Marian sözlüğüne göre kimliklere çevir.
        var ids = new List<long>();
        foreach (var t in _spm.EncodeToTokens(text, out _))
        {
            if (t.Value is "<s>" or "</s>") continue;
            ids.Add(_vocab.TryGetValue(t.Value, out var id) ? id : _unk);
        }
        if (ids.Count > 510) ids.RemoveRange(510, ids.Count - 510);
        ids.Add(_eos);

        var n = ids.Count;
        var inputIds = new DenseTensor<long>(ids.ToArray(), new[] { 1, n });
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });

        // 2) Kodlayıcı (encoder) bir kez çalışır.
        DenseTensor<float> hidden;
        using (var encOut = _encoder.Run(new[]
               {
                   NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                   NamedOnnxValue.CreateFromTensor("attention_mask", mask),
               }))
        {
            hidden = encOut.First().AsTensor<float>().ToDenseTensor();
        }

        // 3) Kod çözücü (decoder) ile açgözlü (greedy) üretim.
        var output = new List<long> { _decoderStart };
        var maxNew = Math.Min(256, n * 3 + 10);
        for (var step = 0; step < maxNew; step++)
        {
            var decIds = new DenseTensor<long>(output.ToArray(), new[] { 1, output.Count });
            using var res = _decoder.Run(
                new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", decIds),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                },
                new[] { "logits" });

            var logits = res.First().AsTensor<float>().ToDenseTensor();
            var vocabSize = logits.Dimensions[2];
            var row = logits.Buffer.Span.Slice((output.Count - 1) * vocabSize, vocabSize);

            var best = -1;
            var bestVal = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                if (v == _pad) continue;
                if (row[v] > bestVal) { bestVal = row[v]; best = v; }
            }

            if (best == _eos || best < 0) break;
            output.Add(best);
        }

        // 4) Parçaları birleştir.
        var sb = new StringBuilder();
        foreach (var id in output.Skip(1))
        {
            var i = (int)id;
            if (_special.Contains(i) || i >= _idToToken.Length) continue;
            sb.Append(_idToToken[i]);
        }
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
