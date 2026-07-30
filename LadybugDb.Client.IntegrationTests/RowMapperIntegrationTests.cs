using ExtendedNumerics;
using LadybugDb.Client.Mapping;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <see cref="RowMapper"/> against the real engine, where the <see cref="LadybugType"/> of every
/// column is the engine's own rather than a unit test's assertion about it. <c>RowMapperTests</c> in
/// the unit suite covers the resolution and error logic exhaustively over hand-built rows; what can
/// only be established here is what the engine actually returns for a given query - the type a
/// <c>count(*)</c> comes back as, that an unset property reads as NULL, what an unaliased column is
/// named in the error message, and that a DECIMAL too wide for <see cref="decimal"/> really does
/// arrive that way.
/// </summary>
public class RowMapperIntegrationTests
{
    /// <summary>The shape a projection produces, and the one the design's own example uses.</summary>
    private record Person(long Dbref, string Name);

    /// <summary>Same columns as <see cref="Person"/>, opposite parameter order - so a plan cached by type alone would read both from the wrong column.</summary>
    private record Reversed(string Name, long Dbref);

    private record WithNullableParent(long Dbref, long? Parent);

    /// <summary>The same two columns as <see cref="WithNullableParent"/>, with a target that cannot hold NULL.</summary>
    private record WithRequiredParent(long Dbref, long Parent);

    private record Money(BigDecimal Balance);

    private record Truncatable(decimal Balance);

    private static async Task<(LadybugDatabase Db, LadybugConnection Connection)> OpenWithObjects(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync(
            "CREATE NODE TABLE Object(dbref INT64, name STRING, parent INT64, PRIMARY KEY(dbref))")) { }
        await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 1, name: 'Limbo', parent: 0})")) { }
        await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 2, name: 'Master Room', parent: 1})")) { }
        await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 3, name: 'Void'})")) { }
        return (db, conn);
    }

    /// <summary>
    /// The exact loop <see cref="RowMapper"/>'s own remarks tell the streaming projection to use -
    /// resolve from the first row, map every row with that one plan - run against a real result, so
    /// the documented seam is compiled and executed rather than only described.
    /// </summary>
    [Test]
    public async Task TheDocumentedStreamingShape_ProjectsEveryRow()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync(
                "MATCH (o:Object) RETURN o.dbref AS Dbref, o.name AS Name ORDER BY o.dbref");

            var people = new List<Person>();
            RowPlan<Person>? plan = null;
            var plans = new List<RowPlan<Person>>();
            await foreach (var row in result)
            {
                plan ??= RowMapper.ResolvePlan<Person>(row);

                // Resolved again per row purely to prove the cache hands back the SAME plan rather
                // than rebuilding one: this is the assertion that reflection is not per row.
                plans.Add(RowMapper.ResolvePlan<Person>(row));
                people.Add(plan.Map(row));
            }

            await Assert.That(people).IsEquivalentTo(new[]
            {
                new Person(1, "Limbo"),
                new Person(2, "Master Room"),
                new Person(3, "Void"),
            });
            await Assert.That(plans.Count).IsEqualTo(3);
            await Assert.That(plans.All(p => ReferenceEquals(p, plan))).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A column the record does not name is ignored, and the record's parameters are matched by name
    /// rather than by position - both against a real result, where the column order is the query's.
    /// </summary>
    [Test]
    public async Task ExtraColumnIsIgnored_AndOrderDoesNotDecideTheMapping()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 2 " +
                "RETURN o.name AS Name, o.parent AS Ignored, o.dbref AS Dbref");
            await using var rows = result.GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();

            var person = RowMapper.Map<Person>(rows.Current);

            await Assert.That(person.Dbref).IsEqualTo(2L);
            await Assert.That(person.Name).IsEqualTo("Master Room");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>count(*)</c> into a bare <c>long</c>. Which <see cref="LadybugType"/> an aggregate comes
    /// back as is the engine's decision, not this client's, so the scalar unwrap for the design's own
    /// example query is measured rather than assumed.
    /// </summary>
    [Test]
    public async Task ScalarUnwrap_ReadsCountStarAsLong()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN count(*)");
            await using var rows = result.GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();

            await Assert.That(rows.Current.GetValue(0).Type).IsEqualTo(LadybugType.Int64);
            await Assert.That(RowMapper.Map<long>(rows.Current)).IsEqualTo(3L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An unset property reads as NULL, which a <c>long?</c> target takes as
    /// <see langword="null"/> and a bare <c>long</c> target refuses by name.
    /// </summary>
    [Test]
    public async Task UnsetProperty_IsNullForANullableTarget_AndAnErrorForANonNullableOne()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var cypher = "MATCH (o:Object) WHERE o.dbref = 3 " +
                         "RETURN o.dbref AS Dbref, o.parent AS Parent";

            await using (var result = await conn.QueryAsync(cypher))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                await Assert.That(rows.Current.GetValue(1).IsNull).IsTrue();

                var mapped = RowMapper.Map<WithNullableParent>(rows.Current);
                await Assert.That(mapped.Dbref).IsEqualTo(3L);
                await Assert.That(mapped.Parent).IsNull();
            }

            await using (var result = await conn.QueryAsync(cypher))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                var row = rows.Current;

                var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<WithRequiredParent>(row));
                await Assert.That(ex).IsNotNull();
                await Assert.That(ex!.Message).Contains("'Parent'");
                await Assert.That(ex.Message).Contains("NULL");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The unmatched-parameter error names the columns the query actually returned - unaliased, so
    /// the message shows the engine's own <c>o.name</c> naming rather than a tidied-up version of it,
    /// which is the whole point of printing them.
    /// </summary>
    [Test]
    public async Task NoMatchingConstructor_NamesTheEnginesOwnColumnNamesAndTheCandidate()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.dbref, o.name");
            await using var rows = result.GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();
            var row = rows.Current;

            var ex = Assert.Throws<InvalidOperationException>(() => RowMapper.Map<Person>(row));

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("'o.dbref'");
            await Assert.That(ex.Message).Contains("'o.name'");
            await Assert.That(ex.Message).Contains("Person(long Dbref, string Name)");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A STRING column read into a <c>long</c> is an error naming the column, the engine's own type
    /// for it, and the target - not a parsed or coerced value.
    /// </summary>
    [Test]
    public async Task WrongTypeTarget_ThrowsNamingTheEnginesTypeRatherThanCoercing()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 1 RETURN o.name AS Dbref, o.name AS Name");
            await using var rows = result.GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();
            var row = rows.Current;

            var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Person>(row));

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("'Dbref'");
            await Assert.That(ex.Message).Contains("String");
            await Assert.That(ex.Message).Contains("long");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>id(n)</c> returns INTERNAL_ID, which no projection target can hold: there is no scalar
    /// accessor for it, so the mapper reports the column and its type instead of quietly producing
    /// something else. Pinned because <c>id(n)</c> looks exactly like a <c>long</c> at the call site.
    /// </summary>
    [Test]
    public async Task InternalIdColumn_HasNoScalarTarget_AndSaysSo()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 1 RETURN id(o)");
            await using var rows = result.GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();
            var row = rows.Current;

            await Assert.That(row.GetValue(0).Type).IsEqualTo(LadybugType.InternalId);

            var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<long>(row));
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("InternalId");
            await Assert.That(ex.Message).Contains("long");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The DECIMAL precision decision against a real DECIMAL(38,0): a value the engine holds and
    /// <see cref="decimal"/> cannot is refused, naming the column, rather than rounded to fit - and
    /// the same column reads losslessly into a <see cref="BigDecimal"/> target, so the refusal is a
    /// choice between two real behaviours.
    /// </summary>
    [Test]
    public async Task Decimal38Digits_RefusesADecimalTarget_AndReadsIntoBigDecimal()
    {
        const string exact = "12345678901234567890123456789012345678";
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Account(id INT64, balance DECIMAL(38,0), PRIMARY KEY(id))")) { }
            await using (var stmt = await conn.PrepareAsync("CREATE (n:Account {id: 1, balance: $b})"))
            {
                stmt.Bind("b", BigDecimal.Parse(exact));
                await using (var _ = await stmt.ExecuteAsync()) { }
            }

            const string cypher = "MATCH (n:Account) RETURN n.balance AS Balance";

            await using (var result = await conn.QueryAsync(cypher))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                var row = rows.Current;

                await Assert.That(row.GetValue(0).Type).IsEqualTo(LadybugType.Decimal);

                var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Truncatable>(row));
                await Assert.That(ex).IsNotNull();
                await Assert.That(ex!.Message).Contains("'Balance'");
                await Assert.That(ex.Message).Contains("losing precision");
                await Assert.That(ex.Message).Contains("BigDecimal");
            }

            await using (var result = await conn.QueryAsync(cypher))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();

                var money = RowMapper.Map<Money>(rows.Current);
                await Assert.That(money.Balance).IsEqualTo(BigDecimal.Parse(exact));
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Two real queries projecting the same record with different column shapes. Keyed by type alone,
    /// the second would inherit the first's plan and read each column from the other's position -
    /// here that produces a type error, but for two columns of the same type it would silently swap
    /// the values.
    /// </summary>
    [Test]
    public async Task TwoColumnShapesForTheSameRecord_BothProjectCorrectly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            await using (var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 1 RETURN o.dbref AS Dbref, o.name AS Name"))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                await Assert.That(RowMapper.Map<Person>(rows.Current)).IsEqualTo(new Person(1, "Limbo"));
            }

            await using (var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 2 RETURN o.name AS Name, o.dbref AS Dbref"))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                await Assert.That(RowMapper.Map<Person>(rows.Current)).IsEqualTo(new Person(2, "Master Room"));
            }

            // A second record over the same two shapes, to also cross the (T, shape) key the other way.
            await using (var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 2 RETURN o.name AS Name, o.dbref AS Dbref"))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                await Assert.That(RowMapper.Map<Reversed>(rows.Current)).IsEqualTo(new Reversed("Master Room", 2));
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
