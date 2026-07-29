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

    /// <summary>
    /// The transaction currently open on this connection, if any. Non-null exactly when a
    /// <see cref="LadybugTransaction"/> has been begun and not yet committed, rolled back, or
    /// disposed - see <see cref="BeginTransactionAsync"/> and <see cref="OnTransactionCompleted"/>.
    /// </summary>
    private LadybugTransaction? _activeTransaction;

    internal LadybugConnection(LadybugDatabase database, LbugConnectionHandle handle)
    {
        _database = database;
        _handle = handle;
    }

    /// <summary>This connection's underlying handle, mirroring <see cref="LadybugDatabase.Handle"/>.</summary>
    internal LbugConnectionHandle Handle => _handle;

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
    /// <exception cref="InvalidOperationException">
    /// This connection already has an active (not yet committed, rolled back, or disposed)
    /// transaction. Checked and rejected here, client-side, before any <c>BEGIN TRANSACTION</c>
    /// reaches the engine - the engine also detects a nested <c>BEGIN TRANSACTION</c> and raises
    /// its own <see cref="LadybugException"/> for it, but doing so leaves the FIRST transaction
    /// invalid on the engine side (a subsequent <c>COMMIT</c> on it fails with "No active
    /// transaction for COMMIT" instead of the clean, documented result), not just the second
    /// call. Never sending the nested <c>BEGIN TRANSACTION</c> in the first place is the only way
    /// to keep the original transaction usable.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The parent <see cref="LadybugDatabase"/> is already fully closed.
    /// </exception>
    public async ValueTask<LadybugTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_activeTransaction is not null)
            throw new InvalidOperationException(
                "This connection already has an active transaction. Commit or roll it back (or " +
                "dispose it) before beginning another - sending a nested BEGIN TRANSACTION to " +
                "the engine would invalidate the first transaction rather than merely rejecting " +
                "the second call.");

        // Reserve a long-lived hold on the database BEFORE issuing BEGIN TRANSACTION to the
        // engine, not after it succeeds - see LbugConnectionHandle's remarks. The engine
        // considers a transaction open the instant its BEGIN TRANSACTION call returns success,
        // which is before ANY of this method's own bookkeeping below would otherwise run
        // (including LadybugTransaction.BeginAsync's own disposal of that call's result). A
        // concurrent LadybugDatabase.Dispose() racing this call must never be able to complete
        // inside that window with nothing holding the database open - taking the hold first is
        // what makes that impossible rather than merely unlikely.
        if (!_handle.TryAcquireDatabaseHoldForTransaction())
            throw new ObjectDisposedException(nameof(LadybugDatabase));

        LadybugTransaction transaction;
        try
        {
            transaction = await LadybugTransaction.BeginAsync(this, cancellationToken);
        }
        catch
        {
            // BEGIN TRANSACTION did not open anything at the engine level (or cancellation means
            // we can no longer be sure it will) - release the hold immediately rather than
            // leaving it for a completion that will never come, which would otherwise wedge a
            // concurrent LadybugDatabase.Dispose() forever.
            _handle.ReleaseDatabaseHoldForTransaction();
            throw;
        }

        _activeTransaction = transaction;
        _database.TrackTransactionOpened(this);
        return transaction;
    }

    /// <summary>
    /// Called by <see cref="LadybugTransaction"/> once it has committed, rolled back, or
    /// otherwise closed itself out, so this connection stops considering it active. Not for
    /// direct use.
    /// </summary>
    internal void OnTransactionCompleted(LadybugTransaction transaction)
    {
        if (!ReferenceEquals(_activeTransaction, transaction)) return;
        _activeTransaction = null;
        _database.TrackTransactionClosed(this);
        _handle.ReleaseDatabaseHoldForTransaction();
    }

    /// <summary>
    /// Closes out this connection's active transaction (if any) synchronously, swallowing any
    /// failure, so that this connection's own <see cref="DisposeAsync"/> - or its parent
    /// <see cref="LadybugDatabase"/>'s <see cref="LadybugDatabase.Dispose"/> - never lets a still
    /// -open transaction reach native connection destruction. See
    /// <see cref="LadybugTransaction.EnsureClosedForDispose"/> for why that native call is unsafe
    /// otherwise. Not for direct use.
    /// </summary>
    internal void EnsureNoOpenTransactionForDispose() => _activeTransaction?.EnsureClosedForDispose();

    /// <summary>Closes the connection. Safe to call even if the parent database was disposed first.</summary>
    public ValueTask DisposeAsync()
    {
        EnsureNoOpenTransactionForDispose();
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
