using System.Text;

namespace LanDisk.Server.Services;

/// <summary>
/// Minimal rolling file log.
///
/// The Windows shell has no console (it would show a black window on every double click), so
/// without this the server's log output would go nowhere at all. It writes one line per event into
/// the application data directory — never into a share, never into the installation directory.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _path = Path.Combine(logDirectory, "landisk.log");
    }

    public string LogFilePath => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Append(string category, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
               .Append(' ')
               .Append(Level(level))
               .Append(' ')
               .Append(category)
               .Append("  ")
               .Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append("    ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        builder.AppendLine();

        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string previous = _path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---"
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message;
            try { message = formatter(state, exception); }
            catch { return; }

            if (string.IsNullOrEmpty(message) && exception is null) return;
            _provider.Append(_category, logLevel, message, exception);
        }
    }
}
