using LadybugDb.Client.Cypher;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>
/// Every read clause and expression form the renderer produces, executed against the real engine.
/// The unit suite's <c>RendererTests</c> pins the exact text; this class proves that text is the
/// engine's dialect, so a dialect change on an engine bump fails here rather than in a consumer.
/// Each test builds the same query the golden test does and asserts the rows the seeded graph must
/// produce.
/// </summary>
/// <remarks>
/// The graph: objects 1..5 (<c>name</c> <c>n1</c>..<c>n5</c>, <c>loc</c> = dbref mod 2, <c>flag</c>
/// true for even dbrefs), <c>Located</c> 1-&gt;2 and 2-&gt;3, one attribute <c>1/DESC</c> attached
/// to object 1 through <c>Has {since: 7}</c>, and a table whose name and column need backticks.
/// </remarks>
public class RendererEngineTests
{
    private static readonly NodePattern O = CypherDsl.Node("Object", "o");

    private static async Task<(LadybugDatabase Db, LadybugConnection Connection)> OpenSeeded(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT64, flag BOOL, PRIMARY KEY(dbref))");
        await conn.ExecuteAsync("CREATE NODE TABLE Attr(akey STRING, aname STRING, aval STRING, PRIMARY KEY(akey))");
        await conn.ExecuteAsync("CREATE REL TABLE Located(FROM Object TO Object)");
        await conn.ExecuteAsync("CREATE REL TABLE Has(FROM Object TO Attr, since INT64)");
        await conn.ExecuteAsync("CREATE NODE TABLE `my table`(id INT64, `my prop` STRING, PRIMARY KEY(id))");
        for (var i = 1; i <= 5; i++)
        {
            await conn.ExecuteAsync(
                "CREATE (:Object {dbref: $d, name: $n, loc: $l, flag: $f})",
                new { d = (long)i, n = $"n{i}", l = (long)(i % 2), f = i % 2 == 0 });
        }

        await conn.ExecuteAsync("MATCH (a:Object {dbref: 1}), (b:Object {dbref: 2}) CREATE (a)-[:Located]->(b)");
        await conn.ExecuteAsync("MATCH (a:Object {dbref: 2}), (b:Object {dbref: 3}) CREATE (a)-[:Located]->(b)");
        await conn.ExecuteAsync("CREATE (:Attr {akey: '1/DESC', aname: 'DESC', aval: 'hello'})");
        await conn.ExecuteAsync("MATCH (a:Object {dbref: 1}), (b:Attr {akey: '1/DESC'}) CREATE (a)-[:Has {since: 7}]->(b)");
        await conn.ExecuteAsync("CREATE (:`my table` {id: 1, `my prop`: 'spaced'})");
        return (db, conn);
    }

    /// <summary>Runs a built query through the parameter-object overload exactly as a caller would, materializing the rows.</summary>
    private static async Task<List<LadybugRow>> Run(LadybugConnection conn, QueryBuilder query)
    {
        var text = query.Render();
        await using var result = text.Parameters.Count == 0
            ? await conn.QueryAsync(text.Cypher)
            : await conn.QueryAsync(text.Cypher, text.Parameters);
        var rows = new List<LadybugRow>();
        await foreach (var row in result) rows.Add(row);
        return rows;
    }

    private static async Task Check(Func<LadybugConnection, Task> body)
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenSeeded(path);
            using var _db = db;
            await using var _conn = conn;
            await body(conn);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public Task MatchWhereReturn_WithParameter() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(2L))).Return(O.Prop("name").As("Name")));
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].GetString("Name")).IsEqualTo("n2");
    });

    [Test]
    public Task Traversal_WithHops() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), minHops: 1, maxHops: 3))
            .Where(CypherDsl.Prop("a", "dbref").Eq(CypherDsl.Literal(1L)))
            .Return(CypherDsl.Prop("b", "dbref").As("Dbref")).OrderBy(CypherDsl.Variable("Dbref").Asc()));
        await Assert.That(rows.Select(r => r.GetInt64("Dbref"))).IsEquivalentTo([2L, 3L]);
    });

    [Test]
    public Task BacktickedIdentifiers() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("my table", "o")).Return(CypherDsl.Prop("o", "my prop").As("my name")));
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].GetString("my name")).IsEqualTo("spaced");
    });

    [Test]
    public Task With_AggregateAndVariables() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .With(O.Prop("loc").As("L"), CypherDsl.CountAll().As("N"))
            .Return(CypherDsl.Variable("L"), CypherDsl.Variable("N"))
            .OrderBy(CypherDsl.Variable("L").Asc()));
        await Assert.That(rows.Select(r => (r.GetInt64("L"), r.GetInt64("N")))).IsEquivalentTo([(0L, 2L), (1L, 3L)]);
    });

    [Test]
    public Task OrderByDescending_SkipAndLimit() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).Return(O.Prop("dbref").As("D")).OrderBy(O.Prop("dbref").Desc()).Skip(1L).Limit(2L));
        await Assert.That(rows.Select(r => r.GetInt64("D"))).IsEquivalentTo([4L, 3L]);
    });

    [Test]
    public Task Distinct() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).ReturnDistinct(O.Prop("loc").As("L")));
        await Assert.That(rows.Select(r => r.GetInt64("L")).Order()).IsEquivalentTo([0L, 1L]);
    });

    [Test]
    public Task IsNull_AndIsNotNull() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .Where(O.Prop("name").IsNotNull().And(O.Prop("loc").IsNull()))
            .Return(CypherDsl.CountAll().As("N")));
        await Assert.That(rows[0].GetInt64("N")).IsEqualTo(0L);
    });

    [Test]
    public Task In_ListOfParameters_AndEmptyList() => Check(async conn =>
    {
        var some = await Run(conn, CypherDsl.Match(O).Where(O.Prop("dbref").In(CypherDsl.Literal(1L), CypherDsl.Literal(3L))).Return(O.Prop("dbref").As("D")));
        await Assert.That(some.Select(r => r.GetInt64("D")).Order()).IsEquivalentTo([1L, 3L]);

        var none = await Run(conn, CypherDsl.Match(O).Where(O.Prop("dbref").In()).Return(O.Prop("dbref").As("D")));
        await Assert.That(none.Count).IsEqualTo(0);
    });

    [Test]
    public Task StringOperators() => Check(async conn =>
    {
        var name = O.Prop("name");
        var rows = await Run(conn, CypherDsl.Match(O)
            .Where(name.StartsWith(CypherDsl.Literal("n")).And(name.Contains(CypherDsl.Literal("1"))).And(name.EndsWith(CypherDsl.Literal("1"))))
            .Return(CypherDsl.CountAll().As("N")));
        await Assert.That(rows[0].GetInt64("N")).IsEqualTo(1L);
    });

    [Test]
    public Task Functions_LabelSizeUpperLower() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .Where(CypherDsl.Func("label", O.Variable()).Eq(CypherDsl.Literal("Object")).And(CypherDsl.Func("size", O.Prop("name")).Eq(CypherDsl.Literal(2L))))
            .Return(CypherDsl.Func("upper", O.Prop("name")).As("U"), CypherDsl.Func("lower", O.Prop("name")).As("L"))
            .OrderBy(CypherDsl.Variable("U").Asc()).Limit(1L));
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].GetString("U")).IsEqualTo("N1");
        await Assert.That(rows[0].GetString("L")).IsEqualTo("n1");
    });

    /// <summary><c>size()</c> is INT64 in the engine; the cast is what lets a C# <c>int</c> receive it.</summary>
    [Test]
    public Task Cast_ToInt32() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).Return(CypherDsl.Func("size", O.Prop("name")).Cast("INT32").As("L")).Limit(1L));
        await Assert.That(rows[0]["L"].Type).IsEqualTo(LadybugType.Int32);
        await Assert.That(rows[0].GetInt32("L")).IsEqualTo(2);
    });

    [Test]
    public Task Exists_Subquery() => Check(async conn =>
    {
        var sub = CypherDsl.Match(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x"))).Where(CypherDsl.Prop("x", "dbref").Eq(CypherDsl.Literal(2L)));
        var rows = await Run(conn, CypherDsl.Match(O).Where(CypherDsl.Exists(sub)).Return(O.Prop("dbref").As("D")));
        await Assert.That(rows.Select(r => r.GetInt64("D"))).IsEquivalentTo([1L]);
    });

    [Test]
    public Task Count_Subquery() => Check(async conn =>
    {
        var sub = CypherDsl.Match(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x")));
        var rows = await Run(conn, CypherDsl.Match(O).Return(O.Prop("dbref").As("D"), CypherDsl.CountSubquery(sub).As("N")).OrderBy(O.Prop("dbref").Asc()));
        await Assert.That(rows.Select(r => r.GetInt64("N"))).IsEquivalentTo([1L, 1L, 0L, 0L, 0L]);
    });

    /// <summary>
    /// Also pins an engine fact the aliases here are chosen around: names resolve case-insensitively,
    /// so <c>RETURN a.dbref AS A ... ORDER BY A</c> is refused with "Cannot order by a. Order by NODE
    /// is not supported" - the <c>A</c> found the node variable <c>a</c>, not the column. A projected
    /// alias must therefore never differ from a pattern variable only by case.
    /// </summary>
    [Test]
    public Task IncomingDirection() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "a").RelFrom("Located", CypherDsl.Node("Object", "b")))
            .Return(CypherDsl.Prop("a", "dbref").As("Src"), CypherDsl.Prop("b", "dbref").As("Dst")).OrderBy(CypherDsl.Variable("Src").Asc()));
        await Assert.That(rows.Select(r => (r.GetInt64("Src"), r.GetInt64("Dst")))).IsEquivalentTo([(2L, 1L), (3L, 2L)]);
    });

    [Test]
    public Task Undirected() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "a").Rel("Located", CypherDsl.Node("Object", "b")))
            .Return(CypherDsl.Prop("a", "dbref").As("A"), CypherDsl.Prop("b", "dbref").As("B")));
        await Assert.That(rows.Count).IsEqualTo(4);
    });

    [Test]
    public Task RelationshipAlias_AndProperty() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Has", CypherDsl.Node("Attr", "b"), alias: "r")).Return(CypherDsl.Prop("r", "since").As("S")));
        await Assert.That(rows.Select(r => r.GetInt64("S"))).IsEquivalentTo([7L]);
    });

    [Test]
    public Task WholeNode() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).Return(O.Variable()).OrderBy(O.Prop("dbref").Asc()).Limit(1L));
        await Assert.That(rows.Count).IsEqualTo(1);
        var node = rows[0].GetValue(0).AsNode();
        await Assert.That(node.Label).IsEqualTo("Object");
        await Assert.That(node.Properties["name"].AsString()).IsEqualTo("n1");
    });

    [Test]
    public Task NamedParameter_ReusedWithSameValue() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .Where(O.Prop("dbref").Eq(CypherDsl.Param("v", 1L)).Or(O.Prop("loc").Eq(CypherDsl.Param("v", 1L))))
            .Return(CypherDsl.CountAll().As("N")));
        await Assert.That(rows[0].GetInt64("N")).IsEqualTo(3L);
    });

    [Test]
    public Task Precedence_OrUnderAnd_AndNot() => Check(async conn =>
    {
        var a = O.Prop("dbref").Eq(CypherDsl.Literal(1L));
        var b = O.Prop("dbref").Eq(CypherDsl.Literal(2L));
        var c = O.Prop("flag").Eq(CypherDsl.Literal(true));
        var rows = await Run(conn, CypherDsl.Match(O).Where(a.Or(b).And(c.Not())).Return(CypherDsl.CountAll().As("N")));
        await Assert.That(rows[0].GetInt64("N")).IsEqualTo(1L);
    });

    [Test]
    public Task Arithmetic_AndNegation() => Check(async conn =>
    {
        var d = O.Prop("dbref");
        var rows = await Run(conn, CypherDsl.Match(O).Where(d.Negate().Lt(CypherDsl.Literal(-1L)))
            .Return(d.Plus(CypherDsl.Literal(1L)).As("S"), d.Times(CypherDsl.Literal(2L)).As("M"), d.Modulo(CypherDsl.Literal(2L)).As("R"))
            .OrderBy(CypherDsl.Variable("S").Asc()).Limit(1L));
        await Assert.That((rows[0].GetInt64("S"), rows[0].GetInt64("M"), rows[0].GetInt64("R"))).IsEqualTo((3L, 4L, 0L));
    });

    [Test]
    public Task Case() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .Return(O.Prop("dbref").As("D"), CypherDsl.Case([CypherDsl.When(O.Prop("flag").Eq(CypherDsl.Literal(true)), CypherDsl.Literal("y"))], @else: CypherDsl.Literal("n")).As("C"))
            .OrderBy(CypherDsl.Variable("D").Asc()));
        await Assert.That(rows.Select(r => r.GetString("C"))).IsEquivalentTo(["n", "y", "n", "y", "n"]);
    });

    [Test]
    public Task Unwind() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Unwind(CypherDsl.List(CypherDsl.Literal(1L), CypherDsl.Literal(2L)), "x").Return(CypherDsl.Variable("x").As("X")));
        await Assert.That(rows.Select(r => r.GetInt64("X"))).IsEquivalentTo([1L, 2L]);
    });

    [Test]
    public Task OptionalMatch() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O)
            .OptionalMatch(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x")))
            .Return(O.Prop("dbref").As("D"), CypherDsl.Prop("x", "dbref").As("X"))
            .OrderBy(CypherDsl.Variable("D").Asc()));
        await Assert.That(rows.Count).IsEqualTo(5);
        await Assert.That(rows[4]["X"].IsNull).IsTrue();
    });

    [Test]
    public Task PatternProperties() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "o").With("dbref", CypherDsl.Literal(1L))).Return(CypherDsl.Prop("o", "name").As("N")));
        await Assert.That(rows.Select(r => r.GetString("N"))).IsEquivalentTo(["n1"]);
    });

    [Test]
    public Task CountDistinct_AndMultiplePaths() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(CypherDsl.Node("Object", "a"), CypherDsl.Node("Object", "b"))
            .Where(CypherDsl.Prop("a", "dbref").Lt(CypherDsl.Prop("b", "dbref")))
            .Return(CypherDsl.Count(CypherDsl.Prop("a", "loc"), distinct: true).As("N")));
        await Assert.That(rows[0].GetInt64("N")).IsEqualTo(2L);
    });

    [Test]
    public Task NullLiteral() => Check(async conn =>
    {
        var rows = await Run(conn, CypherDsl.Match(O).Return(CypherDsl.Literal(null).As("X")).Limit(1L));
        await Assert.That(rows[0]["X"].IsNull).IsTrue();
    });
}
