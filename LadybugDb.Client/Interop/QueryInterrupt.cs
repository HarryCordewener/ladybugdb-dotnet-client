using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// Wires a <see cref="CancellationToken"/> to <c>lbug_connection_interrupt</c> for the duration of
/// one native query call.
/// </summary>
/// <remarks>
/// The engine polls its interrupt flag before each operator call and fails the query with
/// "Interrupted."; setting the flag is an atomic store, safe from the cancelling thread. It also
/// clears the flag when execution starts, after parse/bind/plan, so an interrupt that lands during
/// planning is lost - measured: a 30 ms token fired before a two-UNWIND query finished planning and
/// the query ran to completion. The callback therefore re-sends the interrupt every few
/// milliseconds until the native call returns. A query that completed before the interrupt reached
/// it returns its result: throwing would report a write that did happen as not having happened.
/// </remarks>
internal sealed class QueryInterrupt : IDisposable
{
    private readonly LbugConnectionHandle _connection;
    private CancellationTokenRegistration _registration;
    private volatile bool _done;

    private QueryInterrupt(LbugConnectionHandle connection) => _connection = connection;

    /// <summary>Throws if already cancelled; returns <see langword="null"/> (a no-op for <c>using</c>) for a token that cannot be cancelled.</summary>
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

    private unsafe void TryInterrupt()
    {
        try
        {
            using var lease = _connection.Acquire();
            LbugNative.lbug_connection_interrupt((lbug_connection*)lease.Pointer);
        }
        catch (ObjectDisposedException)
        {
            // The connection closed under the query; there is nothing left to interrupt.
        }
    }

    public void Dispose()
    {
        _done = true;
        _registration.Dispose();
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> for a failure the engine attributes to the
    /// interrupt while <paramref name="cancellationToken"/> is cancelled; <see langword="null"/> otherwise.
    /// </summary>
    internal static OperationCanceledException? AsCancellation(string? failureMessage, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && QueryFailureClassifier.IsInterrupted(failureMessage)
            ? new OperationCanceledException("The query was cancelled. " + failureMessage, cancellationToken)
            : null;
}
