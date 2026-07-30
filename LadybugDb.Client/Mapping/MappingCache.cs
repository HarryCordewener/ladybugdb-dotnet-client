using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace LadybugDb.Client.Mapping;

/// <summary>
/// Holds one resolved <see cref="RowPlan{T}"/> per (<c>T</c>, column shape) pair, so the reflection
/// behind <see cref="RowMapper"/> runs once per query rather than once per row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caching here is a correctness-adjacent requirement, not an optimization.</b> The consumer of
/// this seam streams: a projection is resolved while rows are still being read from the engine, so
/// anything done per row is done once per row of the whole result. Resolving a constructor by
/// reflection per row would make a 100k-row result do 100k reflection passes over the same type.
/// </para>
/// <para>
/// <b>The column shape is part of the key, not just <c>T</c>.</b> Two queries can project the same
/// <c>T</c> while returning different columns - <c>RETURN o.dbref AS Dbref, o.name AS Name</c> and
/// <c>RETURN o.name AS Name, o.dbref AS Dbref</c>, or one of them with an extra ignored column - and
/// a plan resolved for one is wrong for the other, because a plan stores each column's <em>index</em>
/// (see <see cref="RowPlan{T}"/>). Keying on <c>T</c> alone would silently hand the second query the
/// first query's plan and read every column from the wrong position: a wrong value, not an
/// exception. The key is therefore the ordered column-name sequence, which subsumes the name set.
/// </para>
/// <para>
/// Key equality is <see cref="StringComparer.Ordinal"/> even though the plan matches column names to
/// constructor parameters case-<em>in</em>sensitively. Two shapes differing only in case get two
/// cache entries that resolve to equivalent plans - a duplicate entry, never a wrong one - which is
/// the safe direction to be wrong in, and it keeps key equality trivial enough to be obviously
/// correct.
/// </para>
/// <para>
/// The cache is unbounded and never evicts. It is bounded in practice by the number of distinct
/// (<c>T</c>, column shape) pairs a program's queries can produce, which is a property of the source
/// - not of the data, the row count, or any value in a row - so it cannot grow with runtime input
/// the way a value-keyed cache could.
/// </para>
/// </remarks>
internal static class MappingCache
{
    /// <summary>
    /// The stored value is always a <see cref="RowPlan{T}"/> whose <c>T</c> is the key's own
    /// <see cref="PlanKey.Type"/>, which is what makes the cast in <see cref="GetOrAdd{T}"/> safe;
    /// the dictionary is typed as <see cref="object"/> only because one static field cannot be
    /// generic over every <c>T</c>.
    /// </summary>
    private static readonly ConcurrentDictionary<PlanKey, object> Plans = new();

    private static long _plansBuilt;

    /// <summary>
    /// How many plans this cache has built - equivalently, how many times reflection has run.
    /// Exists for the tests that prove a plan is reused across rows rather than rebuilt per row;
    /// nothing in the client's behaviour depends on it.
    /// </summary>
    internal static long PlansBuilt => Interlocked.Read(ref _plansBuilt);

    /// <summary>
    /// Returns the cached plan for projecting a result with these columns into <typeparamref name="T"/>,
    /// building it on first use.
    /// </summary>
    /// <typeparam name="T">The type to project each row into.</typeparam>
    /// <param name="columnNames">The result's column names, in result order.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> cannot be mapped to
    /// these columns - see <see cref="RowMapper.BuildPlan{T}"/> for every case.</exception>
    /// <remarks>
    /// A failed build is not cached (<see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
    /// propagates the factory's exception and stores nothing), so a mapping error is reported the
    /// same way on the first call and every later one rather than only once.
    /// </remarks>
    [RequiresUnreferencedCode(
        "Resolves T's constructor and its parameter types by reflection. Read columns through " +
        "LadybugRow's typed accessors when trimming.")]
    internal static RowPlan<T> GetOrAdd<T>(IReadOnlyList<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(columnNames);

        // The factory is static and closes over nothing, so the delegate is cached per T
        // instantiation instead of allocated per call. Under contention ConcurrentDictionary may
        // run it more than once for the same key; a duplicate plan is harmless - plans are
        // immutable and value-equivalent - and PlansBuilt counts reflection passes, which is
        // exactly what it is there to measure.
        return (RowPlan<T>)Plans.GetOrAdd(new PlanKey(typeof(T), columnNames), static key => Build<T>(key));
    }

    [RequiresUnreferencedCode("Resolves T's constructor and its parameter types by reflection.")]
    private static RowPlan<T> Build<T>(PlanKey key)
    {
        var plan = RowMapper.BuildPlan<T>(key.ColumnNames);

        // Counted after the build, so a build that threw does not report a plan that does not
        // exist - the counter is the tests' evidence that reflection ran once, and a phantom
        // increment would weaken exactly that.
        Interlocked.Increment(ref _plansBuilt);
        return plan;
    }

    /// <summary>A type plus the ordered column names a plan for it was resolved against.</summary>
    private sealed class PlanKey : IEquatable<PlanKey>
    {
        private readonly int _hash;

        internal PlanKey(Type type, IReadOnlyList<string> columnNames)
        {
            Type = type;

            // Copied, not aliased: a key lives in the dictionary for the life of the process, and a
            // caller-owned list that changed afterwards would make the entry unfindable while
            // leaving it holding a plan built for the names it used to have.
            var names = new string[columnNames.Count];
            for (var i = 0; i < names.Length; i++) names[i] = columnNames[i];
            ColumnNames = names;

            var hash = new HashCode();
            hash.Add(type);
            foreach (var name in names) hash.Add(name);
            _hash = hash.ToHashCode();
        }

        internal Type Type { get; }

        internal string[] ColumnNames { get; }

        public bool Equals(PlanKey? other) =>
            other is not null
            && Type == other.Type
            && ColumnNames.AsSpan().SequenceEqual(other.ColumnNames.AsSpan(), StringComparer.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as PlanKey);

        public override int GetHashCode() => _hash;
    }
}
