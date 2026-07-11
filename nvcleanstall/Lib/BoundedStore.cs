namespace CleanDriver.Lib;

// GAP-S02a — inert scaffold (red commit). Real bounded/expiring store lands in green: here it
// is an unbounded dictionary so the cap/expiry/pin tests fail.
public sealed class BoundedStore<T>
{
    private readonly Dictionary<string, T> _d = new();

    public int MaxEntries { get; set; } = int.MaxValue;
    public TimeSpan Retention { get; set; } = TimeSpan.MaxValue;
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public Func<string, bool> Pinned { get; set; } = _ => false;

    public void Add(string key, T value) => _d[key] = value;
    public bool TryGet(string key, out T value) => _d.TryGetValue(key, out value!);
    public T? Get(string key) => _d.TryGetValue(key, out var v) ? v : default;
    public IEnumerable<T> Values => _d.Values;
    public int Count => _d.Count;
}
