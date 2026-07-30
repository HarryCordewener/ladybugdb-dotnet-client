using System.Collections.Concurrent;
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Blocking on this library's methods from a thread that owns a <see cref="SynchronizationContext"/>
/// - a WPF or WinForms UI thread, or legacy ASP.NET - must not deadlock.
/// </summary>
/// <remarks>
/// <para>
/// <b>This was a live defect, reproduced, not a theoretical one.</b> A contended
/// <see cref="SemaphoreSlim"/> awaited without <c>ConfigureAwait(false)</c> posts its continuation
/// back to the captured context; if the thread owning that context is blocked in
/// <c>GetAwaiter().GetResult()</c>, the continuation can never run. Measured against the real client:
/// <b>deadlocked at 0 completed iterations</b> before the fix, 200 after it.
/// </para>
/// <para>
/// <b>Why a behavioural test rather than the CA2007 analyzer.</b> CA2007 was tried first and does not
/// fire here - <c>AnalysisMode</c>'s generated configuration overrides an <c>.editorconfig</c>
/// severity for it, and a deliberately injected <c>await Task.Delay(1)</c> produced no diagnostic. A
/// rule that cannot be made to fail is not a guard. This test can fail, and it tests the property
/// that actually matters rather than the idiom usually used to achieve it: most of this library
/// returns already-completed <see cref="ValueTask"/>s and so cannot deadlock regardless of
/// <c>ConfigureAwait</c>, which is the first line of defense; this catches the case where that stops
/// being true.
/// </para>
/// <para>
/// Each test asserts completion within a timeout, because the failure mode is a hang rather than an
/// exception - an un-timed version would take the whole suite down instead of reporting.
/// </para>
/// </remarks>
public class SynchronizationContextDeadlockTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A single-threaded <see cref="SynchronizationContext"/>: continuations are queued for one
    /// thread. Deliberately has no helper draining the queue - a blocked UI thread has none either,
    /// and an earlier version of this harness reported "no deadlock" purely because a second thread
    /// was running the continuations the blocked thread could not.
    /// </summary>
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread that owns a <see cref="SingleThreadContext"/> and
    /// blocks on it, exactly as UI code calling <c>.GetAwaiter().GetResult()</c> would.
    /// </summary>
    /// <returns><see langword="true"/> if the work finished inside <see cref="Budget"/>.</returns>
    private static bool RanToCompletionOnAContextOwningThread(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
            try { work(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };

        thread.Start();
        var finished = thread.Join(Budget);

        if (failure is not null) throw failure;
        return finished;
    }

    /// <summary>
    /// The regression test for the deadlock itself.
    /// </summary>
    /// <remarks>
    /// <b>Every database object is owned by the worker thread, and none is disposed from the test
    /// thread.</b> That is not tidiness: when this deadlock fires, the gate is never released, so
    /// disposing the connection or database blocks too - <c>EnsureNoOpenTransactionForDispose</c>
    /// takes the same gate synchronously. An earlier version of this test held them in `using`
    /// declarations here and, when calibrated against the broken build, hung the entire suite instead
    /// of failing. A test whose failure mode is "the run never finishes" reports nothing.
    /// </remarks>
    [Test]
    public async Task ContendedTransactionGate_BlockedOnFromAContextOwningThread_DoesNotDeadlock()
    {
        var path = TestDatabase.NewPath();
        var stop = 0;
        var completed = 0;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
            Thread? hammer = null;
            try
            {
                // Owned here, and deliberately never disposed from the test thread.
                var db = new LadybugDatabase(path);
                var conn = db.ConnectAsync().AsTask().GetAwaiter().GetResult();
                conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")
                    .AsTask().GetAwaiter().GetResult();

                void Cycle(string close)
                {
                    conn.ExecuteAsync("BEGIN TRANSACTION").AsTask().GetAwaiter().GetResult();
                    conn.ExecuteAsync(close).AsTask().GetAwaiter().GetResult();
                }

                // Contends the gate so the blocked thread's WaitAsync genuinely suspends. With no
                // contention there is no continuation, and nothing to deadlock on.
                hammer = new Thread(() =>
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        try { Cycle("COMMIT"); } catch { /* losing the race is expected */ }
                    }
                })
                { IsBackground = true };
                hammer.Start();

                for (var i = 0; i < 200; i++)
                {
                    try { Cycle("ROLLBACK"); } catch { /* as above */ }
                    Interlocked.Increment(ref completed);
                }

                Volatile.Write(ref stop, 1);
                hammer.Join(TimeSpan.FromSeconds(5));
                conn.DisposeAsync().AsTask().GetAwaiter().GetResult();
                db.Dispose();
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref stop, 1); }
        })
        { IsBackground = true };

        worker.Start();
        var finished = worker.Join(Budget);
        Volatile.Write(ref stop, 1);

        if (failure is not null) throw failure;

        await Assert.That(finished).IsTrue()
            .Because($"blocking on a contended transaction gate from a thread owning a " +
                     $"SynchronizationContext must not deadlock (completed {Volatile.Read(ref completed)} of 200)");

        if (finished) TestDatabase.Cleanup(path);
    }

    /// <summary>
    /// The rest of the surface, blocked on from a context-owning thread. These complete synchronously
    /// today and so cannot deadlock; this is here to notice when one of them stops doing that.
    /// </summary>
    [Test]
    public async Task CoreSurface_BlockedOnFromAContextOwningThread_DoesNotDeadlock()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var completed = RanToCompletionOnAContextOwningThread(() =>
            {
                using var db = new LadybugDatabase(path);
                var conn = db.ConnectAsync().AsTask().GetAwaiter().GetResult();

                conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, n STRING, PRIMARY KEY(id))")
                    .AsTask().GetAwaiter().GetResult();
                conn.ExecuteAsync("CREATE (:T {id: $id, n: $n})", new { id = 1L, n = "a" })
                    .AsTask().GetAwaiter().GetResult();

                var result = conn.QueryAsync("MATCH (t:T) RETURN t.id").AsTask().GetAwaiter().GetResult();
                result.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var stmt = conn.PrepareAsync("MATCH (t:T) WHERE t.id >= $min RETURN t.n")
                    .AsTask().GetAwaiter().GetResult();
                stmt.ExecuteNonQueryAsync(new { min = 0L }).AsTask().GetAwaiter().GetResult();
                stmt.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var tx = conn.BeginTransactionAsync().AsTask().GetAwaiter().GetResult();
                tx.ExecuteAsync("CREATE (:T {id: 2, n: 'b'})").AsTask().GetAwaiter().GetResult();
                tx.CommitAsync().AsTask().GetAwaiter().GetResult();
                tx.DisposeAsync().AsTask().GetAwaiter().GetResult();

                conn.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });

            await Assert.That(completed).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
