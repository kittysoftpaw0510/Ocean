using Ocean.Core;

namespace Ocean.App.Services;

/// <summary>The video this Windows user watched last and where they stopped, so the home page can resume it.</summary>
public sealed class LastPlayed
{
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShareName { get; set; } = "";
    /// <summary>Virtual folder inside the share, as stored in the catalog ("" = share root).</summary>
    public string Folder { get; set; } = "";
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>Watched to the end: offered as "Play again" from the start.</summary>
    public bool Finished { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Kept per Windows user in %LocalAppData%\Ocean (the service and its data are shared by everyone on the PC;
/// what someone was watching is not).
/// </summary>
public static class LastPlayedStore
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocean", "last-played.json");

    public static LastPlayed? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var lp = Json.Deserialize<LastPlayed>(File.ReadAllText(FilePath));
            return lp is { PeerId.Length: > 0, ItemId.Length: > 0 } ? lp : null;
        }
        catch { return null; }
    }

    public static void Save(LastPlayed? value)
    {
        try
        {
            if (value is null) { File.Delete(FilePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(value));
            File.Move(tmp, FilePath, true);
        }
        catch { /* best effort: losing the resume point is not worth an error */ }
    }
}
