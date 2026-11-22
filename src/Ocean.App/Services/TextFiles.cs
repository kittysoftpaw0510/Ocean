using System.Globalization;
using System.Text;

namespace Ocean.App.Services;

/// <summary>Reading a text file whatever its encoding: a byte-order mark, else UTF-8, else this PC's own code page.</summary>
public static class TextFiles
{
    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The text, with its line ends as they are; null when it isn't text (it holds zero bytes: other data).</summary>
    public static string? Decode(byte[] bytes)
    {
        // a byte-order mark says it
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // UTF-16 without a mark: every other byte zero in plain Latin text
        if (bytes.Length >= 4 && LooksUtf16(bytes, out var little)) return (little ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 64 * 1024)) >= 0) return null;
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { }
        // not UTF-8: what this PC's older programs write (Windows-1252, 949, 936...)
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    private static bool LooksUtf16(byte[] b, out bool little)
    {
        int n = Math.Min(b.Length / 2 * 2, 4096), evenZero = 0, oddZero = 0;
        for (var i = 0; i < n; i += 2) { if (b[i] == 0) evenZero++; if (b[i + 1] == 0) oddZero++; }
        var pairs = n / 2;
        little = oddZero > evenZero;
        return pairs > 0 && (oddZero > pairs * 0.4 && evenZero < pairs * 0.05 || evenZero > pairs * 0.4 && oddZero < pairs * 0.05);
    }
}
