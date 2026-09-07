namespace LadybugDb.Client;

/// <summary>
/// A bounded, least-recently-used cache of prepared statements keyed by statement text, with
/// check-out semantics: a hit removes the entry and hands it to one caller, who returns it when
/// done. A shared entry could not be made safe under <see cref="LadybugConnection"/>'s
/// concurrent-use contract - two callers would interleave one's <c>Bind</c> with the other's
/// execute however each call was locked - so a concurrent second caller misses and prepares its own.
/// </summary>
/// <typeparam name="T">The statement type; generic so the policy is testable without an engine.</typeparam>
internal sealed class StatementCache<T> : IDisposable where T : class, IDisposable
{
    /// <summary>
    /// A checked-out statement with the parameter names it was first bound with (sorted, ordinal).
    /// The engine keeps a statement's previous bound values, so a later call binding a different set
    /// of names would silently run with stale values for the names it left out.
    /// </summary>
    internal sealed class Entry(string key, T statement, string[] parameterNames)
    {
        internal string Key { get; } = key;
        internal T Statement { get; } = statement;
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
    /// Puts an entry (back) in as most recently used, evicting and disposing the least recently
    /// used one when full. Disposes the incoming statement instead when the cache is disposed, has
    /// no capacity, or already holds one for the same key (a concurrent miss prepared its own).
    /// </summary>
    internal void Return(Entry entry)
    {
        T? toDispose = null;
        lock (_gate)
        {
            if (_disposed || _capacity == 0 || _byKey.ContainsKey(entry.Key))
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
