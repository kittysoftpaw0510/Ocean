namespace Ocean.Core.Protocol;

/// <summary>A vote (+1, −1, 0 = take it back) and/or "watched it to the end", sent to the video's owner.</summary>
public sealed class RateDto
{
    /// <summary>Null: leave the vote as it is (only reporting <see cref="Finished"/>).</summary>
    public int? Vote { get; set; }
    public bool Finished { get; set; }
}

/// <summary>A video's totals, as its owner counts them (who voted is never sent).</summary>
public sealed class RatingDto
{
    public int Up { get; set; }
    public int Down { get; set; }
    public int Finished { get; set; }
    public double Score { get; set; }
}

/// <summary>My vote on another PC's video, and the totals after it (Pending: waiting for that PC to come back).</summary>
public sealed class MyRatingDto
{
    public int MyVote { get; set; }
    /// <summary>Kept on this PC and sent to the owner later (see <see cref="Reason"/>).</summary>
    public bool Pending { get; set; }
    public RatingDto? Totals { get; set; }
    /// <summary>
    /// Why the owner has not counted it (null when it has): offline | unreachable | old (its Ocean has no votes yet:
    /// kept until it updates) | locked (no access right now: kept until approved again) | gone (the video is no longer
    /// shared) | rejected.
    /// </summary>
    public string? Reason { get; set; }
}

public sealed class TagsDto
{
    public List<string> Tags { get; set; } = new();
}

/// <summary>"Top on the network": the best-rated videos of every PC I know (mine included).</summary>
public sealed class TopDto
{
    public List<TagCountDto> Tags { get; set; } = new();
    public List<NetItemDto> Items { get; set; } = new();
}

public sealed class TagCountDto
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>A video somewhere on the network (another node's, or one of mine), as the top list and search return it.</summary>
public sealed class NetItemDto
{
    /// <summary>Empty for my own videos.</summary>
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public bool Online { get; set; }
    public bool Mine { get; set; }
    public string ShareName { get; set; } = "";
    /// <summary>Where the file is on this PC (my own videos only), so the app can play it straight from disk.</summary>
    public string? FullPath { get; set; }
    public ItemDto Item { get; set; } = new();
}

/// <summary>Search across every node's last known list and my own shares.</summary>
public sealed class SearchDto
{
    public string Query { get; set; } = "";
    /// <summary>Matches in all, of which <see cref="Items"/> holds the best ones.</summary>
    public int Total { get; set; }
    public List<SearchNodeDto> Nodes { get; set; } = new();
    public List<NetItemDto> Items { get; set; } = new();
}

public sealed class SearchNodeDto
{
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public bool Online { get; set; }
    public bool Mine { get; set; }
    public int Count { get; set; }
}
