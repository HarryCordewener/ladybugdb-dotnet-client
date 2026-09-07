using LadybugDb.Client.Schema;

namespace LadybugDb.Client.IntegrationTests.Linq;

/// <summary>The SharpMUSH-shaped schema from the LINQ design: objects with attributes, located in other objects.</summary>
[Node("Object")]
public sealed record Obj([property: Key] long Dbref, string Name, long? Loc);

[Node("Attr")]
public sealed record Attr([property: Key] string Akey, string Aname, string Aval);

[Rel("Has", From = typeof(Obj), To = typeof(Attr))]
public sealed record Has(long Since);

[Rel("Located", From = typeof(Obj), To = typeof(Obj))]
public sealed record Located;

/// <summary>
/// Opens a fresh database with the MUSH schema created through <see cref="LadybugSchema.CreateTablesAsync"/>
/// and, optionally, seeded: objects 1..20 named <c>obj1</c>..<c>obj20</c>, each <c>Located</c> in
/// object <c>(dbref % 5) + 1</c> (<c>loc</c> holds the same number), and 10 attributes per object
/// (<c>akey</c> <c>"{dbref}/A{i}"</c>, <c>aname</c> <c>"A{i}"</c>, <c>aval</c> <c>"v{dbref}.{i}"</c>)
/// attached through <c>Has {since: i}</c>.
/// </summary>
internal static class MushDatabase
{
    internal static readonly LadybugSchema Schema = LadybugSchema.For(typeof(Obj), typeof(Attr), typeof(Has), typeof(Located));

    internal static async Task<(LadybugDatabase Db, LadybugConnection Connection)> Open(string path, bool seed = true)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await Schema.CreateTablesAsync(conn);
        if (!seed) return (db, conn);

        await using var obj = await conn.PrepareAsync("CREATE (:Object {dbref: $d, name: $n, loc: $l})");
        await using var attr = await conn.PrepareAsync(
            "MATCH (o:Object {dbref: $d}) CREATE (o)-[:Has {since: $s}]->(:Attr {akey: $k, aname: $an, aval: $av})");
        for (var d = 1L; d <= 20; d++)
        {
            await obj.ExecuteNonQueryAsync(new { d, n = $"obj{d}", l = d % 5 + 1 });
            for (var i = 1L; i <= 10; i++)
            {
                await attr.ExecuteNonQueryAsync(new { d, s = i, k = $"{d}/A{i}", an = $"A{i}", av = $"v{d}.{i}" });
            }
        }

        await conn.ExecuteAsync("MATCH (a:Object), (b:Object) WHERE a.loc = b.dbref CREATE (a)-[:Located]->(b)");
        return (db, conn);
    }

    internal static async Task Check(Func<LadybugConnection, Task> body, bool seed = true)
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path, seed);
            using var _db = db;
            await using var _conn = conn;
            await body(conn);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
