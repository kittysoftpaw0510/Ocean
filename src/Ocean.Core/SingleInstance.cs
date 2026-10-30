using System.Security.Cryptography;
using System.Text;

namespace Ocean.Core;

/// <summary>
/// Device-wide single instance (one Ocean app and one Ocean service per PC, across all signed-in users),
/// implemented with mutexes in the Global\ kernel namespace.
///
/// Developers can run several isolated nodes on one PC for testing by setting OCEAN_ALLOW_MULTI=1 together
/// with a distinct OCEAN_DATA_DIR; the lock is then scoped to that data folder.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    /// <summary>True when this process owns the instance.</summary>
    public bool IsFirst { get; }

    /// <summary>True when another instance exists but belongs to a different user (no access to its mutex).</summary>
    public bool OwnedByOtherUser { get; }

    public static bool AllowMulti => Environment.GetEnvironmentVariable("OCEAN_ALLOW_MULTI") == "1";

    /// <summary>"" normally; a data-folder hash when multi-instance testing is enabled.</summary>
    public static string Scope => AllowMulti
        ? "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OceanDefaults.DataDir.ToLowerInvariant())))[..12]
        : "";

    public SingleInstance(string name)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, @"Global\" + name + Scope, out var created);
            IsFirst = created;
            if (!created) { _mutex.Dispose(); _mutex = null; }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but was created by another account (e.g. another user's session or the service).
            IsFirst = false;
            OwnedByOtherUser = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            IsFirst = false;
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
