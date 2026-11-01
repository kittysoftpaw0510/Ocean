namespace Ocean.Core.Protocol;

/// <summary>What the service knows about newer versions (GitHub releases of this project).</summary>
public sealed class UpdateStatusDto
{
    public string Current { get; set; } = "";
    /// <summary>Newest release seen, "" until a check succeeded.</summary>
    public string Latest { get; set; } = "";
    /// <summary>A newer version exists.</summary>
    public bool Available { get; set; }
    /// <summary>Its installer is downloaded and checked; <see cref="InstallerPath"/> runs it.</summary>
    public bool Ready { get; set; }
    public string? InstallerPath { get; set; }
    public long InstallerSize { get; set; }
    /// <summary>Release notes (Markdown as written on GitHub), may be empty.</summary>
    public string Notes { get; set; } = "";
    /// <summary>The release page.</summary>
    public string Url { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public bool Checking { get; set; }
    public bool Downloading { get; set; }
    /// <summary>Download progress 0..1 while <see cref="Downloading"/>.</summary>
    public double Progress { get; set; }
    /// <summary>Why the last check or download failed (empty when fine).</summary>
    public string Error { get; set; } = "";
    /// <summary>Checks run by themselves (Settings).</summary>
    public bool AutoCheck { get; set; } = true;
}
