using System.Globalization;

namespace GpuArbiter.Logging;

public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    public void Write(string name, string text)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{name}-{DateTime.UtcNow:yyyy-MM-dd}.log"), text + Environment.NewLine);
        }
    }
    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) owner.Write("gpu-arbiter", string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:O} {level} {category}: {formatter(state, exception)}"));
        }
    }
}
