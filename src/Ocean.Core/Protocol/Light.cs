namespace Ocean.Core.Protocol;

/// <summary>
/// Lighter copies of videos for watching. The owner makes one (720p, about 1.5 Mbps) once someone has been watching a
/// heavy video for a while, and viewers then ask for <c>{itemId}@light</c> instead of the original. Downloads always
/// get the original. <see cref="ItemDto.Light"/> says whether a copy is being prepared or ready.
/// </summary>
public static class LightVariant
{
    public const string Suffix = "@light";
    public const string Preparing = "preparing";
    public const string Ready = "ready";

    /// <summary>Strips the "@light" suffix; true when it was there.</summary>
    public static bool Split(string itemId, out string baseId)
    {
        if (itemId.EndsWith(Suffix, StringComparison.Ordinal)) { baseId = itemId[..^Suffix.Length]; return true; }
        baseId = itemId;
        return false;
    }

    /// <summary>The light copy of <paramref name="item"/> as an item of its own (same locks, its own id and size).</summary>
    public static ItemDto Of(ItemDto item) => new()
    {
        Id = item.Id + Suffix, ShareId = item.ShareId, Name = Path.ChangeExtension(item.Name, ".mp4"), RelPath = item.RelPath,
        Folder = item.Folder, Size = item.LightSize, ModifiedAt = item.ModifiedAt, AddedAt = item.AddedAt,
        DurationMs = item.DurationMs, Width = item.Width, Height = item.Height, Lock = item.Lock, Unlocked = item.Unlocked,
        IsNew = item.IsNew, IsMine = item.IsMine, Light = Ready, LightSize = item.LightSize,
    };
}

/// <summary>Answer to /p/light/{itemId}: how the lighter copy of a video is doing.</summary>
public sealed class LightStatusDto
{
    /// <summary>none | queued | preparing | ready | off (the owner doesn't make copies, or runs an older Ocean).</summary>
    public string State { get; set; } = "none";
    public int Percent { get; set; }
    /// <summary>Place in the owner's line when queued (1 = next).</summary>
    public int Queue { get; set; }
    public long Size { get; set; }
    public string? Message { get; set; }
}
