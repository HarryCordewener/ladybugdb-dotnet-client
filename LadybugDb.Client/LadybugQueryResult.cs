using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>The result of a Cypher statement.</summary>
public sealed class LadybugQueryResult : IAsyncDisposable
{
    private readonly LbugQueryResultHandle _handle;

    internal LadybugQueryResult(LbugQueryResultHandle handle) => _handle = handle;

    internal LbugQueryResultHandle Handle => _handle;

    public unsafe bool IsSuccess
    {
        get
        {
            using var lease = _handle.Acquire();
            return LbugNative.lbug_query_result_is_success((lbug_query_result*)lease.Pointer) != 0;
        }
    }

    public unsafe bool HasNext
    {
        get
        {
            using var lease = _handle.Acquire();
            return LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
    }

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

    private unsafe string? ReadString(ulong columnIndex)
    {
        bool hasNext;
        using (var lease = _handle.Acquire())
        {
            hasNext = LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
        if (!hasNext) return null;

        using var tupleHandle = LbugFlatTupleHandle.GetNext(_handle, out var tupleState);
        if (tupleState != lbug_state.LbugSuccess)
            throw new LadybugException("Failed to advance to the next row.");

        using var valueHandle = LbugValueHandle.GetValue(tupleHandle, columnIndex, out var valueState);
        if (valueState != lbug_state.LbugSuccess)
            throw new LadybugException($"Failed to read column {columnIndex}.");

        sbyte* raw;
        lbug_state stringState;
        using (var lease = valueHandle.Acquire())
        {
            stringState = LbugNative.lbug_value_get_string((lbug_value*)lease.Pointer, &raw);
        }
        if (stringState != lbug_state.LbugSuccess)
            throw new LadybugException($"Column {columnIndex} is not a string.");

        return NativeString.TakeOwnership(raw);
    }
}
