using KickTranslator.Config;
using Whisper.net.LibraryLoader;

namespace KickTranslator.Hardware;

public sealed record HardwarePlan(
    string Device,
    IReadOnlyList<RuntimeLibrary> RuntimeOrder,
    int Threads,
    string Description,
    bool Fast);

/// <summary>
/// Açılışta donanımı algılar.
/// NVIDIA   -> CUDA (yoksa Vulkan) + büyük çok dilli model
/// APU/iGPU -> Vulkan (yoksa CPU) + küçük model
/// </summary>
public static class HardwareProbe
{
    public static bool HasNvidiaDriver() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    public static bool HasVulkanLoader() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    public static HardwarePlan Decide(TranslatorOptions o)
    {
        var device = (o.Device ?? "auto").Trim().ToLowerInvariant();
        if (device == "auto")
        {
            device = HasNvidiaDriver() ? "cuda"
                   : HasVulkanLoader() ? "vulkan"
                   : "cpu";
        }

        var cores = Environment.ProcessorCount;
        var threads = o.Threads > 0 ? o.Threads : Math.Clamp(cores - 2, 2, 8);

        return device switch
        {
            "cuda" => new HardwarePlan(device,
                new[] { RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                threads, "NVIDIA GPU", Fast: true),
            "vulkan" => new HardwarePlan(device,
                new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                threads, "Entegre/harici GPU (Vulkan)", Fast: false),
            _ => new HardwarePlan("cpu",
                new[] { RuntimeLibrary.Cpu },
                threads, $"CPU ({threads} iş parçacığı)", Fast: false),
        };
    }

    /// <summary>Kaynak dile ve donanıma göre Whisper model dosyası.</summary>
    public static string WhisperModelFor(HardwarePlan plan, string sourceLang, TranslatorOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.WhisperModel)) return o.WhisperModel!.Trim();
        if (plan.Fast) return "ggml-large-v3-turbo-q5_0.bin";       // ~550 MB, çok dilli
        return sourceLang == "en" ? "ggml-small.en-q5_1.bin"        // ~190 MB, sadece İngilizce
                                  : "ggml-small-q5_1.bin";          // ~190 MB, çok dilli
    }
}
