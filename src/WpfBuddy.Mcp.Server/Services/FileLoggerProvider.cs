using Microsoft.Extensions.Logging;

namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Minimal ILoggerProvider that appends all log output to a file, so the
/// general server (host / MCP / ILogger) logs are persisted alongside the
/// probe IPC traffic log. Output also still goes to stderr via the console
/// logger. Override the location with the WPFBUDDY_SERVER_LOG env var.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;

    /// <summary>Minimum level written to the file, from WPFBUDDY_LOG_LEVEL (default Information).</summary>
    public static readonly LogLevel MinLevel = ResolveMinLevel();

    public static string ResolveLogPath() =>
        Environment.GetEnvironmentVariable("WPFBUDDY_SERVER_LOG")
        ?? Path.Combine(Path.GetTempPath(), "wpfbuddy-mcp-server.log");

    private static LogLevel ResolveMinLevel() =>
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("WPFBUDDY_LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    // Append with size-based rotation so the file cannot grow unbounded.
    internal void Write(string line) => LogFileWriter.Append(_path, line);

    public void Dispose() { }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}[{eventId.Id}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
