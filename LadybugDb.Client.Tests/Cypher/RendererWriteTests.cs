using LadybugDb.Client.Cypher;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// Golden-text tests for the write clauses (<c>CREATE</c>, <c>MERGE</c>, <c>SET</c>, <c>DELETE</c>)
/// and the DDL renderer. Executed against the engine by <c>RendererWriteEngineTests</c>.
/// </summary>
public class RendererWriteTests
{
    private static readonly NodePattern O = CypherDsl.Node("Object", "o");

    [Test]
    public async Task Create_RendersPropertiesAsParameters()
    {
        var q = CypherDsl.Create(O.With("dbref", CypherDsl.Literal(10L)).With("name", CypherDsl.Literal("ten"))).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("CREATE (o:Object {dbref: $p0, name: $p1})");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(10L);
        await Assert.That(text.Parameters["p1"]).IsEqualTo("ten");
    }

    [Test]
    public async Task Create_Relationship_WithProperties()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a"), CypherDsl.Node("Attr", "b"))
            .Where(CypherDsl.Prop("a", "dbref").Eq(CypherDsl.Literal(1L)).And(CypherDsl.Prop("b", "akey").Eq(CypherDsl.Literal("1/DESC"))))
            .Create(CypherDsl.NodeRef("a").ToPatternPath().Extend(
                new RelPattern(null, "Has", Direction.Outgoing).With("since", CypherDsl.Literal(7L)),
                CypherDsl.NodeRef("b")))
            .Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (a:Object), (b:Attr) WHERE a.dbref = $p0 AND b.akey = $p1 CREATE (a)-[:Has {since: $p2}]->(b)");
    }

    [Test]
    public async Task Set_RendersOneAssignment()
    {
        var q = CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L)))
            .Set("o", "name", CypherDsl.Literal("TEN")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 SET o.name = $p1");
        await Assert.That(text.Parameters["p1"]).IsEqualTo("TEN");
    }

    [Test]
    public async Task Set_ConsecutiveAssignments_MergeIntoOneClause()
    {
        var q = CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L)))
            .Set("o", "name", CypherDsl.Literal("TEN"))
            .Set(O.Prop("loc"), CypherDsl.Literal(null)).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.dbref = $p0 SET o.name = $p1, o.loc = NULL");
    }

    [Test]
    public async Task DetachDelete_Renders()
    {
        var q = CypherDsl.Match(O).Where(O.Prop("dbref").Eq(CypherDsl.Literal(10L))).DetachDelete("o").Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 DETACH DELETE o");
    }

    [Test]
    public async Task Delete_Renders()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), alias: "r"))
            .Delete("r").Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[r:Located]->(b:Object) DELETE r");
    }

    [Test]
    public async Task Merge_Renders()
    {
        var q = CypherDsl.Merge(O.With("dbref", CypherDsl.Literal(11L))).Return(O.Prop("dbref").As("D")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MERGE (o:Object {dbref: $p0}) RETURN o.dbref AS D");
    }

    [Test]
    public async Task Ddl_CreateNodeTable()
    {
        var ddl = Ddl.CreateNodeTable("Object", [new ColumnDefinition("dbref", "INT64"), new ColumnDefinition("name", "STRING")], "dbref");
        await Assert.That(ddl).IsEqualTo("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
    }

    [Test]
    public async Task Ddl_CreateRelTable_WithAndWithoutColumns()
    {
        await Assert.That(Ddl.CreateRelTable("Located", "Object", "Object", []))
            .IsEqualTo("CREATE REL TABLE Located(FROM Object TO Object)");
        await Assert.That(Ddl.CreateRelTable("Has", "Object", "Attr", [new ColumnDefinition("since", "INT64")]))
            .IsEqualTo("CREATE REL TABLE Has(FROM Object TO Attr, since INT64)");
    }

    [Test]
    public async Task Ddl_BackticksIdentifiers_AndRefusesAKeyThatIsNotAColumn()
    {
        await Assert.That(Ddl.CreateNodeTable("my table", [new ColumnDefinition("my prop", "STRING")], "my prop"))
            .IsEqualTo("CREATE NODE TABLE `my table`(`my prop` STRING, PRIMARY KEY(`my prop`))");
        await Assert.That(Ddl.DropTable("my table")).IsEqualTo("DROP TABLE `my table`");

        var ex = Assert.Throws<ArgumentException>(() => Ddl.CreateNodeTable("T", [new ColumnDefinition("a", "INT64")], "b"));
        await Assert.That(ex!.Message).Contains("'b'");
    }

    [Test]
    public async Task Ddl_RefusesAColumnTypeThatIsNotATypeName()
    {
        var ex = Assert.Throws<ArgumentException>(() => Ddl.CreateNodeTable("T", [new ColumnDefinition("a", "INT64, PRIMARY KEY(a)); DROP TABLE T")], "a"));
        await Assert.That(ex!.Message).Contains("'a'");
    }
}
