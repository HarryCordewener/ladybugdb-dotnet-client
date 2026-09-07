using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Characterization of the row read path: every scalar column, NULLs, and a nested container,
/// read through the typed accessors. Written before <c>LadybugQueryResult.ReadRow</c> moved from
/// per-cell <c>SafeHandle</c>s and per-cell type lookups to stack-allocated borrows and column
/// types cached once per result, so the rewrite had a behavioural pin to keep.
/// </summary>
public class ReadPathTests
{
    [Test]
    public async Task EveryScalarColumnReadsThroughTheCachedColumnType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, s STRING, d DOUBLE, b BOOL, n INT32, l INT64[], PRIMARY KEY(id))");
            await conn.ExecuteAsync("CREATE (:T {id: 1, s: 'a', d: 1.5, b: true, n: 7, l: [1, 2]})");
            await conn.ExecuteAsync("CREATE (:T {id: 2, s: 'b', d: 2.5, b: false, n: 8, l: []})");
            await conn.ExecuteAsync("CREATE (:T {id: 3})");

            var rows = new List<(long, string?, double?, bool?, int?, int?)>();
            await using var r = await conn.QueryAsync("MATCH (t:T) RETURN t.id, t.s, t.d, t.b, t.n, t.l ORDER BY t.id");
            await foreach (var row in r)
            {
                rows.Add((row.GetInt64(0),
                    row.GetValue(1).IsNull ? null : row.GetString(1),
                    row.GetValue(2).IsNull ? null : row.GetDouble(2),
                    row.GetValue(3).IsNull ? null : row.GetBoolean(3),
                    row.GetValue(4).IsNull ? null : row.GetInt32(4),
                    row.GetValue(5).IsNull ? null : row.GetValue(5).AsList().Count));
            }

            var expected = new List<(long, string?, double?, bool?, int?, int?)>
            {
                (1L, "a", 1.5, true, 7, 2),
                (2L, "b", 2.5, false, 8, 0),
                (3L, null, null, null, null, null),
            };
            await Assert.That(rows).IsEquivalentTo(expected);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A NODE column and a scalar column side by side: the cached column type dispatches the node
    /// through the full reader (properties and all) while the scalar takes the direct path.
    /// </summary>
    [Test]
    public async Task NodeAndScalarColumns_ReadSideBySide()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, s STRING, PRIMARY KEY(id))");
            await conn.ExecuteAsync("CREATE (:T {id: 1, s: 'a'})");

            await using var r = await conn.QueryAsync("MATCH (t:T) RETURN t, t.id");
            await foreach (var row in r)
            {
                var node = row.GetValue(0).AsNode();
                await Assert.That(node.Label).IsEqualTo("T");
                await Assert.That(node.Properties["s"].AsString()).IsEqualTo("a");
                await Assert.That(row.GetInt64(1)).IsEqualTo(1L);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
