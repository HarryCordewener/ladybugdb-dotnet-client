namespace LadybugDb.Client;

/// <summary>
/// A bounded, least-recently-used cache of prepared statements keyed by their Cypher text, with
/// check-out semantics: a hit <em>removes</em> the entry and hands it to exactly one caller, who
/// returns it when done. That is what makes it safe under <see cref="LadybugConnection"/>'s
/// concurrent-use contract - two callers of the same statement never interleave one caller's
/// <c>Bind</c> with the other's execute, which a shared entry would allow no matter how each
/// individual call is locked. A concurrent second caller simply misses and prepares its own.
/// </summary>
/// <typeparam name="T">The cached statement type; generic so the policy is testable without an engine.</typeparam>
internal sealed class StatementCache<T> : IDisposable where T : class, IDisposable
{
    /// <summary>What a checked-out entry carries back: the statement and the parameter names it was first bound with.</summary>
    internal sealed class Entry(string key, T statement, string[] parameterNames)
    {
        internal string Key { get; } = key;
        internal T Statement { get; } = statement;

        /// <summary>
        /// Sorted, ordinal. A cached statement keeps its last bound values on the engine side, so a
        /// later call that binds a different set of names would silently run with stale values for
        /// the names it left out. Recording the shape at first use lets the connection refuse that.
        /// </summary>
        internal string[] ParameterNames { get; } = parameterNames;
    }

    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();   // most recent at the head
    private bool _disposed;

    internal StatementCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    internal int Count { get { lock (_gate) return _byKey.Count; } }

    /// <summary>Removes and returns the entry for <paramref name="key"/>, or <see langword="null"/>.</summary>
    internal Entry? TryCheckOut(string key)
    {
        lock (_gate)
        {
            if (!_byKey.Remove(key, out var node)) return null;
            _recency.Remove(node);
            return node.Value;
        }
    }

    /// <summary>
    /// Puts an entry (back) in as most recently used, evicting the least recently used one if the
    /// cache is full. If an entry for the same key arrived meanwhile (a concurrent miss that
    /// prepared its own), the incoming one is disposed and the resident one kept - either is as
    /// good, and keeping one bounds the number of live native statements per key. Returns
    /// whatever was disposed so callers can assert on it; disposes the entry outright if the cache
    /// itself is disposed or its capacity is zero.
    /// </summary>
    internal void Return(Entry entry)
    {
        T? toDispose = null;
        lock (_gate)
        {
            if (_disposed || _capacity == 0)
            {
                toDispose = entry.Statement;
            }
            else if (_byKey.ContainsKey(entry.Key))
            {
                toDispose = entry.Statement;
            }
            else
            {
                if (_byKey.Count >= _capacity)
                {
                    var oldest = _recency.Last!;
                    _recency.RemoveLast();
                    _byKey.Remove(oldest.Value.Key);
                    toDispose = oldest.Value.Statement;
                }
                _byKey[entry.Key] = _recency.AddFirst(entry);
            }
        }
        toDispose?.Dispose();
    }

    /// <summary>Disposes every resident statement. Entries checked out at this moment are disposed when returned.</summary>
    public void Dispose()
    {
        List<Entry> resident;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            resident = [.. _recency];
            _recency.Clear();
            _byKey.Clear();
        }
        foreach (var entry in resident) entry.Statement.Dispose();
    }
}
