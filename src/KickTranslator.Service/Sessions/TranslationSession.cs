using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using KickTranslator.Asr;
using KickTranslator.Audio;
using KickTranslator.Config;
using KickTranslator.Live;
using KickTranslator.Llm;
using KickTranslator.Translation;
using Whisper.net;

namespace KickTranslator.Sessions;

/// <summary>
/// Bir tarayıcı sekmesiyle bir WebSocket bağlantısı. İki çalışma şekli vardır:
///
/// VOD (yayın tekrarı): servis sesi m3u8'den kendisi çeker, oynatmanın önünden işler.
///   → {type:"start", url, position, direction}
///   → {type:"time", position} / {type:"seek", position}
///
/// Canlı yayın: eklenti, videonun çaldığı sesi 16 kHz mono olarak gönderir.
///   → {type:"live-start", direction}
///   → ikili mesaj: [float64 LE: ilk örneğin video zamanı][float32 LE örnekler...]
///
/// Servis → istemci:
///   {type:"cue", start, end, src, dst, live}
///   {type:"status", state, message}
///   {type:"error", message}
/// </summary>
public sealed class TranslationSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxMessageBytes = 1 << 20; // 1 MB

    // Whisper'ın sessizlikte uydurduğu bilinen kalıplar
    private static readonly string[] Hallucinations =
    {
        "thank you.", "thanks for watching!", "thanks for watching.", "thank you for watching.",
        "altyazı m.k.", "izlediğiniz için teşekkürler.", "abone olmayı unutmayın.",
        "you", "bye.", "altyazı", "...",
    };

    private readonly WebSocket _ws;
    private readonly WhisperEngine _whisper;
    private readonly TranslatorRegistry _translators;
    private readonly TranslatorOptions _opts;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string _direction = Directions.EnTr;
    private string? _url;
    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private volatile bool _running;
    private double _playback;
    private double _rangeStart, _rangeEnd;
    private DateTime _lastRestart = DateTime.MinValue;

    // Canlı mod
    private Channel<AudioChunk>? _liveQueue;
    private ISpeechSegmenter? _segmenter;
    private AudioChunk? _partial;
    private DateTime _lastPartialAt;
    private volatile bool _partialsEnabled;

    // Teşhis: günlüğe ve eklentiye düzenli özet
    private readonly SileroVad _vad;
    private readonly LiveStats _stats = new();
    private readonly Queue<(DateTime At, double Seconds)> _busy = new();
    private DateTime _liveStartedAt;

    // Çeviri motoru ve bağlam
    private readonly LlamaServerHost _llm;
    private string _engine = "auto";
    private volatile bool _llmDisabled;      // bu oturumda yavaş kaldığı için kapatıldı
    private bool _llmAnnounced;
    private readonly Queue<double> _llmLatency = new();
    private readonly List<string> _history = new();
    // Canlıda yarım kalan cümleyi sonraki parçayla birleştirmek için
    private string? _prevSrc;
    private double _prevStart, _prevEnd;
    private bool _prevMerged;

    // Düşük gecikme modu: cümle bitmeden taslak çeviri, daha kısa parçalar
    private bool _lowLatency;

    public TranslationSession(WebSocket ws, WhisperEngine whisper, TranslatorRegistry translators,
                              SileroVad vad, LlamaServerHost llm, TranslatorOptions opts, ILogger log)
    {
        _ws = ws;
        _vad = vad;
        _llm = llm;
        _whisper = whisper;
        _translators = translators;
        _opts = opts;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await SendStatusAsync("connected", _whisper.Plan.Description);

        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                    // Normal mesajlar birkaç KB; dev mesajla belleği doldurma girişimini kes
                    if (ms.Length > MaxMessageBytes)
                    {
                        _log.LogWarning("Çok büyük mesaj geldi ({Bytes} bayt), bağlantı kapatılıyor.", ms.Length);
                        await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                        return;
                    }
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    OnLiveAudio(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    await HandleAsync(doc.RootElement);
                }
                catch (JsonException) { /* bozuk mesajı yok say */ }
            }
        }
        catch (WebSocketException) { /* sekme kapandı */ }
        catch (OperationCanceledException) { }
        finally
        {
            await StopRunAsync();
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch { /* yok say */ }
            }
        }
    }

    private async Task HandleAsync(JsonElement msg)
    {
        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        var pos = msg.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : _playback;

        switch (type)
        {
            case "start":
                _direction = Directions.Normalize(msg.TryGetProperty("direction", out var d) ? d.GetString() : null);
                SetEngine(msg);
                _url = msg.TryGetProperty("url", out var u) ? u.GetString() : null;
                _playback = pos;
                if (string.IsNullOrWhiteSpace(_url))
                {
                    await SendAsync(new { type = "error", message = "Video akış adresi bulunamadı." });
                    return;
                }
                // Adres ffmpeg'e verilecek: sadece https ve izin verilen Kick sunucuları
                if (!IsAllowedStreamUrl(_url, out var why))
                {
                    _log.LogWarning("İzin verilmeyen akış adresi reddedildi: {Why}", why);
                    _url = null;
                    await SendAsync(new { type = "error", message = "Bu video adresine izin verilmiyor (" + why + ")." });
                    return;
                }
                await RestartVodAsync(pos);
                break;

            case "live-start":
                _direction = Directions.Normalize(msg.TryGetProperty("direction", out var ld) ? ld.GetString() : null);
                SetEngine(msg);
                _lowLatency = msg.TryGetProperty("latency", out var lat) && lat.GetString() == "low";
                await StartLiveAsync();
                break;

            case "seek":
                _playback = pos;
                if (_url != null && (!_running || pos < _rangeStart - 0.5 || pos > _rangeEnd + 3))
                    await RestartVodAsync(pos);
                break;

            case "time":
                _playback = pos;
                // İşleme oynatmanın gerisinde kaldıysa (yavaş donanım) ileri atla.
                var lag = pos - _rangeEnd;
                if (_url != null && _running && lag > _opts.ChunkSeconds * 2 + 10 &&
                    DateTime.UtcNow - _lastRestart > TimeSpan.FromSeconds(30))
                {
                    _log.LogInformation("İşleme {Lag:0}s geride, ileri atlanıyor.", lag);
                    await RestartVodAsync(pos + 2);
                }
                break;

            case "stop":
                await StopRunAsync();
                break;

            case "diag":
                // Eklentinin gördüğü sorunlar (ses yok, ses izi değişti...) günlükte dursun
                var dm = msg.TryGetProperty("message", out var dmv) ? dmv.GetString() ?? "" : "";
                dm = dm.Replace('\r', ' ').Replace('\n', ' '); // günlüğe sahte satır eklenemesin
                _log.LogInformation("Eklenti: {Msg}", dm.Length > 300 ? dm[..300] : dm);
                break;
        }
    }

    // ===================== VOD =====================

    private async Task RestartVodAsync(double position)
    {
        await StopRunAsync();

        // Sarılan noktadan biraz önce başla: o anda süren cümlenin başı kaçmasın
        position = Math.Max(0, position - 1.0);
        _rangeStart = _rangeEnd = position;
        _history.Clear();
        _lastRestart = DateTime.UtcNow;
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        var url = _url!;
        _running = true;
        _runTask = Task.Run(() => VodPipelineAsync(url, position, token));
    }

    private async Task StopRunAsync()
    {
        _runCts?.Cancel();
        _liveQueue?.Writer.TryComplete();
        try { await _runTask; } catch { /* iptal */ }
        _runCts?.Dispose();
        _runCts = null;
        _liveQueue = null;
        _segmenter = null;
        _running = false;
    }

    private async Task VodPipelineAsync(string url, double start, CancellationToken ct)
    {
        try
        {
            var src = Directions.Source(_direction);
            await SendStatusAsync("loading", "Modeller hazırlanıyor (ilk kullanımda indirilir)...");
            await using var processor = await _whisper.CreateProcessorAsync(src, ct);
            var translator = await _translators.GetAsync(_direction, ct);
            if (translator == null && !WantLlm) await SendMissingTranslatorAsync();
            else await SendStatusAsync("buffering", "Ses işleniyor...");

            await foreach (var chunk in HlsAudioReader.ReadAsync(
                               _opts.ResolvedFfmpeg, url, start, _opts.FirstChunkSeconds, _opts.ChunkSeconds, _log, ct))
            {
                // Oynatmanın çok önüne geçme; kullanıcı sarabilir, boşa iş yapmayalım.
                while (chunk.Start - _playback > _opts.MaxLeadSeconds)
                    await Task.Delay(500, ct);

                var sw = Stopwatch.StartNew();
                await TranscribeAndSendAsync(processor, translator, chunk, live: false, ct);

                _rangeEnd = chunk.Start + chunk.Duration;
                var speed = chunk.Duration / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                await SendAsync(new
                {
                    type = "status",
                    state = "running",
                    message = $"{speed:0.0}x gerçek zaman",
                    ahead = Math.Round(_rangeEnd - _playback, 1),
                });
            }

            await SendStatusAsync("done", "Video sonuna kadar işlendi.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "VOD işleme hatası");
            await SendAsync(new { type = "error", message = ex.Message });
        }
        finally
        {
            _running = false;
        }
    }

    // ===================== Canlı yayın =====================

    private async Task StartLiveAsync()
    {
        await StopRunAsync();
        _url = null;
        _segmenter = CreateSegmenter();
        _history.Clear();
        _prevSrc = null;
        _stats.Reset();
        _busy.Clear();
        _liveStartedAt = DateTime.UtcNow;
        _partial = null;
        _lastPartialAt = DateTime.MinValue;
        _partialsEnabled = _opts.LivePartials switch
        {
            "on" => true,
            "off" => false,
            _ => _whisper.Plan.Fast || _lowLatency, // auto: güçlü GPU'da ya da düşük gecikme seçildiyse
        };
        // Kesinleşmiş parçalar: donanım yetişemezse en eskileri atılır, canlıda gecikme birikmesin.
        _liveQueue = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        _log.LogInformation("Canlı oturum: {Dir} · model {Model} · konuşma algılama {Seg} · taslak {Partial}",
            _direction, _whisper.ModelFor(Directions.Source(_direction)), _segmenter.Kind,
            _partialsEnabled ? (_lowLatency ? "açık (düşük gecikme)" : "açık") : "kapalı");
        _log.LogInformation("Çeviri motoru: istenen {Engine}, kullanılacak {Use}", _engine, WantLlm ? "LLM (hazır olunca)" : "OPUS-MT");
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        var reader = _liveQueue.Reader;
        _running = true;
        _runTask = Task.Run(() => LivePipelineAsync(reader, token));
    }

    private void OnLiveAudio(ReadOnlySpan<byte> data)
    {
        var seg = _segmenter;
        var q = _liveQueue;
        if (seg == null || q == null || data.Length < 8 + 4) return;

        var time = BitConverter.ToDouble(data[..8]);
        var samples = MemoryMarshal.Cast<byte, float>(data[8..(8 + (data.Length - 8) / 4 * 4)]);
        _playback = time;

        _stats.AddAudio(samples);

        var finalized = false;
        foreach (var chunk in seg.Push(time, samples))
        {
            finalized = true;
            // Eski yöntemde sessiz parçaları ele; VAD zaten sadece konuşmayı verir
            if (seg is LiveSegmenter && LiveSegmenter.Rms(chunk.Samples) < 0.003) continue;
            if (q.Reader.Count >= 3) _stats.AddDropped(); // en eski parça atılacak: donanım yetişmiyor
            q.Writer.TryWrite(chunk);
        }

        if (_stats.Due) _ = ReportStatsAsync(seg);

        if (finalized) Interlocked.Exchange(ref _partial, null); // eski taslak artık geçersiz

        // Taslak: konuşma sürerken ~her saniye o ana kadarki kısmı çöz
        if (_partialsEnabled && DateTime.UtcNow - _lastPartialAt > TimeSpan.FromSeconds(PartialInterval))
        {
            var p = seg.Peek(_lowLatency ? 0.6 : 1.0);
            if (p != null)
            {
                _lastPartialAt = DateTime.UtcNow;
                Interlocked.Exchange(ref _partial, p);
            }
        }
    }

    private async Task LivePipelineAsync(ChannelReader<AudioChunk> reader, CancellationToken ct)
    {
        try
        {
            var src = Directions.Source(_direction);
            await SendStatusAsync("loading", "Modeller hazırlanıyor (ilk kullanımda indirilir)...");
            await using var processor = await _whisper.CreateProcessorAsync(src, ct, live: true);
            var translator = await _translators.GetAsync(_direction, ct);
            if (translator == null && !WantLlm) await SendMissingTranslatorAsync();
            else await SendStatusAsync("live", "Canlı çeviri açık");

            while (!ct.IsCancellationRequested)
            {
                // Önce kesinleşmiş parçalar, boşta kalınca en güncel taslak
                if (reader.TryRead(out var chunk))
                {
                    await TranscribeAndSendAsync(processor, translator, chunk, live: true, ct);
                    continue;
                }
                var p = Interlocked.Exchange(ref _partial, null);
                if (p != null && _partialsEnabled)
                {
                    await SendPartialAsync(processor, translator, p, ct);
                    continue;
                }
                if (reader.Completion.IsCompleted) break;
                await Task.Delay(30, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Canlı işleme hatası");
            await SendAsync(new { type = "error", message = ex.Message });
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Bitmemiş konuşmayı tek altyazı olarak gönderir; kesinleşen sürüm gelince yerini alır.</summary>
    private async Task SendPartialAsync(WhisperProcessor processor, MarianTranslator? translator,
                                        AudioChunk chunk, CancellationToken ct)
    {
        var swp = Stopwatch.StartNew();
        var parts = new List<string>();
        await foreach (var seg in processor.ProcessAsync(Normalize(chunk.Samples), ct))
        {
            var t = seg.Text.Trim();
            if (!IsNoise(t)) parts.Add(t);
        }
        var text = string.Join(" ", parts);
        await TrackLoadAsync(swp.Elapsed.TotalSeconds);
        if (text.Length < 2) return;

        // Arada kesinleşmiş parça geldiyse bu taslak eskidi
        if (_segmenter == null) return;

        var dst = translator?.Translate(text);
        await SendAsync(new
        {
            type = "cue",
            start = chunk.Start,
            end = chunk.Start + chunk.Duration,
            src = text,
            dst,
            live = true,
            partial = true,
        });
    }

    // ===================== Ortak =====================

    private async Task TranscribeAndSendAsync(WhisperProcessor processor, MarianTranslator? translator,
                                              AudioChunk chunk, bool live, CancellationToken ct)
    {
        if (live)
        {
            // Canlıda tüm parça tek altyazı olarak gider (ekranda en son altyazı gösterilir)
            var sw = Stopwatch.StartNew();
            var parts = new List<string>();
            await foreach (var seg in processor.ProcessAsync(Normalize(chunk.Samples), ct))
            {
                var t = seg.Text.Trim();
                if (!IsNoise(t)) parts.Add(t);
            }
            var asrMs = sw.ElapsedMilliseconds;
            var text = string.Join(" ", parts);
            _stats.AddChunk(chunk.Duration, asrMs / 1000.0, empty: text.Length < 2);
            await TrackLoadAsync(asrMs / 1000.0);
            if (text.Length < 2) return;

            // Önceki parça cümle ortasında kesildiyse ikisini birlikte çevir (yarım cümle çevirisi kötü olur)
            var start = chunk.Start;
            var end = chunk.Start + chunk.Duration;
            var merged = !_lowLatency && _prevSrc != null && !_prevMerged && !EndsSentence(_prevSrc)
                         && chunk.Start - _prevEnd < 1.0 && chunk.Start >= _prevEnd - 0.5
                         && _prevSrc.Length + text.Length < 220;
            if (merged)
            {
                text = _prevSrc + " " + text;
                start = _prevStart;
                if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
            }

            var (dst, engine) = await TranslateLineAsync(translator, text, live: true, ct);
            Remember(text);
            _prevSrc = text;
            _prevStart = start;
            _prevEnd = end;
            _prevMerged = merged;

            _log.LogInformation("Canlı: ses {Dur:0.0}s · tanıma {Asr} ms · çeviri {Mt} ms ({Engine}{Merged})",
                chunk.Duration, asrMs, sw.ElapsedMilliseconds - asrMs, engine, merged ? ", birleştirildi" : "");
            await SendAsync(new
            {
                type = "cue",
                start,
                end,
                src = text,
                dst,
                live = true,
            });
            return;
        }

        await foreach (var seg in processor.ProcessAsync(chunk.Samples, ct))
        {
            var text = seg.Text.Trim();
            if (IsNoise(text)) continue;

            var (dst, _) = await TranslateLineAsync(translator, text, live: false, ct);
            Remember(text);
            await SendAsync(new
            {
                type = "cue",
                start = chunk.Start + seg.Start.TotalSeconds,
                end = chunk.Start + seg.End.TotalSeconds,
                src = text,
                dst,
                live,
            });
        }
    }

    // ===================== Çeviri motoru =====================

    private void SetEngine(JsonElement msg)
    {
        var e = msg.TryGetProperty("engine", out var ev) ? ev.GetString() : null;
        if (string.IsNullOrWhiteSpace(e) || e == "auto") e = _opts.TranslationEngine;
        _engine = (e ?? "auto").Trim().ToLowerInvariant() switch { "llm" => "llm", "opus" => "opus", _ => "auto" };
        _llmDisabled = false;
        _llmAnnounced = false;
        _llmLatency.Clear();
        if (WantLlm) _ = _llm.EnsureStartedAsync(); // ilk seferde indirir; hazır olana kadar OPUS-MT kullanılır
    }

    /// <summary>Kaliteli çeviri isteniyor mu: "llm" seçildiyse ya da otomatikte ekran kartı güçlüyse.</summary>
    private bool WantLlm => _engine == "llm" || (_engine == "auto" && _whisper.Plan.Fast);

    /// <summary>Satırı çevirir: LLM hazırsa ve yetişiyorsa LLM, değilse OPUS-MT.</summary>
    private async Task<(string? Dst, string Engine)> TranslateLineAsync(MarianTranslator? marian, string text, bool live,
                                                                         CancellationToken ct)
    {
        if (WantLlm && !_llmDisabled && _llm.IsReady)
        {
            var sw = Stopwatch.StartNew();
            string? dst = null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _opts.LlmTimeoutSeconds) * (live ? 1 : 2)));
            try
            {
                var context = _history.Skip(Math.Max(0, _history.Count - _opts.LlmContextLines)).ToList();
                dst = await new LlmTranslator(_llm, _direction).TranslateAsync(text, context, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("LLM çevirisi {Sec:0.0} sn'de bitmedi, OPUS-MT kullanıldı.", sw.Elapsed.TotalSeconds);
            }
            await TrackLlmAsync(sw.Elapsed.TotalSeconds, live);
            if (dst != null)
            {
                if (!_llmAnnounced)
                {
                    _llmAnnounced = true;
                    await SendStatusAsync("info", "Kaliteli çeviri (LLM) açık");
                }
                return (dst, "llm");
            }
        }
        return (marian?.Translate(text), "opus");
    }

    private async Task TrackLlmAsync(double seconds, bool live)
    {
        _llmLatency.Enqueue(seconds);
        while (_llmLatency.Count > 5) _llmLatency.Dequeue();
        if (_llmLatency.Count < 3) return;
        var avg = _llmLatency.Average();
        if (avg > (live ? 2.5 : 6.0))
        {
            _llmDisabled = true;
            _log.LogWarning("LLM çevirisi ortalama {Avg:0.0} sn sürüyor; bu oturumda OPUS-MT'ye geçildi.", avg);
            await SendStatusAsync("info", "Kaliteli çeviri yetişemedi, hızlı çeviriye geçildi");
        }
    }

    private void Remember(string text)
    {
        _history.Add(text);
        if (_history.Count > 12) _history.RemoveAt(0);
    }

    private static bool EndsSentence(string t)
    {
        t = t.TrimEnd();
        return t.Length > 0 && ".?!…".Contains(t[^1]);
    }

    /// <summary>Düşük gecikmede taslak daha sık yenilenir.</summary>
    private double PartialInterval => _lowLatency ? Math.Min(0.6, _opts.LivePartialIntervalSeconds) : _opts.LivePartialIntervalSeconds;

    private bool IsAllowedStreamUrl(string url, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { reason = "geçersiz adres"; return false; }
        if (u.Scheme != Uri.UriSchemeHttps) { reason = "https değil"; return false; }
        if (!u.IsDefaultPort) { reason = "standart dışı port"; return false; }
        var host = u.IdnHost.ToLowerInvariant();
        foreach (var a in _opts.AllowedStreamHosts ?? Array.Empty<string>())
        {
            var allowed = a.Trim().ToLowerInvariant();
            if (allowed.Length == 0) continue;
            if (allowed.StartsWith('.') ? host.EndsWith(allowed) : host == allowed) { reason = ""; return true; }
        }
        reason = "sunucu: " + host;
        return false;
    }

    private ISpeechSegmenter CreateSegmenter()
    {
        // Düşük gecikme: parça daha kısa boşlukta (~160 ms) ve en geç 4 sn'de kesilir
        var min = _lowLatency ? Math.Min(1.0, _opts.LiveMinSeconds) : _opts.LiveMinSeconds;
        var max = _lowLatency ? Math.Min(4.0, _opts.LiveMaxSeconds) : _opts.LiveMaxSeconds;
        if (!string.Equals(_opts.Segmenter, "energy", StringComparison.OrdinalIgnoreCase) && _vad.Available)
            return new VadSegmenter(_vad.CreateStream(), min, max, endSilenceFrames: _lowLatency ? 5 : 8);
        return new LiveSegmenter(min, max);
    }

    /// <summary>
    /// Kısık sesli yayınlarda konuşmayı Whisper için yükseltir (en fazla 10 kat).
    /// Zaten yeterince yüksek sese dokunmaz.
    /// </summary>
    private static float[] Normalize(float[] samples)
    {
        float peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v));
        if (peak < 0.0005f || peak >= 0.25f) return samples;
        var gain = Math.Min(10f, 0.5f / peak);
        var o = new float[samples.Length];
        for (var i = 0; i < o.Length; i++) o[i] = samples[i] * gain;
        return o;
    }

    /// <summary>
    /// Tanımanın (taslak + kesin) son 15 sn'nin ne kadarını kapladığını izler. Ekran kartı neredeyse
    /// hiç boş kalmıyorsa taslakları kapatır; kesin altyazılar gecikmesin ve parçalar atılmasın.
    /// (Whisper her çağrıda sabit ~0,5 sn harcadığı için "tanıma/ses süresi" oranı kısa parçalarda yanıltıcıdır.)
    /// </summary>
    private async Task TrackLoadAsync(double busySeconds)
    {
        var now = DateTime.UtcNow;
        _busy.Enqueue((now, busySeconds));
        while (_busy.Count > 0 && now - _busy.Peek().At > TimeSpan.FromSeconds(15)) _busy.Dequeue();
        if (!_partialsEnabled || now - _liveStartedAt < TimeSpan.FromSeconds(15)) return;
        var load = _busy.Sum(b => b.Seconds) / 15.0;
        if (load > 0.9)
        {
            _partialsEnabled = false;
            Interlocked.Exchange(ref _partial, null);
            _log.LogWarning("Tanıma son 15 sn'nin %{Pct:0}'ini kapladı; taslak altyazı kapatıldı.", load * 100);
            await SendStatusAsync("info", "Taslak altyazı kapatıldı (donanım yetişmiyor)");
        }
    }

    private async Task ReportStatsAsync(ISpeechSegmenter seg)
    {
        var r = _stats.Take(seg.SpeechSeconds);
        _log.LogInformation(
            "Özet ({Period:0}s): gelen ses {Audio:0.0}s (en yüksek seviye {Peak:0.000}) · konuşma {Speech:0.0}s · " +
            "parça {Chunks} (boş {Empty}, atlanan {Dropped}) · tanıma hızı {Load:0.00}x ses süresi",
            r.PeriodSeconds, r.AudioSeconds, r.Peak, r.SpeechSeconds, r.Chunks, r.Empty, r.Dropped, r.Load);
        await SendAsync(new
        {
            type = "stats",
            audio = Math.Round(r.AudioSeconds, 1),
            speech = Math.Round(r.SpeechSeconds, 1),
            peak = Math.Round(r.Peak, 4),
            chunks = r.Chunks,
            empty = r.Empty,
            dropped = r.Dropped,
            load = Math.Round(r.Load, 2),
        });
    }

    /// <summary>30 saniyelik dönemler halinde canlı işleme sayaçları (iki iş parçacığından yazılır).</summary>
    private sealed class LiveStats
    {
        private readonly object _l = new();
        private DateTime _since = DateTime.UtcNow;
        private double _audio, _asr, _chunkSec, _peak, _lastSpeech;
        private long _chunks, _empty, _dropped;

        public bool Due => DateTime.UtcNow - _since > TimeSpan.FromSeconds(30);

        public void Reset()
        {
            lock (_l) { _since = DateTime.UtcNow; _audio = _asr = _chunkSec = _peak = _lastSpeech = 0; _chunks = _empty = _dropped = 0; }
        }

        public void AddAudio(ReadOnlySpan<float> s)
        {
            float pk = 0;
            foreach (var v in s) pk = Math.Max(pk, Math.Abs(v));
            lock (_l) { _audio += s.Length / (double)AudioChunk.SampleRate; _peak = Math.Max(_peak, pk); }
        }

        public void AddDropped() { lock (_l) _dropped++; }

        public void AddChunk(double seconds, double asrSeconds, bool empty)
        {
            lock (_l) { _chunks++; _chunkSec += seconds; _asr += asrSeconds; if (empty) _empty++; }
        }

        public Report Take(double totalSpeech)
        {
            lock (_l)
            {
                var r = new Report((DateTime.UtcNow - _since).TotalSeconds, _audio, _peak,
                    totalSpeech - _lastSpeech, _chunks, _empty, _dropped, _chunkSec > 0 ? _asr / _chunkSec : 0);
                _lastSpeech = totalSpeech;
                _since = DateTime.UtcNow;
                _audio = _asr = _chunkSec = _peak = 0;
                _chunks = _empty = _dropped = 0;
                return r;
            }
        }

        public sealed record Report(double PeriodSeconds, double AudioSeconds, double Peak, double SpeechSeconds,
                                    long Chunks, long Empty, long Dropped, double Load);
    }

    private static bool IsNoise(string t)
    {
        if (t.Length < 2) return true;
        if ((t[0] == '[' && t[^1] == ']') || (t[0] == '(' && t[^1] == ')') || (t[0] == '*' && t[^1] == '*')) return true;
        if (t.Contains('♪')) return true;
        var low = t.ToLowerInvariant();
        return Hallucinations.Contains(low);
    }

    private Task SendMissingTranslatorAsync()
    {
        var dir = _direction == Directions.TrEn ? "TR→EN" : "EN→TR";
        _translators.Errors.TryGetValue(_direction, out var why);
        _log.LogWarning("{Dir} çeviri modeli yok: {Why}", dir, why);
        return SendAsync(new
        {
            type = "error",
            message = $"{dir} çeviri modeli yüklenemedi, altyazı çevrilmeden gösteriliyor.",
        });
    }

    private Task SendStatusAsync(string state, string message) =>
        SendAsync(new { type = "status", state, message });

    private async Task SendAsync(object payload)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        await _sendLock.WaitAsync();
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        finally
        {
            _sendLock.Release();
        }
    }
}
