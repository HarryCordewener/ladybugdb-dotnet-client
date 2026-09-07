using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The code blocks of docs/GUIDE.md, run in the guide's order against one database so each
/// section's sample really works on the state the previous one left. Edit the guide and this file
/// together.
/// </summary>
public class GuideSamplesTests
{
    private record GameObject(long Dbref, string Name);

    [Node("Object")] private record Obj([property: Key] long Dbref, string Name);
    [Node("Attr")] private record AttrNode([property: Key] string Key, string Name, string Value);
    [Rel("Has", From = typeof(Obj), To = typeof(AttrNode))] private record Has;

    private static async Task WithRetryAsync(Func<Task> work, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await work(); return; }
            catch (LadybugWriteConflictException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt));
            }
        }
    }

    [Test]
    public async Task TheGuide_RunsEndToEnd()
    {
        var path = TestDatabase.NewPath();
        var csv = Path.Combine(Path.GetTempPath(), $"objects-{Guid.NewGuid():N}.csv");
        try
        {
            // 2. Open a database
            var config = new LadybugConfig { MaxThreads = 1, EnableMultiWrites = true };
            using var db = new LadybugDatabase(path, config);
            await using var conn = await db.ConnectAsync();

            // 3. Define the schema
            await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
            await conn.ExecuteAsync("CREATE NODE TABLE Attr(key STRING, name STRING, value STRING, PRIMARY KEY(key))");
            await conn.ExecuteAsync("CREATE REL TABLE Has(FROM Object TO Attr)");
            await conn.ExecuteAsync("CREATE REL TABLE Located(FROM Object TO Object)");

            // 4. Write
            await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 1L, name = "Limbo" });
            await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 2L, name = "Wizard" });
            await conn.ExecuteAsync(
                "MATCH (o:Object {dbref: $dbref}) CREATE (o)-[:Has]->(:Attr {key: $key, name: $name, value: $value})",
                new { dbref = 2L, key = "2/DESC", name = "DESC", value = "A tall figure." });
            await conn.ExecuteAsync(
                "MATCH (a:Object {dbref: $a}), (b:Object {dbref: $b}) CREATE (a)-[:Located]->(b)",
                new { a = 2L, b = 1L });

            File.WriteAllText(csv, "dbref,name\n10,Kitchen\n11,Garden\n");
            var escaped = csv.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
            await conn.ExecuteAsync($"COPY Object FROM '{escaped}' (HEADER=true)");

            // 5. Read
            var lines = new List<string>();
            await using (var result = await conn.QueryAsync(
                "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.dbref, o.name ORDER BY o.dbref", new { min = 1L }))
            {
                await foreach (var row in result) lines.Add($"#{row.GetInt64(0)} {row.GetString("o.name")}");
            }
            await Assert.That(lines).IsEquivalentTo(new List<string> { "#1 Limbo", "#2 Wizard", "#10 Kitchen", "#11 Garden" });

            var objects = new List<GameObject>();
            await foreach (var o in conn.Select<GameObject>("MATCH (o:Object) RETURN o.dbref, o.name ORDER BY o.dbref"))
                objects.Add(o);
            await Assert.That(objects[1]).IsEqualTo(new GameObject(2, "Wizard"));

            var count = await conn.Select<long>("MATCH (o:Object) RETURN count(o)").FirstAsync();
            await Assert.That(count).IsEqualTo(4L);

            await using (var nodes = await conn.QueryAsync("MATCH (o:Object {dbref: $d}) RETURN o", new { d = 2L }))
            {
                await foreach (var row in nodes)
                {
                    var node = row.GetValue(0).AsNode();
                    await Assert.That($"{node.Label} {node.Properties["name"].AsString()}").IsEqualTo("Object Wizard");
                }
            }

            // 6. Prepared statements
            var names = new List<string>();
            await using (var byDbref = await conn.PrepareAsync("MATCH (o:Object {dbref: $d}) RETURN o.name"))
            {
                foreach (var d in new[] { 1L, 2L })
                {
                    byDbref.Bind("d", d);
                    await using var r = await byDbref.ExecuteAsync();
                    await foreach (var row in r) names.Add(row.GetString(0));
                }
            }
            await Assert.That(names).IsEquivalentTo(new List<string> { "Limbo", "Wizard" });

            // 7. Transactions and conflicts
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync("MATCH (a:Attr {key: $key}) SET a.value = $value", new { key = "2/DESC", value = "A short figure." });
                await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 3L, name = "Lamp" });
                await tx.CommitAsync();
            }
            await WithRetryAsync(() => conn.ExecuteAsync(
                "MATCH (a:Attr {key: $key}) SET a.value = $value", new { key = "2/DESC", value = "A figure." }).AsTask());
            var desc = await conn.Select<string>("MATCH (a:Attr {key: $key}) RETURN a.value", new { key = "2/DESC" }).FirstAsync();
            await Assert.That(desc).IsEqualTo("A figure.");

            // 8. Cancellation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            var stopped = false;
            try
            {
                await conn.QueryAsync(
                    "UNWIND range(1, 20000) AS a UNWIND range(1, 20000) AS b WITH a * b AS x WHERE x % 7 = 0 RETURN count(x)",
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            await Assert.That(stopped).IsTrue();

            // 12. The single-pass rule the troubleshooting table describes
            await using var once = await conn.QueryAsync("RETURN 1");
            await foreach (var _ in once) { }
            await Assert.That(() => once.GetAsyncEnumerator()).Throws<InvalidOperationException>();
        }
        finally
        {
            TestDatabase.Cleanup(path);
            try { File.Delete(csv); } catch { /* best effort */ }
        }
    }

    /// <summary>Section 5b, on the state section 4 leaves: objects 1 (Limbo) and 2 (Wizard), 2 located in 1, DESC on 2.</summary>
    [Test]
    public async Task TheLinqSection_RunsOnTheGuidesData()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
            await conn.ExecuteAsync("CREATE NODE TABLE Attr(key STRING, name STRING, value STRING, PRIMARY KEY(key))");
            await conn.ExecuteAsync("CREATE REL TABLE Has(FROM Object TO Attr)");
            await conn.ExecuteAsync("CREATE REL TABLE Located(FROM Object TO Object)");
            await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 1L, name = "Limbo" });
            await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 2L, name = "Wizard" });
            await conn.ExecuteAsync(
                "MATCH (o:Object {dbref: $dbref}) CREATE (o)-[:Has]->(:Attr {key: $key, name: $name, value: $value})",
                new { dbref = 2L, key = "2/DESC", name = "DESC", value = "A tall figure." });
            await conn.ExecuteAsync(
                "MATCH (a:Object {dbref: $a}), (b:Object {dbref: $b}) CREATE (a)-[:Located]->(b)",
                new { a = 2L, b = 1L });

            // 5b. LINQ
            var wizard = await conn.Nodes<Obj>().Where(o => o.Dbref == 2).Select(o => o.Name).SingleAsync();
            await Assert.That(wizard).IsEqualTo("Wizard");

            var desc = await conn.Nodes<Obj>()
                .Where(o => o.Dbref == 2)
                .Out<Obj, Has, AttrNode>()
                .Where(p => p.Target.Name == "DESC")
                .Select(p => p.Target.Value)
                .FirstOrDefaultAsync();
            await Assert.That(desc).IsEqualTo("A tall figure.");
            await Assert.That(conn.Nodes<Obj>().Where(o => o.Dbref == 2).Out<Obj, Has, AttrNode>().Where(p => p.Target.Name == "DESC").Select(p => p.Target.Value).ToString())
                .IsEqualTo("MATCH (n0:Object)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.name = $p1 RETURN n1.value AS Value");

            var exits = await conn.Match<Obj>("(r:Object {dbref: $room})<-[:Located]-(n:Object)", new { room = 1L })
                .Select(n => n.Name).ToListAsync();
            await Assert.That(exits).IsEquivalentTo(["Wizard"]);
        }
        finally
        {
            TestDatabase.Cleanup(path);
        }
    }
}
