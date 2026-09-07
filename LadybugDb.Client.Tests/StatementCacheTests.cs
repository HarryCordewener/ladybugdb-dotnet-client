using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>The cache policy in isolation: check-out semantics, least-recently-used eviction, disposal.</summary>
public class StatementCacheTests
{
    private sealed class Fake(string name) : IDisposable
    {
        public string Name { get; } = name;
        public int Disposed { get; private set; }
        public void Dispose() => Disposed++;
    }

    private static StatementCache<Fake>.Entry EntryFor(string key, Fake fake, params string[] names) => new(key, fake, names);

    [Test]
    public async Task CheckOut_RemovesTheEntry_SoASecondCallerMisses()
    {
        using var cache = new StatementCache<Fake>(4);
        var a = new Fake("a");
        cache.Return(EntryFor("A", a));

        var first = cache.TryCheckOut("A");
        var second = cache.TryCheckOut("A");

        await Assert.That(first!.Statement).IsSameReferenceAs(a);
        await Assert.That(second).IsNull();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LeastRecentlyUsed_IsEvictedAndDisposed()
    {
        using var cache = new StatementCache<Fake>(2);
        var a = new Fake("a"); var b = new Fake("b"); var c = new Fake("c");
        cache.Return(EntryFor("A", a));
        cache.Return(EntryFor("B", b));
        cache.Return(cache.TryCheckOut("A")!);   // touch A: B is now the oldest
        cache.Return(EntryFor("C", c));

        await Assert.That(b.Disposed).IsEqualTo(1);
        await Assert.That(a.Disposed).IsEqualTo(0);
        await Assert.That(cache.TryCheckOut("B")).IsNull();
        await Assert.That(cache.TryCheckOut("A")).IsNotNull();
        await Assert.That(cache.TryCheckOut("C")).IsNotNull();
    }

    [Test]
    public async Task ReturningADuplicateKey_KeepsTheResidentAndDisposesTheIncoming()
    {
        using var cache = new StatementCache<Fake>(4);
        var resident = new Fake("r"); var incoming = new Fake("i");
        cache.Return(EntryFor("A", resident));
        cache.Return(EntryFor("A", incoming));

        await Assert.That(incoming.Disposed).IsEqualTo(1);
        await Assert.That(cache.TryCheckOut("A")!.Statement).IsSameReferenceAs(resident);
    }

    [Test]
    public async Task ZeroCapacity_DisposesEverythingReturned()
    {
        using var cache = new StatementCache<Fake>(0);
        var a = new Fake("a");
        cache.Return(EntryFor("A", a));
        await Assert.That(a.Disposed).IsEqualTo(1);
        await Assert.That(cache.TryCheckOut("A")).IsNull();
    }

    [Test]
    public async Task Dispose_DisposesResidents_AndLateReturns()
    {
        var cache = new StatementCache<Fake>(4);
        var a = new Fake("a"); var b = new Fake("b");
        cache.Return(EntryFor("A", a));
        var checkedOut = EntryFor("B", b);

        cache.Dispose();
        cache.Return(checkedOut);

        await Assert.That(a.Disposed).IsEqualTo(1);
        await Assert.That(b.Disposed).IsEqualTo(1);
        cache.Dispose();
        await Assert.That(a.Disposed).IsEqualTo(1);
    }
}
