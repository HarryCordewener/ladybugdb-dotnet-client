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
/// <remarks>
/// <b>Thread-safety.</b> Every public member of this type is safe to call concurrently, from
/// multiple threads, on the same instance - including two overlapping calls to
/// <see cref="BeginTransactionAsync"/>. <see cref="QueryAsync"/> and <see cref="PrepareAsync"/>
/// were already safe this way (each leases the underlying native handles only for the duration
/// of its own call). <see cref="BeginTransactionAsync"/> is additionally serialized against
/// itself and against transaction completion/disposal by <see cref="_transactionGate"/>, so
/// racing it against another <see cref="BeginTransactionAsync"/> call on the same connection
/// deterministically produces exactly one successful transaction and an
/// <see cref="InvalidOperationException"/> for every other caller - never two open transactions,
/// and never the leaked-database failure mode <see cref="_transactionGate"/>'s remarks describe.
/// This says nothing about the ENGINE-level semantics of what those concurrent operations DO -
/// see <see cref="LadybugDatabase"/>'s remarks for the single-writer/<see cref="LadybugConfig.EnableMultiWrites"/>
/// contract that governs whether two transactions can commit concurrently without conflict -
/// only that the C# API surface itself never corrupts its own bookkeeping or crashes the process
/// under concurrent use.
/// </remarks>
public sealed class LadybugConnection : IAsyncDisposable
{
    private readonly LadybugDatabase _database;
    private readonly LbugConnectionHandle _handle;

    /// <summary>
    /// The transaction currently open on this connection, if any. Non-null exactly when a
    /// <see cref="LadybugTransaction"/> has been begun and not yet committed, rolled back, or
    /// disposed - see <see cref="BeginTransactionAsync"/> and <see cref="OnTransactionCompleted"/>.
    /// Every read and write of this field goes through <see cref="_transactionGate"/> - see that
    /// field's remarks for why.
    /// </summary>
    private LadybugTransaction? _activeTransaction;

    /// <summary>
    /// Serializes <see cref="BeginTransactionAsync"/> against itself and against
    /// <see cref="OnTransactionCompleted"/>/<see cref="EnsureNoOpenTransactionForDispose"/> on
    /// this connection, so exactly one caller can ever be checking-and-opening a transaction at a
    /// time. Without this, two threads calling <see cref="BeginTransactionAsync"/> on the SAME
    /// connection concurrently could both observe <see cref="_activeTransaction"/> as
    /// <see langword="null"/> before either set it (a classic check-then-act race), and both
    /// proceed to call <see cref="Interop.LbugConnectionHandle.TryAcquireDatabaseHoldForTransaction"/>
    /// and issue <c>BEGIN TRANSACTION</c> - the documented client-side nested-transaction guard
    /// below existed but essentially never fired under the race (measured: 0-1 rejections out of
    /// 31 racing attempts), so the nested <c>BEGIN TRANSACTION</c> reached the engine almost every
    /// time despite the guard. Combined with the (then-)boolean hold in
    /// <see cref="Interop.LbugConnectionHandle"/>, the loser's failure-path release could cancel
    /// the winner's still-active hold outright, leaking the whole underlying <c>lbug_database</c>
    /// (its ~8 TiB virtual-address reservation never unmapped) on roughly 40% of racing attempts -
    /// measured driving VmSize from a flat 8 TiB to 112 TiB over 30 iterations and killing the
    /// process with a failed <c>mmap</c> soon after. A <see cref="SemaphoreSlim"/> rather than a
    /// <c>lock</c>/<see cref="Lock"/> specifically because the critical section spans an
    /// <see langword="await"/> (the <c>BEGIN TRANSACTION</c> query itself) - this type documents
    /// its async methods as completing synchronously today, but a synchronization primitive whose
    /// correctness depends on that staying true forever is exactly the kind of thing that breaks
    /// silently later, so this uses the primitive that is correct regardless. See
    /// <b>Thread-safety</b> in this type's own remarks for the contract this establishes.
    /// </summary>
    private readonly SemaphoreSlim _transactionGate = new(1, 1);

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
        // Serializes this whole check-acquire-call-record sequence against every other
        // concurrent BeginTransactionAsync call (and against OnTransactionCompleted/
        // EnsureNoOpenTransactionForDispose) on this same connection - see _transactionGate's
        // remarks for why an unsynchronized version of exactly this sequence used to leak the
        // entire underlying database under a race.
        await _transactionGate.WaitAsync(cancellationToken);
        try
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
                // BEGIN TRANSACTION did not open anything at the engine level (or cancellation
                // means we can no longer be sure it will) - release the hold immediately rather
                // than leaving it for a completion that will never come, which would otherwise
                // wedge a concurrent LadybugDatabase.Dispose() forever.
                _handle.ReleaseDatabaseHoldForTransaction();
                throw;
            }

            _activeTransaction = transaction;
            _database.TrackTransactionOpened(this);
            return transaction;
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    /// <summary>
    /// Called by <see cref="LadybugTransaction"/> once it has committed, rolled back, or
    /// otherwise closed itself out, so this connection stops considering it active. Not for
    /// direct use.
    /// </summary>
    internal void OnTransactionCompleted(LadybugTransaction transaction)
    {
        // Synchronous Wait/Release, not WaitAsync: this method is called from synchronous
        // contexts too (LadybugTransaction.EnsureClosedForDispose, itself called from
        // LadybugDatabase.Dispose's non-async path), and the section guarded is tiny and never
        // itself awaits - see _transactionGate's remarks for why every access to
        // _activeTransaction goes through this same gate as BeginTransactionAsync.
        _transactionGate.Wait();
        try
        {
            if (!ReferenceEquals(_activeTransaction, transaction)) return;
            _activeTransaction = null;
        }
        finally
        {
            _transactionGate.Release();
        }

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
    internal void EnsureNoOpenTransactionForDispose()
    {
        // Read _activeTransaction under the same gate BeginTransactionAsync/OnTransactionCompleted
        // use, then release before calling out to EnsureClosedForDispose - that call ends up back
        // in OnTransactionCompleted, which takes this same (non-reentrant) gate itself, so holding
        // it across the call here would deadlock.
        LadybugTransaction? active;
        _transactionGate.Wait();
        try
        {
            active = _activeTransaction;
        }
        finally
        {
            _transactionGate.Release();
        }

        active?.EnsureClosedForDispose();
    }

    /// <summary>Closes the connection. Safe to call even if the parent database was disposed first.</summary>
    /// <remarks>
    /// Deliberately does not dispose <see cref="_transactionGate"/>: this connection's own
    /// thread-safety contract (see this type's remarks) allows <see cref="BeginTransactionAsync"/>
    /// to legitimately be in flight, waiting on that gate, concurrently with this call. A
    /// <see cref="SemaphoreSlim"/> that is never disposed leaks nothing observable - it only holds
    /// an OS wait handle once <see cref="SemaphoreSlim.AvailableWaitHandle"/> is touched, which
    /// this type never does - so leaving it be is strictly safer here than racing a
    /// <see cref="ObjectDisposedException"/> against a concurrent waiter for no real benefit.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        EnsureNoOpenTransactionForDispose();
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
