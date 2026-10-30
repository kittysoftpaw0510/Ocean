namespace Ocean.Core.Protocol;

/// <summary>Extra request headers between nodes.</summary>
public static class PeerHeaders
{
    /// <summary>"download" when the bytes are saved as a copy (can wait), absent when someone is watching.</summary>
    public const string Purpose = "X-Ocean-Purpose";
    public const string Download = "download";

    /// <summary>ErrorDto.Error when the owner already serves enough downloads (503 + Retry-After).</summary>
    public const string Busy = "busy";
}
