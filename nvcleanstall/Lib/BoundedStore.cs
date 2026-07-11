using System.Collections.Concurrent;

namespace CleanDriver.Lib;

// GAP-S02a (BUG-05 pin 5): a concurrent store that bounds its size and expires stale entries,
// but never evicts a PINNED entry — a running job, or a session a running job references. That
// pin is the load-bearing invariant: capping resource growth must not kill in-use state.
//
// The cap is necessarily SOFT for pinned entries: if everything at the cap is pinned, a new
// entry exceeds the cap rather than a running one being evicted — the only reading consistent
// with "never evict a running job."
public sealed class BoundedStore<T>
{
    private sealed record Entry(T Value, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Entry> _d = new();

    public int MaxEntries { get; set; } = int.MaxValue;
    public TimeSpan Retention { get; set; } = TimeSpan.MaxValue;
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public Func<string, bool> Pinned { get; set; } = _ => false;

    public void Add(string key, T value)
    {
        _d[key] = new Entry(value, Clock());
        Evict();
    }

    public bool TryGet(string key, out T value)
    {
        if (_d.TryGetValue(key, out var e)) { value = e.Value; return true; }
        value = default!;
        return false;
    }

    public T? Get(string key) => _d.TryGetValue(key, out var e) ? e.Value : default;
    public IEnumerable<T> Values => _d.Values.Select(e => e.Value);
    public int Count => _d.Count;

    private void Evict()
    {
        var now = Clock();
        // 1. Expire stale entries — but never a pinned one, however old.
        foreach (var kv in _d)
            if (!Pinned(kv.Key) && now - kv.Value.At > Retention)
                _d.TryRemove(kv.Key, out _);

        // 2. Enforce the cap by removing the oldest UNPINNED entries. If only pinned entries
        //    remain, the cap goes soft rather than evicting in-use state.
        while (_d.Count > MaxEntries)
        {
            var oldest = _d.Where(kv => !Pinned(kv.Key))
                           .OrderBy(kv => kv.Value.At)
                           .Select(kv => (string?)kv.Key)
                           .FirstOrDefault();
            if (oldest is null) break; // everything left is pinned — soft cap
            _d.TryRemove(oldest, out _);
        }
    }
}
