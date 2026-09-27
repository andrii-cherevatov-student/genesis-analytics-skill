using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WikipediaInterestSkill.Infrastructure;

public sealed class JsonLinesLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LogLevel _minimum;
    private readonly ConcurrentDictionary<string, JsonLinesLogger> _loggers = new();

    public JsonLinesLoggerProvider(string path, LogLevel minimum = LogLevel.Information)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new JsonLinesLogger(this, c));

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Write(string category, LogLevel level, string message, IEnumerable<KeyValuePair<string, object?>>? state,
        Exception? exception)
    {
        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["level"] = level.ToString(),
            ["category"] = category,
            ["message"] = message,
        };
        if (state is not null)
            foreach (var (key, value) in state)
                if (key != "{OriginalFormat}")
                    entry[char.ToLowerInvariant(key[0]) + key[1..]] = value is IFormattable or string or bool ? value : value?.ToString();
        if (exception is not null) entry["exception"] = exception.ToString();
        var line = JsonSerializer.Serialize(entry, JsonOptions.Compact);
        lock (_lock) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();

    private sealed class JsonLinesLogger(JsonLinesLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception),
                state as IEnumerable<KeyValuePair<string, object?>>, exception);
        }
    }
}
