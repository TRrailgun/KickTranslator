using System.Text.RegularExpressions;
using KickTranslator.Config;
using Microsoft.Win32;
using Whisper.net.LibraryLoader;

namespace KickTranslator.Hardware;

public sealed record HardwarePlan(
    string Device,
    IReadOnlyList<RuntimeLibrary> RuntimeOrder,
    int Threads,
    string Description,
    bool Fast);

public sealed record GpuInfo(string Name, string Vendor, long VramBytes)
{
    public double VramGb => VramBytes / (1024.0 * 1024 * 1024);
}

/// <summary>
/// Açılışta donanımı algılar.
/// NVIDIA                         -> CUDA (yoksa Vulkan) + büyük çok dilli model
/// Harici GPU (Intel Arc, AMD RX) -> Vulkan + büyük çok dilli model (en az 6 GB ekran belleği)
/// APU/iGPU                       -> Vulkan (yoksa CPU) + küçük model
/// </summary>
public static class HardwareProbe
{
    private const double StrongGpuMinGb = 6.0;

    // Ekran belleği ayarlanabilen APU/iGPU'lar yanlışlıkla "güçlü" sayılmasın
    private static readonly Regex IntegratedName = new(
        @"(UHD|HD) Graphics|\bIris\b|^Intel\(R\) Arc\(TM\) Graphics$|^AMD Radeon\(TM\) Graphics$|Radeon\(TM\)? \d{3}M\b|Radeon \d{3}M\b|Vega \d+ Graphics",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasNvidiaDriver() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    public static bool HasVulkanLoader() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    /// <summary>
    /// Ekran kartlarını sürücü kayıtlarından okur. WMI'nin AdapterRAM alanı 4 GB'ta takıldığı için
    /// 64 bitlik "HardwareInformation.qwMemorySize" değeri kullanılır.
    /// </summary>
    public static IReadOnlyList<GpuInfo> ListGpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue; // 0000, 0001...
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k == null) continue;
                    var name = (k.GetValue("DriverDesc") as string ?? "").Trim();
                    if (name.Length == 0) continue;
                    var id = (k.GetValue("MatchingDeviceId") as string ?? "").ToLowerInvariant();
                    var vendor = id.Contains("ven_10de") ? "nvidia"
                               : id.Contains("ven_8086") ? "intel"
                               : id.Contains("ven_1002") ? "amd"
                               : "other";
                    var vram = ReadSize(k.GetValue("HardwareInformation.qwMemorySize"));
                    if (vram == 0) vram = ReadSize(k.GetValue("HardwareInformation.MemorySize"));
                    list.Add(new GpuInfo(name, vendor, vram));
                }
                catch { /* erişilemeyen kayıt: atla */ }
            }
        }
        catch { /* kayıt defteri okunamadı */ }
        return list;
    }

    private static long ReadSize(object? v) => v switch
    {
        long l => l,
        int i => (uint)i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
        _ => 0,
    };

    /// <summary>Whisper için yeterince güçlü, NVIDIA olmayan harici ekran kartı.</summary>
    public static GpuInfo? FindStrongNonNvidiaGpu(IEnumerable<GpuInfo> gpus) =>
        gpus.Where(g => g.Vendor is "intel" or "amd" or "other")
            .Where(g => g.VramGb >= StrongGpuMinGb && !IntegratedName.IsMatch(g.Name))
            .OrderByDescending(g => g.VramBytes)
            .FirstOrDefault();

    public static HardwarePlan Decide(TranslatorOptions o)
    {
        var gpus = ListGpus();
        var device = (o.Device ?? "auto").Trim().ToLowerInvariant();
        GpuInfo? strong = null;

        if (device == "auto")
        {
            strong = FindStrongNonNvidiaGpu(gpus);
            device = HasNvidiaDriver() ? "cuda"
                   : HasVulkanLoader() ? "vulkan"
                   : "cpu";
        }
        else if (device == "vulkan")
        {
            strong = FindStrongNonNvidiaGpu(gpus);
        }

        // Elle seçim: "GpuTier": "high" | "low" (auto = algıla)
        var tier = (o.GpuTier ?? "auto").Trim().ToLowerInvariant();

        var cores = Environment.ProcessorCount;
        var threads = o.Threads > 0 ? o.Threads : Math.Clamp(cores - 2, 2, 8);

        HardwarePlan plan = device switch
        {
            "cuda" => new HardwarePlan(device,
                new[] { RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                threads, Name(gpus, "nvidia") ?? "NVIDIA GPU", Fast: true),
            "vulkan" when strong != null => new HardwarePlan(device,
                new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                threads, $"{strong.Name} (Vulkan, {strong.VramGb:0} GB)", Fast: true),
            "vulkan" => new HardwarePlan(device,
                new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                threads, (Name(gpus, null) ?? "Entegre GPU") + " (Vulkan)", Fast: false),
            _ => new HardwarePlan("cpu",
                new[] { RuntimeLibrary.Cpu },
                threads, $"CPU ({threads} iş parçacığı)", Fast: false),
        };

        return tier switch
        {
            "high" => plan with { Fast = true },
            "low" => plan with { Fast = false },
            _ => plan,
        };
    }

    private static string? Name(IReadOnlyList<GpuInfo> gpus, string? vendor) =>
        gpus.Where(g => vendor == null || g.Vendor == vendor)
            .OrderByDescending(g => g.VramBytes)
            .Select(g => g.Name)
            .FirstOrDefault();

    /// <summary>Kaynak dile ve donanıma göre Whisper model dosyası.</summary>
    public static string WhisperModelFor(HardwarePlan plan, string sourceLang, TranslatorOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.WhisperModel)) return o.WhisperModel!.Trim();
        if (plan.Fast) return "ggml-large-v3-turbo-q5_0.bin";       // ~550 MB, çok dilli
        return sourceLang == "en" ? "ggml-small.en-q5_1.bin"        // ~190 MB, sadece İngilizce
                                  : "ggml-small-q5_1.bin";          // ~190 MB, çok dilli
    }
}
