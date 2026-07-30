using System.Diagnostics.CodeAnalysis;
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
/// <see cref="BeginTransactionAsync"/>. <see cref="QueryAsync(string, CancellationToken)"/> and <see cref="PrepareAsync"/>
/// were already safe this way - concurrent native re-entry on one connection is the underlying
/// <c>lbug_connection</c>'s own guarantee ("Each connection is thread-safe", per
/// <c>third-party/lbug.h</c>'s <c>lbug_connection</c> doc comment), not something this client
/// adds; the per-call handle leases each of them also takes protect against a concurrent
/// <em>disposal</em> racing the call (throwing <see cref="ObjectDisposedException"/> instead of
/// touching freed memory), a separate concern from concurrent re-entry.
/// <see cref="BeginTransactionAsync"/> is additionally serialized against
/// itself and against transaction completion/disposal by <see cref="_transactionGate"/>, so
/// racing it against another <see cref="BeginTransactionAsync"/> call on the same connection
/// deterministically produces exactly one successful transaction and an
/// <see cref="InvalidOperationException"/> for every other caller - never two open transactions,
/// and never the engine-level invalidation <see cref="_transactionGate"/>'s remarks describe.
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
    /// proceed to issue <c>BEGIN TRANSACTION</c> - the documented client-side nested-transaction
    /// guard below existed but essentially never fired under the race (measured: 0-1 rejections
    /// out of 31 racing attempts), so the nested <c>BEGIN TRANSACTION</c> reached the engine
    /// almost every time despite the guard, invalidating the first transaction at the engine
    /// level (see the <see cref="InvalidOperationException"/> thrown below). A
    /// <see cref="SemaphoreSlim"/> rather than a <c>lock</c>/<see cref="Lock"/> specifically
    /// because the critical section spans an <see langword="await"/> (the <c>BEGIN TRANSACTION</c>
    /// query itself) - this type documents its async methods as completing synchronously today,
    /// but a synchronization primitive whose correctness depends on that staying true forever is
    /// exactly the kind of thing that breaks silently later, so this uses the primitive that is
    /// correct regardless. See <b>Thread-safety</b> in this type's own remarks for the contract
    /// this establishes.
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
    /// Executes a parameterized Cypher statement once - preparing it, binding
    /// <paramref name="parameters"/>, executing, and disposing the statement - and returns its
    /// result.
    /// </summary>
    /// <param name="cypher">The Cypher statement, whose <c>$name</c> placeholders name the parameters.</param>
    /// <param name="parameters">
    /// A dictionary keyed by parameter name, or an object - typically an anonymous one, such as
    /// <c>new { dbref = 42L, name = "Limbo" }</c> - whose public properties name the parameters. Each
    /// value dispatches on its runtime type to the matching typed
    /// <see cref="LadybugPreparedStatement"/> <c>Bind</c> overload.
    /// </param>
    /// <param name="cancellationToken">Checked before the statement is prepared.</param>
    /// <returns>The statement's result.</returns>
    /// <remarks>
    /// <para>
    /// For a statement run <em>once</em>, which is what this overload is for. To run the same
    /// statement repeatedly with different values, <see cref="PrepareAsync"/> it and call
    /// <see cref="LadybugPreparedStatement.ExecuteAsync(object, CancellationToken)"/> per execution,
    /// so the engine plans the query once instead of on every call.
    /// </para>
    /// <para>
    /// <b>The returned result deliberately outlives the statement this method disposes.</b> That is
    /// safe, and not by accident: a <see cref="LadybugQueryResult"/> leases its parent
    /// <see cref="LadybugDatabase"/> and its own handle chain (see
    /// <see cref="LbugQueryResultHandle.ExecutePrepared"/>), and never the prepared statement that
    /// produced it - covered directly by the integration suite's
    /// <c>DisposingStatementBeforeConsumingResult_ResultRemainsUsable</c>, and by
    /// <c>OneShotQueryAsync_ResultOutlivesTheInternalStatement</c> for this path specifically,
    /// including the DML case whose result outliving its <em>database</em> is a known
    /// process-crashing shape in this client. What the result does keep alive is the database, so
    /// the ordinary rule still holds: dispose the result before the database.
    /// </para>
    /// <para>
    /// Values bind at their natural width and the engine coerces them to the target column - see
    /// <see cref="LadybugPreparedStatement.ExecuteAsync(object, CancellationToken)"/>'s remarks for
    /// the measured behaviour.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="cypher"/> is <see langword="null"/>, empty, or whitespace; or
    /// <paramref name="parameters"/> is not a usable parameter bag, or names a value whose runtime
    /// type has no <c>Bind</c> overload.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    [RequiresUnreferencedCode(
        "Reads the parameters object's public properties by reflection. Use a dictionary, or " +
        "PrepareAsync with the typed Bind overloads, when trimming.")]
    public async ValueTask<LadybugQueryResult> QueryAsync(
        string cypher, object parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();

        var statement = LadybugPreparedStatement.Prepare(_database.Handle, _handle, cypher);
        try
        {
            return await statement.ExecuteAsync(parameters, cancellationToken);
        }
        finally
        {
            // Runs on both paths: after the result exists (which does not depend on the statement
            // staying alive - see this method's remarks) and if binding or execution threw.
            await statement.DisposeAsync();
        }
    }

    /// <summary>
    /// Prepares a parameterized Cypher statement for repeated execution with different bound
    /// values, avoiding re-planning the same query on every call.
    /// </summary>
    /// <remarks>
    /// No <c>IsClosed</c> pre-check here, for the same reason as <see cref="QueryAsync(string, CancellationToken)"/>:
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
    /// <remarks>
    /// No separate database hold to take here: this connection's own handle has held a long-lived
    /// reference on its owning database since <see cref="LadybugDatabase.ConnectAsync"/> opened it - see
    /// <see cref="Interop.LbugConnectionHandle"/>'s remarks - which already guarantees the database
    /// cannot be destroyed out from under a transaction opened here, or racing this call itself,
    /// regardless of when <c>BEGIN TRANSACTION</c> is considered to have taken effect at the engine
    /// level. What used to require a transaction-specific hold, taken and released around exactly
    /// this method, is now just a consequence of this connection existing at all.
    /// </remarks>
    public async ValueTask<LadybugTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        // Serializes this whole check-then-begin sequence against every other concurrent
        // BeginTransactionAsync call (and against OnTransactionCompleted/
        // EnsureNoOpenTransactionForDispose) on this same connection - see _transactionGate's
        // remarks for why an unsynchronized version of exactly this sequence used to invalidate
        // the winner's transaction at the engine level under a race.
        await _transactionGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeTransaction is not null)
                throw new InvalidOperationException(
                    "This connection already has an active transaction. Commit or roll it back (or " +
                    "dispose it) before beginning another - sending a nested BEGIN TRANSACTION to " +
                    "the engine would invalidate the first transaction rather than merely rejecting " +
                    "the second call.");

            var transaction = await LadybugTransaction.BeginAsync(this, cancellationToken);

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
