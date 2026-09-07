using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests.Linq;

[Node("Object")]
public sealed record NarrowObj([property: Key] long Dbref, string Name, int Loc);

[Node("Object")]
public sealed record WrongKeyObj(long Dbref, [property: Key] string Name);

[Node("Has")]
public sealed record HasAsNode([property: Key] long Since);

[Node("Nowhere")]
public sealed record Nowhere([property: Key] long Id);

[Rel("Has", From = typeof(Obj), To = typeof(Obj))]
public sealed record HasBetweenObjects;

[Node("my table")]
public sealed record Spaced([property: Key, Column("my prop")] long Id, [property: Column("it's")] string Quoted);

/// <summary>
/// <see cref="LadybugSchema"/> against a real catalog: the DDL it renders creates tables its own
/// validation accepts, and every kind of drift between the two is reported by table and column.
/// Also pins the exact column names the three catalog <c>CALL</c>s return, since
/// <c>CatalogReader</c> reads them by name.
/// </summary>
public class SchemaValidationTests
{
    private static async Task Fresh(Func<LadybugConnection, Task> body)
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await body(conn);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    private static async Task<IReadOnlyList<string>> Columns(LadybugConnection conn, string cypher)
    {
        await using var result = await conn.QueryAsync(cypher);
        return result.ColumnNames;
    }

    [Test]
    public Task CatalogCalls_ReturnTheColumnNames_CatalogReaderReadsByName() => MushDatabase.Check(async conn =>
    {
        await Assert.That(await Columns(conn, "CALL show_tables() RETURN *"))
            .IsEquivalentTo(["id", "name", "type", "database name", "comment"]);
        await Assert.That(await Columns(conn, "CALL table_info('Object') RETURN *"))
            .IsEquivalentTo(["property id", "name", "type", "default expression", "primary key"]);
        await Assert.That(await Columns(conn, "CALL table_info('Has') RETURN *"))
            .IsEquivalentTo(["property id", "name", "type", "default expression", "storage_direction"]);
        await Assert.That(await Columns(conn, "CALL show_connection('Has') RETURN *"))
            .IsEquivalentTo(["source table name", "destination table name", "source table primary key", "destination table primary key"]);

        var tables = await CatalogReader.ShowTablesAsync(conn, default);
        await Assert.That(tables.Select(t => (t.Name, t.Kind)).Order())
            .IsEquivalentTo([("Attr", "NODE"), ("Has", "REL"), ("Located", "REL"), ("Object", "NODE")]);
        var columns = await CatalogReader.TableInfoAsync(conn, "Object", default);
        await Assert.That(columns).IsEquivalentTo([new CatalogColumn("dbref", "INT64", true), new CatalogColumn("name", "STRING", false), new CatalogColumn("loc", "INT64", false)]);
        var relColumns = await CatalogReader.TableInfoAsync(conn, "Has", default);
        await Assert.That(relColumns).IsEquivalentTo([new CatalogColumn("since", "INT64", false)]);
        var connections = await CatalogReader.ShowConnectionAsync(conn, "Has", default);
        await Assert.That(connections).IsEquivalentTo([new CatalogConnection("Object", "Attr")]);
    }, seed: false);

    [Test]
    public Task CreateTables_ThenValidate_Passes() => MushDatabase.Check(
        conn => MushDatabase.Schema.ValidateAsync(conn).AsTask(), seed: false);

    [Test]
    public Task MissingColumn_IsReported_ByTableAndColumn() => Fresh(async conn =>
    {
        await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => LadybugSchema.For(typeof(Obj)).ValidateAsync(conn).AsTask());
        await Assert.That(ex!.Mismatches.Count).IsEqualTo(1);
        await Assert.That(ex.Mismatches[0]).Contains("'Object'");
        await Assert.That(ex.Mismatches[0]).Contains("'loc'");
        await Assert.That(ex.Message).Contains("'loc'");
    });

    /// <summary>The widening rule, both ways: a CLR <c>int</c> cannot read an <c>INT64</c> column; a CLR <c>long</c> reads an <c>INT32</c> one.</summary>
    [Test]
    public Task ColumnType_NarrowerClrTypeIsAMismatch_WiderIsNot() => Fresh(async conn =>
    {
        await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT32, PRIMARY KEY(dbref))");
        await LadybugSchema.For(typeof(Obj)).ValidateAsync(conn);

        await conn.ExecuteAsync("DROP TABLE Object");
        await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref))");
        await LadybugSchema.For(typeof(Obj)).ValidateAsync(conn);
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => LadybugSchema.For(typeof(NarrowObj)).ValidateAsync(conn).AsTask());
        await Assert.That(ex!.Mismatches.Count).IsEqualTo(1);
        await Assert.That(ex.Mismatches[0]).Contains("'loc'");
        await Assert.That(ex.Mismatches[0]).Contains("INT64");
        await Assert.That(ex.Mismatches[0]).Contains("int");
    });

    [Test]
    public Task MissingTable_WrongKind_AndWrongKey_AreAllReportedTogether() => MushDatabase.Check(async conn =>
    {
        var schema = LadybugSchema.For(typeof(Nowhere), typeof(HasAsNode), typeof(WrongKeyObj));
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => schema.ValidateAsync(conn).AsTask());
        await Assert.That(ex!.Mismatches.Count).IsEqualTo(3);
        await Assert.That(ex.Mismatches[0]).Contains("'Nowhere'").And.Contains("does not exist");
        await Assert.That(ex.Mismatches[1]).Contains("'Has'").And.Contains("REL table");
        await Assert.That(ex.Mismatches[2]).Contains("'Object'").And.Contains("'dbref'").And.Contains("'Name'");
    }, seed: false);

    [Test]
    public Task RelEndpoints_AreChecked() => MushDatabase.Check(async conn =>
    {
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => LadybugSchema.For(typeof(HasBetweenObjects)).ValidateAsync(conn).AsTask());
        await Assert.That(ex!.Mismatches.Count).IsEqualTo(1);
        await Assert.That(ex.Mismatches[0]).Contains("'Has'").And.Contains("'Object' to 'Attr'").And.Contains("HasBetweenObjects");
    }, seed: false);

    /// <summary>Names that need backticks in DDL and quoting in the catalog CALLs round-trip.</summary>
    [Test]
    public Task NamesNeedingQuotes_CreateAndValidate() => Fresh(async conn =>
    {
        var schema = LadybugSchema.For(typeof(Spaced));
        await schema.CreateTablesAsync(conn);
        await schema.ValidateAsync(conn);
    });

    /// <summary>A second CreateTablesAsync on the same database is the engine's error, not a silent no-op.</summary>
    [Test]
    public Task CreateTables_Twice_IsTheEnginesError() => MushDatabase.Check(async conn =>
    {
        var ex = await Assert.ThrowsAsync<LadybugException>(() => MushDatabase.Schema.CreateTablesAsync(conn).AsTask());
        await Assert.That(ex!.Message).Contains("Object");
    }, seed: false);
}
