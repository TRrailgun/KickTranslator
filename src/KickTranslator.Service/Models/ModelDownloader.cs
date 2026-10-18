using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KickTranslator.Config;
using Microsoft.Extensions.Options;

namespace KickTranslator.Models;

/// <summary>
/// Model dosyalarını ilk ihtiyaçta indirir, ilerlemeyi raporlar.
/// Aynı dosya için eşzamanlı istekler tek indirmede birleşir.
/// </summary>
public sealed class ModelDownloader
{
    private const string WhisperBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private static readonly string[] MarianFiles =
        { "encoder_model.onnx", "decoder_model.onnx", "source.spm", "vocab.json", "config.json" };

    private readonly TranslatorOptions _opts;
    private readonly ILogger<ModelDownloader> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _jobs = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>İndirilen dosya adı → yüzde. Tamamlananlar listeden çıkar.</summary>
    public ConcurrentDictionary<string, int> Progress { get; } = new();

    public ModelDownloader(IOptions<TranslatorOptions> opts, ILogger<ModelDownloader> log)
    {
        _opts = opts.Value;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KickCeviri/1.0");
    }

    public string? ProgressText =>
        Progress.IsEmpty ? null : string.Join(", ", Progress.Select(p => $"{p.Key} %{p.Value}"));

    /// <summary>Whisper modelini getirir (gerekirse indirir), dosya yolunu döner.</summary>
    public Task<string> EnsureWhisperAsync(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(_opts.ModelsDir, fileName);
        if (File.Exists(path)) return Task.FromResult(path);
        return Run("whisper:" + fileName, async () =>
        {
            await DownloadAsync(WhisperBaseUrl + fileName, path, fileName, null, ct);
            return path;
        });
    }

    /// <summary>Çeviri modeli klasörünü getirir (gerekirse zip indirip açar).</summary>
    public Task<string> EnsureMarianAsync(string direction, CancellationToken ct)
    {
        var name = $"opus-mt-{direction}";
        var dir = Path.Combine(_opts.ModelsDir, name);
        if (MarianFiles.All(f => File.Exists(Path.Combine(dir, f)))) return Task.FromResult(dir);

        return Run("marian:" + name, async () =>
        {
            var baseUrl = _opts.TranslationModelsBaseUrl?.Trim();
            if (string.IsNullOrEmpty(baseUrl))
                throw new InvalidOperationException($"Çeviri modeli ({name}) yok ve indirme adresi ayarlanmamış.");
            if (!baseUrl.EndsWith('/')) baseUrl += "/";

            string? sha = null;
            try
            {
                var manifest = await _http.GetStringAsync(baseUrl + "models.json", ct);
                using var doc = JsonDocument.Parse(manifest);
                if (doc.RootElement.TryGetProperty(name + ".zip", out var m) && m.TryGetProperty("sha256", out var s))
                    sha = s.GetString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("models.json okunamadı, doğrulamasız devam: {Msg}", ex.Message);
            }

            var zip = Path.Combine(_opts.ModelsDir, name + ".zip");
            await DownloadAsync(baseUrl + name + ".zip", zip, name, sha, ct);

            var tmpDir = dir + ".tmp";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            ZipFile.ExtractToDirectory(zip, tmpDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmpDir, dir);
            File.Delete(zip);
            return dir;
        });
    }

    private Task<string> Run(string key, Func<Task<string>> work)
    {
        var lazy = _jobs.GetOrAdd(key, k => new Lazy<Task<string>>(async () =>
        {
            try { return await work(); }
            finally { _jobs.TryRemove(k, out var _); }
        }));
        return lazy.Value;
    }

    private async Task DownloadAsync(string url, string path, string label, string? sha256, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _log.LogInformation("İndiriliyor: {Url}", url);
        var tmp = path + ".part";
        Progress[label] = 0;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    hasher.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0) Progress[label] = (int)(done * 100 / total);
                }
            }

            if (sha256 != null)
            {
                var got = Convert.ToHexString(hasher.GetHashAndReset());
                if (!got.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new InvalidOperationException($"{label} indirmesi bozuk (sha256 uyuşmadı). Tekrar denenecek.");
                }
            }

            File.Move(tmp, path, overwrite: true);
            _log.LogInformation("İndirildi: {Path}", path);
        }
        finally
        {
            Progress.TryRemove(label, out _);
        }
    }
}
