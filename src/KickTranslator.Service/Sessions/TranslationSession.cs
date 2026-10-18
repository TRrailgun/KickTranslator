using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using KickTranslator.Asr;
using KickTranslator.Audio;
using KickTranslator.Config;
using KickTranslator.Live;
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
    private LiveSegmenter? _segmenter;
    private AudioChunk? _partial;
    private DateTime _lastPartialAt;
    private bool _partialsEnabled;

    public TranslationSession(WebSocket ws, WhisperEngine whisper, TranslatorRegistry translators,
                              TranslatorOptions opts, ILogger log)
    {
        _ws = ws;
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
                _url = msg.TryGetProperty("url", out var u) ? u.GetString() : null;
                _playback = pos;
                if (string.IsNullOrWhiteSpace(_url))
                {
                    await SendAsync(new { type = "error", message = "Video akış adresi bulunamadı." });
                    return;
                }
                await RestartVodAsync(pos);
                break;

            case "live-start":
                _direction = Directions.Normalize(msg.TryGetProperty("direction", out var ld) ? ld.GetString() : null);
                await StartLiveAsync();
                break;

            case "seek":
                _playback = pos;
                if (_url != null && (!_running || pos < _rangeStart - 0.5 || pos > _rangeEnd + 15))
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
        }
    }

    // ===================== VOD =====================

    private async Task RestartVodAsync(double position)
    {
        await StopRunAsync();

        position = Math.Max(0, position);
        _rangeStart = _rangeEnd = position;
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
            using var processor = await _whisper.CreateProcessorAsync(src, ct);
            var translator = await _translators.GetAsync(_direction, ct);
            if (translator == null) await SendMissingTranslatorAsync();
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
        _segmenter = new LiveSegmenter(_opts.LiveMinSeconds, _opts.LiveMaxSeconds);
        _partial = null;
        _lastPartialAt = DateTime.MinValue;
        _partialsEnabled = _opts.LivePartials switch
        {
            "on" => true,
            "off" => false,
            _ => _whisper.Plan.Fast, // auto: sadece güçlü GPU'da (taslaklar ek işlem ister)
        };
        // Kesinleşmiş parçalar: donanım yetişemezse en eskileri atılır, canlıda gecikme birikmesin.
        _liveQueue = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
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

        var finalized = false;
        foreach (var chunk in seg.Push(time, samples))
        {
            finalized = true;
            if (LiveSegmenter.Rms(chunk.Samples) < 0.003) continue; // sessizlik
            q.Writer.TryWrite(chunk);
        }

        if (finalized) Interlocked.Exchange(ref _partial, null); // eski taslak artık geçersiz

        // Taslak: konuşma sürerken ~her saniye o ana kadarki kısmı çöz
        if (_partialsEnabled && DateTime.UtcNow - _lastPartialAt > TimeSpan.FromSeconds(_opts.LivePartialIntervalSeconds))
        {
            var p = seg.Peek();
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
            using var processor = await _whisper.CreateProcessorAsync(src, ct, live: true);
            var translator = await _translators.GetAsync(_direction, ct);
            if (translator == null) await SendMissingTranslatorAsync();
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
                if (p != null)
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
        var parts = new List<string>();
        await foreach (var seg in processor.ProcessAsync(chunk.Samples, ct))
        {
            var t = seg.Text.Trim();
            if (!IsNoise(t)) parts.Add(t);
        }
        var text = string.Join(" ", parts);
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
            await foreach (var seg in processor.ProcessAsync(chunk.Samples, ct))
            {
                var t = seg.Text.Trim();
                if (!IsNoise(t)) parts.Add(t);
            }
            var asrMs = sw.ElapsedMilliseconds;
            var text = string.Join(" ", parts);
            if (text.Length < 2) return;
            var dst = translator?.Translate(text);
            _log.LogInformation("Canlı: ses {Dur:0.0}s · tanıma {Asr} ms · çeviri {Mt} ms",
                chunk.Duration, asrMs, sw.ElapsedMilliseconds - asrMs);
            await SendAsync(new
            {
                type = "cue",
                start = chunk.Start,
                end = chunk.Start + chunk.Duration,
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

            var dst = translator?.Translate(text);
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
