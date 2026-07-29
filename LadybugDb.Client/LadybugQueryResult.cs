using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>The result of a Cypher statement.</summary>
public sealed class LadybugQueryResult : IAsyncDisposable
{
    private readonly LbugDatabaseHandle _database;
    private readonly LbugQueryResultHandle _handle;

    internal LadybugQueryResult(LbugDatabaseHandle database, LbugQueryResultHandle handle)
    {
        _database = database;
        _handle = handle;
    }

    internal LbugQueryResultHandle Handle => _handle;

    /// <summary>
    /// <see langword="true"/> if there is at least one more row available from
    /// <see cref="ReadStringAsync"/>.
    /// </summary>
    /// <remarks>
    /// Leases the parent <see cref="LadybugDatabase"/>'s handle in addition to this result's own
    /// handle: a <see cref="LadybugQueryResult"/> only protects against its own disposal, not its
    /// ancestor database's, so without the extra lease a disposed database's freed storage would
    /// be dereferenced here - a crash, not a managed exception.
    /// </remarks>
    public unsafe bool HasNext
    {
        get
        {
            using var dbLease = _database.Acquire();
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
    /// Advances one row and reads the column at <paramref name="columnIndex"/> as a string.
    /// Returns <see langword="null"/> when there are no more rows.
    /// </summary>
    /// <remarks>
    /// Milestone 2 replaces this with full typed value marshalling; it exists now to prove the
    /// tuple and value ownership chain end to end.
    /// </remarks>
    public ValueTask<string?> ReadStringAsync(ulong columnIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadString(columnIndex));
    }

    /// <remarks>
    /// Leases the parent <see cref="LadybugDatabase"/>'s handle for the entire read, in addition
    /// to the per-call leases each native step already takes on its own handle - see
    /// <see cref="HasNext"/> for why. The lease is held across every native call this method
    /// makes (has-next check, tuple fetch, value fetch, string read) rather than re-acquired per
    /// call, since none of it can run safely once the database is gone.
    /// </remarks>
    private unsafe string? ReadString(ulong columnIndex)
    {
        using var dbLease = _database.Acquire();

        bool hasNext;
        using (var lease = _handle.Acquire())
        {
            hasNext = LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
        if (!hasNext) return null;

        using var tupleHandle = LbugFlatTupleHandle.GetNext(_handle, out var tupleState);
        if (tupleState != lbug_state.LbugSuccess)
            throw new LadybugException(WithErrorDetail("Failed to advance to the next row."));

        using var valueHandle = LbugValueHandle.GetValue(tupleHandle, columnIndex, out var valueState);
        if (valueState != lbug_state.LbugSuccess)
            throw new LadybugException(WithErrorDetail($"Failed to read column {columnIndex}."));

        sbyte* raw;
        lbug_state stringState;
        using (var lease = valueHandle.Acquire())
        {
            stringState = LbugNative.lbug_value_get_string((lbug_value*)lease.Pointer, &raw);
        }
        if (stringState != lbug_state.LbugSuccess)
            throw new LadybugException(WithErrorDetail($"Column {columnIndex} is not a string."));

        return NativeString.TakeOwnership(raw);
    }

    /// <summary>
    /// Folds the engine's own error detail (if any) into <paramref name="message"/>, the same way
    /// <see cref="LbugDatabaseHandle.Open"/> and <see cref="LbugConnectionHandle.Open"/> do.
    /// </summary>
    /// <remarks>
    /// Consumes <c>lbug_get_last_error()</c> unconditionally on every failure branch above, even
    /// when it turns out there is nothing recorded (<see cref="NativeString.TakeOwnershipOrNull"/>
    /// returns <see langword="null"/>). Leaving it unconsumed is the hazard: a message recorded by
    /// this call and never read would otherwise still be sitting there for an unrelated later
    /// call to pick up and misreport.
    /// </remarks>
    private static unsafe string WithErrorDetail(string message)
    {
        var detail = NativeString.TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
        return detail is null ? message : $"{message} {detail}";
    }
}
