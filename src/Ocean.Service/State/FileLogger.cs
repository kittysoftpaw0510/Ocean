using System.Text;

namespace Ocean.Service.State;

/// <summary>Tiny daily-rolling file logger (no external dependencies).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly LogLevel _min;

    public FileLoggerProvider(string dir, LogLevel min = LogLevel.Information)
    {
        _dir = dir; _min = min;
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "service-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        if (level < _min) return;
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(level switch { LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT" })
            .Append(' ').Append(Short(category)).Append(": ").Append(message);
        if (ex is not null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        line.AppendLine();
        lock (_lock)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"service-{DateTime.Now:yyyyMMdd}.log"), line.ToString()); } catch { }
        }
    }

    private static string Short(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p; private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _p._min;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _p.Write(_cat, logLevel, formatter(state, exception), exception);
    }
}

/// <summary>Shared secret between the service and local UI processes (loopback API).</summary>
public sealed class LocalAuth
{
    public string Token { get; }

    public LocalAuth()
    {
        var path = Core.OceanDefaults.LocalTokenPath;
        string? token = null;
        try { if (File.Exists(path)) token = File.ReadAllText(path).Trim(); } catch { }
        if (string.IsNullOrEmpty(token) || token.Length < 16)
        {
            token = Core.Crypto.Passwords.RandomToken(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, token);
        }
        Token = token;
    }

    public bool Matches(string? provided)
        => !string.IsNullOrEmpty(provided) && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(Token));
}
