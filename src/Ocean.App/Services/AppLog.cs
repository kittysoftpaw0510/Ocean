using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ocean.Core;

namespace Ocean.App.Services;

/// <summary>
/// The app's own log: <c>%ProgramData%\Ocean\logs\app-YYYYMMDD.log</c>, next to the service's log, so one folder
/// (Settings › Open logs) holds everything needed to debug. Falls back to <c>%LocalAppData%\Ocean\logs</c> when
/// that folder isn't writable. Kept 14 days. Never contains the local access token.
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static string? _dir;
    private static readonly Regex TokenInUrl = new(@"([?&]k=)[^&\s]+", RegexOptions.Compiled);

    public static string Directory => _dir ??= ChooseDirectory();

    /// <summary>First lines of a session: who, what, where.</summary>
    public static void Start(string[] args)
    {
        var p = Process.GetCurrentProcess();
        Info("App", $"----- Ocean app {FileVersion()} starting; pid {p.Id}, session {p.SessionId}, user {Environment.UserName}, " +
                    $"Windows {Environment.OSVersion.Version}, args [{string.Join(' ', args)}]");
        Info("App", $"data {OceanDefaults.DataDir}; log {Directory}" +
                    (Environment.GetEnvironmentVariable("OCEAN_DATA_DIR") is { Length: > 0 } ? " (portable: OCEAN_DATA_DIR set)" : ""));
        Prune();
    }

    public static void Info(string area, string message) => Write("INF", area, message, null);
    public static void Warn(string area, string message, Exception? ex = null) => Write("WRN", area, message, ex);
    /// <summary>Errors include the full exception (stack trace) on the following lines.</summary>
    public static void Error(string area, string message, Exception? ex = null) => Write("ERR", area, message, ex, full: true);

    /// <summary>Removes the token from URLs before they are logged.</summary>
    public static string Clean(string? text) => text is null ? "" : TokenInUrl.Replace(text, "$1***");

    private static void Write(string level, string area, string message, Exception? ex, bool full = false)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
            .Append(area).Append(": ").Append(Clean(message));
        if (ex is not null)
        {
            if (full) line.AppendLine().Append("    ").Append(Clean(ex.ToString()).Replace("\n", "\n    "));
            else line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(Clean(Flatten(ex)));
        }
        line.AppendLine();
        lock (Lock)
        {
            try { File.AppendAllText(Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log"), line.ToString()); }
            catch { /* logging must never break the app */ }
        }
        Debug.Write(line.ToString());
    }

    /// <summary>"outer message ← inner message" (socket errors hide in inner exceptions).</summary>
    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 4; e = e.InnerException)
            if (!parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" ← ", parts);
    }

    private static string ChooseDirectory()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(OceanDefaults.DataDir, "logs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocean", "logs"),
                 })
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch { }
        }
        return Path.GetTempPath();
    }

    private static void Prune()
    {
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "app-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    private static string FileVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? OceanDefaults.AppVersion; }
        catch { return OceanDefaults.AppVersion; }
    }
}
