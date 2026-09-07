using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The convenience surface: running a statement without handling a result, reading a column in one
/// hop, projecting from a reused prepared statement, asking a result for its column shape, and
/// running statements through a transaction object.
/// </summary>
/// <remarks>
/// None of these add engine behaviour - each is a shorter spelling of something already possible.
/// So the tests are about the shorter spelling meaning exactly the longer one, especially where a
/// result's lifetime is now owned by the client instead of the caller.
/// </remarks>
public class UsabilitySurfaceTests
{
    private static async Task<LadybugConnection> Seeded(LadybugDatabase db)
    {
        var conn = await db.ConnectAsync();
        await conn.ExecuteAsync(
            "CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
        await conn.ExecuteAsync("CREATE (:Object {dbref: 42, name: 'Limbo'})");
        await conn.ExecuteAsync("CREATE (:Object {dbref: 43, name: 'Hall'})");
        return conn;
    }

    [Test]
    public async Task ExecuteAsync_RunsDdlAndDmlWithoutTheCallerHandlingAResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await Assert.That(await conn.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync())
                .IsEqualTo(2L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ExecuteAsync_BindsParameters()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await conn.ExecuteAsync(
                "CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 44L, name = "Attic" });

            var name = await conn
                .Select<string>("MATCH (o:Object) WHERE o.dbref = 44 RETURN o.name").FirstAsync();
            await Assert.That(name).IsEqualTo("Attic");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The result <see cref="LadybugConnection.ExecuteAsync(string, CancellationToken)"/> disposes for
    /// the caller must actually be released - it owns native memory, and this method is the one place
    /// the caller no longer holds it.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ExecuteAsync_ReleasesTheResultItOwns()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            var before = LadybugQueryResult.LiveCount;
            for (var i = 100; i < 120; i++)
            {
                await conn.ExecuteAsync($"CREATE (:Object {{dbref: {i}, name: 'n{i}'}})");
            }

            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(before);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A transaction-control statement run through <c>ExecuteAsync</c> must be tracked exactly as it
    /// is through <c>QueryAsync</c> - the convenience path must not be a hole in the nested-BEGIN
    /// guard.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_IsCoveredByTheTransactionGuard()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await conn.ExecuteAsync("BEGIN TRANSACTION");

            await Assert.That(async () => await conn.ExecuteAsync("BEGIN TRANSACTION"))
                .Throws<InvalidOperationException>();
            await Assert.That(async () => await conn.BeginTransactionAsync())
                .Throws<InvalidOperationException>();

            await conn.ExecuteAsync("ROLLBACK");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task TypedRowAccessors_ReadTheSameValuesAsGetValue()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var r = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref = 42 RETURN o.dbref AS d, o.name AS n");
            var seen = 0;
            await foreach (var row in r)
            {
                seen++;
                await Assert.That(row.GetInt64(0)).IsEqualTo(row.GetValue(0).AsInt64());
                await Assert.That(row.GetInt64("d")).IsEqualTo(42L);
                await Assert.That(row.GetString(1)).IsEqualTo(row.GetValue(1).AsString());
                await Assert.That(row.GetString("n")).IsEqualTo("Limbo");
            }

            await Assert.That(seen).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A typed accessor must not soften the error the underlying <c>As*</c> accessor raises - it is
    /// the same conversion, so it must be the same refusal.
    /// </summary>
    [Test]
    public async Task TypedRowAccessor_OnAWrongType_ThrowsLikeTheUnderlyingAccessor()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var r = await conn.QueryAsync("MATCH (o:Object) RETURN o.name AS n");
            await foreach (var row in r)
            {
                // Not a guess at which exception type: whatever As* raises for this column, the
                // typed accessor must raise the same, since it is the same conversion.
                var viaValue = Assert.Throws<Exception>(() => row.GetValue(0).AsInt64());
                var viaAccessor = Assert.Throws<Exception>(() => row.GetInt64("n"));

                await Assert.That(viaAccessor!.GetType()).IsEqualTo(viaValue!.GetType());
                await Assert.That(viaAccessor.Message).IsEqualTo(viaValue.Message);
                break;
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The combination that was previously impossible: plan the statement once, and project each
    /// execution into a typed shape.
    /// </summary>
    [Test]
    public async Task PreparedStatementSelect_ProjectsAcrossRepeatedExecutions()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var stmt = await conn.PrepareAsync(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.dbref AS Dbref, o.name AS Name " +
                "ORDER BY o.dbref");

            var first = new List<string>();
            await foreach (var o in stmt.Select<Projected>(new { min = 42L })) first.Add(o.Name);

            var second = new List<string>();
            await foreach (var o in stmt.Select<Projected>(new { min = 43L })) second.Add(o.Name);

            await Assert.That(first).IsEquivalentTo(new[] { "Limbo", "Hall" });
            await Assert.That(second).IsEquivalentTo(new[] { "Hall" });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task PreparedStatementSelect_SupportsScalarUnwrap()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var stmt = await conn.PrepareAsync(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN count(*)");

            await Assert.That(await stmt.Select<long>(new { min = 42L }).FirstAsync()).IsEqualTo(2L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Breaking out of a prepared statement's projection early must release its result, for the same
    /// reason it must on the connection's: the caller never receives it, so nothing else can.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task PreparedStatementSelect_EarlyBreak_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var stmt = await conn.PrepareAsync(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.dbref AS Dbref, o.name AS Name");

            var before = LadybugQueryResult.LiveCount;
            var whileStreaming = before;
            await foreach (var o in stmt.Select<Projected>(new { min = 42L }))
            {
                whileStreaming = LadybugQueryResult.LiveCount;
                break;
            }

            await Assert.That(whileStreaming).IsEqualTo(before + 1);
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(before);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The shape is on the result, not only on a row, so a result with no rows can still report it.
    /// </summary>
    [Test]
    public async Task ColumnNames_AreReadableIncludingFromAZeroRowResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var empty = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref > 9999 RETURN o.dbref AS d, o.name AS n");

            await Assert.That(empty.ColumnNames).IsEquivalentTo(new[] { "d", "n" });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <see cref="LadybugQueryResult.ColumnNames"/> hands out a read-only view, not the array it
    /// owns. <see cref="string"/><c>[]</c> implements <see cref="IReadOnlyList{T}"/>, so returning it
    /// directly would let a caller cast back and rewrite the result's own column names.
    /// </summary>
    [Test]
    public async Task ColumnNames_CannotBeCastBackToTheUnderlyingArray()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using var r = await conn.QueryAsync("MATCH (o:Object) RETURN o.name AS n");
            await Assert.That(r.ColumnNames as string[]).IsNull();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task TransactionDelegation_RunsStatementsInsideTheTransaction()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await tx.ExecuteAsync(
                    "CREATE (:Object {dbref: $dbref, name: 'Attic'})", new { dbref = 44L });

                await Assert.That(await tx.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync())
                    .IsEqualTo(3L);
                await Assert.That(tx.Connection).IsSameReferenceAs(conn);

                await tx.CommitAsync();
            }

            await Assert.That(await conn.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync())
                .IsEqualTo(3L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Delegation must not imply scoping: work run through the transaction object is rolled back with
    /// everything else when the transaction is not committed, because it was never separate from it.
    /// </summary>
    [Test]
    public async Task TransactionDelegation_WorkRollsBackWithTheTransaction()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await Seeded(db);

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await tx.ExecuteAsync("CREATE (:Object {dbref: 44, name: 'Attic'})");
            }

            await Assert.That(await conn.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync())
                .IsEqualTo(2L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    private record Projected(long Dbref, string Name);
}
