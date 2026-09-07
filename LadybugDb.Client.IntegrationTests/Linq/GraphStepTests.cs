using LadybugDb.Client.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>
/// The graph steps against the seeded MUSH graph (see <see cref="MushDatabase"/>): every object
/// <c>d</c> is <c>Located</c> in <c>d % 5 + 1</c> and <c>Has</c> ten attributes <c>A1..A10</c>.
/// </summary>
public class GraphStepTests
{
    [Test]
    public Task RoomContents_ThroughIn() => MushDatabase.Check(async conn =>
    {
        var contents = await conn.Nodes<Obj>().Where(room => room.Dbref == 3).In<Obj, Located, Obj>()
            .OrderBy(p => p.Target.Dbref).Select(p => p.Target.Name).ToListAsync();
        await Assert.That(contents).IsEquivalentTo(["obj2", "obj7", "obj12", "obj17"]);
    });

    [Test]
    public Task ObjectWithAttributes_ThroughOutWithRel() => MushDatabase.Check(async conn =>
    {
        var attributes = await conn.Nodes<Obj>().Where(o => o.Dbref == 5).OutWithRel<Obj, Has, Attr>()
            .Where(p => p.Rel.Since > 8).OrderBy(p => p.Rel.Since)
            .Select(p => new { p.Source.Name, p.Rel.Since, p.Target.Aname, p.Target.Aval }).ToListAsync();
        await Assert.That(attributes.Select(a => (a.Name, a.Since, a.Aname, a.Aval)))
            .IsEquivalentTo([("obj5", 9L, "A9", "v5.9"), ("obj5", 10L, "A10", "v5.10")]);
    });

    [Test]
    public Task OneAttributeOfOneObject() => MushDatabase.Check(async conn =>
    {
        var dbref = 12L;
        var attrName = "A3";
        var value = await conn.Nodes<Obj>().Where(o => o.Dbref == dbref).Out<Obj, Has, Attr>()
            .Where(p => p.Target.Aname == attrName).Select(p => p.Target.Aval).FirstOrDefaultAsync();
        await Assert.That(value).IsEqualTo("v12.3");
    });

    [Test]
    public Task TwoHops_Chained_AndVariableLength() => MushDatabase.Check(async conn =>
    {
        // 1 is in 2, 2 is in 3.
        var chained = await conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>().Out<(Obj Source, Obj Target), Located, Obj>()
            .Select(p => new { Middle = p.Source.Target.Dbref, End = p.Target.Dbref }).SingleAsync();
        await Assert.That((chained.Middle, chained.End)).IsEqualTo((2L, 3L));

        var reachable = await conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>(1, 2)
            .OrderBy(p => p.Target.Dbref).Select(p => p.Target.Dbref).ToListAsync();
        await Assert.That(reachable).IsEquivalentTo([2L, 3L]);
    });

    [Test]
    public Task WhereExists_KeepsTheSourceElement() => MushDatabase.Check(async conn =>
    {
        var inRoom3 = await conn.Nodes<Obj>().WhereExists<Obj, Located, Obj>(room => room.Dbref == 3).OrderBy(o => o.Dbref).ToListAsync();
        await Assert.That(inRoom3.Select(o => o.Dbref)).IsEquivalentTo([2L, 7L, 12L, 17L]);
        await Assert.That(inRoom3[0]).IsEqualTo(new Obj(2, "obj2", 3));
    });

    [Test]
    public Task UnprojectedStep_MaterializesTheTuple() => MushDatabase.Check(async conn =>
    {
        var pairs = await conn.Nodes<Obj>().Where(o => o.Dbref == 5).In<Obj, Located, Obj>().OrderBy(p => p.Target.Dbref).ToListAsync();
        await Assert.That(pairs.Select(p => (p.Source.Dbref, p.Target.Dbref))).IsEquivalentTo([(5L, 4L), (5L, 9L), (5L, 14L), (5L, 19L)]);

        var triple = await conn.Nodes<Obj>().Where(o => o.Dbref == 2).OutWithRel<Obj, Has, Attr>().Where(p => p.Rel.Since == 1).SingleAsync();
        await Assert.That(triple.Source).IsEqualTo(new Obj(2, "obj2", 3));
        await Assert.That(triple.Rel).IsEqualTo(new Has(1));
        await Assert.That(triple.Target).IsEqualTo(new Attr("2/A1", "A1", "v2.1"));

        var nested = await conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>().Out<(Obj Source, Obj Target), Located, Obj>().SingleAsync();
        await Assert.That((nested.Source.Source.Dbref, nested.Source.Target.Dbref, nested.Target.Dbref)).IsEqualTo((1L, 2L, 3L));

        var targetOnly = conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>().Select(p => p.Target).Single();
        await Assert.That(targetOnly).IsEqualTo(new Obj(2, "obj2", 3));
    });

    [Test]
    public Task MismatchedDirection_IsReportedBeforeTheEngineSeesIt() => MushDatabase.Check(async conn =>
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Nodes<Attr>().Out<Attr, Has, Obj>().ToListAsync());
        await Assert.That(ex!.Message).Contains("use In");
    });

    [Test]
    public Task Count_AfterAStep() => MushDatabase.Check(async conn =>
    {
        await Assert.That(await conn.Nodes<Obj>().Out<Obj, Has, Attr>().CountAsync()).IsEqualTo(200L);
        await Assert.That(conn.Nodes<Obj>().Where(o => o.Dbref == 5).In<Obj, Located, Obj>().Count()).IsEqualTo(4);
    });
}
