using System.Text;

namespace LanDisk.Core.Paths;

/// <summary>Machine readable reasons a path or name was rejected.</summary>
public enum PathRejection
{
    Empty,
    AbsolutePath,
    DriveLetter,
    UncPath,
    DevicePath,
    ParentTraversal,
    AlternateDataStream,
    ReservedDeviceName,
    InvalidCharacter,
    ControlCharacter,
    TrailingDotOrSpace,
    TooLong,
    ReparsePoint,
    OutsideShare,
    VolumeMismatch,
    ObjectChanged,
    HardLink,
    NullByte,
    InvalidEncoding
}

public sealed class PathSafetyException : Exception
{
    public PathSafetyException(PathRejection reason, string message) : base(message) => Reason = reason;
    public PathRejection Reason { get; }
}

/// <summary>
/// Pure string-level rules for relative paths and single path segments.
/// Nothing here touches the disk; it only decides whether a client supplied value is
/// structurally acceptable before any file system call is made.
/// </summary>
public static class PathRules
{
    public const int MaxSegments = 64;
    public const int MaxSegmentLength = 255;
    public const int MaxRelativeLength = 2048;

    private static readonly string[] ReservedBaseNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = dot >= 0 ? segment[..dot] : segment;
        foreach (string reserved in ReservedBaseNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Validates one path segment (a file or folder name).</summary>
    public static void ValidateSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            throw new PathSafetyException(PathRejection.Empty, "名称不能为空。");

        if (segment.Length > MaxSegmentLength)
            throw new PathSafetyException(PathRejection.TooLong, $"名称超过 {MaxSegmentLength} 个字符。");

        if (segment.IndexOf('\0') >= 0)
            throw new PathSafetyException(PathRejection.NullByte, "名称包含空字符。");

        if (segment == "." || segment == "..")
            throw new PathSafetyException(PathRejection.ParentTraversal, "名称不能是 . 或 ..。");

        foreach (char c in segment)
        {
            if (c < 0x20)
                throw new PathSafetyException(PathRejection.ControlCharacter, "名称包含控制字符。");
            if (Array.IndexOf(InvalidNameChars, c) >= 0)
                throw new PathSafetyException(PathRejection.InvalidCharacter, $"名称包含非法字符 '{c}'。");
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.'))
            throw new PathSafetyException(PathRejection.TrailingDotOrSpace, "名称不能以空格或句点结尾。");

        if (IsReservedDeviceName(segment))
            throw new PathSafetyException(PathRejection.ReservedDeviceName, $"'{segment}' 是 Windows 保留设备名。");
    }

    /// <summary>
    /// Converts a client supplied relative path into validated segments.
    /// Accepts both '/' and '\' as separators so that a Windows-minded client cannot
    /// smuggle a traversal through the other separator.
    /// </summary>
    public static string[] SplitRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return [];

        // Deliberately not trimmed: silently stripping a trailing space or dot would let a client
        // request a name that is not the one on disk. Segment validation rejects those forms with
        // an explicit reason instead.
        string value = relativePath;
        if (value.Length == 0) return [];

        if (value.Length > MaxRelativeLength)
            throw new PathSafetyException(PathRejection.TooLong, "相对路径过长。");

        if (value.IndexOf('\0') >= 0)
            throw new PathSafetyException(PathRejection.NullByte, "路径包含空字符。");

        // Reject any path that still carries percent-encoding after the framework decoded it once.
        // A literal '%' is legal in NTFS names, but a double-encoded traversal must never survive
        // into a second decode step performed by anything downstream.
        if (value.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%00", StringComparison.OrdinalIgnoreCase))
        {
            throw new PathSafetyException(PathRejection.InvalidEncoding, "路径包含重复编码的字符序列。");
        }

        // Device namespace paths must be classified before the generic UNC check, since
        // "\\.\C:" also begins with a double backslash.
        if (value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new PathSafetyException(PathRejection.DevicePath, "不接受设备命名空间路径。");

        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw new PathSafetyException(PathRejection.UncPath, "不接受 UNC 路径。");

        if (value.StartsWith('\\') || value.StartsWith('/'))
            throw new PathSafetyException(PathRejection.AbsolutePath, "不接受绝对路径。");

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            throw new PathSafetyException(PathRejection.DriveLetter, "不接受盘符路径。");

        if (value.Contains(':'))
            throw new PathSafetyException(PathRejection.AlternateDataStream, "不接受 NTFS 备用数据流。");

        string[] rawSegments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length > MaxSegments)
            throw new PathSafetyException(PathRejection.TooLong, $"路径层级超过 {MaxSegments} 层。");

        foreach (string segment in rawSegments)
        {
            if (segment == ".") continue;
            if (segment == "..")
                throw new PathSafetyException(PathRejection.ParentTraversal, "路径包含 .. 上跳。");
            ValidateSegment(segment);
        }

        return rawSegments.Where(s => s != ".").ToArray();
    }

    /// <summary>Encodes segments back into a canonical relative path using '\'.</summary>
    public static string JoinSegments(IEnumerable<string> segments)
        => string.Join('\\', segments);

    /// <summary>Normalizes a lone relative path string into canonical form.</summary>
    public static string NormalizeRelative(string? relativePath)
        => JoinSegments(SplitRelative(relativePath));

    public static string ToDisplayPath(string? relativePath)
        => NormalizeRelative(relativePath).Replace('\\', '/');

    /// <summary>Case-insensitive ordinal comparison used for path containment on NTFS.</summary>
    public static StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>Removes a trailing separator except for drive roots ("C:\").</summary>
    public static string TrimTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return path;
        if (path.Length == 1 && (path[0] == '\\' || path[0] == '/')) return path;
        return path.TrimEnd('\\', '/');
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies underneath it.
    /// Comparison is segment-wise, so "C:\Share2" is not treated as living inside "C:\Share".
    /// </summary>
    public static bool IsWithinRoot(string root, string candidate)
    {
        string r = TrimTrailingSeparator(Path.GetFullPath(root));
        string c = TrimTrailingSeparator(Path.GetFullPath(candidate));

        if (string.Equals(r, c, PathComparison)) return true;

        string rWithSep = r.EndsWith('\\') ? r : r + "\\";
        if (!c.StartsWith(rWithSep, PathComparison)) return false;

        string remainder = c[rWithSep.Length..];
        return remainder.Length > 0;
    }

    /// <summary>Relative portion of <paramref name="full"/> below <paramref name="root"/>, using '\'.</summary>
    public static string RelativeTo(string root, string full)
    {
        string r = TrimTrailingSeparator(Path.GetFullPath(root));
        string c = TrimTrailingSeparator(Path.GetFullPath(full));
        if (string.Equals(r, c, PathComparison)) return string.Empty;
        string rWithSep = r.EndsWith('\\') ? r : r + "\\";
        if (!c.StartsWith(rWithSep, PathComparison)) return string.Empty;
        return c[rWithSep.Length..];
    }

    /// <summary>
    /// Renders a byte length for the UI using IEC units (KiB/MiB/GiB/TiB) so that the label matches
    /// the 1024 based divisor actually used. The raw byte value is always sent alongside this text
    /// and remains the value used for sorting.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Size text for a listing entry; directories are reported as "—" without any scan.</summary>
    public static string FormatEntrySize(long bytes, bool isDirectory) => isDirectory ? "—" : FormatSize(bytes);

    public static string DescribeBytes(IEnumerable<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }
}
