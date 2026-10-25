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
    // Hugging Face'teki Git LFS kayıtlarından alınan sha256 değerleri: dosya kaynağında değiştirilmiş
    // ya da bozuk inmişse kullanılmaz.
    private static readonly Dictionary<string, string> KnownWhisperSha = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-large-v3-turbo-q5_0.bin"] = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
        ["ggml-small.en-q5_1.bin"] = "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30",
        ["ggml-small-q5_1.bin"] = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
    };

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
        KnownWhisperSha.TryGetValue(fileName, out var sha);
        if (File.Exists(path) && IsVerified(path, sha)) return Task.FromResult(path);
        return Run("whisper:" + fileName, async () =>
        {
            if (File.Exists(path) && await VerifyExistingAsync(path, sha, ct)) return path;
            await DownloadAsync(WhisperBaseUrl + fileName, path, fileName, sha, ct);
            MarkVerified(path, sha);
            return path;
        });
    }

    public bool HasWhisper(string fileName) => File.Exists(Path.Combine(_opts.ModelsDir, fileName));
    public bool HasFile(string fileName) => File.Exists(Path.Combine(_opts.ModelsDir, fileName));

    public bool HasDir(string dirName, string marker)
    {
        var dir = Path.Combine(_opts.ModelsDir, dirName);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, marker, SearchOption.AllDirectories).Any();
    }

    public void DeleteFile(string fileName)
    {
        var p = Path.Combine(_opts.ModelsDir, fileName);
        foreach (var f in new[] { p, p + ".sha256", p + ".part" })
            if (File.Exists(f)) File.Delete(f);
    }

    public void DeleteDir(string dirName)
    {
        var d = Path.Combine(_opts.ModelsDir, dirName);
        if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    /// <summary>Model klasörü tam mı (indirme yapmadan).</summary>
    public bool HasMarian(string model)
    {
        var dir = Path.Combine(_opts.ModelsDir, $"opus-mt-{model}");
        ApplyPendingUpdate(dir);
        return MarianFiles.All(f => File.Exists(Path.Combine(dir, f)));
    }

    /// <summary>Model klasörünü siler (Japonca paketi kaldırılırken).</summary>
    public void DeleteMarian(string model)
    {
        var dir = Path.Combine(_opts.ModelsDir, $"opus-mt-{model}");
        foreach (var d in new[] { dir, dir + ".new" })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    /// <summary>Çeviri modeli klasörünü getirir (gerekirse zip indirip açar).</summary>
    public Task<string> EnsureMarianAsync(string model, CancellationToken ct)
    {
        var name = $"opus-mt-{model}";
        var dir = Path.Combine(_opts.ModelsDir, name);
        ApplyPendingUpdate(dir);
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
            WriteSource(tmpDir, sha ?? "");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmpDir, dir);
            File.Delete(zip);
            return dir;
        });
    }

    /// <summary>Tek dosyayı (ör. GGUF dil modeli) getirir, gerekirse indirir.</summary>
    public Task<string> EnsureFileAsync(string url, string fileName, string label, string? sha256, CancellationToken ct)
    {
        var path = Path.Combine(_opts.ModelsDir, fileName);
        var sha = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        if (sha == null) _log.LogWarning("{File} için sha256 tanımlı değil, doğrulama yapılmayacak.", fileName);
        if (File.Exists(path) && IsVerified(path, sha)) return Task.FromResult(path);
        return Run("file:" + fileName, async () =>
        {
            if (File.Exists(path) && await VerifyExistingAsync(path, sha, ct)) return path;
            await DownloadAsync(url, path, label, sha, ct);
            MarkVerified(path, sha);
            return path;
        });
    }

    /// <summary>Zip'i indirip klasöre açar; klasörde <paramref name="marker"/> dosyası varsa indirmez.</summary>
    public Task<string> EnsureZipAsync(string url, string dirName, string marker, string label, string? sha256,
                                       CancellationToken ct)
    {
        var dir = Path.Combine(_opts.ModelsDir, dirName);
        var sha = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
        if (sha == null) _log.LogWarning("{Dir} için sha256 tanımlı değil, doğrulama yapılmayacak.", dirName);
        var source = url + "|" + sha;   // adres ya da beklenen sha değişirse yeniden indirilir ve doğrulanır
        if (Directory.Exists(dir) && ReadSource(dir) == source &&
            Directory.EnumerateFiles(dir, marker, SearchOption.AllDirectories).Any())
            return Task.FromResult(dir);
        return Run("zip:" + dirName, async () =>
        {
            var zip = Path.Combine(_opts.ModelsDir, dirName + ".zip");
            await DownloadAsync(url, zip, label, sha, ct);
            var tmpDir = dir + ".tmp";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            ZipFile.ExtractToDirectory(zip, tmpDir);
            WriteSource(tmpDir, source);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmpDir, dir);
            File.Delete(zip);
            return dir;
        });
    }

    // ===================== Doğrulama =====================

    private static string MarkerPath(string path) => path + ".sha256";

    /// <summary>Dosya daha önce bu sha256 ile doğrulandı mı (her açılışta GB'larca dosyayı yeniden okumamak için).</summary>
    private static bool IsVerified(string path, string? sha)
    {
        if (sha == null) return true;
        try { return File.ReadAllText(MarkerPath(path)).Trim().Equals(sha, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void MarkVerified(string path, string? sha)
    {
        if (sha == null) return;
        try { File.WriteAllText(MarkerPath(path), sha.ToLowerInvariant()); } catch { /* önemli değil */ }
    }

    /// <summary>Önceki sürümlerin doğrulamadan indirdiği dosyayı bir kez kontrol eder; uymazsa siler.</summary>
    private async Task<bool> VerifyExistingAsync(string path, string? sha, CancellationToken ct)
    {
        if (sha == null) return true;
        _log.LogInformation("Doğrulanıyor: {File}", Path.GetFileName(path));
        string got;
        await using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            got = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
        if (got.Equals(sha, StringComparison.OrdinalIgnoreCase))
        {
            MarkVerified(path, sha);
            return true;
        }
        _log.LogWarning("{File} beklenen dosya değil (sha256 uyuşmadı), silinip yeniden indirilecek.", Path.GetFileName(path));
        File.Delete(path);
        return false;
    }

    // ===================== Güncelleme ve temizlik =====================

    private const string SourceFile = ".source";

    private static void WriteSource(string dir, string value)
    {
        try { File.WriteAllText(Path.Combine(dir, SourceFile), value); } catch { /* önemli değil */ }
    }

    private static string? ReadSource(string dir)
    {
        try { return File.ReadAllText(Path.Combine(dir, SourceFile)).Trim(); } catch { return null; }
    }

    /// <summary>Önceki çalışmada arka planda indirilen yeni model sürümünü (klasör.new) yerine koyar.</summary>
    private void ApplyPendingUpdate(string dir)
    {
        var pending = dir + ".new";
        if (!Directory.Exists(pending)) return;
        try
        {
            if (!MarianFiles.All(f => File.Exists(Path.Combine(pending, f))))
            {
                Directory.Delete(pending, true); // yarım kalmış
                return;
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(pending, dir);
            _log.LogInformation("Çeviri modeli güncellendi: {Dir}", Path.GetFileName(dir));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Model güncellemesi uygulanamadı: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Yayınlanan çeviri modelleri değiştiyse (models.json'daki sha256 farklıysa) yenisini arka planda
    /// "klasör.new" olarak indirir; bir sonraki açılışta eskisinin yerine geçer. Çalışan çeviri etkilenmez.
    /// </summary>
    public async Task CheckMarianUpdatesAsync(CancellationToken ct)
    {
        var baseUrl = _opts.TranslationModelsBaseUrl?.Trim();
        if (string.IsNullOrEmpty(baseUrl) || baseUrl.Contains("KULLANICI_ADINIZ")) return;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        JsonDocument manifest;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            manifest = JsonDocument.Parse(await _http.GetStringAsync(baseUrl + "models.json", cts.Token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation("Model güncellemesi denetlenemedi: {Msg}", ex.Message);
            return;
        }

        using (manifest)
        {
            foreach (var model in Directions.AllModels)
            {
                var name = $"opus-mt-{model}";
                var dir = Path.Combine(_opts.ModelsDir, name);
                if (!Directory.Exists(dir)) continue; // hiç kullanılmamış yön: ilk kullanımda zaten güncel iner
                if (!manifest.RootElement.TryGetProperty(name + ".zip", out var m) ||
                    !m.TryGetProperty("sha256", out var shaEl)) continue;
                var sha = shaEl.GetString() ?? "";
                if (string.Equals(ReadSource(dir), sha, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    _log.LogInformation("Yeni çeviri modeli bulundu, arka planda indiriliyor: {Name}", name);
                    var zip = Path.Combine(_opts.ModelsDir, name + ".update.zip");
                    await DownloadAsync(baseUrl + name + ".zip", zip, name + " (güncelleme)", sha, ct);
                    var pending = dir + ".new";
                    if (Directory.Exists(pending)) Directory.Delete(pending, true);
                    ZipFile.ExtractToDirectory(zip, pending);
                    WriteSource(pending, sha);
                    File.Delete(zip);
                    _log.LogInformation("Model güncellemesi hazır, bir sonraki açılışta kullanılacak: {Name}", name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Model güncellemesi indirilemedi ({Name}): {Msg}", name, ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Artık kullanılmayan dosyaları siler: yarım indirmeler, eski Whisper modelleri (donanım değiştiyse),
    /// eski LLM modelleri. Açılışta, indirmeler başlamadan çağrılır.
    /// </summary>
    public void Cleanup(IEnumerable<string> keepFiles)
    {
        var dir = _opts.ModelsDir;
        if (!Directory.Exists(dir)) return;
        var keep = new HashSet<string>(keepFiles.Where(f => !string.IsNullOrWhiteSpace(f)), StringComparer.OrdinalIgnoreCase);
        long freed = 0;

        void Del(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    freed += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    Directory.Delete(path, true);
                }
                _log.LogInformation("Kullanılmayan dosya silindi: {Name}", Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _log.LogDebug("Silinemedi {Path}: {Msg}", path, ex.Message);
            }
        }

        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".part" || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                Del(f);                                         // yarım kalmış indirme
            else if ((ext is ".bin" || ext is ".gguf") && !keep.Contains(name))
                Del(f);                                         // artık kullanılmayan model
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (d.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) Del(d);
        foreach (var m in Directory.EnumerateFiles(dir, "*.sha256"))
            if (!File.Exists(m[..^".sha256".Length])) Del(m);

        if (freed > 0)
            _log.LogInformation("Temizlik: {Mb:0} MB boşaltıldı.", freed / 1024.0 / 1024.0);
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
