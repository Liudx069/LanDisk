namespace LanDisk.Core.Media;

public enum ContentKind
{
    /// <summary>Safe to render inline inside the app's own page (image, plain text, PDF).</summary>
    SafeInline,
    /// <summary>Streamed to native media elements.</summary>
    Audio,
    Video,
    /// <summary>Active content: always sent as a download attachment.</summary>
    ActiveContent,
    /// <summary>Nothing special: download only.</summary>
    Binary
}

/// <summary>Coarse buckets used by the UI type filter. Classification is extension based only;
/// file content is never inspected or executed to decide a category.</summary>
public enum FileCategory
{
    Folder,
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Other
}

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".csv"] = "text/plain; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".avif"] = "image/avif",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".ogv"] = "video/ogg",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".mpeg"] = "video/mpeg",
        [".mpg"] = "video/mpeg",
        [".ts"] = "video/mp2t",
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".exe"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".bin"] = "application/octet-stream",
        [".iso"] = "application/octet-stream"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".go",
        ".rs", ".rb", ".php", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".html", ".htm", ".css",
        ".srt", ".vtt", ".ass", ".lrc", ".nfo", ".gitignore"
    };

    private static readonly HashSet<string> ActiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".svg", ".svgz", ".js", ".mjs", ".cjs", ".css",
        ".xml", ".xsl", ".xslt", ".swf", ".jar", ".hta", ".vbs", ".wsf", ".hta"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif", ".tif", ".tiff"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm", ".ogv", ".mkv", ".avi", ".wmv", ".flv", ".mpeg", ".mpg", ".ts"
    };

    public static string ForFileName(string fileName)
        => Map.TryGetValue(Path.GetExtension(fileName), out string? type) ? type : "application/octet-stream";

    public static bool IsText(string fileName) => TextExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsActiveContent(string fileName) => ActiveExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsImage(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsAudio(string fileName) => AudioExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsVideo(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsPdf(string fileName) => Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static ContentKind Classify(string fileName)
    {
        if (IsActiveContent(fileName)) return ContentKind.ActiveContent;
        if (IsImage(fileName) || IsPdf(fileName)) return ContentKind.SafeInline;
        if (IsText(fileName)) return ContentKind.SafeInline;
        if (IsAudio(fileName)) return ContentKind.Audio;
        if (IsVideo(fileName)) return ContentKind.Video;
        return ContentKind.Binary;
    }

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".log", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2", ".xz", ".tgz", ".iso", ".cab"
    };

    /// <summary>Bucket for the UI type filter. Directories are reported as <see cref="FileCategory.Folder"/>.</summary>
    public static FileCategory CategoryOf(string fileName, bool isDirectory)
    {
        if (isDirectory) return FileCategory.Folder;

        string extension = Path.GetExtension(fileName);
        if (ImageExtensions.Contains(extension)) return FileCategory.Image;
        if (VideoExtensions.Contains(extension)) return FileCategory.Video;
        if (AudioExtensions.Contains(extension)) return FileCategory.Audio;
        if (ArchiveExtensions.Contains(extension)) return FileCategory.Archive;
        if (DocumentExtensions.Contains(extension)) return FileCategory.Document;
        return FileCategory.Other;
    }

    /// <summary>Sort key for the "type" column: extension without the dot, lower case, empty when none.</summary>
    public static string TypeSortKey(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Length == 0 ? string.Empty : extension[1..].ToLowerInvariant();
    }

    /// <summary>
    /// RFC 5987/6266 compliant Content-Disposition value that survives non-ASCII names.
    ///
    /// The extended <c>filename*</c> parameter is emitted first, and the plain <c>filename</c>
    /// fallback is a readable ASCII reduction rather than a row of underscores, because some
    /// engines (WebKit among them) suggest the download name from the plain parameter only.
    /// </summary>
    public static string ContentDisposition(string fileName, bool attachment)
    {
        string type = attachment ? "attachment" : "inline";
        string encoded = Uri.EscapeDataString(fileName);
        return $"{type}; filename*=UTF-8''{encoded}; filename=\"{BuildAsciiFallback(fileName)}\"";
    }

    /// <summary>
    /// Reduces a name to ASCII. Non-ASCII runs collapse into a single separator, separators are
    /// trimmed, and a name with nothing meaningful left becomes "file" so the download still has a
    /// usable name on clients that ignore the RFC 5987 parameter.
    /// </summary>
    public static string BuildAsciiFallback(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        var builder = new System.Text.StringBuilder(stem.Length);
        foreach (char c in stem)
        {
            if (c < 128 && char.IsLetterOrDigit(c)) builder.Append(c);
            else if (c is ' ' or '-' or '_' or '.' or '+' or '(' or ')') builder.Append(c);
            else builder.Append('_');
        }

        string cleaned = System.Text.RegularExpressions.Regex
            .Replace(builder.ToString(), @"[_\s]+", "_")
            .Trim('_', '-', ' ', '.');

        if (cleaned.Length < 3) cleaned = "file";
        if (cleaned.Length > 120) cleaned = cleaned[..120];

        return cleaned + extension;
    }
}
