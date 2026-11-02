using System.Text;
using Ocean.Core;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>One line of the "Node info" window: a label, its value, and what the copy button copies (if any).</summary>
public sealed record InfoRow(string Label, string Value, string? Copy = null, bool Warn = false)
{
    public bool CanCopy => Copy is not null;
}

public sealed record InfoSection(string Title, IReadOnlyList<InfoRow> Rows);

/// <summary>What the "Node info" window shows about one node (built once from the service's answer).</summary>
public sealed class PeerInfoViewModel
{
    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<InfoSection> Sections { get; }

    public PeerInfoViewModel(PeerInfoDto p)
    {
        Title = DisplayNames.Of(p.User, p.Name);
        Subtitle = p.Online ? "Online" : p.OfflineSince is { } off ? $"Offline  ·  last seen {Format.Ago(off)}" : "Offline";
        Sections = new[]
        {
            new InfoSection("Who", Who(p)),
            new InfoSection("Status", Status(p)),
            new InfoSection("Network", Network(p)),
            new InfoSection("Shared", Shared(p)),
        };
    }

    private static List<InfoRow> Who(PeerInfoDto p)
    {
        var rows = new List<InfoRow>();
        if (p.User.Length > 0) rows.Add(new("User", p.User));
        rows.Add(new("Ocean name", p.Name));
        rows.Add(new("PC name", p.Host.Length > 0 ? p.Host : "Unknown"));
        rows.Add(new("Windows", p.Os ?? "Not reported (its Ocean is 1.3.3 or older)"));
        rows.Add(VersionRow(p));
        rows.Add(new("Node ID", NodeIdShort(p.Id), Copy: p.Id));
        return rows;
    }

    private static InfoRow VersionRow(PeerInfoDto p)
    {
        if (p.AppVersion is null) return new("Ocean version", "Not known yet (it hasn't connected to this PC since your update)");
        var cmp = Compare(p.AppVersion, p.MyVersion);
        var note = cmp switch
        {
            0 => "same as yours",
            < 0 => $"older than yours ({p.MyVersion})",
            _ => $"newer than yours ({p.MyVersion})",
        };
        var seen = p.AppVersionSeenAt is { } t ? $"  ·  seen {Format.Ago(t)}" : "";
        return new("Ocean version", $"{p.AppVersion}  ·  {note}{seen}", Warn: cmp < 0);
    }

    private static List<InfoRow> Status(PeerInfoDto p)
    {
        var rows = new List<InfoRow>
        {
            new("Status", p.Online ? "Online" : p.OfflineSince is { } off ? $"Offline since {Local(off)}" : "Offline"),
            new("Last heard", $"{Local(p.LastSeen)}  ({Format.Ago(p.LastSeen)})"),
            new("First seen", Local(p.FirstSeen)),
        };
        if (p.CastItem is { Length: > 0 } cast) rows.Add(new("Multicasting", cast));
        return rows;
    }

    private static List<InfoRow> Network(PeerInfoDto p)
    {
        var rows = new List<InfoRow>
        {
            new("Connection", p.Connection switch
            {
                "bridge" => $"Through {p.Via ?? "another PC"} (no direct network path from this PC)"
                            + (p.Bridges.Count > 1 ? $"  ·  {p.Bridges.Count} PCs can bridge" : ""),
                "reverse" => "It connects to this PC (its firewall blocks incoming connections)",
                _ => "Direct",
            }),
        };
        if (p.Connection == "bridge" && p.Bridges.Count > 1) rows.Add(new("Bridges", string.Join(", ", p.Bridges)));
        rows.Add(p.Address is { } ip
            ? new InfoRow("IP address", $"{ip}  ·  port {p.Port}", Copy: ip)
            : new InfoRow("IP address", "None (only reached through another PC)"));
        var others = p.Addresses.Where(a => !a.Preferred).ToList();
        foreach (var a in others)
            rows.Add(new InfoRow("Also heard on", $"{a.Address}  ·  {Format.Ago(a.LastHeard)}"
                + (a.Local ? "" : "  ·  not on your local network, never used"), Copy: a.Address, Warn: !a.Local));
        return rows;
    }

    private static List<InfoRow> Shared(PeerInfoDto p)
    {
        if (!p.CatalogLoaded) return new() { new("Videos", "Its list hasn't been loaded yet (open the node to load it)") };
        return new()
        {
            new("Shares", p.ShareCount.ToString()),
            new("Videos", $"{p.ItemCount}  ·  {Format.Bytes(p.TotalBytes)}"),
            new("List updated", p.CatalogAt is { } at ? $"{Local(at)}  ({Format.Ago(at)})" : "Unknown"),
        };
    }

    /// <summary>The whole window as plain text, for "Copy all".</summary>
    public string AsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title).AppendLine(Subtitle);
        foreach (var s in Sections)
        {
            sb.AppendLine().AppendLine(s.Title);
            foreach (var r in s.Rows) sb.Append("  ").Append(r.Label).Append(": ").AppendLine(r.Copy is { } full && r.Label == "Node ID" ? full : r.Value);
        }
        return sb.ToString();
    }

    private static string NodeIdShort(string id) => id.Length > 12 ? id[..12] : id;
    private static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("g");

    private static int Compare(string a, string b)
        => Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);
}
