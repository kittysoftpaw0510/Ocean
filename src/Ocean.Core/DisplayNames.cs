namespace Ocean.Core;

/// <summary>
/// What people see for a PC everywhere in Ocean: the "Your name" set on it, or the PC's name when that is empty.
/// </summary>
public static class DisplayNames
{
    public static string Of(string? user, string? pc)
        => !string.IsNullOrWhiteSpace(user) ? user.Trim() : (pc ?? "").Trim();
}
