using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using KickTranslator.Config;
using KickTranslator.Models;
using Microsoft.Extensions.Options;

namespace KickTranslator.Llm;

/// <summary>
/// Kaliteli çeviri için yerel dil modeli sunucusu (llama.cpp, "llama-server.exe").
/// Ayrı bir işlem olarak çalışır: uygulamayla aynı süreçte iki farklı ggml kütüphanesi
/// (Whisper ve llama) çakışmasın, bir sorun olursa ana uygulama etkilenmesin.
/// İlk ihtiyaçta sunucu (~30 MB) ve model (~2.5 GB) indirilir. Sadece 127.0.0.1'i dinler.
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    public enum LlmState { NotStarted, Downloading, Starting, Ready, Failed }

    private readonly TranslatorOptions _opts;
    private readonly ModelDownloader _models;
    private readonly ILogger<LlamaServerHost> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _gate = new();
    private readonly Queue<string> _lastLines = new();
    private Task<bool>? _start;
    private Process? _proc;
    // Her açılışta yeni üretilen erişim anahtarı: tarayıcıdaki siteler ve başka programlar sunucuyu kullanamasın
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private volatile bool _warming;

    public LlmState State { get; private set; } = LlmState.NotStarted;
    public string? Error { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{_opts.LlmPort}";

    public LlamaServerHost(IOptions<TranslatorOptions> opts, ModelDownloader models, ILogger<LlamaServerHost> log)
    {
        _opts = opts.Value;
        _models = models;
        _log = log;
    }

    public bool IsReady => State == LlmState.Ready;

    private string ModelName => Path.GetFileName(new Uri(_opts.LlmModelUrl).AbsolutePath);

    /// <summary>Sunucu ve model diskte var mı (kullanıcı daha önce indirmiş mi).</summary>
    public bool IsInstalled => _models.HasFile(ModelName) && _models.HasDir("llama-server", "llama-server.exe");

    /// <summary>
    /// Sunucuyu başlatır. <paramref name="allowDownload"/> false ise (çeviri oturumları) sadece daha önce
    /// indirilmişse başlatır; ~2.5 GB'lık model kullanıcı eklentiden "İndir" demeden inmez.
    /// Aynı anda gelen istekler tek başlatmada birleşir.
    /// </summary>
    public Task<bool> EnsureStartedAsync(bool allowDownload = false)
    {
        lock (_gate)
        {
            if (!allowDownload && !IsInstalled && State != LlmState.Ready) return Task.FromResult(false);
            if (_start == null || (_start.IsCompleted && !_start.Result))
            {
                // Başarısız denemeden sonra en fazla 2 dakikada bir kendiliğinden yeniden dene (kullanıcı isterse hemen)
                if (!allowDownload && _start != null && DateTime.UtcNow - _failedAt < TimeSpan.FromMinutes(2)) return _start;
                _start = Task.Run(StartAsync);
            }
            return _start;
        }
    }

    /// <summary>Sunucuyu durdurur ve modeli diskten siler (eklentideki "Kaldır").</summary>
    public void Remove()
    {
        lock (_gate)
        {
            if (State is LlmState.Downloading or LlmState.Starting)
                throw new InvalidOperationException("İndirme ya da başlatma sürerken kaldırılamaz.");
            KillProcess();
            _start = null;
            State = LlmState.NotStarted;
            Error = null;
            _models.DeleteFile(ModelName);
            _models.DeleteDir("llama-server");
            _log.LogInformation("Kaliteli çeviri modeli kaldırıldı.");
        }
    }

    private DateTime _failedAt = DateTime.MinValue;

    private async Task<bool> StartAsync()
    {
        try
        {
            State = LlmState.Downloading;
            var serverDir = await _models.EnsureZipAsync(_opts.LlmServerUrl, "llama-server", "llama-server.exe",
                "Kaliteli çeviri sunucusu", _opts.LlmServerSha256, CancellationToken.None);
            var modelName = ModelName;
            var model = await _models.EnsureFileAsync(_opts.LlmModelUrl, modelName,
                "Kaliteli çeviri modeli", _opts.LlmModelSha256, CancellationToken.None);

            // Eski sürümlerden sahipsiz kalmış kendi sunucumuz varsa kapat (port boşalsın)
            KillStale(serverDir);
            if (await HealthyAsync())
                throw new InvalidOperationException(
                    $"{_opts.LlmPort} portu başka bir program tarafından kullanılıyor (appsettings.json → LlmPort ile değiştirin).");

            var exe = Directory.EnumerateFiles(serverDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new FileNotFoundException("llama-server.exe bulunamadı.");

            State = LlmState.Starting;
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
                     {
                         "-m", model,
                         "--host", "127.0.0.1",
                         "--port", _opts.LlmPort.ToString(),
                         "-ngl", "99",       // tüm katmanlar ekran kartında
                         "-c", "4096",
                         "-np", "2",         // canlı + taslak/yedek için 2 eşzamanlı istek
                         "-sm", "none",      // tek ekran kartı (entegre GPU'ya bölünmesin)
                         "--api-key", _apiKey,
                     })
                psi.ArgumentList.Add(a);

            _log.LogInformation("LLM sunucusu başlatılıyor: {Model}", modelName);
            var p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server başlatılamadı.");
            ChildProcessJob.Add(p); // uygulama nasıl kapanırsa kapansın sunucu da kapansın
            _proc = p;
            p.OutputDataReceived += (_, e) => Remember(e.Data);
            p.ErrorDataReceived += (_, e) => Remember(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Model ekran kartına yüklenene kadar bekle
            var until = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < until)
            {
                if (p.HasExited)
                    throw new InvalidOperationException($"llama-server kapandı (kod {p.ExitCode}): {LastLines()}");
                if (await HealthyAsync() && !p.HasExited)
                {
                    await WarmupAsync();
                    State = LlmState.Ready;
                    Error = null;
                    _log.LogInformation("LLM sunucusu hazır.");
                    return true;
                }
                await Task.Delay(1000);
            }
            throw new TimeoutException("LLM sunucusu 3 dakikada hazır olmadı: " + LastLines());
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = LlmState.Failed;
            _failedAt = DateTime.UtcNow;
            _log.LogError(ex, "LLM sunucusu başlatılamadı");
            KillProcess();
            return false;
        }
    }

    /// <summary>
    /// İlk istek ekran kartında ayrıca hazırlık yapar (birkaç saniye sürebilir); bunu canlı çeviriden
    /// önce, kullanıcıyı bekletmeden yap.
    /// </summary>
    private async Task WarmupAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _warming = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for (var i = 0; i < 2; i++)
                await CompleteAsync("Translate to Turkish, reply with the translation only:\nHello, how are you doing today?", 24, cts.Token);
            _log.LogInformation("LLM ısınma: {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning("LLM ısınma başarısız: {Msg}", ex.Message);
        }
        finally
        {
            _warming = false;
        }
    }

    private async Task<bool> HealthyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var r = await _http.GetAsync(BaseUrl + "/health", cts.Token);
            return r.IsSuccessStatusCode; // model yüklenirken 503 döner
        }
        catch { return false; }
    }

    /// <summary>Sohbet tamamlama isteği; başarısızsa null.</summary>
    public async Task<string?> CompleteAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        if (!IsReady && !_warming) return null;
        var body = new
        {
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.2,
            top_p = 0.9,
            max_tokens = maxTokens,
            stream = false,
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/chat/completions")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            using var r = await _http.SendAsync(req, ct);
            if (!r.IsSuccessStatusCode)
            {
                _log.LogWarning("LLM isteği başarısız: {Code}", (int)r.StatusCode);
                if (_proc is { HasExited: true }) State = LlmState.Failed;
                return null;
            }
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("LLM isteği hatası: {Msg}", ex.Message);
            if (_proc is { HasExited: true }) { State = LlmState.Failed; Error = "llama-server kapandı: " + LastLines(); }
            return null;
        }
    }

    /// <summary>Bu uygulamanın klasöründeki, önceki çalışmalardan kalmış llama-server işlemlerini kapatır.</summary>
    private void KillStale(string serverDir)
    {
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(serverDir, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Sahipsiz kalmış LLM sunucusu kapatılıyor (PID {Pid}).", p.Id);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch { /* başka kullanıcının işlemi / erişim yok */ }
            finally { p.Dispose(); }
        }
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains(_apiKey)) line = line.Replace(_apiKey, "***"); // anahtar günlüğe düşmesin
        lock (_lastLines)
        {
            _lastLines.Enqueue(line);
            while (_lastLines.Count > 15) _lastLines.Dequeue();
        }
    }

    private string LastLines()
    {
        lock (_lastLines) return string.Join(" | ", _lastLines.TakeLast(4));
    }

    private void KillProcess()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); }
        catch { /* zaten kapanmış */ }
        _proc = null;
    }

    public void Dispose()
    {
        KillProcess();
        _http.Dispose();
    }
}
