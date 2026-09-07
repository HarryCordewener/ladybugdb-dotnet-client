using System.Linq.Expressions;
using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests.Linq;

[Node("Object")]
public sealed record Obj([property: Key] long Dbref, string Name, long? Loc);

[Node("Attr")]
public sealed record Attr([property: Key] string Akey, string Aname, string Aval);

[Node("Flagged")]
public sealed record Flagged([property: Key] long Id, bool IsRoom);

[Rel("Has", From = typeof(Obj), To = typeof(Attr))]
public sealed record Has(long Since);

[Rel("Located", From = typeof(Obj), To = typeof(Obj))]
public sealed record Located;

public sealed record Dto(long Id, string Label);

public sealed record Counted(long? Loc, int N);

/// <summary>
/// Expression tree in, exact Cypher and parameters out - one test per whitelist row and one per
/// refusal. No engine: <c>QueryableTests</c> in the integration suite runs every shape here
/// against the real one. Translation never touches the connection, so the queryables here are bound
/// to none.
/// </summary>
public class TranslatorTests
{
    private static IQueryable<T> NodesOf<T>() => new LadybugQueryable<T>(new LadybugQueryProvider(connection: null, LadybugSchema.Default), new NodesRoot(typeof(T)));

    private static IQueryable<T> MatchOf<T>(string pattern, Dictionary<string, object?>? parameters = null, string variable = "n") =>
        new LadybugQueryable<T>(new LadybugQueryProvider(connection: null, LadybugSchema.Default), new MatchRoot(typeof(T), pattern, parameters ?? [], variable));

    private static TranslatedQuery Translate<T>(IQueryable<T> query) => QueryTranslator.Translate(query.Expression, LadybugSchema.Default);

    /// <summary>
    /// Translates a scalar terminal (<c>Count()</c>, <c>First()</c>, ...) without executing it: the
    /// terminal is written as a lambda over the source, and its body - the <c>Queryable</c> call the
    /// provider would receive - is translated with the source's expression substituted in.
    /// </summary>
    private static TranslatedQuery Translate<T, TResult>(IQueryable<T> source, Expression<Func<IQueryable<T>, TResult>> terminal) =>
        QueryTranslator.Translate(new Substitute(terminal.Parameters[0], source.Expression).Visit(terminal.Body), LadybugSchema.Default);

    private static NotSupportedException Refused<T>(Func<IQueryable<T>> query) =>
        Assert.Throws<NotSupportedException>(() => Translate(query()))!;

    private static NotSupportedException Refused<T, TResult>(IQueryable<T> source, Expression<Func<IQueryable<T>, TResult>> terminal) =>
        Assert.Throws<NotSupportedException>(() => Translate(source, terminal))!;

    private sealed class Substitute(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }

    // ------------------------------------------------------------------------------ predicates

    [Test]
    public async Task WhereEqualsClosure_RendersParameter()
    {
        var dbref = 42L;
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref == dbref).Select(o => o.Name));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(42L);
    }

    [Test]
    public async Task Comparisons_RenderTheirOperators()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref < 5 && o.Dbref <= 5 && o.Dbref > 1 && o.Dbref >= 1 && o.Dbref != 3).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.dbref < $p0 AND o.dbref <= $p1 AND o.dbref > $p2 AND o.dbref >= $p3 AND o.dbref <> $p4 RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(5L);
    }

    [Test]
    public async Task BooleanConnectives_RenderWithPrecedence()
    {
        var q = Translate(NodesOf<Obj>().Where(o => (o.Dbref == 1 || o.Dbref == 2) && !(o.Name == "x")).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE (o.dbref = $p0 OR o.dbref = $p1) AND NOT (o.name = $p2) RETURN o.dbref AS Dbref");
    }

    [Test]
    public async Task NullTests_RenderIsNull()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Loc == null || o.Name != null || o.Loc.HasValue).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.loc IS NULL OR o.name IS NOT NULL OR o.loc IS NOT NULL RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NullableComparison_UnwrapsTheLift()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Loc == 3 && o.Loc!.Value > 1).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.loc = $p0 AND o.loc > $p1 RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(3L);
    }

    [Test]
    public async Task StringMethods_RenderTheirOperators()
    {
        var prefix = "ob";
        var q = Translate(NodesOf<Obj>()
            .Where(o => o.Name.StartsWith(prefix) && o.Name.EndsWith("1") && o.Name.Contains("j") && o.Name.Length > 2 && o.Name.ToUpper() == "OBJ1" && o.Name.ToLower() == "obj1")
            .Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.name STARTS WITH $p0 AND o.name ENDS WITH $p1 AND o.name CONTAINS $p2 AND size(o.name) > $p3 " +
            "AND upper(o.name) = $p4 AND lower(o.name) = $p5 RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo("ob");
    }

    [Test]
    public async Task StringMethod_WithAComparisonArgument_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Where(o => o.Name.StartsWith("x", StringComparison.OrdinalIgnoreCase)));
        await Assert.That(ex.Message).Contains("o.Name.StartsWith(\"x\", OrdinalIgnoreCase)");
        await Assert.That(ex.Message).Contains("Match<T>");
    }

    [Test]
    public async Task CollectionContains_RendersIn()
    {
        var list = new List<long> { 1, 3 };
        long[] array = [2, 4];
        var q = Translate(NodesOf<Obj>().Where(o => list.Contains(o.Dbref) || array.Contains(o.Dbref)).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.dbref IN [$p0, $p1] OR o.dbref IN [$p2, $p3] RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters["p3"]).IsEqualTo(4L);
    }

    [Test]
    public async Task ClosureMember_IsEvaluatedOnce_IntoAParameter()
    {
        var room = new Dto(7, "seven");
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref == room.Id && o.Name == room.Label.ToUpper()).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 AND o.name = $p1 RETURN o.dbref AS Dbref");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(7L);
        await Assert.That(q.Text.Parameters["p1"]).IsEqualTo("SEVEN");
    }

    [Test]
    public async Task MemberToMemberComparison_Renders()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Loc == o.Dbref).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.loc = o.dbref RETURN o.dbref AS Dbref");
    }

    [Test]
    public async Task BareBooleanMember_IsRefused()
    {
        var ex = Refused(() => NodesOf<Flagged>().Where(f => f.IsRoom));
        await Assert.That(ex.Message).Contains("f.IsRoom");
        await Assert.That(ex.Message).Contains("== true");

        var negated = Refused(() => NodesOf<Flagged>().Where(f => !f.IsRoom));
        await Assert.That(negated.Message).Contains("f.IsRoom");

        var explicitForm = Translate(NodesOf<Flagged>().Where(f => f.IsRoom == true).Select(f => f.Id));
        await Assert.That(explicitForm.Text.Cypher).IsEqualTo("MATCH (f:Flagged) WHERE f.isRoom = $p0 RETURN f.id AS Id");
    }

    [Test]
    public async Task UnsupportedMethod_IsRefused_NamingTheSubExpression()
    {
        var ex = Refused(() => NodesOf<Obj>().Where(o => o.Name.Trim() == "x"));
        await Assert.That(ex.Message).Contains("o.Name.Trim()");
        await Assert.That(ex.Message).Contains("Match<T>");
    }

    [Test]
    public async Task UnmappedProperty_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Where(o => o.ToString() == "x"));
        await Assert.That(ex.Message).Contains("o.ToString()");
    }

    // ----------------------------------------------------------------------------- projections

    [Test]
    public async Task NoSelect_ReturnsTheWholeNode_WithDefaultAlias()
    {
        var q = Translate(NodesOf<Obj>());
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (n:Object) RETURN n");
        await Assert.That(q.Shape).IsTypeOf<NodeShape>();
        await Assert.That(q.ElementType).IsEqualTo(typeof(Obj));
    }

    [Test]
    public async Task IdentitySelect_ReturnsTheWholeNode()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref > 1).Select(o => o));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref > $p0 RETURN o");
        await Assert.That(q.Shape).IsTypeOf<NodeShape>();
    }

    [Test]
    public async Task AnonymousType_RendersOneAliasPerMember()
    {
        var q = Translate(NodesOf<Obj>().Select(o => new { o.Dbref, o.Name, Len = o.Name.Length, Upper = o.Name.ToUpper() }));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) RETURN o.dbref AS Dbref, o.name AS Name, cast(size(o.name), 'INT32') AS Len, upper(o.name) AS Upper");
        await Assert.That(q.Shape).IsTypeOf<RowShape>();
    }

    [Test]
    public async Task RecordConstructor_RendersConstructorParameterNames()
    {
        var q = Translate(NodesOf<Obj>().Select(o => new Dto(o.Dbref, o.Name)));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN o.dbref AS Id, o.name AS Label");
        await Assert.That(q.ElementType).IsEqualTo(typeof(Dto));
    }

    /// <summary>
    /// C# forbids a tuple literal inside an expression tree (CS8143), so a tuple projection is
    /// written through the constructor, which is the same path a record takes.
    /// </summary>
    [Test]
    public async Task ValueTuple_RendersItemNames()
    {
        var q = Translate(NodesOf<Obj>().Select(o => new ValueTuple<long, string>(o.Dbref, o.Name)));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN o.dbref AS item1, o.name AS item2");
    }

    [Test]
    public async Task MemberInit_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Select(o => new Mutable { Id = o.Dbref }));
        await Assert.That(ex.Message).Contains("new Mutable()");
        await Assert.That(ex.Message).Contains("constructor");
    }

    [Test]
    public async Task WholeNodeInsideAProjection_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Select(o => new { Node = o, o.Name }));
        await Assert.That(ex.Message).Contains("Node = o");
    }

    [Test]
    public async Task SelectAfterSelect_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Select(o => new { o.Name }).Select(x => x.Name));
        await Assert.That(ex.Message).Contains("Select");
    }

    // ------------------------------------------------------------------------ ordering, paging

    [Test]
    public async Task OrderBy_ThenBy_Skip_Take_Render()
    {
        var page = 2;
        var q = Translate(NodesOf<Obj>().OrderBy(o => o.Name).ThenByDescending(o => o.Dbref).Skip(page * 10).Take(10).Select(o => o.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) RETURN o.dbref AS Dbref ORDER BY o.name, o.dbref DESC SKIP $p0 LIMIT $p1");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(20L);
        await Assert.That(q.Text.Parameters["p1"]).IsEqualTo(10L);
    }

    [Test]
    public async Task OrderByDescending_AfterSelect_RendersTheProjectedExpression()
    {
        var q = Translate(NodesOf<Obj>().Select(o => new { o.Name, o.Dbref }).OrderByDescending(x => x.Dbref).Where(x => x.Name != "x"));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.name <> $p0 RETURN o.name AS Name, o.dbref AS Dbref ORDER BY o.dbref DESC");
    }

    [Test]
    public async Task Distinct_RendersReturnDistinct()
    {
        var q = Translate(NodesOf<Obj>().Select(o => o.Loc).Distinct());
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN DISTINCT o.loc AS Loc");
    }

    [Test]
    public async Task Where_AfterSkipOrTake_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Take(3).Where(o => o.Dbref > 1));
        await Assert.That(ex.Message).Contains("Where");
        await Assert.That(ex.Message).Contains("Take");
    }

    // ------------------------------------------------------------------------------- terminals

    [Test]
    public async Task Count_RendersCountStar()
    {
        var count = Translate(NodesOf<Obj>().Where(o => o.Dbref > 1), q => q.Count());
        await Assert.That(count.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref > $p0 RETURN count(*) AS Count");
        await Assert.That(count.Terminal).IsEqualTo(Terminal.Count);

        var withPredicate = Translate(NodesOf<Obj>(), q => q.LongCount(o => o.Dbref > 1));
        await Assert.That(withPredicate.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref > $p0 RETURN count(*) AS Count");
        await Assert.That(withPredicate.Terminal).IsEqualTo(Terminal.LongCount);
    }

    [Test]
    public async Task Any_RendersCountStar()
    {
        var q = Translate(NodesOf<Obj>(), q => q.Any(o => o.Name == "x"));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.name = $p0 RETURN count(*) AS Found");
        await Assert.That(q.Terminal).IsEqualTo(Terminal.Any);
    }

    [Test]
    public async Task First_AndSingle_RenderLimits()
    {
        var first = Translate(NodesOf<Obj>().OrderBy(o => o.Dbref), q => q.FirstOrDefault(o => o.Dbref > 1));
        await Assert.That(first.Text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref > $p0 RETURN o ORDER BY o.dbref LIMIT $p1");
        await Assert.That(first.Text.Parameters["p1"]).IsEqualTo(1L);
        await Assert.That(first.Terminal).IsEqualTo(Terminal.FirstOrDefault);

        var single = Translate(NodesOf<Obj>().Select(o => o.Name), q => q.Single());
        await Assert.That(single.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN o.name AS Name LIMIT $p0");
        await Assert.That(single.Text.Parameters["p0"]).IsEqualTo(2L);
        await Assert.That(single.Terminal).IsEqualTo(Terminal.Single);
    }

    [Test]
    public async Task Count_AfterSelect_IsRefused()
    {
        var ex = Refused(NodesOf<Obj>().Select(o => o.Loc).Distinct(), q => q.Count());
        await Assert.That(ex.Message).Contains("Count");
    }

    [Test]
    public async Task UnsupportedOperator_IsRefused_NamingIt()
    {
        var ex = Refused(() => NodesOf<Obj>().Reverse());
        await Assert.That(ex.Message).Contains("Reverse");
        await Assert.That(ex.Message).Contains("Match<T>");
    }

    // ----------------------------------------------------------------------------- graph steps

    [Test]
    public async Task Out_RendersThePatternSegment_AndBindsBothEnds()
    {
        var d = 1L;
        var n = "DESC";
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref == d).Out<Obj, Has, Attr>().Where(p => p.Target.Aname == n && p.Source.Name != "x").Select(p => p.Target.Aval));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (n0:Object)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.aname = $p1 AND n0.name <> $p2 RETURN n1.aval AS Aval");
        await Assert.That(q.Text.Parameters["p1"]).IsEqualTo("DESC");
    }

    [Test]
    public async Task In_RendersTheReversedArrow()
    {
        var q = Translate(NodesOf<Obj>().Where(room => room.Dbref == 3).In<Obj, Located, Obj>().Select(p => new { p.Target.Dbref, p.Target.Name }));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (n0:Object)<-[:Located]-(n1:Object) WHERE n0.dbref = $p0 RETURN n1.dbref AS Dbref, n1.name AS Name");
    }

    [Test]
    public async Task OutWithRel_BindsTheRelationship()
    {
        var q = Translate(NodesOf<Obj>().OutWithRel<Obj, Has, Attr>().Where(p => p.Rel.Since > 5).Select(p => new { p.Rel.Since, p.Target.Aname }));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (n0:Object)-[r0:Has]->(n1:Attr) WHERE r0.since > $p0 RETURN r0.since AS Since, n1.aname AS Aname");
    }

    [Test]
    public async Task VariableLength_RendersHops()
    {
        var q = Translate(NodesOf<Obj>().Out<Obj, Located, Obj>(1, 3).Select(p => p.Target.Dbref));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (n0:Object)-[:Located*1..3]->(n1:Object) RETURN n1.dbref AS Dbref");
    }

    [Test]
    public async Task ChainedSteps_NestTheTuple_AndNumberTheNodes()
    {
        var q = Translate(NodesOf<Obj>().Out<Obj, Located, Obj>().Out<(Obj Source, Obj Target), Located, Obj>()
            .Where(p => p.Source.Source.Dbref == 1).Select(p => new { Start = p.Source.Source.Name, Middle = p.Source.Target.Name, End = p.Target.Name }));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (n0:Object)-[:Located]->(n1:Object)-[:Located]->(n2:Object) WHERE n0.dbref = $p0 RETURN n0.name AS Start, n1.name AS Middle, n2.name AS `End`");
    }

    [Test]
    public async Task UnprojectedStep_ReturnsEveryVariable_AsATuple()
    {
        var q = Translate(NodesOf<Obj>().OutWithRel<Obj, Has, Attr>());
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (n0:Object)-[r0:Has]->(n1:Attr) RETURN n0, r0, n1");
        await Assert.That(q.Shape).IsTypeOf<TupleShape>();
        await Assert.That(q.ElementType).IsEqualTo(typeof((Obj, Has, Attr)));

        var target = Translate(NodesOf<Obj>().Out<Obj, Has, Attr>().Select(p => p.Target));
        await Assert.That(target.Text.Cypher).IsEqualTo("MATCH (n0:Object)-[:Has]->(n1:Attr) RETURN n1");
        await Assert.That(target.Shape).IsTypeOf<NodeShape>();
    }

    [Test]
    public async Task WhereExists_RendersTheSubquery()
    {
        var q = Translate(NodesOf<Obj>().WhereExists<Obj, Located, Obj>(x => x.Dbref == 3).Select(o => o.Name));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE EXISTS { MATCH (o)-[:Located]->(x:Object) WHERE x.dbref = $p0 } RETURN o.name AS Name");
        await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(3L);
    }

    [Test]
    public async Task MismatchedRelationship_IsRefused_NamingBothTables()
    {
        var reversed = Assert.Throws<InvalidOperationException>(() => Translate(NodesOf<Attr>().Out<Attr, Has, Obj>()));
        await Assert.That(reversed!.Message).Contains("'Object' to 'Attr'");
        await Assert.That(reversed.Message).Contains("'Attr' to 'Object'");
        await Assert.That(reversed.Message).Contains("use In");

        var unrelated = Assert.Throws<InvalidOperationException>(() => Translate(NodesOf<Obj>().Out<Obj, Located, Attr>()));
        await Assert.That(unrelated!.Message).Contains("Located");
        await Assert.That(unrelated.Message).Contains("'Attr'");
    }

    [Test]
    public async Task Step_AfterSelect_IsRefused()
    {
        var ex = Refused(() => NodesOf<Obj>().Select(o => o.Name).Out<string, Has, Attr>());
        await Assert.That(ex.Message).Contains("Out");
    }

    // ---------------------------------------------------------------------------- Match<T>

    [Test]
    public async Task MatchPattern_IsRenderedVerbatim_WithItsParameters()
    {
        var q = Translate(MatchOf<Obj>("(r:Object {dbref: $room})-[:Located]->(n:Object)", new() { ["room"] = 5L }).Select(e => e.Name));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (r:Object {dbref: $room})-[:Located]->(n:Object) RETURN n.name AS Name");
        await Assert.That(q.Text.Parameters["room"]).IsEqualTo(5L);
    }

    [Test]
    public async Task MatchPattern_BindsTheNamedVariable_AndTheChainApplies()
    {
        var q = Translate(MatchOf<Obj>("(x:Object)-[:Located]->(r:Object {dbref: $room})", new() { ["room"] = 3L }, "x")
            .Where(x => x.Name != "y").OrderBy(x => x.Dbref).Take(2));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (x:Object)-[:Located]->(r:Object {dbref: $room}) WHERE x.name <> $p0 RETURN x ORDER BY x.dbref LIMIT $p1");
        await Assert.That(q.Shape).IsTypeOf<NodeShape>();
        await Assert.That(q.Text.Parameters["room"]).IsEqualTo(3L);
        await Assert.That(q.Text.Parameters["p1"]).IsEqualTo(2L);
    }

    [Test]
    public async Task MatchPattern_StepsContinueInASecondMatch_AvoidingTheCallersVariables()
    {
        var q = Translate(MatchOf<Obj>("(n:Object {dbref: $d})-[:Located]->(n1:Object)", new() { ["d"] = 1L })
            .Out<Obj, Has, Attr>().Select(p => p.Target.Aname));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (n:Object {dbref: $d})-[:Located]->(n1:Object) MATCH (n)-[:Has]->(n2:Attr) RETURN n2.aname AS Aname");
    }

    [Test]
    public async Task MatchPattern_ReservedParameterName_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Translate(MatchOf<Obj>("(n:Object {dbref: $p0})", new() { ["p0"] = 1L })));
        await Assert.That(ex!.Message).Contains("p0");
    }

    // ------------------------------------------------------------------------------ GroupBy

    [Test]
    public async Task GroupBy_KeyAndCount_RenderImplicitGrouping()
    {
        var q = Translate(NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.LongCount() }).OrderBy(x => x.Key));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN o.loc AS Key, count(*) AS N ORDER BY Key");
        await Assert.That(q.Shape).IsTypeOf<RowShape>();
    }

    [Test]
    public async Task GroupBy_IntCount_IsCastToInt32()
    {
        var q = Translate(NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new Counted(g.Key, g.Count())));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Object) RETURN o.loc AS Loc, cast(count(*), 'INT32') AS N");
    }

    [Test]
    public async Task GroupBy_SumMinMaxAverage_RenderTheirFunctions()
    {
        var q = Translate(NodesOf<Obj>().Where(o => o.Dbref > 1).GroupBy(o => o.Loc)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Dbref), Lo = g.Min(x => x.Dbref), Hi = g.Max(x => x.Dbref), Mean = g.Average(x => x.Dbref) })
            .OrderByDescending(x => x.Total));
        await Assert.That(q.Text.Cypher).IsEqualTo(
            "MATCH (o:Object) WHERE o.dbref > $p0 RETURN o.loc AS Key, cast(sum(o.dbref), 'INT64') AS Total, min(o.dbref) AS Lo, max(o.dbref) AS Hi, avg(o.dbref) AS Mean ORDER BY Total DESC");
    }

    [Test]
    public async Task GroupBy_AfterAStep_GroupsOnEitherEnd()
    {
        var q = Translate(NodesOf<Obj>().Out<Obj, Has, Attr>().GroupBy(p => p.Source.Dbref).Select(g => new { g.Key, N = g.LongCount() }));
        await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (n0:Object)-[:Has]->(n1:Attr) RETURN n0.dbref AS Key, count(*) AS N");
    }

    [Test]
    public async Task GroupBy_Refusals_NameTheRule()
    {
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc)).Message).Contains("GroupBy needs a Select");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Where(g => g.Key == 1)).Message).Contains("after GroupBy");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => g.LongCount())).Message).Contains("must include g.Key");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => g.Key)).Message).Contains("needs an aggregate");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, First = g.First().Name })).Message).Contains("First");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.Count(x => x.Dbref > 2) })).Message).Contains("predicate");
        await Assert.That(Refused(() => NodesOf<Obj>().Select(o => o.Loc).GroupBy(l => l)).Message).Contains("WITH");
        await Assert.That(Refused(NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.LongCount() }), q => q.Count()).Message).Contains("Count after Select");
        await Assert.That(Refused(() => NodesOf<Obj>().GroupBy(o => o.Loc).Select(g => new { g.Key, N = g.LongCount() }).Where(x => x.N > 1)).Message).Contains("HAVING");
    }

    /// <summary>Translation reads closures when it runs, so the same query re-translated after the variable changes carries the new value.</summary>
    [Test]
    public async Task Closure_IsReadAtTranslation_NotAtConstruction()
    {
        var dbref = 1L;
        var query = NodesOf<Obj>().Where(o => o.Dbref == dbref).Select(o => o.Name);
        dbref = 2L;
        await Assert.That(Translate(query).Text.Parameters["p0"]).IsEqualTo(2L);
    }

    public sealed class Mutable
    {
        public long Id { get; set; }
    }
}
