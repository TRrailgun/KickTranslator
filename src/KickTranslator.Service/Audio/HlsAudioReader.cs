using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KickTranslator.Audio;

public sealed record AudioChunk(double Start, float[] Samples)
{
    public double Duration => Samples.Length / (double)HlsAudioReader.SampleRate;
}

/// <summary>
/// ffmpeg ile HLS (m3u8) akışından 16 kHz mono ses çeker ve parçalara böler.
/// Parçalar kelime ortasından kesilmesin diye hedef uzunluğun sonundaki en sessiz noktadan kesilir.
/// </summary>
public static class HlsAudioReader
{
    public const int SampleRate = 16000;
    private const int FrameSamples = SampleRate / 20; // 50 ms

    public static async IAsyncEnumerable<AudioChunk> ReadAsync(
        string ffmpegPath,
        string url,
        double startPosition,
        double firstChunkSeconds,
        double chunkSeconds,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-headers", "Referer: https://kick.com/\r\nOrigin: https://kick.com\r\n",
            "-user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
            "-ss", startPosition.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", url,
            "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "pipe:1",
        })
        {
            psi.ArgumentList.Add(a);
        }

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg başlatılamadı.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "ffmpeg bulunamadı. 'winget install Gyan.FFmpeg' ile kurun veya appsettings.json içindeki FfmpegPath'i ayarlayın.");
        }

        using var _proc = proc;
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                log.LogWarning("ffmpeg: {Line}", line);
        });

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* yok say */ }
        });

        var stream = proc.StandardOutput.BaseStream;
        var raw = new byte[64 * 1024];
        var rawFilled = 0;
        var pending = new List<float>(SampleRate * 40);
        var position = startPosition;
        var first = true;
        var anyData = false;

        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(raw.AsMemory(rawFilled), ct);
            }
            catch (OperationCanceledException) { yield break; }
            catch (IOException) when (ct.IsCancellationRequested) { yield break; }

            if (n == 0) break;
            anyData = true;
            rawFilled += n;

            var usable = rawFilled / 4 * 4;
            pending.AddRange(MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, usable)).ToArray());
            var rest = rawFilled - usable;
            if (rest > 0) Buffer.BlockCopy(raw, usable, raw, 0, rest);
            rawFilled = rest;

            var target = (int)((first ? firstChunkSeconds : chunkSeconds) * SampleRate);
            while (pending.Count >= target)
            {
                var cut = FindQuietCut(CollectionsMarshal.AsSpan(pending), target);
                var samples = pending.GetRange(0, cut).ToArray();
                pending.RemoveRange(0, cut);

                yield return new AudioChunk(position, samples);
                position += cut / (double)SampleRate;
                first = false;
                target = (int)(chunkSeconds * SampleRate);
            }
        }

        if (ct.IsCancellationRequested) yield break;

        if (pending.Count > SampleRate / 2)
            yield return new AudioChunk(position, pending.ToArray());

        await proc.WaitForExitAsync(CancellationToken.None);
        await stderrTask;

        if (!anyData && proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg ses alamadı (çıkış kodu {proc.ExitCode}). Akış adresi geçersiz veya erişim engellenmiş olabilir.");
    }

    /// <summary>Hedef uzunluğun son ~4 saniyesinde en düşük enerjili 50 ms'lik çerçeveyi bulur.</summary>
    private static int FindQuietCut(ReadOnlySpan<float> s, int target)
    {
        var window = Math.Min(4 * SampleRate, target / 3);
        var from = Math.Max(FrameSamples, target - window);
        var best = target;
        var bestEnergy = double.MaxValue;

        for (var f = from; f + FrameSamples <= target; f += FrameSamples)
        {
            double e = 0;
            for (var i = f; i < f + FrameSamples; i++) e += s[i] * s[i];
            if (e < bestEnergy)
            {
                bestEnergy = e;
                best = f + FrameSamples / 2;
            }
        }
        return best;
    }
}
