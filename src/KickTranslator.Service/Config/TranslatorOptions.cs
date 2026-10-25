namespace KickTranslator.Config;

public sealed class TranslatorOptions
{
    /// <summary>Servisin dinlediği adres. Sadece bu makineden erişilir.</summary>
    public string Listen { get; set; } = "http://127.0.0.1:5757";

    /// <summary>auto | cuda | vulkan | cpu</summary>
    public string Device { get; set; } = "auto";

    /// <summary>auto | high | low. Algılama yanılırsa elle: high = büyük model + taslak altyazı.</summary>
    public string GpuTier { get; set; } = "auto";

    /// <summary>Boşsa donanıma ve kaynak dile göre seçilir.</summary>
    public string? WhisperModel { get; set; }

    public string DataDir { get; set; } = @"%LOCALAPPDATA%\KickTranslator";

    /// <summary>Hazır çeviri modellerinin (opus-mt-*.zip ve models.json) indirileceği genel adres (ör. Hugging Face, GitHub Releases).</summary>
    public string TranslationModelsBaseUrl { get; set; } = "";

    /// <summary>Kurulum klasöründe ffmpeg.exe varsa o kullanılır, yoksa bu değer (PATH).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Yayın tekrarında sesin çekilebileceği sunucular (https zorunlu). Eklentiden gelen adres bunlardan
    /// birine ait değilse ffmpeg'e verilmez. Başında nokta olan değer tüm alt alan adlarını kapsar.
    /// </summary>
    public string[] AllowedStreamHosts { get; set; } = { "kick.com", ".kick.com", ".cloudfront.net", ".live-video.net" };

    public double FirstChunkSeconds { get; set; } = 4;
    public double ChunkSeconds { get; set; } = 20;
    public double MaxLeadSeconds { get; set; } = 90;
    public int Threads { get; set; } = 0;

    // ---- Canlı yayın ----
    /// <summary>Bir altyazı parçasının en kısa / en uzun süresi (saniye).</summary>
    public double LiveMinSeconds { get; set; } = 1.5;
    public double LiveMaxSeconds { get; set; } = 6.0;
    /// <summary>Konuşma sürerken taslak altyazı: auto (sadece güçlü GPU) | on | off</summary>
    public string LivePartials { get; set; } = "auto";
    /// <summary>Konuşma algılama: vad (Silero, önerilir) | energy (eski, ses seviyesine bakar)</summary>
    public string Segmenter { get; set; } = "vad";
    public double LivePartialIntervalSeconds { get; set; } = 1.0;

    // ---- Çeviri motoru ----
    /// <summary>auto (güçlü GPU'da LLM, yoksa OPUS-MT) | llm | opus. Eklentiden de seçilebilir.</summary>
    public string TranslationEngine { get; set; } = "auto";
    /// <summary>llama.cpp sunucusu (Vulkan derlemesi: NVIDIA, Intel Arc ve AMD'de çalışır).</summary>
    public string LlmServerUrl { get; set; } =
        "https://github.com/ggml-org/llama.cpp/releases/download/b11829/llama-b11829-bin-win-vulkan-x64.zip";
    /// <summary>
    /// İndirilen sunucu zip'inin beklenen sha256 değeri. Uymazsa dosya silinir ve çalıştırılmaz.
    /// Adresi değiştirirseniz bunu da güncelleyin (boş = doğrulama yok, önerilmez).
    /// </summary>
    public string LlmServerSha256 { get; set; } = "839ff55ce9cbee6a099b403b640ca6b694ee5d74b815e39f77fc5d2b20962f0d";
    /// <summary>Çeviri için kullanılan dil modeli (GGUF).</summary>
    public string LlmModelUrl { get; set; } =
        "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf";
    /// <summary>Dil modeli dosyasının beklenen sha256 değeri (boş = doğrulama yok, önerilmez).</summary>
    public string LlmModelSha256 { get; set; } = "04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19";
    /// <summary>LLM sunucusunun yerel portu (sadece 127.0.0.1).</summary>
    public int LlmPort { get; set; } = 5767;
    /// <summary>Bağlam olarak modele verilen önceki altyazı satırı sayısı.</summary>
    public int LlmContextLines { get; set; } = 3;
    /// <summary>Bir çeviri bu süreden uzun sürerse OPUS-MT'ye düşülür.</summary>
    public double LlmTimeoutSeconds { get; set; } = 2.5;

    public string ResolvedDataDir => Environment.ExpandEnvironmentVariables(DataDir);
    public string ModelsDir => Path.Combine(ResolvedDataDir, "models");
    public string LogsDir => Path.Combine(ResolvedDataDir, "logs");

    public string ResolvedFfmpeg
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            return File.Exists(local) ? local : FfmpegPath;
        }
    }
}

/// <summary>Desteklenen çeviri yönleri ve her yönün kullandığı OPUS-MT modelleri.</summary>
public static class Directions
{
    public const string EnTr = "en-tr";
    public const string TrEn = "tr-en";
    public const string JaTr = "ja-tr";
    public const string TrJa = "tr-ja";

    public static readonly string[] All = { EnTr, TrEn, JaTr, TrJa };

    public static string Normalize(string? d) => All.Contains(d) ? d! : EnTr;
    public static string Source(string d) => Normalize(d)[..2];
    public static string Target(string d) => Normalize(d)[3..];

    /// <summary>Japonca yönleri isteğe bağlı paketle gelir, kullanıcı indirmeden kullanılamaz.</summary>
    public static bool NeedsJapanesePack(string d) => d is JaTr or TrJa;

    /// <summary>
    /// Yönün çeviri zinciri (opus-mt-{ad} klasörleri). Türkçe→Japonca için doğrudan model olmadığından
    /// İngilizce üzerinden iki adımda çevrilir.
    /// </summary>
    public static string[] ModelChain(string d) => Normalize(d) switch
    {
        TrEn => new[] { "tr-en" },
        JaTr => new[] { "ja-tr" },
        TrJa => new[] { "tr-en", "en-ja" },
        _ => new[] { "en-tr" },
    };

    /// <summary>Japonca paketinin indirdiği çeviri modelleri.</summary>
    public static readonly string[] JapanesePackModels = { "ja-tr", "tr-en", "en-ja" };

    /// <summary>Uygulamanın tanıdığı tüm çeviri modelleri (güncelleme denetimi için).</summary>
    public static readonly string[] AllModels = { "en-tr", "tr-en", "ja-tr", "en-ja" };

    public static string Label(string d) => Normalize(d).ToUpperInvariant().Replace("-", "→");

    public static string LanguageName(string code) => code switch
    {
        "tr" => "Turkish",
        "ja" => "Japanese",
        _ => "English",
    };
}
