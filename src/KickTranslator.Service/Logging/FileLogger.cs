using System.Collections.Concurrent;
using System.Text;

namespace KickTranslator.Logging;

/// <summary>Basit dosya günlüğü: %LOCALAPPDATA%\KickTranslator\logs\service-YYYYMMDD.log (7 gün saklanır).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_dir, $"service-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* günlük yazılamazsa uygulamayı durdurma */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "service-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose() => _queue.CompleteAdding();

    // Günlükler herkese açık hata bildirimlerine eklenebilir: Windows kullanıcı adı görünmesin
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string Redact(string line) =>
        Profile.Length > 3 ? line.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : line;

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cat = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel.ToString()[..4]}] {cat}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            owner.Enqueue(Redact(line));
        }
    }
}
