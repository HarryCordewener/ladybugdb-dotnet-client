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

public sealed record Dto(long Id, string Label);

/// <summary>
/// Expression tree in, exact Cypher and parameters out - one test per whitelist row and one per
/// refusal. No engine: <c>QueryableTests</c> in the integration suite runs every shape here
/// against the real one. Translation never touches the connection, so the queryables here are bound
/// to none.
/// </summary>
public class TranslatorTests
{
    private static IQueryable<T> NodesOf<T>() => new LadybugQueryable<T>(new LadybugQueryProvider(connection: null, LadybugSchema.Default), new NodesRoot(typeof(T)));

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
