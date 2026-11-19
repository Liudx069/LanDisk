using System.Globalization;

namespace LanDisk.Core.Media;

/// <summary>A single byte range as requested by the client.</summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;
}

public sealed class RangeParseResult
{
    public bool IsRangeRequest { get; init; }
    public bool IsSatisfiable { get; init; }
    public bool IsMultipleRanges { get; init; }
    public ByteRange? Range { get; init; }
    public string? RawValue { get; init; }
}

/// <summary>
/// Parses the HTTP Range header. Only single byte ranges are honoured; multi-range requests are
/// reported so the caller can fall back to a plain 200 response, which is allowed by RFC 9110
/// and keeps the implementation free of multipart/byteranges bookkeeping.
/// </summary>
public static class RangeHeader
{
    public static RangeParseResult Parse(string? headerValue, long resourceLength)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return new RangeParseResult { IsRangeRequest = false, IsSatisfiable = true };

        string value = headerValue.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = true, RawValue = value };

        string spec = value[6..].Trim();
        if (spec.Length == 0)
            return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };

        if (spec.Contains(','))
            return new RangeParseResult { IsRangeRequest = true, IsMultipleRanges = true, IsSatisfiable = true, RawValue = value };

        int dash = spec.IndexOf('-');
        if (dash < 0)
            return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };

        string firstPart = spec[..dash].Trim();
        string lastPart = spec[(dash + 1)..].Trim();

        if (resourceLength <= 0)
            return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };

        long start;
        long end;

        if (firstPart.Length == 0)
        {
            // suffix-range: bytes=-N  → last N bytes
            if (!long.TryParse(lastPart, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
                return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };

            if (suffix >= resourceLength) { start = 0; end = resourceLength - 1; }
            else { start = resourceLength - suffix; end = resourceLength - 1; }
        }
        else
        {
            if (!long.TryParse(firstPart, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0)
                return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };

            if (lastPart.Length == 0)
            {
                end = resourceLength - 1;
            }
            else
            {
                if (!long.TryParse(lastPart, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
                    return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };
                if (end > resourceLength - 1) end = resourceLength - 1;
            }

            if (start >= resourceLength)
                return new RangeParseResult { IsRangeRequest = true, IsSatisfiable = false, RawValue = value };
        }

        return new RangeParseResult
        {
            IsRangeRequest = true,
            IsSatisfiable = true,
            Range = new ByteRange(start, end),
            RawValue = value
        };
    }

    /// <summary>
    /// Evaluates If-Range. When the validator does not match the current representation the
    /// Range header must be ignored and the full entity returned with 200.
    /// </summary>
    public static bool IfRangeMatches(string? ifRange, string etag, DateTimeOffset lastModified)
    {
        if (string.IsNullOrWhiteSpace(ifRange)) return true;
        string value = ifRange.Trim();

        if (value.StartsWith('"') || value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return string.Equals(value, etag, StringComparison.Ordinal);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            return parsed.ToUnixTimeSeconds() == lastModified.ToUnixTimeSeconds();

        return false;
    }

    public static string BuildETag(long length, DateTimeOffset lastWrite)
        => $"\"{length:x}-{lastWrite.ToUnixTimeMilliseconds():x}\"";

    public static string HttpDate(DateTimeOffset value) => value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
}
