using LadybugDb.Client.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>
/// The <c>Match&lt;T&gt;</c> escape hatch and the grouped projections against the seeded MUSH
/// graph (see <see cref="MushDatabase"/>): object <c>d</c> has <c>loc</c> <c>d % 5 + 1</c>, so every
/// location holds four objects.
/// </summary>
public class MatchAndAggregateTests
{
    // ----------------------------------------------------------------------------- Match<T>

    [Test]
    public Task MatchPattern_RoomContents_ThroughTheCallersPattern() => MushDatabase.Check(async conn =>
    {
        var contents = await conn.Match<Obj>("(o:Object)-[:Located]->(r:Object {dbref: $room})", new { room = 3L }, "o")
            .OrderBy(o => o.Dbref).Select(o => o.Name).ToListAsync();
        await Assert.That(contents).IsEquivalentTo(["obj2", "obj7", "obj12", "obj17"]);
    });

    [Test]
    public Task MatchPattern_WholeNode_AndDictionaryParameters() => MushDatabase.Check(async conn =>
    {
        var one = await conn.Match<Obj>("(n:Object {dbref: $d})", new Dictionary<string, object?> { ["d"] = 1L }).SingleAsync();
        await Assert.That(one).IsEqualTo(new Obj(1, "obj1", 2));

        var none = conn.Match<Obj>("(n:Object {dbref: $d})", new { d = 99L }).SingleOrDefault();
        await Assert.That(none).IsNull();
    });

    [Test]
    public Task MatchPattern_ThenAStep_ContinuesFromTheVariable() => MushDatabase.Check(async conn =>
    {
        var value = await conn.Match<Obj>("(x:Object {dbref: $d})", new { d = 5L }, "x")
            .Out<Obj, Has, Attr>().Where(p => p.Target.Aname == "A3").Select(p => p.Target.Aval).SingleAsync();
        await Assert.That(value).IsEqualTo("v5.3");
    });

    [Test]
    public Task MatchPattern_Validation() => MushDatabase.Check(async conn =>
    {
        await Assert.That(() => conn.Match<Obj>("(o:Object)", variable: "n")).Throws<ArgumentException>();
        await Assert.That(() => conn.Match<Obj>("(o:Object)", variable: "my var")).Throws<ArgumentException>();
        var parse = await Assert.ThrowsAsync<LadybugException>(() => conn.Match<Obj>("(n:Object").ToListAsync());
        await Assert.That(parse!.Message).Contains("(n:Object");
    });

    // ------------------------------------------------------------------------------ GroupBy

    [Test]
    public Task GroupBy_CountPerKey() => MushDatabase.Check(async conn =>
    {
        var perLoc = await conn.Nodes<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.Count() }).OrderBy(x => x.Key).ToListAsync();
        await Assert.That(perLoc.Select(x => (x.Key!.Value, x.N))).IsEquivalentTo([(1L, 4), (2L, 4), (3L, 4), (4L, 4), (5L, 4)]);

        var sync = conn.Nodes<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.LongCount() }).OrderBy(x => x.Key).ToList();
        await Assert.That(sync.Select(x => (x.Key!.Value, x.N))).IsEquivalentTo([(1L, 4L), (2L, 4L), (3L, 4L), (4L, 4L), (5L, 4L)]);
    });

    [Test]
    public Task GroupBy_SumMinMaxAverage_IntoARecord() => MushDatabase.Check(async conn =>
    {
        var stats = await conn.Nodes<Obj>().GroupBy(o => o.Loc)
            .Select(g => new LocStats(g.Key, g.Sum(x => x.Dbref), g.Min(x => x.Dbref), g.Max(x => x.Dbref), g.Average(x => x.Dbref)))
            .OrderBy(s => s.Loc).ToListAsync();
        await Assert.That(stats[0]).IsEqualTo(new LocStats(1, 50, 5, 20, 12.5));
        await Assert.That(stats[1]).IsEqualTo(new LocStats(2, 34, 1, 16, 8.5));
    });

    [Test]
    public Task GroupBy_OrderedByAnAggregate_AndPaged() => MushDatabase.Check(async conn =>
    {
        // Objects 1..7: loc 2 holds 1 and 6, loc 3 holds 2 and 7, locs 4, 5 and 1 hold one each.
        var busiest = await conn.Nodes<Obj>().Where(o => o.Dbref <= 7).GroupBy(o => o.Loc)
            .Select(g => new { g.Key, N = g.LongCount() })
            .OrderByDescending(x => x.N).ThenBy(x => x.Key).Take(3).ToListAsync();
        await Assert.That(busiest.Select(x => (x.Key!.Value, x.N))).IsEquivalentTo([(2L, 2L), (3L, 2L), (1L, 1L)]);
    });

    [Test]
    public Task GroupBy_AfterAStep() => MushDatabase.Check(async conn =>
    {
        var lateAttributes = await conn.Nodes<Obj>().OutWithRel<Obj, Has, Attr>().Where(p => p.Rel.Since > 8)
            .GroupBy(p => p.Source.Dbref).Select(g => new { g.Key, N = g.Count() })
            .OrderBy(x => x.Key).Take(3).ToListAsync();
        await Assert.That(lateAttributes.Select(x => (x.Key, x.N))).IsEquivalentTo([(1L, 2), (2L, 2), (3L, 2)]);
    });
}

public sealed record LocStats(long? Loc, long Total, long Lo, long Hi, double Mean);
