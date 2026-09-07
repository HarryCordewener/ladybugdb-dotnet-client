using LadybugDb.Client.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>
/// A LINQ query owns the <see cref="LadybugQueryResult"/> it streams from, exactly as
/// <see cref="LadybugConnection.Select{T}"/> does, and releases it on every exit path - proven the
/// way <c>SelectDisposalTests</c> proves it, against <see cref="LadybugQueryResult.LiveCount"/>,
/// which counts explicit disposal only and so cannot be satisfied by a finalizer. Each test asserts
/// the count went up while streaming before asserting it came back down, for the reason that class
/// gives. <see cref="NotInParallelAttribute"/> because the counter is process-wide.
/// </summary>
[NotInParallel]
public class QueryableDisposalTests
{
    private sealed class BoomException : Exception;

    [Test]
    public Task AsyncEarlyBreak_ReleasesTheResult() => MushDatabase.Check(async conn =>
    {
        var baseline = LadybugQueryResult.LiveCount;
        var whileStreaming = -1L;
        await foreach (var o in conn.Nodes<Obj>().OrderBy(o => o.Dbref).AsAsyncEnumerable())
        {
            whileStreaming = LadybugQueryResult.LiveCount;
            await Assert.That(o.Dbref).IsEqualTo(1L);
            break;
        }

        await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
        await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
    });

    [Test]
    public Task AsyncThrowingBody_ReleasesTheResult() => MushDatabase.Check(async conn =>
    {
        var baseline = LadybugQueryResult.LiveCount;
        var whileStreaming = -1L;
        await Assert.ThrowsAsync<BoomException>(async () =>
        {
            await foreach (var _ in conn.Nodes<Obj>().Select(o => o.Name).AsAsyncEnumerable())
            {
                whileStreaming = LadybugQueryResult.LiveCount;
                throw new BoomException();
            }
        });

        await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
        await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
    });

    [Test]
    public Task SyncEarlyBreak_AndThrowingBody_ReleaseTheResult() => MushDatabase.Check(async conn =>
    {
        var baseline = LadybugQueryResult.LiveCount;
        var whileStreaming = -1L;
        foreach (var _ in conn.Nodes<Obj>())
        {
            whileStreaming = LadybugQueryResult.LiveCount;
            break;
        }

        await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
        await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);

        Assert.Throws<BoomException>(() =>
        {
            foreach (var _ in conn.Nodes<Obj>())
            {
                whileStreaming = LadybugQueryResult.LiveCount;
                throw new BoomException();
            }
        });
        await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
        await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
    });

    /// <summary>The terminals stop reading after their LIMIT and still release the result.</summary>
    [Test]
    public Task Terminals_ReleaseTheResult() => MushDatabase.Check(async conn =>
    {
        var baseline = LadybugQueryResult.LiveCount;
        _ = await conn.Nodes<Obj>().OrderBy(o => o.Dbref).FirstAsync();
        _ = await conn.Nodes<Obj>().CountAsync();
        _ = conn.Nodes<Obj>().First();
        _ = conn.Nodes<Obj>().Count();

        // A mapping failure on the first row is the exit path where the caller's loop body never
        // runs at all, and so the one most easily left leaking.
        await conn.ExecuteAsync("CREATE (:Object {dbref: 99, name: 'nowhere'})");
        await Assert.ThrowsAsync<LadybugException>(() => conn.Nodes<StrictObj>().Where(o => o.Dbref == 99).ToListAsync());
        Assert.Throws<LadybugException>(() => conn.Nodes<StrictObj>().Where(o => o.Dbref == 99).ToList());
        await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
    });
}
