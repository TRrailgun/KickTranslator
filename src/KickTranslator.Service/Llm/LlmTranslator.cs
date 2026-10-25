using System.Text;
using System.Text.RegularExpressions;
using KickTranslator.Config;
using KickTranslator.Translation;

namespace KickTranslator.Llm;

/// <summary>
/// Dil modeliyle çeviri: konuşma dilini, argoyu ve yarım cümleleri önceki satırların bağlamıyla
/// daha doğal çevirir. Şablondan bağımsız olsun diye tek bir kullanıcı mesajı gönderilir.
/// </summary>
public sealed class LlmTranslator(LlamaServerHost host, string direction) : ISubtitleTranslator
{
    private static readonly Regex Prefix = new(@"^(translation|çeviri|turkish|english|japanese|türkçe|ingilizce|japonca|翻訳)\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "llm";

    public async Task<string?> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken ct)
    {
        var d = Directions.Normalize(direction);
        var src = Directions.LanguageName(Directions.Source(d));
        var dst = Directions.LanguageName(Directions.Target(d));

        var sb = new StringBuilder();
        sb.Append($"You translate live-stream speech into {dst} subtitles. ");
        sb.Append("The text comes from automatic speech recognition, so it can be informal, contain slang or small recognition errors. ");
        sb.Append($"Write natural, fluent {dst} that a native speaker would say, keep the tone and meaning, ");
        sb.Append("keep names, game terms and usernames as they are, and do not add explanations.\n\n");
        if (context.Count > 0)
        {
            sb.Append($"Previous lines ({src}, for context only, do not translate them):\n");
            foreach (var c in context) sb.Append("- ").Append(c).Append('\n');
            sb.Append('\n');
        }
        sb.Append($"Line to translate ({src}):\n").Append(text).Append("\n\n");
        sb.Append($"Reply with only the {dst} translation of that line, on a single line.");

        var maxTokens = Math.Clamp(text.Length, 32, 256);
        var reply = await host.CompleteAsync(sb.ToString(), maxTokens, ct);
        return Clean(reply, text);
    }

    private static string? Clean(string? reply, string source)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var line = reply.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault() ?? "";
        line = Prefix.Replace(line, "").Trim().Trim('"', '“', '”', '«', '»').Trim();
        if (line.Length == 0) return null;
        // Model saçmaladıysa (aşırı uzun cevap) güvenme
        if (line.Length > source.Length * 4 + 40) return null;
        return line;
    }
}
