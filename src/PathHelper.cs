using System.IO;
using System.Text;

namespace MegaSchematicsDownloader;

/// <summary>Pure helpers: file name sanitizing and size formatting.</summary>
public static class PathHelper
{
    private const int MaxSegmentLength = 150;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Turns a MEGA name into one safe Windows path segment (never contains a separator).</summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var bad = char.IsControl(ch) || ch == '/' || ch == '\\' || Array.IndexOf(invalid, ch) >= 0;
            builder.Append(bad ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) return "_unnamed";

        var dot = cleaned.IndexOf('.');
        var stem = (dot >= 0 ? cleaned.Substring(0, dot) : cleaned).TrimEnd();
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxSegmentLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 20) extension = string.Empty;
            var head = cleaned.Substring(0, MaxSegmentLength - extension.Length).TrimEnd('.', ' ');
            if (head.Length > 0 && char.IsHighSurrogate(head[head.Length - 1]))
                head = head.Substring(0, head.Length - 1);
            cleaned = head.Length == 0 ? "_unnamed" + extension : head + extension;
        }

        return cleaned;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
