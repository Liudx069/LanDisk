using System.IO.Enumeration;
using System.Text;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Media;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;

namespace LanDisk.Core.Storage;

public sealed class DirectoryEntryDto
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset ModifiedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Opaque object version token; write operations must echo it back.</summary>
    public required string ObjectId { get; init; }

    public bool IsHidden { get; init; }
    public bool IsReparsePoint { get; init; }
    public string ContentKind { get; init; } = "binary";
    public string MimeType { get; init; } = "application/octet-stream";
    public FileCategory Category { get; init; } = FileCategory.Other;
    public bool CanPreview { get; init; }
    public bool CanPlay { get; init; }

    /// <summary>
    /// Unicode-normalised name used as the first tie breaker. Normalising means two names that
    /// differ only in composition order sort identically, which keeps paging deterministic.
    /// </summary>
    public string SortName { get; init; } = string.Empty;
}

public sealed class DirectoryListing
{
    public required string RelativePath { get; init; }
    public required string DisplayPath { get; init; }
    public required IReadOnlyList<DirectoryEntryDto> Entries { get; init; }
    public required int TotalCount { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required bool HasMore { get; init; }
    public required long ScanMilliseconds { get; init; }
    public required IReadOnlyList<BreadcrumbDto> Breadcrumbs { get; init; }
    public required ShareCapabilities Capabilities { get; init; }
    public required DeviceAccessLevel DeviceLevel { get; init; }

    /// <summary>Number of raw directory entries inspected. For a recursive scan this can exceed
    /// <see cref="TotalCount"/> because non-matching entries are still counted.</summary>
    public int ScannedCount { get; init; }

    /// <summary>True when <see cref="BrowseQuery.MaxScan"/> or <see cref="BrowseQuery.MaxResults"/>
    /// was reached, so the client knows the result is incomplete rather than empty.</summary>
    public bool Truncated { get; init; }

    public DirectorySortField Sort { get; init; } = DirectorySortField.Name;
    public bool Descending { get; init; }
}

public sealed record BreadcrumbDto(string Name, string RelativePath);

/// <summary>
/// The complete (but bounded) result of a scan, before paging. Callers that need the whole ordered
/// set — "recent" is the main one — must use this instead of a page, otherwise a fixed page size
/// silently caps how many candidates can ever be considered.
/// </summary>
public sealed class CollectedListing
{
    public required IReadOnlyList<DirectoryEntryDto> Entries { get; init; }
    public required int ScannedCount { get; init; }
    public required bool ScanLimitReached { get; init; }
    public required bool ResultLimitReached { get; init; }
    public required long ScanMilliseconds { get; init; }

    public bool Truncated => ScanLimitReached || ResultLimitReached;
}

/// <summary>
/// Directory listing built on FileSystemEnumerable so that attributes, size and timestamps come
/// from the enumeration buffer instead of one stat call per entry. That matters for the
/// 10,000 entry case, where a naive implementation performs 10,000 extra syscalls.
///
/// Ordering guarantees:
///  * directories are grouped before files in both directions;
///  * the requested field is compared, then normalised name, then the relative path, which is
///    unique — so the ordering is a total order and paging never drifts between requests;
///  * sorting and filtering both complete before the page is sliced.
/// </summary>
public sealed class DirectoryBrowser
{
    private readonly PathResolver _resolver;

    public DirectoryBrowser(PathResolver resolver) => _resolver = resolver;

    public DirectoryListing List(ShareDefinition share, string? relativePath, DeviceAccessLevel deviceLevel, BrowseQuery query)
    {
        int offset = Math.Max(0, query.Offset);
        int limit = Math.Clamp(query.Limit, 1, 1000);

        using ResolvedTarget target = _resolver.Resolve(share, relativePath, ResolveMode.Existing);
        if (!target.IsDirectory)
            throw new InvalidOperationException("目标不是目录。");

        string relative = target.RelativePath;
        CollectedListing collected = CollectCore(share, target, query);

        List<DirectoryEntryDto> page = collected.Entries.Skip(offset).Take(limit).ToList();

        return new DirectoryListing
        {
            RelativePath = relative,
            DisplayPath = PathRules.ToDisplayPath(relative),
            Entries = page,
            TotalCount = collected.Entries.Count,
            Offset = offset,
            Limit = limit,
            HasMore = offset + page.Count < collected.Entries.Count,
            ScanMilliseconds = collected.ScanMilliseconds,
            Breadcrumbs = BuildBreadcrumbs(share, relative),
            Capabilities = share.Capabilities,
            DeviceLevel = deviceLevel,
            ScannedCount = collected.ScannedCount,
            Truncated = collected.Truncated,
            Sort = query.Sort,
            Descending = query.Descending
        };
    }

    /// <summary>
    /// Scans, filters and orders without paging, so a caller can consider every candidate the caps
    /// allowed and know exactly whether anything was left out.
    /// </summary>
    public CollectedListing Collect(ShareDefinition share, string? relativePath, BrowseQuery query)
    {
        using ResolvedTarget target = _resolver.Resolve(share, relativePath, ResolveMode.Existing);
        if (!target.IsDirectory)
            throw new InvalidOperationException("目标不是目录。");

        return CollectCore(share, target, query);
    }

    private CollectedListing CollectCore(ShareDefinition share, ResolvedTarget target, BrowseQuery query)
    {
        string rootFull = PathRules.TrimTrailingSeparator(Path.GetFullPath(share.RootPath));
        string relative = target.RelativePath;

        // A recursive scan always starts from a directory that has already been through the
        // resolver, so it can never wander outside the share.
        string scanRoot = query.Scope == BrowseScope.Share ? rootFull : target.FinalPath;
        string scanBase = query.Scope == BrowseScope.Share ? string.Empty : relative;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var entries = new List<DirectoryEntryDto>(512);
        int scanned = 0;
        bool scanLimitReached = false;
        bool resultLimitReached = false;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = query.IsRecursive,
            IgnoreInaccessible = true,
            // Hidden/system files stay visible, but nothing carrying the reparse point attribute
            // is ever traversed: a junction inside a share must not lead outside it.
            AttributesToSkip = query.IsRecursive ? FileAttributes.ReparsePoint : 0,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
            MaxRecursionDepth = Math.Clamp(query.MaxDepth, 1, 128)
        };

        string internalStoreName = AppPaths.VolumeStoreName;
        string? search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        var enumerable = new FileSystemEnumerable<DirectoryEntryDto?>(
            scanRoot,
            (ref FileSystemEntry entry) => Materialize(ref entry, scanBase, internalStoreName),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                string name = entry.FileName.ToString();
                if (name is "." or "..") return false;
                if (string.Equals(name, internalStoreName, StringComparison.OrdinalIgnoreCase)) return false;
                if (search is not null && !NameMatches(name, search)) return false;
                return true;
            },

            // Recursion must NOT be pruned by the name filter, otherwise a search for "报告" would
            // never look inside a directory called "2026". Only the internal store is skipped.
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !string.Equals(entry.FileName.ToString(), internalStoreName, StringComparison.OrdinalIgnoreCase)
        };

        foreach (DirectoryEntryDto? dto in enumerable)
        {
            scanned++;
            if (dto is not null && MatchesFilters(dto, query))
            {
                entries.Add(dto);
                if (entries.Count >= query.MaxResults)
                {
                    resultLimitReached = true;
                    break;
                }
            }

            if (scanned >= query.MaxScan)
            {
                scanLimitReached = true;
                break;
            }
        }

        SortInPlace(entries, query.Sort, query.Descending);

        // The reported time covers enumeration, filtering and ordering together, so it is an
        // honest figure for "how long did the server spend on this listing".
        stopwatch.Stop();

        return new CollectedListing
        {
            Entries = entries,
            ScannedCount = scanned,
            ScanLimitReached = scanLimitReached,
            ResultLimitReached = resultLimitReached,
            ScanMilliseconds = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>Convenience overload kept for the simple "list this directory" case.</summary>
    public DirectoryListing List(
        ShareDefinition share,
        string? relativePath,
        DeviceAccessLevel deviceLevel,
        DirectorySortField sort,
        bool descending,
        int offset,
        int limit,
        string? filter)
        => List(share, relativePath, deviceLevel, new BrowseQuery
        {
            Sort = sort,
            Descending = descending,
            Offset = offset,
            Limit = limit,
            Search = filter
        });

    private static bool NameMatches(string name, string search)
    {
        if (name.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;

        // Falls back to a Unicode-normalised comparison so a search typed with decomposed
        // characters still matches a composed file name.
        string normalizedName = name.Normalize(NormalizationForm.FormC);
        string normalizedSearch = search.Normalize(NormalizationForm.FormC);
        return normalizedName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilters(DirectoryEntryDto entry, BrowseQuery query)
    {
        if (query.FilesOnly && entry.IsDirectory) return false;

        if (query.Category is { } category && entry.Category != category) return false;

        if (query.ModifiedAfter is { } after && entry.ModifiedAt < after) return false;
        if (query.ModifiedBefore is { } before && entry.ModifiedAt > before) return false;

        if (query.MinSize is not null || query.MaxSize is not null)
        {
            // Directories have no meaningful size here, so a size filter excludes them entirely
            // rather than pretending they are zero bytes.
            if (entry.IsDirectory) return false;
            if (query.MinSize is { } min && entry.Length < min) return false;
            if (query.MaxSize is { } max && entry.Length > max) return false;
        }

        return true;
    }

    internal static void SortInPlace(List<DirectoryEntryDto> entries, DirectorySortField field, bool descending)
    {
        Comparison<DirectoryEntryDto> comparison = BuildComparison(field, descending);
        entries.Sort(comparison);
    }

    internal static Comparison<DirectoryEntryDto> BuildComparison(DirectorySortField field, bool descending)
    {
        int direction = descending ? -1 : 1;

        return (a, b) =>
        {
            // Directories always come first, in both directions. Reversing the whole sequence
            // (as a naive OrderBy(...).Reverse() would) buries folders at the bottom.
            int group = (a.IsDirectory ? 0 : 1).CompareTo(b.IsDirectory ? 0 : 1);
            if (group != 0) return group;

            int primary = field switch
            {
                DirectorySortField.Size => a.Length.CompareTo(b.Length),
                DirectorySortField.Modified => a.ModifiedAt.CompareTo(b.ModifiedAt),
                DirectorySortField.Type => string.CompareOrdinal(
                    MimeTypes.TypeSortKey(a.Name), MimeTypes.TypeSortKey(b.Name)),
                _ => NaturalComparer.Instance.Compare(a.SortName, b.SortName)
            };
            if (primary != 0) return direction * primary;

            if (field != DirectorySortField.Name)
            {
                int byName = NaturalComparer.Instance.Compare(a.SortName, b.SortName);
                if (byName != 0) return direction * byName;
            }

            // Unique, always ascending: this is what makes paging stable.
            return string.CompareOrdinal(a.RelativePath, b.RelativePath);
        };
    }

    private static DirectoryEntryDto? Materialize(ref FileSystemEntry entry, string parentRelative, string internalStoreName)
    {
        try
        {
            string name = entry.FileName.ToString();
            if (string.Equals(name, internalStoreName, StringComparison.OrdinalIgnoreCase)) return null;

            bool isDirectory = entry.IsDirectory;
            uint attributes = 0;
            try { attributes = (uint)entry.Attributes; } catch { }

            bool isReparse = (attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            bool isHidden = (attributes & 0x0002) != 0;

            long length = 0;
            if (!isDirectory)
            {
                try { length = entry.Length; } catch { }
            }

            DateTimeOffset modified = DateTimeOffset.MinValue;
            DateTimeOffset created = DateTimeOffset.MinValue;
            // FileSystemEntry already exposes DateTimeOffset values, so no conversion is needed.
            // The local offset is preserved, which makes the timestamp unambiguous on the wire.
            try { modified = entry.LastWriteTimeUtc.ToLocalTime(); } catch { }
            try { created = entry.CreationTimeUtc.ToLocalTime(); } catch { }

            string relative = string.IsNullOrEmpty(parentRelative) ? name : parentRelative + "\\" + name;

            // Enumerated entries carry no file index, so the version token is derived from the
            // metadata the caller will echo back; the authoritative identity is re-checked
            // through an open handle before any write.
            string objectId = $"{length:x}-{modified.UtcTicks:x}";

            string mime = isDirectory ? "inode/directory" : MimeTypes.ForFileName(name);
            ContentKind kind = isDirectory ? ContentKind.Binary : MimeTypes.Classify(name);

            return new DirectoryEntryDto
            {
                Name = name,
                RelativePath = relative,
                IsDirectory = isDirectory,
                Length = length,
                ModifiedAt = modified,
                CreatedAt = created,
                ObjectId = objectId,
                IsHidden = isHidden,
                IsReparsePoint = isReparse,
                ContentKind = kind.ToString(),
                MimeType = mime,
                Category = MimeTypes.CategoryOf(name, isDirectory),
                CanPreview = !isDirectory && kind == ContentKind.SafeInline,
                CanPlay = !isDirectory && kind is ContentKind.Audio or ContentKind.Video,
                SortName = name.Normalize(NormalizationForm.FormC)
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<BreadcrumbDto> BuildBreadcrumbs(ShareDefinition share, string relative)
    {
        var list = new List<BreadcrumbDto> { new(share.DisplayName, string.Empty) };
        if (string.IsNullOrEmpty(relative)) return list;

        string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string accumulated = string.Empty;
        foreach (string segment in segments)
        {
            accumulated = accumulated.Length == 0 ? segment : accumulated + "\\" + segment;
            list.Add(new BreadcrumbDto(segment, accumulated));
        }
        return list;
    }
}

/// <summary>Human friendly ordering so that "file2" sorts before "file10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                ReadOnlySpan<char> nx = x.AsSpan(si, i - si).TrimStart('0');
                ReadOnlySpan<char> ny = y.AsSpan(sj, j - sj).TrimStart('0');
                if (nx.Length != ny.Length) return nx.Length - ny.Length;
                int cmp = nx.SequenceCompareTo(ny);
                if (cmp != 0) return cmp;
                continue;
            }

            if (x[i] != y[j])
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                // Same letter, different case: fall back to the ordinal value so the comparison
                // stays antisymmetric ("A" vs "a" must not compare equal in both directions).
                c = x[i].CompareTo(y[j]);
                if (c != 0) return c;
            }
            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
