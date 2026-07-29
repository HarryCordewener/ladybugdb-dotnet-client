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
/// <em>original</em> result returned from a <see cref="LadybugConnection.QueryAsync"/> call, not
/// necessarily this result's immediate predecessor in the chain. <see cref="Interop.LbugQueryResultHandle.GetNextQueryResult"/>
/// documents the empirical finding this depends on - every result but the original comes back
/// from the native API as a <em>view</em> the original's storage owns (its own
/// <c>lbug_query_result_destroy</c> is a proven no-op), so it dies the moment the original is
/// destroyed, not merely its immediate predecessor. <c>_root</c> threads that same original
/// through every link of the chain for exactly that reason, so a native call three results deep
/// still checks the one handle whose disposal actually matters.
/// </para>
/// </remarks>
public sealed class LadybugQueryResult : IAsyncDisposable, IAsyncEnumerable<LadybugRow>
{
    private readonly LbugDatabaseHandle _database;
    private readonly LbugQueryResultHandle _handle;

    /// <summary>
    /// The result that actually owns native storage for this whole chain - itself, for a result
    /// returned directly from <see cref="LadybugConnection.QueryAsync"/>; the same value passed
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

    /// <summary>Constructs a result directly owned by this handle - see <see cref="_root"/>.</summary>
    internal LadybugQueryResult(LbugDatabaseHandle database, LbugQueryResultHandle handle)
        : this(database, handle, handle)
    {
    }

    private unsafe LadybugQueryResult(LbugDatabaseHandle database, LbugQueryResultHandle handle, LbugQueryResultHandle root)
    {
        _database = database;
        _handle = handle;
        _root = root;
        _columnNames = ReadColumnNames();
    }

    internal LbugQueryResultHandle Handle => _handle;

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

    /// <summary>Closes the result. Safe to call even if the parent database was disposed first.</summary>
    public ValueTask DisposeAsync()
    {
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
    public IAsyncEnumerator<LadybugRow> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(this, cancellationToken);

    /// <summary>
    /// Advances to the next statement's result, for a script that ran more than one Cypher
    /// statement in a single <see cref="LadybugConnection.QueryAsync"/> call. Returns
    /// <see langword="null"/> when this was the last statement.
    /// </summary>
    /// <remarks>
    /// Single-shot: verified empirically alongside the ownership finding in
    /// <see cref="Interop.LbugQueryResultHandle.GetNextQueryResult"/>, calling this a second time
    /// without an intervening statement never returns the same result again - the native
    /// <c>lbug_query_result_has_next_query_result</c> reports <see langword="false"/> once this
    /// result has been retrieved once, exactly like advancing a row via <see cref="GetAsyncEnumerator"/>.
    /// </remarks>
    public ValueTask<LadybugQueryResult?> NextResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NextResult());
    }

    private unsafe LadybugQueryResult? NextResult()
    {
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

        // _root, not _handle: the new result is a child of the ORIGINAL owning result, not of
        // this one - see this type's remarks and Interop.LbugQueryResultHandle.GetNextQueryResult.
        return new LadybugQueryResult(_database, nextHandle, _root);
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
        private LadybugRow _current;

        public LadybugRow Current => _current;

        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = result.ReadRow();
            if (row is null) return ValueTask.FromResult(false);
            _current = row.Value;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
