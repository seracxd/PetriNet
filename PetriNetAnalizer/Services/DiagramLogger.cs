namespace PetriNetAnalyzer.Services;

// ── Log level ─────────────────────────────────────────────────────────────────

/// <summary>
/// Severity levels for diagram logging, ordered from most verbose to least.
/// Set <see cref="IDiagramLogger.MinLevel"/> to control what gets stored/forwarded.
/// </summary>
public enum DiagramLogLevel
{
    Trace = 0,   // Registry.Find calls — extremely noisy
    Debug = 1,   // All other command/history operations
    Info = 2,   // High-level lifecycle events only
    Warning = 3,   // Unexpected-but-recoverable situations
    Error = 4,   // Exceptions and hard failures
    None = 99,  // Silence everything
}

// ── Logger interface ──────────────────────────────────────────────────────────

public interface IDiagramLogger
{
    DiagramLogLevel MinLevel { get; set; }

    void Log(DiagramLogLevel level, string category, string message);
    IReadOnlyList<LogEntry> GetEntries();
    void Clear();
}

/// <summary>Convenience extension so call-sites can keep using the two-arg overload.</summary>
public static class DiagramLoggerExtensions
{
    /// <summary>Logs at <see cref="DiagramLogLevel.Debug"/> — the default for most operations.</summary>
    public static void Log(this IDiagramLogger log, string category, string message)
        => log.Log(DiagramLogLevel.Debug, category, message);
}

public readonly record struct LogEntry(DateTime Timestamp, DiagramLogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Level,-7}] [{Category,-14}] {Message}";
}

// ── Ring-buffer implementation ────────────────────────────────────────────────

/// <summary>
/// Keeps the last <paramref name="capacity"/> log entries in memory.
/// Entries below <see cref="MinLevel"/> are silently dropped before storage.
/// Thread-safe for Blazor Server circuits.
/// </summary>
public sealed class RingBufferLogger : IDiagramLogger
{
    private readonly int _capacity;
    private readonly Queue<LogEntry> _entries;
    private readonly object _lock = new();

    public DiagramLogLevel MinLevel { get; set; } = DiagramLogLevel.Debug;

    public RingBufferLogger(int capacity = 500)
    {
        _capacity = capacity;
        _entries = new Queue<LogEntry>(capacity + 1);
    }

    public void Log(DiagramLogLevel level, string category, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTime.Now, level, category, message);
        lock (_lock)
        {
            if (_entries.Count >= _capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<LogEntry> GetEntries()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}

/// <summary>No-op logger — zero overhead when logging is disabled.</summary>
public sealed class NullLogger : IDiagramLogger
{
    public static readonly NullLogger Instance = new();
    private NullLogger() { }
    public DiagramLogLevel MinLevel { get; set; } = DiagramLogLevel.None;
    public void Log(DiagramLogLevel level, string category, string message) { }
    public IReadOnlyList<LogEntry> GetEntries() => Array.Empty<LogEntry>();
    public void Clear() { }
}