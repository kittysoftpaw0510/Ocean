namespace Ocean.Core.Protocol;

/// <summary>Extra request headers between nodes.</summary>
public static class PeerHeaders
{
    /// <summary>"download" when the bytes are saved as a copy (can wait), absent when someone is watching.</summary>
    public const string Purpose = "X-Ocean-Purpose";
    public const string Download = "download";

    /// <summary>The kinds of file this Ocean can open ("video,audio,image,pdf"); an older one sends none and gets videos only.</summary>
    public const string Kinds = "X-Ocean-Kinds";

    /// <summary>ErrorDto.Error when the owner already serves enough downloads (503 + Retry-After).</summary>
    public const string Busy = "busy";
}
