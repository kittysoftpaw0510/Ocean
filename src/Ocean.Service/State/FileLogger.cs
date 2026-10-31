using System.Text;

namespace Ocean.Service.State;

/// <summary>Daily file logger (no external dependencies); size and age are limited by <see cref="Core.LogFile"/>.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Core.LogFile _file;
    private readonly LogLevel _min;

    public FileLoggerProvider(string dir, LogLevel min = LogLevel.Information)
    {
        _file = new Core.LogFile(dir, "service");
        _min = min;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        if (level < _min) return;
        var lvl = level switch { LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT" };
        _file.Write(lvl, Short(category), message, ex is null ? null : $" | {ex.GetType().Name}: {ex.Message}");
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
