using LanDisk.Core.Media;

namespace LanDisk.Core.Storage;

/// <summary>Which columns the browser can sort by. Kept as the wire name for <c>?sort=</c>.</summary>
public enum DirectorySortField { Name, Size, Modified, Type }

/// <summary>How far a search reaches. Plain listing always uses <see cref="Folder"/>.</summary>
public enum BrowseScope
{
    /// <summary>Only the directory being viewed.</summary>
    Folder,
    /// <summary>The directory being viewed and everything below it.</summary>
    FolderTree,
    /// <summary>The whole share, regardless of the directory being viewed.</summary>
    Share
}

/// <summary>
/// One browse request. Filters are applied on the server, before paging, so the page the client
/// receives is a slice of the already ordered, already filtered result set.
/// </summary>
public sealed class BrowseQuery
{
    public DirectorySortField Sort { get; init; } = DirectorySortField.Name;
    public bool Descending { get; init; }

    /// <summary>Substring match on the entry name. Applied at every scope.</summary>
    public string? Search { get; init; }

    public BrowseScope Scope { get; init; } = BrowseScope.Folder;

    /// <summary>Restrict to one coarse category; null disables the category filter.</summary>
    public FileCategory? Category { get; init; }

    public DateTimeOffset? ModifiedAfter { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>Applies to file sizes only. Directories have no size and are excluded when either bound is set.</summary>
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }

    /// <summary>When true only files are returned (used by "recent").</summary>
    public bool FilesOnly { get; init; }

    public int Offset { get; init; }
    public int Limit { get; init; } = 200;

    /// <summary>Upper bound on entries inspected by a recursive scan, so one request cannot walk an
    /// unbounded tree. Reaching it is reported to the client rather than silently truncating.</summary>
    public int MaxScan { get; init; } = 200_000;

    /// <summary>Upper bound on how many matching entries are kept in memory before paging.</summary>
    public int MaxResults { get; init; } = 20_000;

    public int MaxDepth { get; init; } = 32;

    public bool IsRecursive => Scope is BrowseScope.FolderTree or BrowseScope.Share;

    public bool HasFieldFilters =>
        Category is not null || ModifiedAfter is not null || ModifiedBefore is not null
        || MinSize is not null || MaxSize is not null || FilesOnly;
}
