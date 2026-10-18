namespace KickTranslator.Config;

public sealed class TranslatorOptions
{
    /// <summary>Servisin dinlediği adres. Sadece bu makineden erişilir.</summary>
    public string Listen { get; set; } = "http://127.0.0.1:5757";

    /// <summary>auto | cuda | vulkan | cpu</summary>
    public string Device { get; set; } = "auto";

    /// <summary>Boşsa donanıma ve kaynak dile göre seçilir.</summary>
    public string? WhisperModel { get; set; }

    public string DataDir { get; set; } = @"%LOCALAPPDATA%\KickTranslator";

    /// <summary>Hazır çeviri modellerinin (opus-mt-*.zip ve models.json) indirileceği genel adres (ör. Hugging Face, GitHub Releases).</summary>
    public string TranslationModelsBaseUrl { get; set; } = "";

    /// <summary>Kurulum klasöründe ffmpeg.exe varsa o kullanılır, yoksa bu değer (PATH).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    public double FirstChunkSeconds { get; set; } = 8;
    public double ChunkSeconds { get; set; } = 20;
    public double MaxLeadSeconds { get; set; } = 90;
    public int Threads { get; set; } = 0;

    // ---- Canlı yayın ----
    /// <summary>Bir altyazı parçasının en kısa / en uzun süresi (saniye).</summary>
    public double LiveMinSeconds { get; set; } = 1.5;
    public double LiveMaxSeconds { get; set; } = 6.0;
    /// <summary>Konuşma sürerken taslak altyazı: auto (sadece güçlü GPU) | on | off</summary>
    public string LivePartials { get; set; } = "auto";
    public double LivePartialIntervalSeconds { get; set; } = 1.0;

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

/// <summary>Desteklenen çeviri yönleri.</summary>
public static class Directions
{
    public const string EnTr = "en-tr";
    public const string TrEn = "tr-en";

    public static string Normalize(string? d) => d == TrEn ? TrEn : EnTr;
    public static string Source(string d) => Normalize(d)[..2];
    public static string Target(string d) => Normalize(d)[3..];
}
