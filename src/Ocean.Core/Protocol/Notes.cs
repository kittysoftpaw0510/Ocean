namespace Ocean.Core.Protocol;

/// <summary>
/// Notes: anyone who can open a video can leave notes on it (feedback, a remark at a moment of it), and vote each
/// note 👍/👎. A video's notes are kept by its owner (<c>notes.json</c> there), fetched only when someone opens them;
/// only their number travels in the catalog (<see cref="ItemDto.NoteCount"/>). Same access rule as the video.
/// </summary>
public sealed class NoteDto
{
    public string Id { get; set; } = "";
    /// <summary>The writer's node id.</summary>
    public string AuthorId { get; set; } = "";
    /// <summary>How the writer was called when writing ("user", else the PC's name).</summary>
    public string AuthorName { get; set; } = "";
    public string AuthorHost { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The moment of the video the note is about (seconds), or null for the video as a whole.</summary>
    public double? AtSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public int Up { get; set; }
    public int Down { get; set; }
    /// <summary>For the PC asking: its own vote on this note (+1, −1, 0).</summary>
    public int MyVote { get; set; }
    /// <summary>For the PC asking: it wrote this note (it may edit and delete it).</summary>
    public bool Mine { get; set; }
}

public sealed class NotesDto
{
    public List<NoteDto> Notes { get; set; } = new();
    /// <summary>The PC asking owns the video (it may delete any note on it).</summary>
    public bool IsOwner { get; set; }
    /// <summary>
    /// Set by this PC's service when the notes could not be read fresh: "offline", "unreachable", "old" (the owner's
    /// Ocean has no notes yet), "locked", "gone". Notes may still hold the last copy fetched.
    /// </summary>
    public string? Problem { get; set; }
    /// <summary>When <see cref="Notes"/> were read from the owner (an older copy when <see cref="Problem"/> is set).</summary>
    public DateTimeOffset? FetchedAt { get; set; }
    /// <summary>Why a write was refused (too long, too fast...), for the user.</summary>
    public string? Error { get; set; }
}

public sealed class NoteWriteDto
{
    public string Text { get; set; } = "";
    public double? AtSeconds { get; set; }
}

public sealed class NoteVoteDto
{
    /// <summary>+1, −1, or 0 (take it back).</summary>
    public int Vote { get; set; }
}

/// <summary>Event to the app: someone left a note on one of my videos.</summary>
public sealed class NoteAddedDto
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Text { get; set; } = "";
    public double? AtSeconds { get; set; }
}

public static class NoteLimits
{
    public const int MaxLength = 1000;
    /// <summary>A video keeps at most this many notes (the oldest of the least useful go first).</summary>
    public const int MaxPerVideo = 500;
    /// <summary>One PC may add a note to a video at most this often (seconds).</summary>
    public const int MinGapSeconds = 5;
}
