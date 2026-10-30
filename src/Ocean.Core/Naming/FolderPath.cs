namespace Ocean.Core.Naming;

/// <summary>
/// Virtual folder paths inside a share: forward-slash separated, no leading/trailing slash, "" = share root.
/// Used both for the on-disk structure (default) and for folders the owner creates/renames in Ocean.
/// </summary>
public static class FolderPath
{
    public const int MaxSegment = 120;

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            var p = new string(raw.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            if (p.Length == 0 || p == "." || p == "..") continue;
            if (p.Length > MaxSegment) p = p[..MaxSegment].TrimEnd();
            clean.Add(p);
        }
        return string.Join('/', clean);
    }

    public static string Combine(string parent, string child)
    {
        parent = Normalize(parent); child = Normalize(child);
        if (parent.Length == 0) return child;
        if (child.Length == 0) return parent;
        return parent + "/" + child;
    }

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static string Name(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it ("" contains everything).</summary>
    public static bool IsUnder(string path, string folder)
        => folder.Length == 0 || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The child of <paramref name="folder"/> that leads to <paramref name="path"/>, or null if path is not strictly below folder.</summary>
    public static string? ChildOn(string path, string folder)
    {
        if (!IsUnder(path, folder) || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = folder.Length == 0 ? path : path[(folder.Length + 1)..];
        var i = rest.IndexOf('/');
        return Combine(folder, i < 0 ? rest : rest[..i]);
    }

    /// <summary>Replaces the <paramref name="from"/> prefix of <paramref name="path"/> with <paramref name="to"/>.</summary>
    public static string Rebase(string path, string from, string to)
    {
        if (!IsUnder(path, from)) return path;
        var rest = string.Equals(path, from, StringComparison.OrdinalIgnoreCase) ? "" : (from.Length == 0 ? path : path[(from.Length + 1)..]);
        return Combine(to, rest);
    }

    /// <summary>"a/b/c" → ["a", "a/b", "a/b/c"].</summary>
    public static IEnumerable<string> SelfAndAncestors(string path)
    {
        if (path.Length == 0) yield break;
        var idx = 0;
        while ((idx = path.IndexOf('/', idx)) >= 0) { yield return path[..idx]; idx++; }
        yield return path;
    }

    public static string Display(string path) => path.Replace("/", "  ›  ");
}
