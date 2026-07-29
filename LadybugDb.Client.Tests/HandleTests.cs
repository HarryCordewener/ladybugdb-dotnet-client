using System.Runtime.CompilerServices;
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
            return true;
        }
    }
}
