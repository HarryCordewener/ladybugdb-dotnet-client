using LadybugDb.Client.Cypher;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>
/// The write clauses (<c>CREATE</c>, <c>MERGE</c>, <c>SET</c>, <c>DELETE</c>) and the DDL renderer,
/// executed against the real engine. The unit suite's <c>RendererWriteTests</c> pins the text; this
/// class proves the engine accepts it and does what the text says, reading the graph back after
/// every write.
/// </summary>
public class RendererWriteEngineTests
{
    private static readonly NodePattern O = CypherDsl.Node("Object", "o");

    private static async Task<(LadybugDatabase Db, LadybugConnection Connection)> OpenWithSchema(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await conn.ExecuteAsync(Ddl.CreateNodeTable("Object",
            [new ColumnDefinition("dbref", "INT64"), new ColumnDefinition("name", "STRING"), new ColumnDefinition("loc", "INT64")], "dbref"));
        await conn.ExecuteAsync(Ddl.CreateNodeTable("Attr",
            [new ColumnDefinition("akey", "STRING"), new ColumnDefinition("aname", "STRING"), new ColumnDefinition("aval", "STRING")], "akey"));
        await conn.ExecuteAsync(Ddl.CreateRelTable("Located", "Object", "Object", []));
        await conn.ExecuteAsync(Ddl.CreateRelTable("Has", "Object", "Attr", [new ColumnDefinition("since", "INT64")]));
        return (db, conn);
    }

    private static async Task Execute(LadybugConnection conn, QueryBuilder query)
    {
        var text = query.Render();
        if (text.Parameters.Count == 0) await conn.ExecuteAsync(text.Cypher);
        else await conn.ExecuteAsync(text.Cypher, text.Parameters);
    }

    private static async Task<List<LadybugRow>> Rows(LadybugConnection conn, string cypher)
    {
        await using var result = await conn.QueryAsync(cypher);
        var rows = new List<LadybugRow>();
        await foreach (var row in result) rows.Add(row);
        return rows;
    }

    private static async Task Check(Func<LadybugConnection, Task> body)
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithSchema(path);
            using var _db = db;
            await using var _conn = conn;
            await body(conn);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The DDL that <see cref="OpenWithSchema"/> runs is itself the test: a fresh database accepted every rendered statement, and the catalog lists the tables.</summary>
    [Test]
    public Task Ddl_CreatesTables_ThatTheCatalogLists() => Check(async conn =>
    {
        var rows = await Rows(conn, "CALL show_tables() RETURN *");
        await Assert.That(rows.Select(r => r.GetString("name")).Order()).IsEquivalentTo(["Attr", "Has", "Located", "Object"]);
    });

    [Test]
    public Task Ddl_BacktickedNames_AndDropTable() => Check(async conn =>
    {
        await conn.ExecuteAsync(Ddl.CreateNodeTable("my table", [new ColumnDefinition("my prop", "STRING")], "my prop"));
        await conn.ExecuteAsync("CREATE (:`my table` {`my prop`: 'x'})");
        await conn.ExecuteAsync(Ddl.DropTable("my table"));
        var rows = await Rows(conn, "CALL show_tables() RETURN *");
        await Assert.That(rows.Select(r => r.GetString("name"))).DoesNotContain("my table");
    });

    [Test]
    public Task Create_WithPropertyParameters() => Check(async conn =>
    {
        await Execute(conn, CypherDsl.Create(O.With("dbref", CypherDsl.Literal(10L)).With("name", CypherDsl.Literal("ten"))));
        var rows = await Rows(conn, "MATCH (o:Object) RETURN o.dbref AS D, o.name AS N");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That((rows[0].GetInt64("D"), rows[0].GetString("N"))).IsEqualTo((10L, "ten"));
    });

    [Test]
    public Task Create_Relationship_WithProperties() => Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (:Object {dbref: 1, name: 'one'}), (:Attr {akey: '1/DESC', aname: 'DESC', aval: 'hi'})");
        await Execute(conn, CypherDsl.Match(CypherDsl.Node("Object", "a"), CypherDsl.Node("Attr", "b"))
            .Where(CypherDsl.Prop("a", "dbref").Eq(CypherDsl.Literal(1L)).And(CypherDsl.Prop("b", "akey").Eq(CypherDsl.Literal("1/DESC"))))
            .Create(CypherDsl.NodeRef("a").ToPatternPath().Extend(
                new RelPattern(null, "Has", Direction.Outgoing).With("since", CypherDsl.Literal(7L)),
                CypherDsl.NodeRef("b"))));
        var rows = await Rows(conn, "MATCH (:Object)-[r:Has]->(:Attr) RETURN r.since AS S");
        await Assert.That(rows.Select(r => r.GetInt64("S"))).IsEquivalentTo([7L]);
    });

    [Test]
    public Task Set_OneAssignment() => Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (:Object {dbref: 10, name: 'ten'})");
        await Execute(conn, CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L))).Set("o", "name", CypherDsl.Literal("TEN")));
        var rows = await Rows(conn, "MATCH (o:Object) RETURN o.name AS N");
        await Assert.That(rows.Select(r => r.GetString("N"))).IsEquivalentTo(["TEN"]);
    });

    /// <summary>Also pins that <c>SET x = NULL</c> is how this engine clears a property - it has no <c>REMOVE</c>.</summary>
    [Test]
    public Task Set_ConsecutiveAssignments_AndNullClearsAProperty() => Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (:Object {dbref: 10, name: 'ten', loc: 3})");
        await Execute(conn, CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L)))
            .Set("o", "name", CypherDsl.Literal("TEN"))
            .Set(O.Prop("loc"), CypherDsl.Literal(null)));
        var rows = await Rows(conn, "MATCH (o:Object) RETURN o.name AS N, o.loc AS L");
        await Assert.That(rows[0].GetString("N")).IsEqualTo("TEN");
        await Assert.That(rows[0]["L"].IsNull).IsTrue();
    });

    [Test]
    public Task DetachDelete_RemovesTheNodeAndItsRelationships() => Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (a:Object {dbref: 10, name: 'ten'}), (b:Object {dbref: 11, name: 'eleven'}), (a)-[:Located]->(b)");
        await Execute(conn, CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L))).DetachDelete("o"));
        var objects = await Rows(conn, "MATCH (o:Object) RETURN o.dbref AS D");
        await Assert.That(objects.Select(r => r.GetInt64("D"))).IsEquivalentTo([11L]);
        var rels = await Rows(conn, "MATCH ()-[r:Located]->() RETURN count(*) AS N");
        await Assert.That(rels[0].GetInt64("N")).IsEqualTo(0L);
    });

    [Test]
    public Task Delete_Relationship() => Check(async conn =>
    {
        await conn.ExecuteAsync("CREATE (a:Object {dbref: 10, name: 'ten'}), (b:Object {dbref: 11, name: 'eleven'}), (a)-[:Located]->(b)");
        await Execute(conn, CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), alias: "r")).Delete("r"));
        var rels = await Rows(conn, "MATCH ()-[r:Located]->() RETURN count(*) AS N");
        await Assert.That(rels[0].GetInt64("N")).IsEqualTo(0L);
        var objects = await Rows(conn, "MATCH (o:Object) RETURN count(*) AS N");
        await Assert.That(objects[0].GetInt64("N")).IsEqualTo(2L);
    });

    [Test]
    public Task Merge_CreatesOnce_ThenMatches() => Check(async conn =>
    {
        var merge = CypherDsl.Merge(O.With("dbref", CypherDsl.Literal(11L))).Return(O.Prop("dbref").As("D"));
        for (var i = 0; i < 2; i++)
        {
            var text = merge.Render();
            await using var result = await conn.QueryAsync(text.Cypher, text.Parameters);
            var rows = new List<LadybugRow>();
            await foreach (var row in result) rows.Add(row);
            await Assert.That(rows.Select(r => r.GetInt64("D"))).IsEquivalentTo([11L]);
        }

        var count = await Rows(conn, "MATCH (o:Object) RETURN count(*) AS N");
        await Assert.That(count[0].GetInt64("N")).IsEqualTo(1L);
    });
}
