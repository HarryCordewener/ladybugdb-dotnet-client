using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// Turns a <see cref="CancellationToken"/> into <c>lbug_connection_interrupt</c> for the duration
/// of one native query call.
/// </summary>
/// <remarks>
/// <para>
/// The engine checks its interrupt flag before every operator invocation
/// (<c>PhysicalOperator::getNextTuple</c>) and fails the query with "Interrupted."; interrupting is
/// an atomic store, safe from any thread, which is what lets the token's callback run on whichever
/// thread cancels while the calling thread is still inside the native call.
/// </para>
/// <para>
/// <b>Why the callback keeps re-sending the interrupt until the call returns.</b> The engine clears
/// the flag when a query <em>starts executing</em> (<c>ActiveQuery::reset</c>, after parsing,
/// binding and planning). An interrupt that lands in that window - measured: a two-<c>UNWIND</c>
/// query planned for longer than a 30 ms token - is wiped before execution ever looks at it, and
/// the query runs to completion. So on cancellation the scope sends the interrupt immediately and
/// then again every few milliseconds from the thread pool until the native call has returned; the
/// repeats cost one atomic store each and stop the moment the scope is disposed. The same clearing
/// is what guarantees a token cancelled after a query finished cannot poison the next one.
/// </para>
/// <para>
/// A query the engine reports as interrupted surfaces as <see cref="OperationCanceledException"/>;
/// a query that completed before the interrupt reached it returns its result. Throwing on a
/// completed query would tell a caller their <c>CREATE</c> did not happen when it did.
/// </para>
/// </remarks>
internal sealed class QueryInterrupt : IDisposable
{
    private readonly LbugConnectionHandle _connection;
    private CancellationTokenRegistration _registration;
    private volatile bool _done;

    private QueryInterrupt(LbugConnectionHandle connection) => _connection = connection;

    /// <summary>
    /// Registers the interrupt on <paramref name="cancellationToken"/> if it can be cancelled at
    /// all; <see langword="null"/> otherwise, so the common uncancellable call pays one branch and
    /// no allocation (a <c>using</c> on <see langword="null"/> is a no-op).
    /// </summary>
    internal static QueryInterrupt? Register(LbugConnectionHandle connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) return null;

        var scope = new QueryInterrupt(connection);
        scope._registration = cancellationToken.UnsafeRegister(static state => ((QueryInterrupt)state!).OnCancelled(), scope);
        return scope;
    }

    private void OnCancelled()
    {
        TryInterrupt();
        if (!_done) _ = RepeatUntilDoneAsync();
    }

    private async Task RepeatUntilDoneAsync()
    {
        while (!_done)
        {
            await Task.Delay(2).ConfigureAwait(false);
            if (!_done) TryInterrupt();
        }
    }

    /// <summary>
    /// Sends the interrupt. Never throws: a callback runs on the cancelling thread, and a
    /// connection disposed between registration and cancellation has nothing left to interrupt.
    /// </summary>
    private unsafe void TryInterrupt()
    {
        try
        {
            using var lease = _connection.Acquire();
            LbugNative.lbug_connection_interrupt((lbug_connection*)lease.Pointer);
        }
        catch (ObjectDisposedException)
        {
            // Closed for new work; the query it would have interrupted is gone with it.
        }
    }

    /// <summary>The native call has returned: stop re-sending and drop the registration.</summary>
    public void Dispose()
    {
        _done = true;
        _registration.Dispose();
    }

    /// <summary>
    /// The exception a failed query turns into when <paramref name="cancellationToken"/> was
    /// cancelled and the engine reports the interruption: an <see cref="OperationCanceledException"/>
    /// carrying the token, which is what every awaiting caller expects from a cancelled operation.
    /// <see langword="null"/> when the failure is unrelated to cancellation.
    /// </summary>
    internal static OperationCanceledException? AsCancellation(string? failureMessage, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && QueryFailureClassifier.IsInterrupted(failureMessage)
            ? new OperationCanceledException(
                "The query was cancelled: the engine reported it interrupted. " + failureMessage, cancellationToken)
            : null;
}
