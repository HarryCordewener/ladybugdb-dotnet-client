using LadybugDb.Client.Cypher;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// Golden-text tests for <see cref="CypherRenderer"/> over the read clauses: one per clause and per
/// expression form, asserting the exact Cypher text and the parameters collected alongside it.
/// Every string asserted here is also executed against the real engine by the integration suite's
/// <c>RendererEngineTests</c>, so a dialect change fails there while a rendering regression fails
/// here.
/// </summary>
public class RendererTests
{
    private static readonly NodePattern O = CypherDsl.Node("Object", "o");

    [Test]
    public async Task MatchWhereReturn_RendersWithParameters()
    {
        var q = CypherDsl.Match(O).Where(CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Literal(42L)))
            .Return(CypherDsl.Prop("o", "name").As("Name")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(42L);
    }

    [Test]
    public async Task Traversal_RendersDirectionAndHops()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), minHops: 1, maxHops: 3))
            .Return(CypherDsl.Prop("b", "dbref").As("Dbref")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[:Located*1..3]->(b:Object) RETURN b.dbref AS Dbref");
    }

    [Test]
    public async Task Identifier_WithSpace_IsBackticked()
    {
        await Assert.That(Identifier.Render("my table")).IsEqualTo("`my table`");
        await Assert.That(Identifier.Render("a`b")).IsEqualTo("`a``b`");
    }

    [Test]
    public async Task Identifier_Plain_IsLeftAlone_AndBlank_IsRefused()
    {
        await Assert.That(Identifier.Render("dbref_2")).IsEqualTo("dbref_2");
        await Assert.That(Identifier.Render("_x")).IsEqualTo("_x");
        await Assert.That(Identifier.Render("2x")).IsEqualTo("`2x`");
        Assert.Throws<ArgumentException>(() => Identifier.Render(""));
        Assert.Throws<ArgumentException>(() => Identifier.Render("  "));
    }

    [Test]
    public async Task BacktickedIdentifiers_RenderInsidePatternsAndProjections()
    {
        var q = CypherDsl.Match(CypherDsl.Node("my table", "o"))
            .Return(CypherDsl.Prop("o", "my prop").As("my name")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:`my table`) RETURN o.`my prop` AS `my name`");
    }

    [Test]
    public async Task With_RendersAggregateAndVariables()
    {
        var q = CypherDsl.Match(O)
            .With(CypherDsl.Prop("o", "loc").As("L"), CypherDsl.CountAll().As("N"))
            .Return(CypherDsl.Variable("L"), CypherDsl.Variable("N"))
            .OrderBy(CypherDsl.Variable("L").Asc()).Build();
        await Assert.That(q.Render().Cypher)
            .IsEqualTo("MATCH (o:Object) WITH o.loc AS L, count(*) AS N RETURN L, N ORDER BY L");
    }

    [Test]
    public async Task OrderByDescending_SkipAndLimit_RenderAsParameters()
    {
        var q = CypherDsl.Match(O).Return(CypherDsl.Prop("o", "dbref").As("D"))
            .OrderBy(CypherDsl.Prop("o", "dbref").Desc()).Skip(1L).Limit(2L).Build();
        var text = q.Render();
        await Assert.That(text.Cypher)
            .IsEqualTo("MATCH (o:Object) RETURN o.dbref AS D ORDER BY o.dbref DESC SKIP $p0 LIMIT $p1");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(1L);
        await Assert.That(text.Parameters["p1"]).IsEqualTo(2L);
    }

    [Test]
    public async Task Distinct_RendersReturnDistinct()
    {
        var q = CypherDsl.Match(O).ReturnDistinct(CypherDsl.Prop("o", "loc").As("L")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:Object) RETURN DISTINCT o.loc AS L");
    }

    [Test]
    public async Task IsNull_AndIsNotNull_Render()
    {
        var q = CypherDsl.Match(O)
            .Where(CypherDsl.Prop("o", "name").IsNotNull().And(CypherDsl.Prop("o", "loc").IsNull()))
            .Return(CypherDsl.CountAll().As("N")).Build();
        await Assert.That(q.Render().Cypher)
            .IsEqualTo("MATCH (o:Object) WHERE o.name IS NOT NULL AND o.loc IS NULL RETURN count(*) AS N");
    }

    [Test]
    public async Task In_RendersOneParameterPerElement()
    {
        var q = CypherDsl.Match(O)
            .Where(CypherDsl.Prop("o", "dbref").In(CypherDsl.Literal(1L), CypherDsl.Literal(3L)))
            .Return(CypherDsl.Prop("o", "dbref").As("D")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref IN [$p0, $p1] RETURN o.dbref AS D");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(1L);
        await Assert.That(text.Parameters["p1"]).IsEqualTo(3L);
    }

    [Test]
    public async Task In_EmptyList_RendersEmptyBrackets()
    {
        var q = CypherDsl.Match(O).Where(CypherDsl.Prop("o", "dbref").In()).Return(CypherDsl.Prop("o", "dbref").As("D")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref IN [] RETURN o.dbref AS D");
    }

    [Test]
    public async Task StringOperators_Render()
    {
        var name = CypherDsl.Prop("o", "name");
        var q = CypherDsl.Match(O)
            .Where(name.StartsWith(CypherDsl.Literal("n")).And(name.Contains(CypherDsl.Literal("1"))).And(name.EndsWith(CypherDsl.Literal("1"))))
            .Return(CypherDsl.CountAll().As("N")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.name STARTS WITH $p0 AND o.name CONTAINS $p1 AND o.name ENDS WITH $p2 RETURN count(*) AS N");
        await Assert.That(text.Parameters.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Functions_RenderLabelSizeUpperLower()
    {
        var q = CypherDsl.Match(O)
            .Where(CypherDsl.Func("label", CypherDsl.Variable("o")).Eq(CypherDsl.Literal("Object"))
                .And(CypherDsl.Func("size", CypherDsl.Prop("o", "name")).Eq(CypherDsl.Literal(2L))))
            .Return(CypherDsl.Func("upper", CypherDsl.Prop("o", "name")).As("U"), CypherDsl.Func("lower", CypherDsl.Prop("o", "name")).As("L"))
            .Limit(1L).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE label(o) = $p0 AND size(o.name) = $p1 RETURN upper(o.name) AS U, lower(o.name) AS L LIMIT $p2");
    }

    [Test]
    public async Task Cast_RendersTheTypeQuoted_AndRefusesANonTypeName()
    {
        var q = CypherDsl.Match(O).Return(CypherDsl.Func("size", CypherDsl.Prop("o", "name")).Cast("INT32").As("L")).Limit(1L).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:Object) RETURN cast(size(o.name), 'INT32') AS L LIMIT $p0");

        var bad = CypherDsl.Match(O).Return(CypherDsl.Prop("o", "name").Cast("INT32') AS x, 1 AS y --").As("L")).Build();
        Assert.Throws<InvalidOperationException>(() => bad.Render());
    }

    [Test]
    public async Task VariableLength_WithoutUpperBound_IsRefused()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), minHops: 1))
            .Return(CypherDsl.Prop("b", "dbref").As("D")).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => q.Render());
        await Assert.That(ex!.Message).Contains("Located");
        await Assert.That(ex.Message).Contains("upper bound");
    }

    [Test]
    public async Task VariableLength_WithOnlyUpperBound_DefaultsMinToOne()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Located", CypherDsl.Node("Object", "b"), maxHops: 2))
            .Return(CypherDsl.Prop("b", "dbref").As("D")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[:Located*1..2]->(b:Object) RETURN b.dbref AS D");
    }

    [Test]
    public async Task Exists_RendersSubquery()
    {
        var sub = CypherDsl.Match(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x")))
            .Where(CypherDsl.Prop("x", "dbref").Eq(CypherDsl.Literal(2L)));
        var q = CypherDsl.Match(O).Where(CypherDsl.Exists(sub)).Return(CypherDsl.Prop("o", "dbref").As("D")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE EXISTS { MATCH (o)-[:Located]->(x:Object) WHERE x.dbref = $p0 } RETURN o.dbref AS D");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(2L);
    }

    [Test]
    public async Task CountSubquery_Renders()
    {
        var sub = CypherDsl.Match(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x")));
        var q = CypherDsl.Match(O).Return(CypherDsl.Prop("o", "dbref").As("D"), CypherDsl.CountSubquery(sub).As("N"))
            .OrderBy(CypherDsl.Prop("o", "dbref").Asc()).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) RETURN o.dbref AS D, COUNT { MATCH (o)-[:Located]->(x:Object) } AS N ORDER BY o.dbref");
    }

    [Test]
    public async Task IncomingDirection_RendersReversedArrow()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelFrom("Located", CypherDsl.Node("Object", "b")))
            .Return(CypherDsl.Prop("a", "dbref").As("A"), CypherDsl.Prop("b", "dbref").As("B")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)<-[:Located]-(b:Object) RETURN a.dbref AS A, b.dbref AS B");
    }

    [Test]
    public async Task Undirected_RendersNoArrowhead()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").Rel("Located", CypherDsl.Node("Object", "b")))
            .Return(CypherDsl.Prop("a", "dbref").As("A"), CypherDsl.Prop("b", "dbref").As("B")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[:Located]-(b:Object) RETURN a.dbref AS A, b.dbref AS B");
    }

    [Test]
    public async Task RelationshipAlias_AndProperty_Render()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a").RelTo("Has", CypherDsl.Node("Attr", "b"), alias: "r"))
            .Return(CypherDsl.Prop("r", "since").As("S")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[r:Has]->(b:Attr) RETURN r.since AS S");
    }

    [Test]
    public async Task WholeNode_RendersVariable()
    {
        var q = CypherDsl.Match(O).Return(CypherDsl.Variable("o")).Limit(1L).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (o:Object) RETURN o LIMIT $p0");
    }

    [Test]
    public async Task NamedParameter_KeepsItsName_AndIsBound()
    {
        var q = CypherDsl.Match(O).Where(CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Param("room", 42L)))
            .Return(CypherDsl.Prop("o", "name").As("Name")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $room RETURN o.name AS Name");
        await Assert.That(text.Parameters["room"]).IsEqualTo(42L);
    }

    [Test]
    public async Task NamedParameter_ReusedWithSameValue_BindsOnce_AndConflictingValue_IsRefused()
    {
        var same = CypherDsl.Match(O)
            .Where(CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Param("v", 1L)).Or(CypherDsl.Prop("o", "loc").Eq(CypherDsl.Param("v", 1L))))
            .Return(CypherDsl.CountAll().As("N")).Build().Render();
        await Assert.That(same.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $v OR o.loc = $v RETURN count(*) AS N");
        await Assert.That(same.Parameters.Count).IsEqualTo(1);

        var conflicting = CypherDsl.Match(O)
            .Where(CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Param("v", 1L)).Or(CypherDsl.Prop("o", "loc").Eq(CypherDsl.Param("v", 2L))))
            .Return(CypherDsl.CountAll().As("N")).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => conflicting.Render());
        await Assert.That(ex!.Message).Contains("'v'");
    }

    [Test]
    public async Task NamedParameter_InTheGeneratedNameShape_IsRefused()
    {
        var q = CypherDsl.Match(O).Where(CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Param("p0", 42L)))
            .Return(CypherDsl.Prop("o", "name").As("Name")).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => q.Render());
        await Assert.That(ex!.Message).Contains("p0");
    }

    [Test]
    public async Task Precedence_ParenthesizesOrUnderAnd_AndNotOverComparison()
    {
        var a = CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Literal(1L));
        var b = CypherDsl.Prop("o", "dbref").Eq(CypherDsl.Literal(2L));
        var c = CypherDsl.Prop("o", "flag").Eq(CypherDsl.Literal(true));
        var q = CypherDsl.Match(O).Where(a.Or(b).And(c.Not())).Return(CypherDsl.CountAll().As("N")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE (o.dbref = $p0 OR o.dbref = $p1) AND NOT (o.flag = $p2) RETURN count(*) AS N");
    }

    [Test]
    public async Task Precedence_LeavesAssociativeChainsFlat()
    {
        var a = CypherDsl.Prop("o", "dbref").Gt(CypherDsl.Literal(1L));
        var b = CypherDsl.Prop("o", "dbref").Lt(CypherDsl.Literal(5L));
        var c = CypherDsl.Prop("o", "dbref").Ne(CypherDsl.Literal(3L));
        var q = CypherDsl.Match(O).Where(a.And(b).And(c)).Return(CypherDsl.CountAll().As("N")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.dbref > $p0 AND o.dbref < $p1 AND o.dbref <> $p2 RETURN count(*) AS N");
    }

    [Test]
    public async Task Arithmetic_AndNegation_Render()
    {
        var d = CypherDsl.Prop("o", "dbref");
        var q = CypherDsl.Match(O).Where(d.Negate().Lt(CypherDsl.Literal(-1L)))
            .Return(d.Plus(CypherDsl.Literal(1L)).As("S"), d.Times(CypherDsl.Literal(2L)).As("M"), d.Modulo(CypherDsl.Literal(2L)).As("R"))
            .Limit(1L).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE -o.dbref < $p0 RETURN o.dbref + $p1 AS S, o.dbref * $p2 AS M, o.dbref % $p3 AS R LIMIT $p4");
    }

    [Test]
    public async Task Case_Renders()
    {
        var q = CypherDsl.Match(O)
            .Return(CypherDsl.Case([CypherDsl.When(CypherDsl.Prop("o", "flag").Eq(CypherDsl.Literal(true)), CypherDsl.Literal("y"))], @else: CypherDsl.Literal("n")).As("C"))
            .Limit(1L).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) RETURN CASE WHEN o.flag = $p0 THEN $p1 ELSE $p2 END AS C LIMIT $p3");
    }

    [Test]
    public async Task Unwind_RendersListOfParameters()
    {
        var q = CypherDsl.Unwind(CypherDsl.List(CypherDsl.Literal(1L), CypherDsl.Literal(2L)), "x").Return(CypherDsl.Variable("x").As("X")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo("UNWIND [$p0, $p1] AS x RETURN x AS X");
    }

    [Test]
    public async Task OptionalMatch_Renders()
    {
        var q = CypherDsl.Match(O)
            .OptionalMatch(CypherDsl.NodeRef("o").RelTo("Located", CypherDsl.Node("Object", "x")))
            .Return(CypherDsl.Prop("o", "dbref").As("D"), CypherDsl.Prop("x", "dbref").As("X"))
            .OrderBy(CypherDsl.Variable("D").Asc()).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (o:Object) OPTIONAL MATCH (o)-[:Located]->(x:Object) RETURN o.dbref AS D, x.dbref AS X ORDER BY D");
    }

    [Test]
    public async Task PatternProperties_RenderAsParameters()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "o").With("dbref", CypherDsl.Literal(1L)))
            .Return(CypherDsl.Prop("o", "name").As("N")).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object {dbref: $p0}) RETURN o.name AS N");
        await Assert.That(text.Parameters["p0"]).IsEqualTo(1L);
    }

    [Test]
    public async Task CountDistinct_AndMultiplePaths_Render()
    {
        var q = CypherDsl.Match(CypherDsl.Node("Object", "a"), CypherDsl.Node("Object", "b"))
            .Where(CypherDsl.Prop("a", "dbref").Lt(CypherDsl.Prop("b", "dbref")))
            .Return(CypherDsl.Count(CypherDsl.Prop("a", "loc"), distinct: true).As("N")).Build();
        await Assert.That(q.Render().Cypher).IsEqualTo(
            "MATCH (a:Object), (b:Object) WHERE a.dbref < b.dbref RETURN count(DISTINCT a.loc) AS N");
    }

    [Test]
    public async Task NullLiteral_RendersTheKeyword()
    {
        var q = CypherDsl.Match(O).Return(CypherDsl.Literal(null).As("X")).Limit(1L).Build();
        var text = q.Render();
        await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) RETURN NULL AS X LIMIT $p0");
        await Assert.That(text.Parameters.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Query_WithoutClauses_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new Query([]).Render());
    }
}
