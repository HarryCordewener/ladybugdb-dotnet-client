using System.Runtime.InteropServices;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// A connection to a <see cref="LadybugDatabase"/>.
/// Methods are async-shaped but currently complete synchronously: the engine is embedded and
/// the work is CPU and local-disk bound, so offloading would add cost without benefit. The
/// signatures are async so genuine offloading can be added later without an API break.
/// </summary>
public sealed class LadybugConnection : IAsyncDisposable
{
    private readonly LadybugDatabase _database;
    private readonly LbugConnectionHandle _handle;

    internal LadybugConnection(LadybugDatabase database, LbugConnectionHandle handle)
    {
        _database = database;
        _handle = handle;
    }

    /// <summary>
    /// Executes a Cypher statement and returns its result.
    /// </summary>
    /// <remarks>
    /// No <c>IsClosed</c> pre-check here: <see cref="Execute"/> leases both this connection's
    /// handle and its parent <see cref="LadybugDatabase"/>'s handle internally (via
    /// <see cref="LbugQueryResultHandle.Execute"/>), and those leases already throw
    /// <see cref="ObjectDisposedException"/> if the connection - or the database it belongs to -
    /// has been disposed. A separate check-then-call here would just reintroduce the TOCTOU
    /// window leases exist to close.
    /// </remarks>
    public ValueTask<LadybugQueryResult> QueryAsync(string cypher, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Execute(cypher));
    }

    /// <summary>
    /// Prepares a parameterized Cypher statement for repeated execution with different bound
    /// values, avoiding re-planning the same query on every call.
    /// </summary>
    /// <remarks>
    /// No <c>IsClosed</c> pre-check here, for the same reason as <see cref="QueryAsync"/>:
    /// <see cref="LadybugPreparedStatement.Prepare"/> leases both this connection's handle and its
    /// parent <see cref="LadybugDatabase"/>'s handle internally.
    /// </remarks>
    public ValueTask<LadybugPreparedStatement> PrepareAsync(string cypher, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(LadybugPreparedStatement.Prepare(_database.Handle, _handle, cypher));
    }

    private unsafe LadybugQueryResult Execute(string cypher)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(cypher);
        try
        {
            var handle = LbugQueryResultHandle.Execute(_database.Handle, _handle, (sbyte*)utf8, out var state);

            // Non-null only on failure: NativeString.TakeOwnership never returns null (it maps a
            // null native pointer to string.Empty), so this doubles as the success/failure flag
            // without a separate bool - an empty message is still a genuine failure signal.
            string? failureMessage = null;
            using (var lease = handle.Acquire())
            {
                var result = (lbug_query_result*)lease.Pointer;
                var success = state == lbug_state.LbugSuccess
                    && LbugNative.lbug_query_result_is_success(result) != 0;
                if (!success)
                    failureMessage = NativeString.TakeOwnership(
                        LbugNative.lbug_query_result_get_error_message(result));
            }

            if (failureMessage is not null)
            {
                handle.Dispose();
                throw QueryFailureClassifier.Classify(failureMessage, cypher);
            }

            return LadybugQueryResult.Create(_database.Handle, handle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>
    /// Begins a transaction on this connection by issuing <c>BEGIN TRANSACTION</c>. See
    /// <see cref="LadybugTransaction"/> for the full lifecycle contract - commit or roll back
    /// exactly once, or let disposal roll back automatically.
    /// </summary>
    /// <param name="cancellationToken">Forwarded to the underlying <c>BEGIN TRANSACTION</c> query.</param>
    public ValueTask<LadybugTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        LadybugTransaction.BeginAsync(this, cancellationToken);

    /// <summary>Closes the connection. Safe to call even if the parent database was disposed first.</summary>
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
