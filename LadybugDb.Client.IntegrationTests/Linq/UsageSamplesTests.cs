using System.Linq.Expressions;
using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>A node with a boolean, for the bare-boolean refusal the guide's table shows; never queried against the engine.</summary>
[Node("Object")]
public sealed record Flagged([property: Key] long Dbref, bool IsRoom);

/// <summary>
/// The code blocks of the LINQ chapter of docs/USAGE.md, in the chapter's order, each run against
/// the seeded MUSH graph (see <see cref="MushDatabase"/>) with the Cypher the chapter's comments
/// show pinned exactly. Edit the chapter and this file together.
/// </summary>
public class UsageSamplesTests
{
    /// <summary>The Cypher a chain ending in <paramref name="terminal"/> runs - the terminal's LIMIT or count(*) included.</summary>
    private static string Cypher<T, TResult>(IQueryable<T> source, Expression<Func<IQueryable<T>, TResult>> terminal) =>
        QueryTranslator.Translate(new Substitute(terminal.Parameters[0], source.Expression).Visit(terminal.Body), LadybugSchema.Default).Text.Cypher;

    private sealed class Substitute(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }

    [Test]
    public async Task SchemaDescriptors()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            var schema = LadybugSchema.For(typeof(Obj), typeof(Attr), typeof(Has), typeof(Located));
            await schema.CreateTablesAsync(conn);   // CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref)), ...
            await schema.ValidateAsync(conn);       // throws SchemaMismatchException listing every mismatch, or returns

            await Assert.That(schema.CreateTableStatements()[0]).IsEqualTo("CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref))");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public Task EntryPoints() => MushDatabase.Check(async conn =>
    {
        var dbref = 7L;
        var found = await conn.Nodes<Obj>()
            .Where(o => o.Dbref == dbref)
            .Select(o => new { o.Name, o.Loc })
            .FirstOrDefaultAsync();
        // MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name, o.loc AS Loc LIMIT $p1
        await Assert.That(found).IsNotNull();
        await Assert.That((found!.Name, found.Loc)).IsEqualTo(("obj7", (long?)3));
        await Assert.That(Cypher(conn.Nodes<Obj>().Where(o => o.Dbref == dbref).Select(o => new { o.Name, o.Loc }), q => q.FirstOrDefault()))
            .IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name, o.loc AS Loc LIMIT $p1");

        var rendered = conn.Nodes<Obj>().Where(o => o.Dbref == dbref).Select(o => o.Name).ToString();
        await Assert.That(rendered).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name");

        var page = await conn.Nodes<Obj>().OrderBy(o => o.Name).Skip(2).Take(3).ToListAsync();   // List<Obj>
        // MATCH (o:Object) RETURN o ORDER BY o.name SKIP $p0 LIMIT $p1
        await Assert.That(page.Select(o => o.Name)).IsEquivalentTo(["obj11", "obj12", "obj13"]);
        await Assert.That(conn.Nodes<Obj>().OrderBy(o => o.Name).Skip(2).Take(3).ToString()).IsEqualTo("MATCH (o:Object) RETURN o ORDER BY o.name SKIP $p0 LIMIT $p1");

        var inRoom3 = new List<Obj>();
        foreach (var o in conn.Nodes<Obj>().Where(o => o.Loc == 3))   // synchronous: the engine is in-process, see below
        {
            inRoom3.Add(o);
        }

        await Assert.That(inRoom3.Select(o => o.Dbref).Order()).IsEquivalentTo([2L, 7L, 12L, 17L]);
    });

    [Test]
    public Task PredicateWhitelist() => MushDatabase.Check(async conn =>
    {
        var names = new[] { "obj1", "obj2" };
        var some = await conn.Nodes<Obj>()
            .Where(o => (o.Name.StartsWith("obj1") && o.Name.Length == 5) || names.Contains(o.Name) || o.Loc == null)
            .OrderBy(o => o.Dbref)
            .Select(o => o.Dbref)
            .ToListAsync();
        // MATCH (o:Object) WHERE o.name STARTS WITH $p0 AND size(o.name) = $p1 OR o.name IN [$p2, $p3] OR o.loc IS NULL
        // RETURN o.dbref AS Dbref ORDER BY o.dbref
        await Assert.That(some).IsEquivalentTo([1L, 2L, 10L, 11L, 12L, 13L, 14L, 15L, 16L, 17L, 18L, 19L]);
        await Assert.That(conn.Nodes<Obj>()
            .Where(o => (o.Name.StartsWith("obj1") && o.Name.Length == 5) || names.Contains(o.Name) || o.Loc == null)
            .OrderBy(o => o.Dbref).Select(o => o.Dbref).ToString())
            .IsEqualTo("MATCH (o:Object) WHERE o.name STARTS WITH $p0 AND size(o.name) = $p1 OR o.name IN [$p2, $p3] OR o.loc IS NULL RETURN o.dbref AS Dbref ORDER BY o.dbref");
    });

    [Test]
    public Task GraphSteps() => MushDatabase.Check(async conn =>
    {
        // one attribute of one object
        var value = await conn.Nodes<Obj>()
            .Where(o => o.Dbref == 5)
            .Out<Obj, Has, Attr>()
            .Where(p => p.Target.Aname == "A3")
            .Select(p => p.Target.Aval)
            .FirstOrDefaultAsync();
        // MATCH (n0:Object)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.aname = $p1 RETURN n1.aval AS Aval LIMIT $p2
        await Assert.That(value).IsEqualTo("v5.3");
        await Assert.That(Cypher(conn.Nodes<Obj>().Where(o => o.Dbref == 5).Out<Obj, Has, Attr>().Where(p => p.Target.Aname == "A3").Select(p => p.Target.Aval), q => q.FirstOrDefault()))
            .IsEqualTo("MATCH (n0:Object)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.aname = $p1 RETURN n1.aval AS Aval LIMIT $p2");

        // contents of a room: the objects whose Located points at it
        var contents = await conn.Nodes<Obj>()
            .Where(room => room.Dbref == 3)
            .In<Obj, Located, Obj>()
            .OrderBy(p => p.Target.Dbref)
            .Select(p => p.Target.Name)
            .ToListAsync();
        // MATCH (n0:Object)<-[:Located]-(n1:Object) WHERE n0.dbref = $p0 RETURN n1.name AS Name ORDER BY n1.dbref
        await Assert.That(contents).IsEquivalentTo(["obj2", "obj7", "obj12", "obj17"]);
        await Assert.That(conn.Nodes<Obj>().Where(room => room.Dbref == 3).In<Obj, Located, Obj>().OrderBy(p => p.Target.Dbref).Select(p => p.Target.Name).ToString())
            .IsEqualTo("MATCH (n0:Object)<-[:Located]-(n1:Object) WHERE n0.dbref = $p0 RETURN n1.name AS Name ORDER BY n1.dbref");

        // the relationship's own properties
        var recent = await conn.Nodes<Obj>()
            .Where(o => o.Dbref == 5)
            .OutWithRel<Obj, Has, Attr>()
            .Where(p => p.Rel.Since > 8)
            .Select(p => new { p.Target.Aname, p.Rel.Since })
            .ToListAsync();
        // MATCH (n0:Object)-[r0:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND r0.since > $p1 RETURN n1.aname AS Aname, r0.since AS Since
        await Assert.That(recent.Select(r => (r.Aname, r.Since)).Order()).IsEquivalentTo([("A10", 10L), ("A9", 9L)]);
        await Assert.That(conn.Nodes<Obj>().Where(o => o.Dbref == 5).OutWithRel<Obj, Has, Attr>().Where(p => p.Rel.Since > 8).Select(p => new { p.Target.Aname, p.Rel.Since }).ToString())
            .IsEqualTo("MATCH (n0:Object)-[r0:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND r0.since > $p1 RETURN n1.aname AS Aname, r0.since AS Since");

        // variable length: everything one or two Located hops away
        var nearby = await conn.Nodes<Obj>()
            .Where(o => o.Dbref == 1)
            .Out<Obj, Located, Obj>(1, 2)
            .Select(p => p.Target.Dbref)
            .ToListAsync();
        // MATCH (n0:Object)-[:Located*1..2]->(n1:Object) WHERE n0.dbref = $p0 RETURN n1.dbref AS Dbref
        await Assert.That(nearby.Order()).IsEquivalentTo([2L, 3L]);
        await Assert.That(conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>(1, 2).Select(p => p.Target.Dbref).ToString())
            .IsEqualTo("MATCH (n0:Object)-[:Located*1..2]->(n1:Object) WHERE n0.dbref = $p0 RETURN n1.dbref AS Dbref");

        // keep the objects that have an attribute named A10, without projecting it
        var described = await conn.Nodes<Obj>()
            .WhereExists<Obj, Has, Attr>(a => a.Aname == "A10")
            .CountAsync();
        // MATCH (n:Object) WHERE EXISTS { MATCH (n)-[:Has]->(a:Attr) WHERE a.aname = $p0 } RETURN count(*) AS Count
        await Assert.That(described).IsEqualTo(20L);
        await Assert.That(Cypher(conn.Nodes<Obj>().WhereExists<Obj, Has, Attr>(a => a.Aname == "A10"), q => q.LongCount()))
            .IsEqualTo("MATCH (n:Object) WHERE EXISTS { MATCH (n)-[:Has]->(a:Attr) WHERE a.aname = $p0 } RETURN count(*) AS Count");

        // no Select: the tuple itself, each item materialized from its NODE or REL value
        var pairs = await conn.Nodes<Obj>().Where(o => o.Dbref == 5).In<Obj, Located, Obj>().ToListAsync();   // List<(Obj Source, Obj Target)>
        await Assert.That(pairs.Select(p => (p.Source.Dbref, p.Target.Dbref)).Order()).IsEquivalentTo([(5L, 4L), (5L, 9L), (5L, 14L), (5L, 19L)]);

        var twoHops = await conn.Nodes<Obj>().Where(o => o.Dbref == 1).Out<Obj, Located, Obj>().Out<(Obj Source, Obj Target), Located, Obj>()
            .Select(p => new { Middle = p.Source.Target.Dbref, End = p.Target.Dbref }).SingleAsync();
        await Assert.That((twoHops.Middle, twoHops.End)).IsEqualTo((2L, 3L));
    });

    [Test]
    public Task Aggregates() => MushDatabase.Check(async conn =>
    {
        var perRoom = await conn.Nodes<Obj>()
            .GroupBy(o => o.Loc)
            .Select(g => new { Room = g.Key, N = g.Count(), Highest = g.Max(x => x.Dbref) })
            .OrderByDescending(x => x.N).ThenBy(x => x.Room)
            .ToListAsync();
        // MATCH (o:Object) RETURN o.loc AS Room, cast(count(*), 'INT32') AS N, max(o.dbref) AS Highest ORDER BY N DESC, Room
        await Assert.That(perRoom.Select(x => (x.Room!.Value, x.N, x.Highest))).IsEquivalentTo([(1L, 4, 20L), (2L, 4, 16L), (3L, 4, 17L), (4L, 4, 18L), (5L, 4, 19L)]);
        await Assert.That(conn.Nodes<Obj>().GroupBy(o => o.Loc).Select(g => new { Room = g.Key, N = g.Count(), Highest = g.Max(x => x.Dbref) }).OrderByDescending(x => x.N).ThenBy(x => x.Room).ToString())
            .IsEqualTo("MATCH (o:Object) RETURN o.loc AS Room, cast(count(*), 'INT32') AS N, max(o.dbref) AS Highest ORDER BY N DESC, Room");
    });

    [Test]
    public Task TerminalsAndTheAsyncBoundary() => MushDatabase.Check(async conn =>
    {
        var total = await conn.Nodes<Obj>().CountAsync();                                  // RETURN count(*)
        var hasAttributes = await conn.Nodes<Attr>().AnyAsync();
        var first = await conn.Nodes<Obj>().Where(o => o.Dbref == 1).SingleAsync();        // LIMIT 2, then checked
        await Assert.That((total, hasAttributes, first)).IsEqualTo((20L, true, new Obj(1, "obj1", 2)));

        var seen = new List<long>();
        await foreach (var o in conn.Nodes<Obj>().OrderBy(o => o.Dbref).AsAsyncEnumerable())
        {
            if (o.Dbref == 2) break;   // the underlying result is released here, as Select<T> releases its own
            seen.Add(o.Dbref);
        }

        await Assert.That(seen).IsEquivalentTo([1L]);

        var endingInZero = await conn.Nodes<Obj>().AsAsyncEnumerable()
            .Where(o => o.Name.EndsWith('0'))    // client-side: a Func, not an Expression
            .CountAsync();
        await Assert.That(endingInZero).IsEqualTo(2);

        await Assert.That(conn.Nodes<Obj>().Where(o => o.Loc == 1).ToList().Count).IsEqualTo(4);
        await Assert.That(conn.Nodes<Obj>().Count()).IsEqualTo(20);
        var foreign = new[] { new Obj(1, "x", null) }.AsQueryable();
        await Assert.ThrowsAsync<InvalidOperationException>(() => foreign.ToListAsync());
    });

    [Test]
    public Task Refusals() => MushDatabase.Check(async conn =>
    {
        string? message = null;
        try
        {
            await conn.Nodes<Obj>().Where(o => o.Name.Trim() == "x").ToListAsync();
        }
        catch (NotSupportedException ex)
        {
            message = ex.Message;
        }

        await Assert.That(message).IsEqualTo(
            "Expression 'o.Name.Trim()' cannot be translated to Cypher: not a translatable value. Only the whitelist in docs/USAGE.md (LINQ) " +
            "translates, and nothing is evaluated on the client. For anything else, write the Cypher yourself with " +
            "LadybugConnection.Match<T>(pattern, parameters), which keeps the typed result.");

        await Assert.That(Refused(() => conn.Nodes<Flagged>().Where(o => o.IsRoom)))
            .Contains("a bare boolean is ambiguous under Cypher's three-valued NULL logic; write it as a comparison, 'o.IsRoom == true' or 'o.IsRoom == false'");
        await Assert.That(Refused(() => conn.Nodes<Obj>().Take(5).Where(o => o.Loc == 2)))
            .Contains("Where after Take would apply to the rows Take already cut, which Cypher's clause order cannot express; put Where first");
        await Assert.That(Refused(() => conn.Nodes<Obj>().Select(o => o.Name).Count()))
            .Contains("Count after Select would need a WITH stage; count before projecting, or write the Cypher");
        await Assert.That(Refused(() => conn.Nodes<Obj>().Select(o => new { Node = o })))
            .Contains("a whole node inside a projection has no column to map to; project its properties, or return the node alone");
        await Assert.That(Refused(() => conn.Nodes<Obj>().GroupBy(o => o.Loc).Select(g => g.LongCount())))
            .Contains("a projection after GroupBy must include g.Key, or the aggregate would run over every row rather than per group");
        var reversed = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Nodes<Attr>().Out<Attr, Has, Obj>().ToListAsync());
        await Assert.That(reversed!.Message).IsEqualTo(
            "Has connects 'Object' to 'Attr', but Out<Attr, Has, Obj> needs a relationship from 'Attr' to 'Object'. The direction is reversed: use In instead.");
    });

    private static string Refused<T>(Func<IQueryable<T>> query) =>
        Assert.Throws<NotSupportedException>(() => query().ToString())!.Message;

    private static string Refused(Func<int> terminal) =>
        Assert.Throws<NotSupportedException>(() => terminal())!.Message;

    [Test]
    public Task TheEscapeHatch() => MushDatabase.Check(async conn =>
    {
        var room = 3L;
        var exits = await conn.Match<Obj>("(r:Object {dbref: $room})<-[:Located]-(n:Object)", new { room })
            .OrderBy(n => n.Dbref)
            .Select(n => new { n.Dbref, n.Name })
            .ToListAsync();
        // MATCH (r:Object {dbref: $room})<-[:Located]-(n:Object) RETURN n.dbref AS Dbref, n.name AS Name ORDER BY n.dbref
        await Assert.That(exits.Select(e => (e.Dbref, e.Name))).IsEquivalentTo([(2L, "obj2"), (7L, "obj7"), (12L, "obj12"), (17L, "obj17")]);
        await Assert.That(conn.Match<Obj>("(r:Object {dbref: $room})<-[:Located]-(n:Object)", new { room }).OrderBy(n => n.Dbref).Select(n => new { n.Dbref, n.Name }).ToString())
            .IsEqualTo("MATCH (r:Object {dbref: $room})<-[:Located]-(n:Object) RETURN n.dbref AS Dbref, n.name AS Name ORDER BY n.dbref");

        var late = await conn.Match<Obj>("(o:Object {dbref: $d})", new { d = 5L }, variable: "o")
            .OutWithRel<Obj, Has, Attr>()
            .Where(p => p.Rel.Since == 10)
            .Select(p => p.Target.Aname)
            .SingleAsync();
        // MATCH (o:Object {dbref: $d}) MATCH (o)-[r0:Has]->(n1:Attr) WHERE r0.since = $p0 RETURN n1.aname AS Aname LIMIT $p1
        await Assert.That(late).IsEqualTo("A10");
        await Assert.That(Cypher(conn.Match<Obj>("(o:Object {dbref: $d})", new { d = 5L }, variable: "o").OutWithRel<Obj, Has, Attr>().Where(p => p.Rel.Since == 10).Select(p => p.Target.Aname), q => q.Single()))
            .IsEqualTo("MATCH (o:Object {dbref: $d}) MATCH (o)-[r0:Has]->(n1:Attr) WHERE r0.since = $p0 RETURN n1.aname AS Aname LIMIT $p1");

        await Assert.That(() => conn.Match<Obj>("(o:Object)")).Throws<ArgumentException>();
    });
}
