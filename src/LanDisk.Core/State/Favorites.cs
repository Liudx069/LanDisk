using LanDisk.Core.Model;
using LanDisk.Core.Storage;

namespace LanDisk.Core.State;

/// <summary>A path a device chose to bookmark. Favourites are per device identity.</summary>
public sealed class FavoriteEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = string.Empty;
    public string ShareId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class FavoriteStore : JsonCollectionStore<FavoriteEntry>
{
    public FavoriteStore(string path) : base(path, () => []) { }

    public IReadOnlyList<FavoriteEntry> ForOwner(string ownerId)
        => Read(list => list.Where(f => f.OwnerId == ownerId).OrderByDescending(f => f.AddedAt).ToList());

    public FavoriteEntry? Find(string ownerId, string shareId, string relativePath)
        => Read(list => list.FirstOrDefault(f =>
            f.OwnerId == ownerId
            && f.ShareId == shareId
            && string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));

    public FavoriteEntry? FindById(string id) => Read(list => list.FirstOrDefault(f => f.Id == id));

    public void Add(FavoriteEntry entry) => Mutate(list =>
    {
        bool exists = list.Any(f => f.OwnerId == entry.OwnerId
                                    && f.ShareId == entry.ShareId
                                    && string.Equals(f.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (!exists) list.Add(entry);
    });

    public bool Remove(string id)
    {
        bool removed = false;
        Mutate(list => removed = list.RemoveAll(f => f.Id == id) > 0);
        return removed;
    }

    /// <summary>Drops bookmarks pointing into a share that no longer exists.</summary>
    public int RemoveForShare(string shareId)
    {
        int removed = 0;
        Mutate(list => removed = list.RemoveAll(f => f.ShareId == shareId));
        return removed;
    }
}

/// <summary>Per principal (paired device or the local administrator) UI preferences.</summary>
public sealed class UiPreference
{
    public string Sort { get; set; } = "Name";
    public bool Descending { get; set; }
    public string View { get; set; } = "list";
}

public sealed class PreferenceStore : JsonCollectionStore<PreferenceRecord>
{
    public PreferenceStore(string path) : base(path, () => []) { }

    public UiPreference Get(string ownerId)
    {
        PreferenceRecord? record = Read(list => list.FirstOrDefault(p => p.OwnerId == ownerId));
        return record?.Preferences ?? new UiPreference();
    }

    public void Set(string ownerId, UiPreference preferences) => Mutate(list =>
    {
        PreferenceRecord? existing = list.FirstOrDefault(p => p.OwnerId == ownerId);
        if (existing is null)
        {
            list.Add(new PreferenceRecord { OwnerId = ownerId, Preferences = preferences });
        }
        else
        {
            existing.Preferences = preferences;
        }
    });

    public void Remove(string ownerId) => Mutate(list => list.RemoveAll(p => p.OwnerId == ownerId));
}

public sealed class PreferenceRecord
{
    public string OwnerId { get; set; } = string.Empty;
    public UiPreference Preferences { get; set; } = new();
}

/// <summary>
/// Short lived cache of recursive share scans used by "recent" and cross-folder search.
/// The cache key includes the share's volume identity, so a reconnected or reformatted disk
/// never serves a stale snapshot, and the entry is dropped as soon as a scan reports truncation.
/// </summary>
public sealed class ShareScanCache
{
    private sealed record Snapshot(object Value, DateTimeOffset TakenAt);

    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
    private const int MaxEntries = 8;

    public T GetOrAdd<T>(string key, Func<T> factory)
    {
        lock (_gate)
        {
            if (_snapshots.TryGetValue(key, out Snapshot? cached)
                && DateTimeOffset.Now - cached.TakenAt < Lifetime
                && cached.Value is T hit)
            {
                return hit;
            }
        }

        T listing = factory();

        lock (_gate)
        {
            if (_snapshots.Count >= MaxEntries)
            {
                string? oldest = _snapshots.OrderBy(p => p.Value.TakenAt).FirstOrDefault().Key;
                if (oldest is not null) _snapshots.Remove(oldest);
            }
            _snapshots[key] = new Snapshot(listing!, DateTimeOffset.Now);
        }

        return listing;
    }

    public void Invalidate(string keyPrefix)
    {
        lock (_gate)
        {
            foreach (string key in _snapshots.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList())
            {
                _snapshots.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _snapshots.Clear();
    }
}
