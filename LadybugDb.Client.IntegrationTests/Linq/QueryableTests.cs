using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

public sealed record NameAndLoc(string Name, long? Loc);

/// <summary>The same table as <see cref="Obj"/>, with a non-nullable <c>Loc</c>, so a NULL <c>loc</c> has nowhere to go.</summary>
[Node("Object")]
public sealed record StrictObj([property: Key] long Dbref, string Name, long Loc);

/// <summary>
/// Every shape <c>TranslatorTests</c> pins, run against the real engine on the seeded MUSH graph
/// (see <see cref="MushDatabase"/>) and asserted against known data - both through the async
/// terminals and through synchronous <c>foreach</c>, which share one translation and one row
/// mapping.
/// </summary>
public class QueryableTests
{
    // --------------------------------------------------------------------------- whole nodes

    [Test]
    public Task NoSelect_MaterializesTheNodeRecord() => MushDatabase.Check(async conn =>
    {
        var objects = await conn.Nodes<Obj>().OrderBy(o => o.Dbref).ToListAsync();
        await Assert.That(objects.Count).IsEqualTo(20);
        await Assert.That(objects[0]).IsEqualTo(new Obj(1, "obj1", 2));
        await Assert.That(objects[19]).IsEqualTo(new Obj(20, "obj20", 1));
    });

    [Test]
    public Task SyncForeach_AndToList_MaterializeTheSameRows() => MushDatabase.Check(async conn =>
    {
        var query = conn.Nodes<Obj>().Where(o => o.Dbref <= 3).OrderBy(o => o.Dbref);
        var sync = new List<Obj>();
        foreach (var o in query) sync.Add(o);
        await Assert.That(sync).IsEquivalentTo(await query.ToListAsync());
        await Assert.That(query.ToList().Select(o => o.Name)).IsEquivalentTo(["obj1", "obj2", "obj3"]);
    });

    // ----------------------------------------------------------------------------- predicates

    [Test]
    public Task WhereEqualsClosure() => MushDatabase.Check(async conn =>
    {
        var dbref = 7L;
        var name = await conn.Nodes<Obj>().Where(o => o.Dbref == dbref).Select(o => o.Name).SingleAsync();
        await Assert.That(name).IsEqualTo("obj7");
    });

    [Test]
    public Task Comparisons_AndConnectives() => MushDatabase.Check(async conn =>
    {
        var dbrefs = await conn.Nodes<Obj>()
            .Where(o => (o.Dbref < 3 || o.Dbref >= 19) && o.Dbref != 2 && !(o.Name == "obj20"))
            .OrderBy(o => o.Dbref).Select(o => o.Dbref).ToListAsync();
        await Assert.That(dbrefs).IsEquivalentTo([1L, 19L]);
    });

    [Test]
    public Task NullTests() => MushDatabase.Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (:Object {dbref: 99, name: 'nowhere'})");
        var nowhere = await conn.Nodes<Obj>().Where(o => o.Loc == null).Select(o => o.Name).ToListAsync();
        await Assert.That(nowhere).IsEquivalentTo(["nowhere"]);
        var somewhere = await conn.Nodes<Obj>().Where(o => o.Loc != null && o.Loc.HasValue).CountAsync();
        await Assert.That(somewhere).IsEqualTo(20L);
        var located = await conn.Nodes<Obj>().Where(o => o.Loc == 3).Select(o => o.Dbref).OrderBy(d => d).ToListAsync();
        await Assert.That(located).IsEquivalentTo([2L, 7L, 12L, 17L]);
    });

    [Test]
    public Task StringMethods() => MushDatabase.Check(async conn =>
    {
        var prefix = "obj1";
        var starts = await conn.Nodes<Obj>().Where(o => o.Name.StartsWith(prefix)).CountAsync();
        await Assert.That(starts).IsEqualTo(11L);
        var ends = await conn.Nodes<Obj>().Where(o => o.Name.EndsWith("0")).Select(o => o.Dbref).OrderBy(d => d).ToListAsync();
        await Assert.That(ends).IsEquivalentTo([10L, 20L]);
        var contains = await conn.Nodes<Obj>().Where(o => o.Name.Contains("j2")).CountAsync();
        await Assert.That(contains).IsEqualTo(2L);
        var lengths = await conn.Nodes<Obj>().Where(o => o.Name.Length == 4).CountAsync();
        await Assert.That(lengths).IsEqualTo(9L);
        var upper = await conn.Nodes<Obj>().Where(o => o.Name.ToUpper() == "OBJ5" && o.Name.ToLower() == "obj5").Select(o => o.Dbref).SingleAsync();
        await Assert.That(upper).IsEqualTo(5L);
    });

    [Test]
    public Task CollectionContains() => MushDatabase.Check(async conn =>
    {
        var wanted = new List<long> { 3, 5, 8 };
        var names = await conn.Nodes<Obj>().Where(o => wanted.Contains(o.Dbref)).OrderBy(o => o.Dbref).Select(o => o.Name).ToListAsync();
        await Assert.That(names).IsEquivalentTo(["obj3", "obj5", "obj8"]);
        var none = await conn.Nodes<Obj>().Where(o => Array.Empty<long>().Contains(o.Dbref)).AnyAsync();
        await Assert.That(none).IsFalse();
    });

    [Test]
    public Task MemberToMember() => MushDatabase.Check(async conn =>
    {
        // loc = dbref % 5 + 1, so loc == dbref never holds; loc < dbref holds from dbref 5 on except where loc wraps.
        var self = await conn.Nodes<Obj>().Where(o => o.Loc == o.Dbref).AnyAsync();
        await Assert.That(self).IsFalse();
        var below = await conn.Nodes<Obj>().Where(o => o.Loc < o.Dbref).CountAsync();
        await Assert.That(below).IsEqualTo(16L);
    });

    /// <summary>A refusal surfaces from enumeration, where translation happens - and names the sub-expression.</summary>
    [Test]
    public Task Refusal_SurfacesFromEnumeration() => MushDatabase.Check(async conn =>
    {
        var query = conn.Nodes<Obj>().Where(o => o.Name.Trim() == "x");
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => query.ToListAsync());
        await Assert.That(ex!.Message).Contains("o.Name.Trim()");
        var sync = Assert.Throws<NotSupportedException>(() => query.ToList());
        await Assert.That(sync!.Message).Contains("o.Name.Trim()");
    });

    // ----------------------------------------------------------------------------- projections

    [Test]
    public Task AnonymousType_Record_AndTuple() => MushDatabase.Check(async conn =>
    {
        var anonymous = await conn.Nodes<Obj>().Where(o => o.Dbref == 4).Select(o => new { o.Name, o.Loc, Len = o.Name.Length, Upper = o.Name.ToUpper() }).SingleAsync();
        await Assert.That((anonymous.Name, anonymous.Loc, anonymous.Len, anonymous.Upper)).IsEqualTo(("obj4", (long?)5, 4, "OBJ4"));

        var record = await conn.Nodes<Obj>().Where(o => o.Dbref == 4).Select(o => new NameAndLoc(o.Name, o.Loc)).SingleAsync();
        await Assert.That(record).IsEqualTo(new NameAndLoc("obj4", 5));

        var tuple = await conn.Nodes<Obj>().Where(o => o.Dbref == 4).Select(o => new ValueTuple<long, string>(o.Dbref, o.Name)).SingleAsync();
        await Assert.That(tuple).IsEqualTo((4L, "obj4"));
    });

    [Test]
    public Task ScalarSelect_AndDistinct() => MushDatabase.Check(async conn =>
    {
        var locs = await conn.Nodes<Obj>().Select(o => o.Loc).Distinct().ToListAsync();
        await Assert.That(locs.Select(l => l!.Value).Order()).IsEquivalentTo([1L, 2L, 3L, 4L, 5L]);
    });

    [Test]
    public Task IdentitySelect_ReturnsNodes() => MushDatabase.Check(async conn =>
    {
        var first = await conn.Nodes<Obj>().Where(o => o.Dbref > 18).OrderBy(o => o.Dbref).Select(o => o).FirstAsync();
        await Assert.That(first).IsEqualTo(new Obj(19, "obj19", 5));
    });

    // ------------------------------------------------------------------------ ordering, paging

    [Test]
    public Task OrderBy_ThenBy_Skip_Take() => MushDatabase.Check(async conn =>
    {
        var page = 1;
        var rows = await conn.Nodes<Obj>().OrderBy(o => o.Loc).ThenByDescending(o => o.Dbref).Skip(page * 4).Take(4)
            .Select(o => new { o.Dbref, o.Loc }).ToListAsync();
        // loc 1 holds dbrefs 5, 10, 15, 20 (descending); page 1 is loc 2: 16, 11, 6, 1.
        await Assert.That(rows.Select(r => r.Dbref)).IsEquivalentTo([16L, 11L, 6L, 1L]);
        await Assert.That(rows.All(r => r.Loc == 2)).IsTrue();
    });

    [Test]
    public Task OrderBy_AfterSelect() => MushDatabase.Check(async conn =>
    {
        var rows = await conn.Nodes<Obj>().Select(o => new { o.Name, o.Dbref }).Where(x => x.Dbref > 17).OrderByDescending(x => x.Dbref).ToListAsync();
        await Assert.That(rows.Select(r => r.Name)).IsEquivalentTo(["obj20", "obj19", "obj18"]);
    });

    // ------------------------------------------------------------------------------- terminals

    [Test]
    public Task Count_Any_First_Single() => MushDatabase.Check(async conn =>
    {
        await Assert.That(await conn.Nodes<Obj>().CountAsync()).IsEqualTo(20L);
        await Assert.That(conn.Nodes<Obj>().Count(o => o.Loc == 1)).IsEqualTo(4);
        await Assert.That(conn.Nodes<Obj>().LongCount()).IsEqualTo(20L);
        await Assert.That(await conn.Nodes<Obj>().Where(o => o.Dbref > 100).AnyAsync()).IsFalse();
        await Assert.That(conn.Nodes<Obj>().Any(o => o.Dbref == 100)).IsFalse();
        await Assert.That(conn.Nodes<Obj>().Any()).IsTrue();

        await Assert.That((await conn.Nodes<Obj>().OrderBy(o => o.Dbref).FirstAsync()).Dbref).IsEqualTo(1L);
        await Assert.That(conn.Nodes<Obj>().OrderByDescending(o => o.Dbref).First().Dbref).IsEqualTo(20L);
        await Assert.That(await conn.Nodes<Obj>().Where(o => o.Dbref == 100).FirstOrDefaultAsync()).IsNull();
        await Assert.That(conn.Nodes<Obj>().FirstOrDefault(o => o.Dbref == 100)).IsNull();
        await Assert.That((await conn.Nodes<Obj>().Where(o => o.Dbref == 3).SingleAsync()).Name).IsEqualTo("obj3");
        await Assert.That(await conn.Nodes<Obj>().Where(o => o.Dbref == 300).SingleOrDefaultAsync()).IsNull();
        await Assert.That(conn.Nodes<Obj>().Single(o => o.Dbref == 3).Name).IsEqualTo("obj3");

        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Nodes<Obj>().Where(o => o.Dbref == 100).FirstAsync());
        await Assert.That(none!.Message).Contains("no elements");
        var many = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Nodes<Obj>().Where(o => o.Loc == 1).SingleAsync());
        await Assert.That(many!.Message).Contains("more than one");
        Assert.Throws<InvalidOperationException>(() => conn.Nodes<Obj>().Single(o => o.Loc == 1));
    });

    [Test]
    public Task ToString_RendersTheCypher() => MushDatabase.Check(async conn =>
    {
        var query = conn.Nodes<Obj>().Where(o => o.Dbref == 1).Select(o => o.Name);
        await Assert.That(query.ToString()).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name");
    });

    /// <summary>The whole-node path reports a NULL into a non-nullable parameter the way Select&lt;T&gt; does, never as a default.</summary>
    [Test]
    public Task NullProperty_IntoANonNullableParameter_IsReported() => MushDatabase.Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (:Object {dbref: 99, name: 'nowhere'})");
        var ex = await Assert.ThrowsAsync<LadybugException>(() => conn.Nodes<StrictObj>().Where(o => o.Dbref == 99).ToListAsync());
        await Assert.That(ex!.Message).Contains("'loc'");
        await Assert.That(ex.Message).Contains("NULL");
        await Assert.That((await conn.Nodes<Obj>().Where(o => o.Dbref == 99).SingleAsync()).Loc).IsNull();
    });

    [Test]
    public Task Cancellation_IsHonouredBeforeTheStatementRuns() => MushDatabase.Check(async conn =>
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => conn.Nodes<Obj>().ToListAsync(cts.Token));
    });

    [Test]
    public Task ForeignProvider_IsRefusedByTheAsyncTerminals() => MushDatabase.Check(async conn =>
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new[] { 1 }.AsQueryable().ToListAsync());
        await Assert.That(ex!.Message).Contains("not a LadybugDB");
    });
}
