namespace PetriEditor.Server.Logging;

/// <summary>
/// Minimal ILoggerProvider that appends Warning/Error/Critical log entries
/// to a plain-text file in the application base directory.
/// No external packages required.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine($"--- Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, _writer, _lock);

    public void Dispose()
    {
        _writer.WriteLine($"--- Session ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        _writer.Dispose();
    }

    private sealed class FileLogger(string category, StreamWriter writer, Lock lk) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) =>
            level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var prefix = level switch
            {
                LogLevel.Warning  => "WARN",
                LogLevel.Error    => "ERR ",
                LogLevel.Critical => "CRIT",
                _                 => level.ToString()[..4].ToUpper()
            };
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{prefix}] {category}: {formatter(state, exception)}";
            if (exception != null) line += $"\n  Exception: {exception}";
            lock (lk) writer.WriteLine(line);
        }
    }
}
