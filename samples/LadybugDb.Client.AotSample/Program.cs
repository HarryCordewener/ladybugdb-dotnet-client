// LadybugDb.Client under Native AOT.
//
// Published with PublishAot=true by CI's aot-publish job and then executed; a non-zero exit
// fails the job. The program opens a database, creates a table, inserts through a prepared
// statement, and reads the rows back through LadybugRow's typed accessors, checking every value
// it gets. It deliberately uses only the non-reflective surface: the parameter-object overloads
// and Select<T> are annotated [RequiresUnreferencedCode] and are the part of the client that an
// AOT consumer opts out of (or accepts the trim warning for).

using LadybugDb.Client;

var path = Path.Combine(Path.GetTempPath(), $"lbug-aot-{Guid.NewGuid():N}");
try
{
    using var db = new LadybugDatabase(path, new LadybugConfig { MaxThreads = 2 });
    Console.WriteLine($"engine {LadybugDatabase.EngineVersion} (client generated against {LadybugDatabase.MinimumEngineVersion})");

    await using var conn = await db.ConnectAsync();
    await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, mass DOUBLE, PRIMARY KEY(dbref))");

    await using (var insert = await conn.PrepareAsync("CREATE (o:Object {dbref: $dbref, name: $name, mass: $mass})"))
    {
        foreach (var (dbref, name, mass) in new[] { (42L, "Limbo", 0.5), (43L, "The Void", 1.25) })
        {
            insert.Bind("dbref", dbref);
            insert.Bind("name", name);
            insert.Bind("mass", mass);
            await insert.ExecuteNonQueryAsync();
        }
    }

    var seen = 0;
    await using var result = await conn.QueryAsync(
        "MATCH (o:Object) RETURN o.dbref AS dbref, o.name AS name, o.mass AS mass ORDER BY o.dbref");
    await foreach (var row in result)
    {
        var dbref = row.GetInt64("dbref");
        var name = row.GetString("name");
        var mass = row.GetDouble(2);
        Console.WriteLine($"{dbref}: {name} ({mass})");

        var expected = dbref switch
        {
            42 => ("Limbo", 0.5),
            43 => ("The Void", 1.25),
            _ => throw new InvalidOperationException($"unexpected dbref {dbref}"),
        };
        if (name != expected.Item1 || mass != expected.Item2)
            throw new InvalidOperationException($"row {dbref} read back as ({name}, {mass}), expected {expected}");
        seen++;
    }

    if (seen != 2)
        throw new InvalidOperationException($"expected 2 rows, read {seen}");

    Console.WriteLine("LadybugDb.Client AOT sample: OK");
    return 0;
}
finally
{
    // A database is a file plus .wal/.shadow/.lock/.tmp siblings, not a directory.
    foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { /* best effort */ }
    }
}
