using LanDisk.Core.Model;

namespace LanDisk.Core.State;

/// <summary>A JSON-file backed collection guarded by a lock, with atomic writes.</summary>
public abstract class JsonCollectionStore<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items;

    protected JsonCollectionStore(string path, Func<List<T>> seed)
    {
        Path = path;
        _items = Load(path, seed);
    }

    public string Path { get; }

    private static List<T> Load(string path, Func<List<T>> seed)
    {
        try
        {
            if (!File.Exists(path)) return seed();
            return Configuration.JsonStore.Read(path, seed);
        }
        catch
        {
            return seed();
        }
    }

    protected TResult Read<TResult>(Func<List<T>, TResult> selector)
    {
        lock (_gate) return selector(_items);
    }

    protected void Mutate(Action<List<T>> action)
    {
        lock (_gate)
        {
            action(_items);
            Configuration.JsonStore.Write(Path, _items);
        }
    }

    /// <summary>
    /// Changes the in-memory records without writing the file.
    ///
    /// <para>Used for high-frequency bookkeeping such as "this device was just heard from": a phone polling
    /// every fifteen seconds must not rewrite devices.json 240 times an hour. The caller decides when the
    /// accumulated change is worth persisting (see <see cref="SaveNow"/>) or flushes it on shutdown.</para>
    /// </summary>
    protected void MutateInMemory(Action<List<T>> action)
    {
        lock (_gate) action(_items);
    }

    /// <summary>Writes the current in-memory state to disk.</summary>
    protected void SaveNow() => Mutate(_ => { });

    protected List<T> Snapshot() => Read(list => list.ToList());

    public int Count => Read(list => list.Count);
}

public sealed class ShareRegistry : JsonCollectionStore<ShareDefinition>
{
    public ShareRegistry(string path) : base(path, () => []) { }

    public IReadOnlyList<ShareDefinition> All() => Snapshot();

    public ShareDefinition? Find(string id) => Read(list => list.FirstOrDefault(s => s.Id == id));

    public void Add(ShareDefinition share) => Mutate(list => list.Add(share));

    public void Update(string id, Action<ShareDefinition> mutate) => Mutate(list =>
    {
        ShareDefinition? existing = list.FirstOrDefault(s => s.Id == id);
        if (existing is null) throw new KeyNotFoundException("共享不存在：" + id);
        mutate(existing);
    });

    public bool Remove(string id)
    {
        bool removed = false;
        Mutate(list => removed = list.RemoveAll(s => s.Id == id) > 0);
        return removed;
    }

    public void ReplaceAll(IEnumerable<ShareDefinition> shares) => Mutate(list =>
    {
        list.Clear();
        list.AddRange(shares);
    });
}

public sealed class DeviceRegistry : JsonCollectionStore<DeviceDefinition>
{
    public DeviceRegistry(string path) : base(path, () => []) { }

    public IReadOnlyList<DeviceDefinition> All() => Snapshot();

    public DeviceDefinition? Find(string id) => Read(list => list.FirstOrDefault(d => d.Id == id));

    public DeviceDefinition? FindByTokenHash(string hash)
        => Read(list => list.FirstOrDefault(d => !d.Revoked && string.Equals(d.TokenHash, hash, StringComparison.Ordinal)));

    public void Add(DeviceDefinition device) => Mutate(list => list.Add(device));

    public void Update(string id, Action<DeviceDefinition> mutate) => Mutate(list =>
    {
        DeviceDefinition? existing = list.FirstOrDefault(d => d.Id == id);
        if (existing is null) throw new KeyNotFoundException("设备不存在：" + id);
        mutate(existing);
    });

    public bool Remove(string id)
    {
        bool removed = false;
        Mutate(list => removed = list.RemoveAll(d => d.Id == id) > 0);
        return removed;
    }

    /// <summary>
    /// Records that this device was just heard from, in memory only.
    ///
    /// <para>Called for every authenticated request, which is what makes presence a measurement rather than
    /// a guess. The timestamp lives in RAM immediately so the very next request already sees it; the file is
    /// written by the host's periodic flush instead of once per request.</para>
    /// </summary>
    public void Touch(string id, string? remoteAddress, DateTimeOffset at) => MutateInMemory(list =>
    {
        DeviceDefinition? device = list.FirstOrDefault(d => d.Id == id);
        if (device is null || device.Revoked) return;
        device.LastSeenAt = at;
        if (!string.IsNullOrWhiteSpace(remoteAddress)) device.LastSeenIp = remoteAddress;
    });

    /// <summary>Persists whatever <see cref="Touch"/> accumulated.</summary>
    public void FlushActivity() => SaveNow();
}

public sealed class RecycleIndex : JsonCollectionStore<RecycleEntry>
{
    public RecycleIndex(string path) : base(path, () => []) { }

    public IReadOnlyList<RecycleEntry> All() => Snapshot();

    public RecycleEntry? Find(string id) => Read(list => list.FirstOrDefault(e => e.Id == id));

    public void Add(RecycleEntry entry) => Mutate(list => list.Add(entry));

    public void Update(string id, Action<RecycleEntry> mutate) => Mutate(list =>
    {
        RecycleEntry? existing = list.FirstOrDefault(e => e.Id == id);
        if (existing is null) throw new KeyNotFoundException("回收站条目不存在：" + id);
        mutate(existing);
    });

    public void Remove(string id)
    {
        Mutate(list => list.RemoveAll(e => e.Id == id));
    }

    /// <summary>Entries that are still holding payload in the store.</summary>
    public IReadOnlyList<RecycleEntry> Live() => Read(list => list
        .Where(e => e.State is RecycleState.Staged or RecycleState.Committed)
        .ToList());
}

public sealed class TransferLog : JsonCollectionStore<TransferRecord>
{
    private const int MaxRecords = 500;

    public TransferLog(string path) : base(path, () => []) { }

    public IReadOnlyList<TransferRecord> Recent(int limit)
        => Read(list => list.OrderByDescending(t => t.UpdatedAt).Take(limit).ToList());

    public IReadOnlyList<TransferRecord> ForDevice(string deviceId, int limit)
        => Read(list => list.Where(t => t.DeviceId == deviceId)
            .OrderByDescending(t => t.UpdatedAt).Take(limit).ToList());

    /// <summary>
    /// Removes one transfer record.
    ///
    /// <para>Used when a request turned out not to be a user-visible download at all: a <c>HEAD</c> probe, a
    /// <c>304 Not Modified</c> cache validation or a <c>416</c> range rejection delivers no body, and the
    /// browser performs those on its own. Creating a record and then marking it "cancelled" would put
    /// "文件名 / 已取消" on the transfers page for something the user never started — a worse lie than the
    /// stuck "进行中" this work set out to remove.</para>
    /// </summary>
    public bool Remove(string id)
    {
        bool removed = false;
        Mutate(list =>
        {
            int index = list.FindIndex(t => t.Id == id);
            if (index < 0) return;
            if (IsTerminal(list[index].State)) return;   // 已成终态的是真实事件，保留
            list.RemoveAt(index);
            removed = true;
        });
        return removed;
    }

    public void Upsert(TransferRecord record) => Mutate(list =>
    {
        int index = list.FindIndex(t => t.Id == record.Id);
        if (index >= 0)
        {
            // 终态不可被回退。先前是无条件替换，于是一条已 Committed/Cancelled/Failed 的记录
            // 只要后面还有人用同一个 Id 写一次 InProgress，就会重新变成"进行中"。
            if (IsTerminal(list[index].State) && !IsTerminal(record.State)) return;
            list[index] = record;
        }
        else list.Add(record);
        if (list.Count > MaxRecords)
        {
            var ordered = list.OrderByDescending(t => t.UpdatedAt).Take(MaxRecords).ToList();
            list.Clear();
            list.AddRange(ordered);
        }
    });

    /// <summary>True for the states a transfer can never leave.</summary>
    public static bool IsTerminal(TransferState state)
        => state is TransferState.Committed or TransferState.Failed or TransferState.Cancelled;

    /// <summary>
    /// Writes a terminal state for one transfer, exactly once.
    ///
    /// <para>Idempotent by construction: if the record is already terminal the call is a no-op, so a
    /// cancellation arriving after a completion — or a retry after either — can never produce two different
    /// terminal states or fall back to "in progress". Returns true when this call was the one that moved it.</para>
    /// </summary>
    public bool Terminalize(string id, TransferState state, long transferredBytes, string? error)
    {
        if (!IsTerminal(state)) throw new ArgumentOutOfRangeException(nameof(state), "Terminalize 只接受终态。");

        bool applied = false;
        Mutate(list =>
        {
            int index = list.FindIndex(t => t.Id == id);
            if (index < 0) return;
            if (IsTerminal(list[index].State)) return;

            list[index].State = state;
            list[index].TransferredBytes = transferredBytes;
            list[index].Error = error;
            list[index].UpdatedAt = DateTimeOffset.Now;
            applied = true;
        });
        return applied;
    }
}

/// <summary>Append-only, size-capped, redacting audit log.</summary>
public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _retention;

    public AuditLog(string path, int retentionEntries)
    {
        _path = path;
        _retention = Math.Max(100, retentionEntries);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public void Write(AuditEntry entry)
    {
        string line = Configuration.JsonStore.SerializeOneLine(entry);
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
                TrimIfNeeded();
            }
            catch
            {
                // Logging must never break a user operation.
            }
        }
    }

    public IReadOnlyList<AuditEntry> Tail(int limit)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];
                string[] lines = File.ReadAllLines(_path);
                return lines.Reverse()
                    .Take(Math.Clamp(limit, 1, 2000))
                    .Select(l => Configuration.JsonStore.TryParseOneLine<AuditEntry>(l))
                    .Where(e => e is not null)
                    .Select(e => e!)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < 4 * 1024 * 1024) return;
        string[] lines = File.ReadAllLines(_path);
        if (lines.Length <= _retention) return;
        File.WriteAllLines(_path, lines[^_retention..]);
    }
}
