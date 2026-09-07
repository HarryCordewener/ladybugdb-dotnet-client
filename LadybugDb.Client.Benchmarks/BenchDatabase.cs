using System.Globalization;
using System.Text;
using LadybugDb.Client;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// A MUSH-shaped database on disk: <c>Obj</c> nodes keyed by dbref with a name and a location,
/// ten sparse attributes per object, and <c>Located</c> edges from each object to its location.
/// Mirrors the schema of <c>benchmarks/workload_bench.py</c> so numbers are comparable with the
/// Python engine control. Bulk-loaded with <c>COPY FROM</c> CSV, which is setup, not a measured path.
/// </summary>
internal sealed class BenchDatabase : IDisposable
{
    internal const int AttrsPerObj = 10;
    internal static readonly string[] AttrNames =
        ["DESC", "SEX", "LAST", "ALIAS", "IDLE", "AWAY", "EMAIL", "MAILCURF", "TZ", "PENNIES"];

    public string Path { get; }
    public string Model { get; }
    public int Size { get; }
    public LadybugDatabase Database { get; private set; }
    public double LoadSeconds { get; private set; }

    private readonly LadybugConfig _config;

    private BenchDatabase(string path, string model, int size, LadybugConfig config)
    {
        Path = path;
        Model = model;
        Size = size;
        _config = config;
        Database = new LadybugDatabase(path, config);
    }

    public static string NewPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lbug-bench-{Guid.NewGuid():N}");

    /// <param name="size">How many objects to create; each gets ten attributes and one location edge.</param>
    /// <param name="model"><c>edge</c> (attributes as their own node table) or <c>map</c> (attributes as a MAP column).</param>
    /// <param name="config">Engine configuration, or the defaults.</param>
    /// <param name="seed">Seed for the location assignment.</param>
    public static BenchDatabase Create(int size, string model = "edge", LadybugConfig? config = null, int seed = 20260727)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(size, 0);
        // An unrecognized model would silently take the edge branch and then be recorded under its
        // own name in the results, publishing a mislabeled benchmark.
        if (model is not ("edge" or "map"))
            throw new ArgumentException($"Unknown schema model '{model}'; expected 'edge' or 'map'.", nameof(model));

        var path = NewPath();
        Cleanup(path);
        var db = new BenchDatabase(path, model, size, config ?? new LadybugConfig());
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            db.LoadAsync(new Random(seed)).GetAwaiter().GetResult();
            db.LoadSeconds = sw.Elapsed.TotalSeconds;
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Closes and reopens the database, returning the time the reopen and a first point lookup took.</summary>
    public async Task<TimeSpan> ReopenAsync()
    {
        Database.Dispose();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Database = new LadybugDatabase(Path, _config);
        await using var conn = await Database.ConnectAsync();
        await using var r = await conn.QueryAsync("MATCH (o:Obj) WHERE o.dbref = 0 RETURN o.dbref");
        _ = r.HasNext;
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task LoadAsync(Random rng)
    {
        var dir = Path + "-csv";
        Directory.CreateDirectory(dir);
        try
        {
            var objCsv = System.IO.Path.Combine(dir, "obj.csv");
            var attrCsv = System.IO.Path.Combine(dir, "attr.csv");
            var hasCsv = System.IO.Path.Combine(dir, "has.csv");
            var locCsv = System.IO.Path.Combine(dir, "located.csv");

            using (var obj = new StreamWriter(objCsv, false, new UTF8Encoding(false)))
            using (var attr = new StreamWriter(attrCsv, false, new UTF8Encoding(false)))
            using (var has = new StreamWriter(hasCsv, false, new UTF8Encoding(false)))
            using (var loc = new StreamWriter(locCsv, false, new UTF8Encoding(false)))
            {
                obj.WriteLine(Model == "map" ? "dbref,name,loc,attrs" : "dbref,name,loc");
                attr.WriteLine("akey,aname,aval");
                has.WriteLine("from,to");
                loc.WriteLine("from,to");
                var rooms = Math.Max(1, Size / 10);
                for (var i = 0; i < Size; i++)
                {
                    var location = rng.Next(0, rooms);
                    if (Model == "map")
                    {
                        var map = new StringBuilder("\"{");
                        for (var j = 0; j < AttrsPerObj; j++)
                        {
                            if (j > 0) map.Append(", ");
                            map.Append(AttrNames[j]).Append('=').Append('v').Append(i).Append('_').Append(j);
                        }
                        map.Append("}\"");
                        obj.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},obj{i},{location},{map}"));
                    }
                    else
                    {
                        obj.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},obj{i},{location}"));
                        for (var j = 0; j < AttrsPerObj; j++)
                        {
                            attr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i}/{AttrNames[j]},{AttrNames[j]},v{i}_{j}"));
                            has.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},{i}/{AttrNames[j]}"));
                        }
                    }
                    loc.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},{location}"));
                }
            }

            await using var conn = await Database.ConnectAsync();
            async Task Run(string cypher) => await conn.ExecuteAsync(cypher);

            if (Model == "map")
            {
                    await Run("CREATE NODE TABLE Obj(dbref INT64, name STRING, loc INT64, attrs MAP(STRING,STRING), PRIMARY KEY(dbref))");
            }
            else
            {
                await Run("CREATE NODE TABLE Obj(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref))");
                await Run("CREATE NODE TABLE Attr(akey STRING, aname STRING, aval STRING, PRIMARY KEY(akey))");
                await Run("CREATE REL TABLE Has(FROM Obj TO Attr)");
            }
            await Run("CREATE REL TABLE Located(FROM Obj TO Obj)");

            await Run($"COPY Obj FROM '{Escape(objCsv)}' (HEADER=true)");
            if (Model == "edge")
            {
                await Run($"COPY Attr FROM '{Escape(attrCsv)}' (HEADER=true)");
                await Run($"COPY Has FROM '{Escape(hasCsv)}' (HEADER=true)");
            }
            await Run($"COPY Located FROM '{Escape(locCsv)}' (HEADER=true)");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Ladybug string literals escape with a backslash, never SQL-style doubling.</summary>
    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    public static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    public long DiskBytes()
    {
        long total = 0;
        foreach (var p in new[] { Path, Path + ".wal", Path + ".shadow", Path + ".lock", Path + ".tmp" })
        {
            try { if (File.Exists(p)) total += new FileInfo(p).Length; } catch { /* best effort */ }
        }
        return total;
    }

    public void Dispose()
    {
        Database.Dispose();
        Cleanup(Path);
    }
}
