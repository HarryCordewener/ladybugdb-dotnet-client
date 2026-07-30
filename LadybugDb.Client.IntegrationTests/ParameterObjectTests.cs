using System.Collections.Concurrent;
using ExtendedNumerics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The parameter-object surface end-to-end against the real engine:
/// <see cref="LadybugConnection.QueryAsync(string, object, CancellationToken)"/> and
/// <see cref="LadybugPreparedStatement.ExecuteAsync(object, CancellationToken)"/>, each with both
/// accepted parameter forms (an anonymous object and a dictionary).
/// </summary>
/// <remarks>
/// <c>ParameterBinderTests</c> in the unit suite covers the discrimination step in isolation. This
/// class covers what that step cannot: that the names and values it produces reach the engine, bind
/// to the right placeholders, and read back unchanged - in particular for
/// <c>Dictionary&lt;string, long&gt;</c>, the shape that used to bind a dictionary's own property
/// names as parameters and run the query anyway with no error raised.
/// </remarks>
public class ParameterObjectTests
{
    private const string Schema =
        "CREATE NODE TABLE O(id INT64, name STRING, score DOUBLE, PRIMARY KEY(id))";

    private static async Task<(LadybugDatabase Db, LadybugConnection Conn)> Open(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync(Schema)) { }
        return (db, conn);
    }

    // ------------------------------------------------------------------ both forms, both entry points

    /// <summary>
    /// The ergonomic case the design exists for: one call, parameters named by an anonymous object.
    /// </summary>
    [Test]
    public async Task OneShotQueryAsync_AnonymousObject_BindsByPropertyName()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: $name, score: $score})",
                    new { id = 42L, name = "Limbo", score = 1.5 })) { }

                await AssertSingleRow(conn, 42L, "Limbo", 1.5);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The runtime-computed-names case, which the anonymous form cannot express. Read directly with
    /// no reflection - the discrimination is a type test.
    /// </summary>
    [Test]
    public async Task OneShotQueryAsync_Dictionary_BindsByKey()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var parameters = new Dictionary<string, object?>
                {
                    ["id"] = 7L,
                    ["name"] = "Void",
                    ["score"] = 0.25,
                };

                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: $name, score: $score})", parameters)) { }

                await AssertSingleRow(conn, 7L, "Void", 0.25);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>Same two forms, on the prepared-statement entry point.</summary>
    [Test]
    public async Task PreparedExecuteAsync_BothParameterForms_BindByNameAndKey()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using var stmt = await conn.PrepareAsync(
                    "CREATE (n:O {id: $id, name: $name, score: $score})");

                await using (var _ = await stmt.ExecuteAsync(new { id = 1L, name = "anon", score = 1.0 })) { }
                await using (var _ = await stmt.ExecuteAsync(
                    new Dictionary<string, object?> { ["id"] = 2L, ["name"] = "dict", ["score"] = 2.0 })) { }

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN n.id, n.name ORDER BY n.id");
                var seen = new List<(long, string)>();
                await foreach (var row in r)
                    seen.Add((row.GetValue(0).AsInt64(), row.GetValue(1).AsString()));

                await Assert.That(seen).IsEquivalentTo(new[] { (1L, "anon"), (2L, "dict") });
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Prepared once, executed three times with different parameter objects - the reason the
    /// prepared-statement entry point exists alongside the one-shot overload. Also pins that bound
    /// values persist across executions rather than being reset by one.
    /// </summary>
    [Test]
    public async Task PreparedStatement_IsReusableAcrossExecutions_WithParameterObjects()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using var stmt = await conn.PrepareAsync(
                    "CREATE (n:O {id: $id, name: $name, score: $score})");

                for (var i = 1L; i <= 3; i++)
                {
                    await using var _ = await stmt.ExecuteAsync(
                        new { id = i, name = $"n{i}", score = (double)i });
                }

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN count(n)");
                await foreach (var row in r)
                    await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(3L);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // ------------------------------------------------------- the dictionary shape that silently broke

    /// <summary>
    /// <b>The regression that motivated the discrimination order, proven end-to-end.</b> A
    /// <c>Dictionary&lt;string, long&gt;</c> does not implement
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> - generic interfaces are invariant in their
    /// value type - so testing only that interface sent it to the reflection path, where the
    /// "parameters" became <c>Comparer</c>, <c>Count</c>, <c>Capacity</c>, <c>Keys</c>,
    /// <c>Values</c>, and <c>Item</c>. No exception was raised.
    /// </summary>
    /// <remarks>
    /// The unit suite asserts the names produced; this asserts the row that reaches the database, so
    /// the case is covered at the level a caller would actually notice it at. If the non-generic
    /// <c>IDictionary</c> step is ever dropped, this fails with the engine's "Parameter id not
    /// found." rather than passing quietly.
    /// </remarks>
    [Test]
    public async Task DictionaryOfLong_BindsItsKeys_EndToEndThroughTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var parameters = new Dictionary<string, long> { ["id"] = 99L, ["other"] = 5L };

                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: 'from-long-dict', score: 0.0})", parameters)) { }

                await AssertSingleRow(conn, 99L, "from-long-dict", 0.0);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same coverage for the other dictionary shapes the binder claims to handle, so the claim is
    /// not resting on <c>Dictionary&lt;TKey, TValue&gt;</c> alone.
    /// </summary>
    [Test]
    [Arguments("sorted")]
    [Arguments("concurrent")]
    public async Task OtherDictionaryShapes_BindTheirKeys_EndToEndThroughTheEngine(string shape)
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                object parameters = shape switch
                {
                    "sorted" => new SortedDictionary<string, long> { ["id"] = 11L },
                    _ => new ConcurrentDictionary<string, long>(
                        new Dictionary<string, long> { ["id"] = 11L }),
                };

                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: 'x', score: 0.0})", parameters)) { }

                await AssertSingleRow(conn, 11L, "x", 0.0);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A dictionary keyed by anything but <see cref="string"/> names no parameters.</summary>
    [Test]
    public async Task NonStringKeyedDictionary_ThrowsArgumentException_NamingTheKeyType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:O {id: $id, name: 'x', score: 0.0})",
                        new Dictionary<int, long> { [1] = 1L }));

                await Assert.That(ex!.Message).Contains("Int32");
                await Assert.That(ex.ParamName).IsEqualTo("parameters");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // ------------------------------------------------------------------------------ value dispatch

    /// <summary>
    /// Every one of the 19 bindable runtime types reaches its typed <c>Bind</c> overload through the
    /// dispatch, and reads back unchanged. Dispatching one of these to the wrong overload would
    /// corrupt the value silently rather than throwing, so each is round-tripped rather than merely
    /// accepted.
    /// </summary>
    [Test]
    public async Task EveryBindableRuntimeType_DispatchesToItsTypedBindOverload()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE A(id INT64, b BOOL, i8 INT8, i16 INT16, i32 INT32, i64 INT64, " +
                "u8 UINT8, u16 UINT16, u32 UINT32, u64 UINT64, f FLOAT, d DOUBLE, s STRING, " +
                "dt DATE, ts TIMESTAMP, tstz TIMESTAMP_TZ, iv INTERVAL, g UUID, i128 INT128, " +
                "dec DECIMAL(10, 3), PRIMARY KEY(id))")) { }

            var date = new DateOnly(2026, 7, 29);
            var stamp = new DateTime(2026, 7, 29, 1, 2, 3, DateTimeKind.Utc);
            var stampTz = new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero);
            var interval = TimeSpan.FromSeconds(4242);
            var guid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
            var big = Int128.Parse("170141183460469231731687303715884105727");
            var dec = BigDecimal.Parse("12345.678");

            await using (var _ = await conn.QueryAsync(
                "CREATE (n:A {id: 1, b: $b, i8: $i8, i16: $i16, i32: $i32, i64: $i64, u8: $u8, " +
                "u16: $u16, u32: $u32, u64: $u64, f: $f, d: $d, s: $s, dt: $dt, ts: $ts, " +
                "tstz: $tstz, iv: $iv, g: $g, i128: $i128, dec: $dec})",
                new
                {
                    b = true,
                    i8 = sbyte.MinValue,
                    i16 = short.MinValue,
                    i32 = int.MinValue,
                    i64 = long.MinValue,
                    u8 = byte.MaxValue,
                    u16 = ushort.MaxValue,
                    u32 = uint.MaxValue,
                    u64 = ulong.MaxValue,
                    f = 3.5f,
                    d = 2.718281828459045,
                    s = "ladybug",
                    dt = date,
                    ts = stamp,
                    tstz = stampTz,
                    iv = interval,
                    g = guid,
                    i128 = big,
                    dec,
                })) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:A) RETURN n.b, n.i8, n.i16, n.i32, n.i64, n.u8, n.u16, n.u32, n.u64, " +
                "n.f, n.d, n.s, n.dt, n.ts, n.tstz, n.iv, n.g, n.i128, n.dec");

            var rows = 0;
            await foreach (var row in r)
            {
                rows++;
                await Assert.That(row.GetValue(0).AsBoolean()).IsTrue();
                await Assert.That(row.GetValue(1).AsSByte()).IsEqualTo(sbyte.MinValue);
                await Assert.That(row.GetValue(2).AsInt16()).IsEqualTo(short.MinValue);
                await Assert.That(row.GetValue(3).AsInt32()).IsEqualTo(int.MinValue);
                await Assert.That(row.GetValue(4).AsInt64()).IsEqualTo(long.MinValue);
                await Assert.That(row.GetValue(5).AsByte()).IsEqualTo(byte.MaxValue);
                await Assert.That(row.GetValue(6).AsUInt16()).IsEqualTo(ushort.MaxValue);
                await Assert.That(row.GetValue(7).AsUInt32()).IsEqualTo(uint.MaxValue);
                await Assert.That(row.GetValue(8).AsUInt64()).IsEqualTo(ulong.MaxValue);
                await Assert.That(row.GetValue(9).AsSingle()).IsEqualTo(3.5f);
                await Assert.That(row.GetValue(10).AsDouble()).IsEqualTo(2.718281828459045);
                await Assert.That(row.GetValue(11).AsString()).IsEqualTo("ladybug");
                await Assert.That(row.GetValue(12).AsDateOnly()).IsEqualTo(date);
                await Assert.That(row.GetValue(13).AsDateTime()).IsEqualTo(stamp);
                await Assert.That(row.GetValue(14).AsDateTimeOffset()).IsEqualTo(stampTz);
                await Assert.That(row.GetValue(15).AsTimeSpan()).IsEqualTo(interval);
                await Assert.That(row.GetValue(16).AsGuid()).IsEqualTo(guid);
                await Assert.That(row.GetValue(17).AsInt128()).IsEqualTo(big);
                await Assert.That(row.GetValue(18).AsBigDecimal()).IsEqualTo(dec);
            }

            await Assert.That(rows).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A <see langword="null"/> value binds a typed <c>NULL</c>, not a missing parameter.</summary>
    [Test]
    public async Task NullValue_BindsNull()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: 1, name: $name, score: 0.0})",
                    new { name = (string?)null })) { }

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN n.name");
                var rows = 0;
                await foreach (var row in r)
                {
                    rows++;
                    await Assert.That(row.GetValue(0).IsNull).IsTrue();
                }

                await Assert.That(rows).IsEqualTo(1);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An unbindable value names both the parameter and its runtime type - either alone leaves the
    /// caller to guess which parameter, or what about it.
    /// </summary>
    [Test]
    public async Task UnsupportedValueType_ThrowsArgumentException_NamingParameterAndType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:O {id: 1, name: $name, score: 0.0})",
                        new { name = new Uri("https://example.invalid") }));

                await Assert.That(ex!.Message).Contains("'name'");
                await Assert.That(ex.Message).Contains("Uri");
                await Assert.That(ex.ParamName).IsEqualTo("parameters");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same rejection on the prepared-statement entry point, for a value type a caller is
    /// genuinely likely to reach for: <see langword="decimal"/>, which the client does not bind (the
    /// engine's DECIMAL holds 38 digits, <see langword="decimal"/> 28-29, so
    /// <see cref="BigDecimal"/> is the lossless type). The message says what to do about it.
    /// </summary>
    [Test]
    public async Task DecimalValue_IsRejected_AndPointsAtBigDecimal()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using var stmt = await conn.PrepareAsync(
                    "CREATE (n:O {id: 1, name: 'x', score: $score})");

                var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await stmt.ExecuteAsync(new { score = 9.99m }));

                await Assert.That(ex!.Message).Contains("'score'");
                await Assert.That(ex.Message).Contains("Decimal");
                await Assert.That(ex.Message).Contains("BigDecimal");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An <see langword="enum"/> boxes as itself, not as its underlying integer, so it does not match
    /// the <see langword="int"/> case and is reported rather than silently binding its ordinal - which
    /// is the difference between a caller learning their enum is not supported and a column quietly
    /// holding <c>1</c>.
    /// </summary>
    [Test]
    public async Task EnumValue_IsReported_NotSilentlyBoundAsItsOrdinal()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:O {id: $id, name: 'x', score: 0.0})",
                        new { id = DayOfWeek.Monday }));

                await Assert.That(ex!.Message).Contains("'id'");
                await Assert.That(ex.Message).Contains("DayOfWeek");

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN count(n)");
                await foreach (var row in r)
                    await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// One unbindable value leaves the statement's <em>other</em> bound values untouched. The binder
    /// checks every value's type before binding any, so a caller who catches the
    /// <see cref="ArgumentException"/> and executes again gets the values they had, not a half-applied
    /// mixture of old and new.
    /// </summary>
    [Test]
    public async Task UnbindableValue_LeavesPreviouslyBoundValuesUntouched()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using var stmt = await conn.PrepareAsync(
                    "CREATE (n:O {id: $id, name: $name, score: 0.0})");

                // The values that must survive the rejected call below, bound BEFORE it - rebinding
                // them afterward would overwrite the very evidence this test looks for. (An earlier
                // draft did exactly that and passed against a deliberately single-pass binder.)
                stmt.Bind("id", 1L);
                stmt.Bind("name", "kept");

                // The unbindable value is LAST, so a one-pass binder would already have overwritten
                // both `id` and `name` by the time it reached `bad` and threw.
                var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await stmt.ExecuteAsync(new { id = 2L, name = "clobbered", bad = new object() }));
                await Assert.That(ex!.Message).Contains("'bad'");

                await using (var _ = await stmt.ExecuteAsync()) { }

                // 1/"kept", not 2/"clobbered": nothing the rejected object named was applied.
                await AssertSingleRow(conn, 1L, "kept", 0.0);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // -------------------------------------------------------------------------- null and resolution

    /// <summary>
    /// <c>QueryAsync(cypher, null)</c> compiles unambiguously - the existing
    /// <c>(string, CancellationToken)</c> overload cannot take <see langword="null"/>, so there is no
    /// ambiguity to resolve - and throws, naming the parameter. A caller who means "no parameters"
    /// wants the single-argument overload.
    /// </summary>
    /// <remarks>
    /// The <c>null!</c> below is deliberate, and is itself the finding: <c>parameters</c> is declared
    /// non-nullable <see langword="object"/>, so a nullable-aware caller writing a bare
    /// <c>null</c> gets CS8625 at compile time - strictly better than a runtime throw. The
    /// <see cref="ArgumentNullException"/> asserted here is the guard behind that, for callers who
    /// have nullable reference types off or who arrive via a <see langword="dynamic"/> or reflective
    /// path where the annotation buys nothing.
    /// </remarks>
    [Test]
    public async Task QueryAsyncWithNullParameters_ThrowsArgumentNullException_NamingTheParameter()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                    await conn.QueryAsync("MATCH (n:O) RETURN n.id", null!));

                await Assert.That(ex!.ParamName).IsEqualTo("parameters");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The same for the prepared-statement entry point.</summary>
    [Test]
    public async Task ExecuteAsyncWithNullParameters_ThrowsArgumentNullException_NamingTheParameter()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                await using var stmt = await conn.PrepareAsync("MATCH (n:O) RETURN n.id");

                var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                    await stmt.ExecuteAsync(null!));

                await Assert.That(ex!.ParamName).IsEqualTo("parameters");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <b>Overload resolution, asserted by compiling and running every intended call form.</b> Adding
    /// an <see langword="object"/> parameter beside an existing
    /// <see cref="CancellationToken"/> overload is exactly where a design like this goes wrong
    /// silently - a token quietly boxing into the parameters slot would send
    /// <c>QueryAsync(cypher, ct)</c> down the reflection path and bind a
    /// <see cref="CancellationToken"/>'s own properties. Each call below is written the way a caller
    /// would write it; that they compile, and that the results are right, is the assertion.
    /// </summary>
    [Test]
    public async Task EveryIntendedCallForm_ResolvesToTheOverloadItLooksLike()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                using var cts = new CancellationTokenSource();
                var ct = cts.Token;

                // Bare, and with a token: must reach QueryAsync(string, CancellationToken). If a
                // token boxed into the object overload instead, these would throw ArgumentException
                // ("CancellationToken ... is a single value") rather than run.
                await using (var _ = await conn.QueryAsync("MATCH (n:O) RETURN count(n)")) { }
                await using (var _ = await conn.QueryAsync("MATCH (n:O) RETURN count(n)", ct)) { }

                // With parameters, and with parameters plus a token.
                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: 'a', score: 0.0})", new { id = 1L })) { }
                await using (var _ = await conn.QueryAsync(
                    "CREATE (n:O {id: $id, name: 'b', score: 0.0})", new { id = 2L }, ct)) { }

                await using var stmt = await conn.PrepareAsync(
                    "CREATE (n:O {id: $id, name: 'c', score: 0.0})");
                await using (var _ = await stmt.ExecuteAsync(new { id = 3L })) { }
                await using (var _ = await stmt.ExecuteAsync(new { id = 4L }, ct)) { }

                // Bare and token-only on the statement, which must still reach ExecuteAsync(CancellationToken).
                stmt.Bind("id", 5L);
                await using (var _ = await stmt.ExecuteAsync()) { }
                stmt.Bind("id", 6L);
                await using (var _ = await stmt.ExecuteAsync(ct)) { }

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN count(n)");
                await foreach (var row in r)
                    await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(6L);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>An already-cancelled token is honoured before anything is prepared or bound.</summary>
    [Test]
    public async Task CancelledToken_ThrowsBeforeBinding()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using (db)
            await using (conn)
            {
                using var cts = new CancellationTokenSource();
                await cts.CancelAsync();

                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:O {id: $id, name: 'x', score: 0.0})", new { id = 1L }, cts.Token));

                await using var r = await conn.QueryAsync("MATCH (n:O) RETURN count(n)");
                await foreach (var row in r)
                    await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    private static async Task AssertSingleRow(
        LadybugConnection conn, long id, string name, double score)
    {
        await using var r = await conn.QueryAsync("MATCH (n:O) RETURN n.id, n.name, n.score");
        var rows = 0;
        await foreach (var row in r)
        {
            rows++;
            await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(id);
            await Assert.That(row.GetValue(1).AsString()).IsEqualTo(name);
            await Assert.That(row.GetValue(2).AsDouble()).IsEqualTo(score);
        }

        await Assert.That(rows).IsEqualTo(1);
    }
}
