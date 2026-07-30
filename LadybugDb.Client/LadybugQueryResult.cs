using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>The result of a Cypher statement, enumerable row by row.</summary>
/// <remarks>
/// <para>
/// Every native call this type makes leases the parent <see cref="LadybugDatabase"/>'s handle in
/// addition to this result's own handle: a <see cref="LadybugQueryResult"/> only protects against
/// its own disposal, not its ancestor database's, so without the extra lease a disposed database's
/// freed storage would be dereferenced here - a crash, not a managed exception. See
/// <see cref="Interop.LbugStructHandle.Acquire"/> for how the lease itself works.
/// </para>
/// <para>
/// A result obtained from <see cref="NextResultAsync"/> additionally leases <c>_root</c>: the
/// <em>original</em> result returned from a <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/> call, not
/// necessarily this result's immediate predecessor in the chain. <see cref="Interop.LbugQueryResultHandle.GetNextQueryResult"/>
/// documents the empirical finding this depends on - every result but the original comes back
/// from the native API as a <em>view</em> the original's storage owns (its own
/// <c>lbug_query_result_destroy</c> is a proven no-op), so it dies the moment the original is
/// destroyed, not merely its immediate predecessor. <c>_root</c> threads that same original
/// through every link of the chain for exactly that reason, so a native call three results deep
/// still checks the one handle whose disposal actually matters. <c>_root</c> is the
/// <see cref="Interop.LbugQueryResultHandle"/> itself, not the owning <see cref="LadybugQueryResult"/> -
/// deliberately: a <see cref="System.Runtime.InteropServices.SafeHandle"/> keeps its own storage
/// alive as long as anything holds a live reference to the handle, regardless of whether the
/// managed wrapper around it was ever disposed or even still has a reachable reference, so a
/// dropped-but-undisposed root cannot be finalized out from under a live descendant.
/// </para>
/// <para>
/// <b>Single-pass.</b> Row iteration and <see cref="NextResultAsync"/> both read the query's own
/// native cursor - there is no separate, independent position for each caller. Calling
/// <see cref="GetAsyncEnumerator"/> a second time, or calling <see cref="NextResultAsync"/> again
/// on a result after it already returned a real "next" once, does not restart or independently
/// advance anything: it re-reads whatever the shared cursor currently points to, which is
/// normally further along than where the caller left off, and can silently duplicate or skip rows
/// or statements. Both are guarded here to fail loudly (<see cref="InvalidOperationException"/>)
/// instead of doing that silently - see the remarks on each member.
/// </para>
/// </remarks>
public sealed class LadybugQueryResult : IAsyncDisposable, IAsyncEnumerable<LadybugRow>
{
    private readonly LbugDatabaseHandle _database;
    private readonly LbugQueryResultHandle _handle;

    /// <summary>
    /// The result that actually owns native storage for this whole chain - itself, for a result
    /// returned directly from <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/>; the same value passed
    /// down from the predecessor, for a result returned from <see cref="NextResultAsync"/>. See
    /// this type's remarks.
    /// </summary>
    private readonly LbugQueryResultHandle _root;

    /// <summary>
    /// Every column name in this result, read once here rather than per row: the names cannot
    /// change over the lifetime of a result, and <c>lbug_query_result_get_column_name</c> is a
    /// native call (and a <c>char*</c> that must be freed via <see cref="NativeString"/>) that
    /// there is no reason to repeat for every row <see cref="GetAsyncEnumerator"/> yields.
    /// </summary>
    private readonly string[] _columnNames;

    /// <summary><see langword="true"/> once <see cref="GetAsyncEnumerator"/> has been called - see its remarks.</summary>
    private bool _enumerated;

    /// <summary><see langword="true"/> once <see cref="NextResultAsync"/> has returned a real result - see its remarks.</summary>
    private bool _nextResultConsumed;

    /// <summary>
    /// <c>1</c> once <see cref="DisposeAsync"/> has run, so a second disposal - which is legal, and
    /// which <c>await using</c> plus an explicit <see cref="DisposeAsync"/> produces routinely - does
    /// not decrement <see cref="_liveCount"/> twice.
    /// </summary>
    private int _disposed;

    /// <summary>Backing field for <see cref="LiveCount"/>.</summary>
    private static long _liveCount;

    /// <summary>
    /// How many <see cref="LadybugQueryResult"/> instances have been constructed and not yet
    /// disposed, process-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exists for the tests, and specifically for one class of them.</b> Nothing in this client's
    /// behaviour reads it. A caller who never receives a result - which is the whole shape of
    /// <see cref="LadybugConnection.Select{T}"/>, where the result lives and dies inside a compiler-
    /// generated iterator - has no handle to assert disposal against, so without a counter here the
    /// only available evidence that the iterator released its result is an
    /// <see cref="Environment.WorkingSet"/> measurement: a whole-process metric this repository has
    /// already had to quarantine on hosted CI, and one that a
    /// <see cref="System.Runtime.InteropServices.SafeHandle"/> finalizer can silently paper over. This
    /// counter makes "the result was released" a deterministic assertion instead.
    /// </para>
    /// <para>
    /// Deliberately counts <em>explicit disposal</em> only: this type has no finalizer, so a leaked
    /// result never decrements this, which is exactly the property the leak assertion needs. Note the
    /// counter is process-wide - there is nothing to scope it to a single test - so a test reading it
    /// must run alone (<c>[NotInParallel]</c>), for the same reason <c>LeakTests</c> does.
    /// </para>
    /// </remarks>
    internal static long LiveCount => Interlocked.Read(ref _liveCount);

    /// <summary>
    /// Constructs a result directly owned by <paramref name="handle"/> (see <see cref="_root"/>),
    /// disposing <paramref name="handle"/> if construction fails - reading column names below can
    /// throw, and without this the already-adopted handle would be orphaned to the finalizer
    /// instead of being released deterministically.
    /// </summary>
    internal static LadybugQueryResult Create(LbugDatabaseHandle database, LbugQueryResultHandle handle) =>
        CreateCore(database, handle, handle);

    /// <summary>Constructs a child result - see <see cref="NextResult"/> for the call site and why <paramref name="root"/> differs from <paramref name="handle"/>.</summary>
    private static LadybugQueryResult CreateCore(LbugDatabaseHandle database, LbugQueryResultHandle handle, LbugQueryResultHandle root)
    {
        try
        {
            return new LadybugQueryResult(database, handle, root);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private unsafe LadybugQueryResult(LbugDatabaseHandle database, LbugQueryResultHandle handle, LbugQueryResultHandle root)
    {
        _database = database;
        _handle = handle;
        _root = root;
        _columnNames = ReadColumnNames();

        // Last, so a constructor that threw above (ReadColumnNames can) does not count a result that
        // was never handed out and so can never be disposed - see LiveCount.
        Interlocked.Increment(ref _liveCount);
    }

    internal LbugQueryResultHandle Handle => _handle;

    /// <summary>
    /// This result's column names, in result order - read once from the engine when the result was
    /// constructed (see <see cref="_columnNames"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live array, not a copy: this is <see langword="internal"/> precisely so it can be, and
    /// callers inside this assembly must treat it as read-only. <see cref="LadybugRow"/> is handed the
    /// same array for the same reason.
    /// </para>
    /// <para>
    /// <b>Why the result, and not just a row, exposes the column shape.</b> A projection resolved from
    /// the first <em>row</em> cannot be resolved at all for a result that returns none, so a query
    /// returning zero rows would silently "succeed" against a <c>T</c> that could never have mapped
    /// its columns. Reading the shape here lets <see cref="LadybugConnection.Select{T}"/> resolve its
    /// plan before the first row and report a mismatched <c>T</c> on an empty result too.
    /// </para>
    /// </remarks>
    internal string[] ColumnNames => _columnNames;

    /// <summary>
    /// <see langword="true"/> if there is at least one more row available from
    /// <see cref="GetAsyncEnumerator"/>.
    /// </summary>
    public unsafe bool HasNext
    {
        get
        {
            using var dbLease = _database.Acquire();
            using var rootLease = _root.Acquire();
            using var lease = _handle.Acquire();
            return LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
    }

    /// <summary>Closes the result. Safe to call even if the parent database was disposed first, and idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        // Once, however many times this is called - see LiveCount and _disposed.
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _liveCount);

        _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Enumerates every row of this result, advancing one row per <c>MoveNextAsync</c> call.
    /// </summary>
    /// <param name="cancellationToken">
    /// Checked between rows (not within one - a single row's native reads always run to
    /// completion once started). Honoured whether passed here directly or via <c>await foreach
    /// (... in result.WithCancellation(token))</c>, which routes into this same parameter.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// This is the second call to <see cref="GetAsyncEnumerator"/> on this result. There is only
    /// one native cursor behind a result, not one per enumerator: a second, independent
    /// enumeration is not possible, and returning a fresh <see cref="IAsyncEnumerator{T}"/> that
    /// silently shares state with the first (as the .NET 10 in-box <c>System.Linq.AsyncEnumerable</c>
    /// operators - <c>CountAsync</c> followed by <c>ToListAsync</c>, for example - would naturally
    /// trigger by calling this twice) would make the second enumeration silently yield zero or
    /// partial rows instead of the full result, with no exception anywhere. Loudly refusing the
    /// second call is safer than that: if you need the rows more than once, materialize them
    /// yourself (e.g. <c>await result.ToListAsync()</c>) and reuse the list.
    /// </exception>
    public IAsyncEnumerator<LadybugRow> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (_enumerated)
            throw new InvalidOperationException(
                "This LadybugQueryResult has already been enumerated once. It is single-pass: " +
                "there is one native cursor behind the whole result, not one per enumerator, so a " +
                "second enumeration cannot see the rows the first one already consumed. Materialize " +
                "the rows once (e.g. into a List<LadybugRow>) if you need to use them more than once.");
        _enumerated = true;
        return new Enumerator(this, cancellationToken);
    }

    /// <summary>
    /// Advances to the next statement's result, for a script that ran more than one Cypher
    /// statement in a single <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/> call. Returns
    /// <see langword="null"/> when this was the last statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Verified empirically against the real engine</b> (a three-statement script, walked
    /// through every combination of receiver) rather than assumed from the C header, which
    /// documents no ownership or repeat-call contract for <c>lbug_query_result_get_next_query_result</c>
    /// at all. There is exactly one cursor behind the whole chain, not one per result: calling this
    /// again on an EARLIER result, after a LATER one already produced a further result, does not
    /// raise an error or return the same thing again - it silently returns whatever the later call
    /// already produced (a duplicate of a result already in hand), and can leave the earlier
    /// receiver reporting no further results even while a legitimate later statement in the script
    /// has never been read through any receiver. <c>has_next_query_result</c> tracks the same
    /// shared position, so it is not a reliable "have I already consumed this one" check per
    /// receiver either.
    /// </para>
    /// <para>
    /// The only pattern the native API supports safely is walking strictly forward from whichever
    /// result was most recently returned: <c>current = await current.NextResultAsync();</c> in a
    /// loop. This method enforces exactly that shape: once a call here has returned a real (non-
    /// <see langword="null"/>) result, every subsequent call on THIS SAME <see cref="LadybugQueryResult"/>
    /// throws <see cref="InvalidOperationException"/> instead of silently returning a stale
    /// duplicate - continue the chain from the newly returned result, not by calling this again on
    /// an already-advanced one.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// This method already returned a non-<see langword="null"/> result once on this same instance
    /// - see the remarks above.
    /// </exception>
    public ValueTask<LadybugQueryResult?> NextResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NextResult());
    }

    private unsafe LadybugQueryResult? NextResult()
    {
        if (_nextResultConsumed)
            throw new InvalidOperationException(
                "NextResultAsync() already returned a result on this instance once. LadybugDB " +
                "shares one cursor across the whole chained script, not one per result, so calling " +
                "this again here would not independently advance - it would silently return " +
                "whatever the shared cursor currently points to (typically a duplicate of a result " +
                "already returned elsewhere), and can leave later statements unread. Continue " +
                "walking the chain from the result THIS call already returned instead: " +
                "`current = await current.NextResultAsync();`.");

        using var dbLease = _database.Acquire();
        using var rootLease = _root.Acquire();

        bool hasNext;
        using (var lease = _handle.Acquire())
        {
            hasNext = LbugNative.lbug_query_result_has_next_query_result((lbug_query_result*)lease.Pointer) != 0;
        }
        if (!hasNext) return null;

        var nextHandle = LbugQueryResultHandle.GetNextQueryResult(_handle, out var state);
        if (state != lbug_state.LbugSuccess)
        {
            nextHandle.Dispose();
            throw new LadybugException(NativeString.WithErrorDetail("Failed to read the next query result."));
        }

        _nextResultConsumed = true;

        // _root, not _handle: the new result is a child of the ORIGINAL owning result, not of
        // this one - see this type's remarks and Interop.LbugQueryResultHandle.GetNextQueryResult.
        return CreateCore(_database, nextHandle, _root);
    }

    /// <remarks>
    /// Leases <see cref="_database"/> and <see cref="_root"/> for the entire read, in addition to
    /// the per-call lease each native step already takes on its own handle - see
    /// <see cref="HasNext"/> for why. The lease is held across every native call this method makes
    /// (has-next check, tuple fetch, one value fetch per column) rather than re-acquired per call,
    /// since none of it can run safely once the database or the owning result is gone.
    /// </remarks>
    private unsafe LadybugRow? ReadRow()
    {
        using var dbLease = _database.Acquire();
        using var rootLease = _root.Acquire();

        bool hasNext;
        using (var lease = _handle.Acquire())
        {
            hasNext = LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
        if (!hasNext) return null;

        using var tupleHandle = LbugFlatTupleHandle.GetNext(_handle, out var tupleState);
        if (tupleState != lbug_state.LbugSuccess)
            throw new LadybugException(NativeString.WithErrorDetail("Failed to advance to the next row."));

        var columnCount = _columnNames.Length;
        var values = new LadybugValue[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            using var valueHandle = LbugValueHandle.GetValue(tupleHandle, (ulong)i, out var valueState);
            if (valueState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read column {i}."));

            using var lease = valueHandle.Acquire();
            values[i] = ValueReader.Read((lbug_value*)lease.Pointer);
        }

        return new LadybugRow(values, _columnNames);
    }

    /// <remarks>
    /// Runs once, from the constructor, under the same database+root+own-handle lease shape as
    /// every other native call this type makes - see <see cref="HasNext"/>.
    /// </remarks>
    private unsafe string[] ReadColumnNames()
    {
        using var dbLease = _database.Acquire();
        using var rootLease = _root.Acquire();
        using var lease = _handle.Acquire();

        var result = (lbug_query_result*)lease.Pointer;
        var count = LbugNative.lbug_query_result_get_num_columns(result);
        var names = new string[count];
        for (ulong i = 0; i < count; i++)
        {
            sbyte* namePtr;
            var state = LbugNative.lbug_query_result_get_column_name(result, i, &namePtr);
            if (state != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read the name of column {i}."));
            names[i] = NativeString.TakeOwnership(namePtr);
        }
        return names;
    }

    /// <summary>
    /// Does not own any native handle of its own - every <c>MoveNextAsync</c> borrows the
    /// enumerated <see cref="LadybugQueryResult"/>'s handles for exactly the duration of one
    /// native call, the same lease-per-call shape <see cref="ReadRow"/> already used before
    /// enumeration existed.
    /// <see cref="DisposeAsync"/> is therefore a no-op: disposing the enumerator (which
    /// <c>await foreach</c> does automatically) must never dispose the result it enumerates,
    /// since the caller's own <c>await using</c> on the result owns that.
    /// </summary>
    private sealed class Enumerator(LadybugQueryResult result, CancellationToken cancellationToken) : IAsyncEnumerator<LadybugRow>
    {
        /// <summary>Not yet positioned on a row: before the first <see cref="MoveNextAsync"/>, or after the last one returned <see langword="false"/>.</summary>
        private LadybugRow? _current;

        /// <summary>
        /// The current row. Throws <see cref="InvalidOperationException"/> - never
        /// <see cref="NullReferenceException"/> - before the first successful
        /// <see cref="MoveNextAsync"/> or after one has returned <see langword="false"/>, matching
        /// the shape every other BCL enumerator uses for the same "not currently positioned on an
        /// element" case (<see cref="LadybugRow"/> is a struct, so an unguarded default instance
        /// would silently expose null-backed arrays instead of failing clearly).
        /// </summary>
        public LadybugRow Current => _current ??
            throw new InvalidOperationException(
                "Current was read before a successful MoveNextAsync() (or after one returned " +
                "false). Call MoveNextAsync() first and check its result.");

        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = result.ReadRow();
            _current = row;
            return ValueTask.FromResult(row is not null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
