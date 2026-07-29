using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LadybugDb.Client;
using LadybugDb.Client.Interop;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// These tests exercise <see cref="LbugStructHandle"/>'s ownership contract in isolation, with
/// no native library involved (a plain <see cref="int"/> release counter stands in for the real
/// destroy call). That is deliberate: the entire init-failure-window fix rests on one specific
/// runtime behavior - "SafeHandle never invokes ReleaseHandle while IsInvalid is true" - and that
/// needs to stay pinned down by a real, always-run test, not re-derived from a throwaway probe
/// each time someone touches this code. If a future change moved <c>SetHandle</c> back to
/// allocation time instead of <see cref="LbugStructHandle.Adopt"/> time, the unadopted-handle
/// tests below would start failing immediately.
/// </summary>
public class HandleTests
{
    [Test]
    public async Task NewHandle_IsInvalidBeforeAllocation()
    {
        using var h = new UnallocatedHandle();
        await Assert.That(h.IsInvalid).IsTrue();
    }

    [Test]
    public async Task LadybugException_CarriesTheFailingStatement()
    {
        var ex = new LadybugException("boom", "MATCH (n) RETURN n");
        await Assert.That(ex.Statement).IsEqualTo("MATCH (n) RETURN n");
        await Assert.That(ex.Message).Contains("boom");
    }

    [Test]
    public async Task LadybugException_WithNullStatement_OmitsStatementSuffix()
    {
        var ex = new LadybugException("boom");
        await Assert.That(ex.Statement).IsNull();
        await Assert.That(ex.Message).IsEqualTo("boom");
        await Assert.That(ex.Message).DoesNotContain("Statement:");
    }

    [Test]
    public async Task LadybugException_WithInnerException_CarriesIt()
    {
        var inner = new InvalidOperationException("native marshalling failed");
        var ex = new LadybugException("boom", inner);
        await Assert.That(ex.InnerException).IsEqualTo(inner);
        await Assert.That(ex.Statement).IsNull();
    }

    [Test]
    public async Task UnadoptedHandle_DoubleDispose_NeverReleases()
    {
        var releases = new int[1];
        var h = new CountingHandle(releases);

        h.Dispose();
        h.Dispose();

        await Assert.That(releases[0]).IsEqualTo(0);
    }

    [Test]
    public async Task UnadoptedHandle_FinalizeOnly_NeverReleases()
    {
        var releases = new int[1];
        CreateUnadoptedAndAbandon(releases);

        CollectAndWaitForFinalizers();

        await Assert.That(releases[0]).IsEqualTo(0);
    }

    [Test]
    public async Task AdoptedHandle_IsInvalid_FlipsFalseOnAdopt()
    {
        var releases = new int[1];
        using var h = CountingHandle.CreateAdopted(releases);

        await Assert.That(h.IsInvalid).IsFalse();
    }

    [Test]
    public async Task AdoptedHandle_DoubleDispose_ReleasesExactlyOnce()
    {
        var releases = new int[1];
        var h = CountingHandle.CreateAdopted(releases);

        h.Dispose();
        h.Dispose();

        await Assert.That(releases[0]).IsEqualTo(1);
    }

    [Test]
    public async Task AdoptedHandle_FinalizeOnly_ReleasesExactlyOnce()
    {
        var releases = new int[1];
        CreateAdoptedAndAbandon(releases);

        CollectAndWaitForFinalizers();

        await Assert.That(releases[0]).IsEqualTo(1);
    }

    [Test]
    public async Task AdoptedHandles_DisposedAcrossThreads_ReleaseCountMatchesHandleCount()
    {
        const int handleCount = 200;
        const int threadCount = 16;
        var releases = new int[1];

        var handles = new CountingHandle[handleCount];
        for (var i = 0; i < handleCount; i++)
            handles[i] = CountingHandle.CreateAdopted(releases);

        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var worker = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = worker; i < handleCount; i += threadCount)
                    handles[i].Dispose();
            });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();

        await Assert.That(releases[0]).IsEqualTo(handleCount);
    }

    [Test]
    public async Task Acquire_AfterDispose_ThrowsObjectDisposed()
    {
        var releases = new int[1];
        var h = CountingHandle.CreateAdopted(releases);
        h.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var lease = h.Acquire();
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task Acquire_WhileOutstanding_DefersRelease()
    {
        // A Lease is a ref struct specifically so it cannot be held across an await - it must
        // stay scoped to a single synchronous block, which is asserted here without an `await`
        // inside the `using`.
        var releases = new int[1];
        var h = CountingHandle.CreateAdopted(releases);

        int releasesWhileLeased;
        using (h.Acquire())
        {
            h.Dispose();
            releasesWhileLeased = releases[0];
        }

        await Assert.That(releasesWhileLeased).IsEqualTo(0);
        await Assert.That(releases[0]).IsEqualTo(1);
    }

    /// <summary>
    /// Property 1 from the refcount-ownership fix: a long-held reference must defer a parent's
    /// real destroy while <see cref="SafeHandle.IsClosed"/> already reports
    /// <see langword="true"/> immediately once the parent's own <c>Dispose()</c> runs. Both halves
    /// matter - a hold that also delayed <c>IsClosed</c> would make new work against a "closing"
    /// parent silently succeed instead of throwing <see cref="ObjectDisposedException"/>, and a
    /// hold that did not actually defer the destroy would be exactly the original bug this fix
    /// closes.
    /// </summary>
    [Test]
    public async Task AcquireParentHolds_Success_DefersParentDestroyUntilChildReleases()
    {
        var parentReleases = new int[1];
        var parent = CountingHandle.CreateAdopted(parentReleases);
        var childReleases = new int[1];
        var child = CountingHandle.CreateAdopted(childReleases);

        await Assert.That(child.AcquireParentHolds(parent)).IsTrue();

        parent.Dispose();

        // Half 1: "closed for new work" reflects Dispose() immediately - new work against this
        // parent is refused right away, not "eventually". Checked via IsClosedForNewWork
        // directly, not the raw SafeHandle.IsClosed: verified separately (see the standalone
        // probe this fix was calibrated against) that IsClosed itself does NOT flip true here
        // while the child's hold above keeps the real reference count above zero - only
        // IsClosedForNewWork does, which is exactly why it exists.
        await Assert.That(parent.IsClosedForNewWork).IsTrue();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var lease = parent.Acquire();
        });

        // Half 2: the real destroy is nonetheless deferred - not skipped, not run early - while
        // the child's hold is still outstanding.
        await Assert.That(parentReleases[0]).IsEqualTo(0);

        child.Dispose();

        // Only once the child (the thing actually holding it) itself releases does the parent's
        // destroy finally run - exactly once.
        await Assert.That(parentReleases[0]).IsEqualTo(1);
        await Assert.That(childReleases[0]).IsEqualTo(1);
    }

    /// <summary>
    /// Property 2 from the refcount-ownership fix: a failed acquisition must never leak a
    /// reference. Regresses the specific mechanism that broke this for the earlier, bespoke
    /// single-parent version of this idea (<c>LbugConnectionHandle</c>'s old
    /// <c>_databaseHoldCount</c>): <c>SafeHandle.DangerousAddRef</c> throws
    /// <see cref="ObjectDisposedException"/> for an already-closed handle instead of returning
    /// <c>acquired == false</c>, so a caller that pre-increments its own counter before calling it
    /// - rather than recording success only after - leaks that increment past the throw. This
    /// covers the multi-parent case specifically: the first parent succeeds, the second (already
    /// closed) fails, and the direct assertion is that the first parent's hold was actually rolled
    /// back, not merely that <c>AcquireParentHolds</c> returned <see langword="false"/> - proven by
    /// the first parent's own subsequent, independent <c>Dispose()</c> destroying it immediately
    /// and exactly once, which a leaked hold would have deferred or duplicated.
    /// </summary>
    [Test]
    public async Task AcquireParentHolds_PartialFailure_RollsBackEveryAlreadyAcquiredHold()
    {
        var openReleases = new int[1];
        var open = CountingHandle.CreateAdopted(openReleases);
        var closedReleases = new int[1];
        var closed = CountingHandle.CreateAdopted(closedReleases);
        closed.Dispose();
        await Assert.That(closedReleases[0]).IsEqualTo(1);

        var childReleases = new int[1];
        var child = CountingHandle.CreateAdopted(childReleases);

        // `open` (first) succeeds, `closed` (second) fails - AcquireParentHolds must roll `open`'s
        // hold back rather than leaving it acquired with nothing tracking it.
        await Assert.That(child.AcquireParentHolds(open, closed)).IsFalse();

        // The direct "no leaked ref" assertion: `open` returns to exactly the state it was in
        // before the failed attempt - a single outstanding (baseline) reference, so its own
        // Dispose() below destroys it immediately and exactly once. A leaked hold from the failed
        // attempt would instead have deferred this, or (if double-released) thrown from
        // ReleaseHandle/corrupted the count.
        open.Dispose();
        await Assert.That(openReleases[0]).IsEqualTo(1);

        child.Dispose();
        await Assert.That(childReleases[0]).IsEqualTo(1);
    }

    /// <summary>Simplest single-parent shape of <see cref="AcquireParentHolds_PartialFailure_RollsBackEveryAlreadyAcquiredHold"/>: a lone already-closed parent.</summary>
    [Test]
    public async Task AcquireParentHolds_ParentAlreadyClosed_ReturnsFalseAndLeaksNothing()
    {
        var parentReleases = new int[1];
        var parent = CountingHandle.CreateAdopted(parentReleases);
        parent.Dispose();
        await Assert.That(parentReleases[0]).IsEqualTo(1);

        var childReleases = new int[1];
        var child = CountingHandle.CreateAdopted(childReleases);

        await Assert.That(child.AcquireParentHolds(parent)).IsFalse();

        child.Dispose();
        await Assert.That(childReleases[0]).IsEqualTo(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateUnadoptedAndAbandon(int[] releases) => _ = new CountingHandle(releases);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAdoptedAndAbandon(int[] releases) => _ = CountingHandle.CreateAdopted(releases);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectAndWaitForFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class UnallocatedHandle : LbugStructHandle
    {
        protected override bool ReleaseHandle() => true;
    }

    /// <summary>A handle whose "native" release is just incrementing a shared counter, so ownership logic can be tested without liblbug.</summary>
    private sealed class CountingHandle : LbugStructHandle
    {
        private readonly int[] _releases;

        internal CountingHandle(int[] releases) => _releases = releases;

        internal static unsafe CountingHandle CreateAdopted(int[] releases)
        {
            var storage = AllocateUnowned((nuint)sizeof(byte));
            var h = new CountingHandle(releases);
            h.Adopt(storage);
            return h;
        }

        protected override unsafe bool ReleaseHandle()
        {
            Interlocked.Increment(ref _releases[0]);
            FreeStorage();
            // Mirrors every real LbugStructHandle's ReleaseHandle ordering: the "destroy" above
            // (here, just the counter) runs first, then any long-lived parent holds this handle
            // took via AcquireParentHolds are released - see ReleaseParentHolds's own remarks for
            // why that order matters. A no-op for every handle in this file that never called
            // AcquireParentHolds in the first place.
            ReleaseParentHolds();
            return true;
        }
    }
}
