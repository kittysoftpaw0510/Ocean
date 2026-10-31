namespace Ocean.Core.Protocol;

/// <summary>Access policy of a share (group of videos).</summary>
public enum AccessMode { Public, Password, Ask }

/// <summary>Per-item override of the share policy.</summary>
public enum ItemAccessMode { Inherit, Public, Password, Ask }

/// <summary>Identifies a lock that a browsing node must open: "s:{shareId}" or "i:{itemId}".</summary>
public sealed class LockInfo
{
    public string Id { get; set; } = "";
    public AccessMode Mode { get; set; }
}

// ---------------------------------------------------------------------------
// Peer-to-peer catalog (served by owner node)
// ---------------------------------------------------------------------------

public sealed class CatalogDto
{
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Host { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<ShareDto> Shares { get; set; } = new();
    /// <summary>
    /// Set on a delta answer: only items changed since this version are listed (share headers are complete),
    /// and <see cref="RemovedItems"/> lists what disappeared. Null on a full catalog.
    /// </summary>
    public long? DeltaFrom { get; set; }
    public List<string>? RemovedItems { get; set; }
}

public sealed class ShareDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AccessMode Mode { get; set; }
    public LockInfo? Lock { get; set; }
    public bool Unlocked { get; set; }
    public int ItemCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NewestItemAt { get; set; }
    /// <summary>Every folder in the share (including empty ones the owner created), "a/b" form. Empty when locked.</summary>
    public List<string> Folders { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
}

public class ItemDto
{
    public string Id { get; set; } = "";
    public string ShareId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Forward-slash relative path inside the share, e.g. "Family/2024/beach.mp4".</summary>
    public string RelPath { get; set; } = "";
    /// <summary>Folder the item is shown in, "" = share root. Defaults to its folder on disk; the owner can reorganize.</summary>
    public string Folder { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public long? DurationMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public LockInfo? Lock { get; set; }
    public bool Unlocked { get; set; }
    /// <summary>Set by the browsing side: added since the user's last visit.</summary>
    public bool IsNew { get; set; }
    /// <summary>Set by the browsing side: the same video (name + size) is in one of my own shares,
    /// e.g. the other PC downloaded it from me and shares it again. Never counted as new.</summary>
    public bool IsMine { get; set; }
}

public sealed class AccessRequestDto
{
    public string LockId { get; set; } = "";
    public string? Password { get; set; }
}

public static class AccessStatus
{
    public const string Granted = "granted";
    public const string Pending = "pending";
    public const string Denied = "denied";
    public const string WrongPassword = "wrong_password";
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
    public const string Error = "error";
}

public sealed class AccessResponseDto
{
    public string Status { get; set; } = AccessStatus.Error;
    public string? Token { get; set; }
    public string? RequestId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Message { get; set; }
}

public sealed class PingDto
{
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string UserName { get; set; } = "";
    public long CatalogVersion { get; set; }
}

// ---------------------------------------------------------------------------
// Local API (loopback, consumed by the UI)
// ---------------------------------------------------------------------------

public sealed class NodeStatusDto
{
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Version { get; set; } = "";
    public bool RealmSet { get; set; }
    public bool SharingPaused { get; set; }
    public bool RunningAsService { get; set; }
    public string DataDir { get; set; } = "";
    public int PeerPort { get; set; }
    public int NewDays { get; set; }
    public long CatalogVersion { get; set; }
    /// <summary>How the service talks to the network right now, e.g. "Compatibility mode (classic networking): ...".</summary>
    public string NetworkInfo { get; set; } = "";
}

public sealed class PeerDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public long CatalogVersion { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public bool Online { get; set; }
    public DateTimeOffset? OfflineSince { get; set; }
    public int ShareCount { get; set; }
    public int ItemCount { get; set; }
    public int NewCount { get; set; }
    public bool CatalogLoaded { get; set; }
    /// <summary>Reached through this node (name), when there is no network path to it from here.</summary>
    public string? Via { get; set; }
}

public sealed class ItemOverrideDto
{
    public ItemAccessMode Mode { get; set; }
    public bool HasPassword { get; set; }
}

public sealed class ItemOverrideUpsertDto
{
    public ItemAccessMode Mode { get; set; }
    public string? Password { get; set; }
}

public sealed class ShareDefinitionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AccessMode Mode { get; set; }
    public bool HasPassword { get; set; }
    public List<string> Roots { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int ItemCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Scanning { get; set; }
    public List<string> Folders { get; set; } = new();
    public Dictionary<string, ItemOverrideDto> Overrides { get; set; } = new();
}

public sealed class ShareUpsertDto
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public AccessMode Mode { get; set; }
    /// <summary>Null keeps the existing password; empty string clears it.</summary>
    public string? Password { get; set; }
    public List<string> Roots { get; set; } = new();
}

public sealed class LocalItemDto : ItemDto
{
    public string FullPath { get; set; } = "";
    public ItemAccessMode OverrideMode { get; set; }
}

public sealed class PendingAccessDto
{
    public string RequestId { get; set; } = "";
    public string LockId { get; set; } = "";
    public string LockLabel { get; set; } = "";
    public string ShareName { get; set; } = "";
    public string? ItemName { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientUser { get; set; } = "";
    public string ClientHost { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GrantDto
{
    public string Id { get; set; } = "";
    public string LockId { get; set; } = "";
    public string LockLabel { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientUser { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DecisionDto
{
    /// <summary>"once" (8 hours), "always", or "deny".</summary>
    public string Decision { get; set; } = "deny";
}

public enum DownloadStatus { Queued, Running, Done, Failed, Canceled }

public sealed class DownloadJobDto
{
    public string Id { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long Received { get; set; }
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }
    public string Destination { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public double SpeedBps { get; set; }
}

public sealed class DownloadRequestDto
{
    public string PeerId { get; set; } = "";
    public List<string>? ItemIds { get; set; }
    public string? ShareId { get; set; }
    /// <summary>With ShareId: only this folder (and its subfolders).</summary>
    public string? Folder { get; set; }
    public string? Destination { get; set; }
}

public sealed class MoveItemsDto
{
    public List<string> ItemIds { get; set; } = new();
    public string Folder { get; set; } = "";
}

/// <summary>Adds disk folders/files to an existing share, optionally into one of its folders.</summary>
public sealed class AddToShareDto
{
    public List<string> Paths { get; set; } = new();
    public string Folder { get; set; } = "";
}

public sealed class FolderOpDto
{
    public string Path { get; set; } = "";
    /// <summary>Rename target.</summary>
    public string? To { get; set; }
}

public sealed class SettingsDto
{
    public string NodeName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? Secret { get; set; }
    public string DownloadDir { get; set; } = "";
    public int UploadLimitKBps { get; set; }
    public bool SharingPaused { get; set; }
    public int NewDays { get; set; }
    public int MaxConcurrentDownloads { get; set; }
    public bool NotifyNewVideos { get; set; }
    public int OfflineKeepHours { get; set; } = 24;
    public bool AllowPublicNetworks { get; set; } = true;
    /// <summary>Read-only: false when Ocean runs without administrator rights and can't change Windows Firewall itself.</summary>
    public bool FirewallManaged { get; set; }
    /// <summary>"auto" | "classic" | "fast" (applies when the service restarts).</summary>
    public string NetworkMode { get; set; } = "auto";
    /// <summary>Disk space for watched video bytes from other PCs (GB, 0 = off).</summary>
    public int StreamCacheGB { get; set; } = 5;
    /// <summary>Read-only: how much of it is used now.</summary>
    public long StreamCacheUsedBytes { get; set; }
}

public sealed class UnlockRequestDto
{
    public string LockId { get; set; } = "";
    public string? Password { get; set; }
}

public sealed class ErrorDto
{
    public string Error { get; set; } = "";
    public string? Message { get; set; }
    public LockInfo? Lock { get; set; }
}
