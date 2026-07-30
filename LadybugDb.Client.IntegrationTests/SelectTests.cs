using ExtendedNumerics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <see cref="LadybugConnection.Select{T}"/> against the real engine: the streaming typed projection
/// the whole mapping seam exists to serve. What can only be established here is what the engine
/// actually returns - the <see cref="LadybugType"/> of a declared <c>INT32</c> column, that a
/// zero-row result still carries a column shape, and that the projection really does run lazily
/// against a live cursor rather than materializing.
/// </summary>
/// <remarks>
/// Disposal of the result the iterator owns is covered separately, in <c>SelectDisposalTests</c>,
/// which must run alone; keeping it out of this class lets everything here run in parallel with the
/// rest of the suite.
/// </remarks>
public class SelectTests
{
    /// <summary>The shape the design's own example projects.</summary>
    private record Person(long Dbref, string Name);

    private record WithNullableParent(long Dbref, long? Parent);

    /// <summary>Names a column no query below returns, so resolution must reject it.</summary>
    private record Mismatched(long Dbref, string Nmae);

    private record Widths(long Small, long Big);

    /// <summary>A target too narrow for the INT64 column it names.</summary>
    private record Narrowed(int Big);

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

    // -------------------------------------------------------------------------------- projection

    /// <summary>The design's own example, end to end: a record, a parameter object, and one call.</summary>
    [Test]
    public async Task Select_ProjectsARecordWithParameters()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var people = new List<Person>();
            await foreach (var person in conn.Select<Person>(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.dbref AS Dbref, o.name AS Name " +
                "ORDER BY o.dbref",
                new { min = 2L }))
            {
                people.Add(person);
            }

            await Assert.That(people).IsEquivalentTo(new[]
            {
                new Person(2, "Master Room"),
                new Person(3, "Void"),
            });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>parameters: null</c> - which is what omitting the argument means - runs the plain
    /// <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/> path rather than raising
    /// <see cref="ArgumentNullException"/> the way the parameter-taking overload does for an explicit
    /// null bag. Both spellings are asserted: the omitted argument and the explicit <c>null</c>.
    /// </summary>
    [Test]
    public async Task Select_WithoutParameters_RunsTheUnparameterizedPath()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            const string cypher = "MATCH (o:Object) RETURN o.dbref AS Dbref, o.name AS Name ORDER BY o.dbref";

            var omitted = new List<Person>();
            await foreach (var person in conn.Select<Person>(cypher)) omitted.Add(person);

            var explicitNull = new List<Person>();
            await foreach (var person in conn.Select<Person>(cypher, null)) explicitNull.Add(person);

            await Assert.That(omitted.Count).IsEqualTo(3);
            await Assert.That(omitted).IsEquivalentTo(explicitNull);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A dictionary is a parameter bag too - the runtime-computed-names case.</summary>
    [Test]
    public async Task Select_TakesADictionaryOfParameters()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var parameters = new Dictionary<string, long> { ["min"] = 3L };

            var names = new List<string>();
            await foreach (var name in conn.Select<string>(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.name AS Name", parameters))
            {
                names.Add(name);
            }

            await Assert.That(names).IsEquivalentTo(new[] { "Void" });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>Scalar unwrap, including the design's own <c>count(*)</c> example.</summary>
    [Test]
    public async Task Select_UnwrapsASingleColumnIntoAScalar()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var counts = new List<long>();
            await foreach (var total in conn.Select<long>("MATCH (o:Object) RETURN count(*)"))
                counts.Add(total);

            await Assert.That(counts).IsEquivalentTo(new[] { 3L });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>An unset property is NULL, which a nullable target takes as <see langword="null"/>.</summary>
    [Test]
    public async Task Select_ReadsANullColumnIntoANullableTarget()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var rows = new List<WithNullableParent>();
            await foreach (var row in conn.Select<WithNullableParent>(
                "MATCH (o:Object) WHERE o.dbref = 3 RETURN o.dbref AS Dbref, o.parent AS Parent"))
            {
                rows.Add(row);
            }

            await Assert.That(rows).IsEquivalentTo(new[] { new WithNullableParent(3, null) });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // -------------------------------------------------------------------- zero rows still validate

    /// <summary>
    /// <b>The zero-row case.</b> The plan is resolved from the result's column shape before the first
    /// row, so a <c>T</c> that could never have mapped these columns is reported even when the query
    /// matches nothing. Resolved from the first row instead, this query would enumerate to completion
    /// and report nothing at all - a projection that "worked" while proving nothing.
    /// </summary>
    [Test]
    public async Task Select_WithZeroRows_StillRejectsAMismatchedTarget()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            const string noRows =
                "MATCH (o:Object) WHERE o.dbref = 99999 RETURN o.dbref AS Dbref, o.name AS Name";

            // The same query with a matching target really does return no rows, so the throw below is
            // about the target and not about the query having matched something after all.
            var matched = 0;
            await foreach (var _ in conn.Select<Person>(noRows)) matched++;
            await Assert.That(matched).IsEqualTo(0);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in conn.Select<Mismatched>(noRows)) { }
            });

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("'Dbref'");
            await Assert.That(ex.Message).Contains("'Name'");
            await Assert.That(ex.Message).Contains("Nmae");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same for the scalar-unwrap error: a zero-row, two-column result still reports that a scalar
    /// target needs exactly one column.
    /// </summary>
    [Test]
    public async Task Select_WithZeroRows_StillRejectsAScalarAgainstTwoColumns()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in conn.Select<long>(
                    "MATCH (o:Object) WHERE o.dbref = 99999 RETURN o.dbref AS Dbref, o.name AS Name")) { }
            });

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("2 column(s)");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // ---------------------------------------------------------------------------------- widening

    /// <summary>
    /// A column the engine really does return as <c>INT32</c> reads into the <see cref="long"/> a
    /// record naturally declares, and an <c>INT64</c> column into an <see cref="int"/> still does not.
    /// The <see cref="LadybugType"/> is asserted first, so this measures the engine's own type for a
    /// declared INT32 column rather than assuming it.
    /// </summary>
    [Test]
    public async Task Select_WidensANarrowerColumn_ButStillRefusesToNarrow()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE W(id INT64, small INT32, big INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:W {id: 1, small: 2147483647, big: 9223372036854775807})")) { }

            const string cypher = "MATCH (n:W) RETURN n.small AS Small, n.big AS Big";

            await using (var result = await conn.QueryAsync(cypher))
            {
                await using var rows = result.GetAsyncEnumerator();
                await Assert.That(await rows.MoveNextAsync()).IsTrue();
                await Assert.That(rows.Current.GetValue(0).Type).IsEqualTo(LadybugType.Int32);
                await Assert.That(rows.Current.GetValue(1).Type).IsEqualTo(LadybugType.Int64);
            }

            var widened = new List<Widths>();
            await foreach (var row in conn.Select<Widths>(cypher)) widened.Add(row);
            await Assert.That(widened).IsEquivalentTo(new[] { new Widths(int.MaxValue, long.MaxValue) });

            var ex = await Assert.ThrowsAsync<LadybugException>(async () =>
            {
                await foreach (var _ in conn.Select<Narrowed>("MATCH (n:W) RETURN n.big AS Big")) { }
            });

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("'Big'");
            await Assert.That(ex.Message).Contains("Int64");
            await Assert.That(ex.Message).Contains("int");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A DECIMAL too wide for <see cref="decimal"/> is refused rather than rounded, through
    /// <see cref="LadybugConnection.Select{T}"/> as much as through the mapper directly - and the same
    /// column reads losslessly into a <see cref="BigDecimal"/> target.
    /// </summary>
    [Test]
    public async Task Select_RefusesADecimalItCannotHold_AndReadsItIntoBigDecimal()
    {
        const string exact = "12345678901234567890123456789012345678";
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Account(id INT64, balance DECIMAL(38,0), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:Account {id: 1, balance: $b})", new { b = BigDecimal.Parse(exact) })) { }

            const string cypher = "MATCH (n:Account) RETURN n.balance AS Balance";

            var ex = await Assert.ThrowsAsync<LadybugException>(async () =>
            {
                await foreach (var _ in conn.Select<decimal>(cypher)) { }
            });
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("losing precision");

            var balances = new List<BigDecimal>();
            await foreach (var balance in conn.Select<BigDecimal>(cypher)) balances.Add(balance);
            await Assert.That(balances).IsEquivalentTo(new[] { BigDecimal.Parse(exact) });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // --------------------------------------------------------------------- laziness and validation

    /// <summary>
    /// Nothing runs until enumeration starts: a statement the engine rejects produces no exception
    /// from the <see cref="LadybugConnection.Select{T}"/> call itself, only from the first
    /// <c>MoveNextAsync</c>. That is what "streams rather than materializes" means at the entry point,
    /// and it is why the argument checks that CAN be eager are - see below.
    /// </summary>
    [Test]
    public async Task Select_DoesNotRunTheQueryUntilEnumerationStarts()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            // No await, no throw: the iterator has not been started.
            var pending = conn.Select<Person>("THIS IS NOT CYPHER");

            await Assert.ThrowsAsync<LadybugException>(async () =>
            {
                await using var rows = pending.GetAsyncEnumerator();
                await rows.MoveNextAsync();
            });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A null/blank <c>cypher</c> is rejected by the call itself, not deferred to the first
    /// <c>MoveNextAsync</c> - the one validation an iterator can still do eagerly, and the one a
    /// caller is most likely to hit by passing a variable that was never set.
    /// </summary>
    [Test]
    public async Task Select_ValidatesCypherEagerlyRatherThanOnFirstMoveNext()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            // Not awaited and never enumerated: the throw must come from the call.
            Assert.Throws<ArgumentException>(() => conn.Select<Person>("   "));
            Assert.Throws<ArgumentException>(() => conn.Select<Person>(null!));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An unusable parameter bag surfaces on enumeration (it cannot be known before the query runs)
    /// and still names the parameter - the projection does not swallow or re-wrap
    /// <see cref="Mapping.ParameterBinder"/>'s error.
    /// </summary>
    [Test]
    public async Task Select_ReportsAnUnbindableParameterValue()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await foreach (var _ in conn.Select<Person>(
                    "MATCH (o:Object) WHERE o.dbref = $min RETURN o.dbref AS Dbref, o.name AS Name",
                    new { min = new object() })) { }
            });

            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("'min'");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Cancellation mid-enumeration stops the stream, whether the token was passed to
    /// <see cref="LadybugConnection.Select{T}"/> or attached with <c>WithCancellation</c> - both route
    /// into the same annotated iterator parameter. The rows consumed before the cancel are asserted so
    /// this cannot pass by never having started.
    /// </summary>
    [Test]
    public async Task Select_HonoursCancellationMidEnumeration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Q(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 50; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:Q {{id: {i}}})")) { }

            const string cypher = "MATCH (n:Q) RETURN n.id AS Id";

            using (var cts = new CancellationTokenSource())
            {
                var seen = 0;
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var _ in conn.Select<long>(cypher, null, cts.Token))
                    {
                        if (++seen == 5) await cts.CancelAsync();
                    }
                });
                await Assert.That(seen).IsEqualTo(5);
            }

            using (var cts = new CancellationTokenSource())
            {
                var seen = 0;
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var _ in conn.Select<long>(cypher).WithCancellation(cts.Token))
                    {
                        if (++seen == 3) await cts.CancelAsync();
                    }
                });
                await Assert.That(seen).IsEqualTo(3);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Two queries returning different column shapes for the same record both project correctly through
    /// <see cref="LadybugConnection.Select{T}"/> - the plan cache is keyed by shape, and a projection
    /// resolved once per query is exactly where a type-only key would read the wrong columns.
    /// </summary>
    [Test]
    public async Task Select_ProjectsTwoColumnShapesOfTheSameRecord()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var forward = new List<Person>();
            await foreach (var person in conn.Select<Person>(
                "MATCH (o:Object) WHERE o.dbref = 1 RETURN o.dbref AS Dbref, o.name AS Name"))
            {
                forward.Add(person);
            }

            var reversed = new List<Person>();
            await foreach (var person in conn.Select<Person>(
                "MATCH (o:Object) WHERE o.dbref = 2 RETURN o.name AS Name, o.dbref AS Dbref"))
            {
                reversed.Add(person);
            }

            await Assert.That(forward).IsEquivalentTo(new[] { new Person(1, "Limbo") });
            await Assert.That(reversed).IsEquivalentTo(new[] { new Person(2, "Master Room") });
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
