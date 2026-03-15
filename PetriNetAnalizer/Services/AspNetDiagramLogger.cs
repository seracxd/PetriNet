using Microsoft.Extensions.Logging;

namespace PetriNetAnalyzer.Services;

/// <summary>
/// Implements <see cref="IDiagramLogger"/> by forwarding to the standard
/// ASP.NET <see cref="ILogger"/> pipeline AND keeping a local ring buffer
/// for the in-app debug panel.
///
/// Level mapping:
///   DiagramLogLevel.Trace/Debug → ILogger.LogDebug
///   DiagramLogLevel.Info        → ILogger.LogInformation
///   DiagramLogLevel.Warning     → ILogger.LogWarning
///   DiagramLogLevel.Error       → ILogger.LogError
///
/// Two independent filters:
///   1. MinLevel — set at runtime from the settings panel (drops before ring buffer too)
///   2. appsettings.json Logging:LogLevel — controls what ASP.NET forwards to sinks
/// </summary>
public sealed class AspNetDiagramLogger : IDiagramLogger
{
    private readonly ILogger<AspNetDiagramLogger> _logger;
    private readonly Queue<LogEntry> _entries;
    private readonly object _lock = new();
    private readonly int _capacity;

    public DiagramLogLevel MinLevel { get; set; } = DiagramLogLevel.Debug;

    public AspNetDiagramLogger(ILogger<AspNetDiagramLogger> logger, DiagramSettings settings)
    {
        _logger = logger;
        _capacity = settings.LogBufferCapacity;
        _entries = new Queue<LogEntry>(_capacity + 1);
    }

    public void Log(DiagramLogLevel level, string category, string message)
    {
        if (level < MinLevel) return;

        switch (level)
        {
            case DiagramLogLevel.Trace:
            case DiagramLogLevel.Debug:
                _logger.LogDebug("[{Category}] {Message}", category, message);
                break;
            case DiagramLogLevel.Info:
                _logger.LogInformation("[{Category}] {Message}", category, message);
                break;
            case DiagramLogLevel.Warning:
                _logger.LogWarning("[{Category}] {Message}", category, message);
                break;
            case DiagramLogLevel.Error:
                _logger.LogError("[{Category}] {Message}", category, message);
                break;
        }

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