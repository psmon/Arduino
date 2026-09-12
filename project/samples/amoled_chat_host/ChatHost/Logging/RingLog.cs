using System.Collections.Concurrent;

namespace ChatHost.Logging;

/// <summary>In-memory tail of the log so the web UI (GET /api/log) can show what the host is doing.</summary>
public sealed class RingLog
{
    private readonly ConcurrentQueue<string> _lines = new();
    private const int Max = 400;

    public void Add(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Max && _lines.TryDequeue(out _)) { }
    }

    public IReadOnlyList<string> Tail(int n) => _lines.Reverse().Take(n).Reverse().ToList();
}

public sealed class RingLoggerProvider(RingLog ring) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(ring, categoryName);
    public void Dispose() { }

    private sealed class RingLogger(RingLog ring, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (category.StartsWith("Microsoft.", StringComparison.Ordinal) && logLevel < LogLevel.Warning) return;
            var short_ = category[(category.LastIndexOf('.') + 1)..];
            var msg = $"{DateTime.Now:HH:mm:ss} {logLevel.ToString()[..4].ToUpperInvariant()} [{short_}] {formatter(state, exception)}";
            if (exception != null) msg += " :: " + exception.GetType().Name + ": " + exception.Message;
            ring.Add(msg);
        }
    }
}
