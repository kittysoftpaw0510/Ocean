namespace Ocean.Core.Protocol;

/// <summary>Extra request headers between nodes.</summary>
public static class PeerHeaders
{
    /// <summary>"download" when the bytes are saved as a copy (can wait), absent when someone is watching.</summary>
    public const string Purpose = "X-Ocean-Purpose";
    public const string Download = "download";
    /// <summary>
    /// Getting a file ready before it is clicked (Ocean 1.7.5 and 1.7.6 did, while browsing): served after anyone
    /// watching, no download slot, not counted as watching.
    /// </summary>
    public const string Prefetch = "prefetch";

    /// <summary>The kinds of file this Ocean can open ("video,audio,image,pdf"); an older one sends none and gets videos only.</summary>
    public const string Kinds = "X-Ocean-Kinds";

    /// <summary>ErrorDto.Error when the owner already serves enough downloads (503 + Retry-After).</summary>
    public const string Busy = "busy";
}
