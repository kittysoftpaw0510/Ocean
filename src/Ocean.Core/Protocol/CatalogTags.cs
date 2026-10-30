using System.Globalization;

namespace Ocean.Core.Protocol;

/// <summary>
/// Catalog ETags: <c>"&lt;version&gt;-&lt;view&gt;"</c>, where view fingerprints what the caller may see (which locks it
/// has open). A node sends the tag of the catalog it holds; the owner answers 304 (unchanged), a delta, or the full
/// catalog.
/// </summary>
public static class CatalogTags
{
    public const string BaseHeader = "X-Ocean-Catalog-Base";

    public static string Make(long version, ulong view) => $"\"{version}-{view:x}\"";

    public static bool TryParse(string? tag, out long version, out ulong view)
    {
        version = 0; view = 0;
        if (string.IsNullOrEmpty(tag)) return false;
        var t = tag.Trim();
        if (t.StartsWith("W/", StringComparison.Ordinal)) t = t[2..];
        t = t.Trim('"');
        var dash = t.IndexOf('-');
        return dash > 0
               && long.TryParse(t.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out version)
               && ulong.TryParse(t.AsSpan(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out view);
    }
}
