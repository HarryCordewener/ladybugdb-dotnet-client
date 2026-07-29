using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class PreparedStatementTests
{
    [Test]
    public async Task EveryIntegerWidth_BindsAtItsExactBoundary()
    {
        // A mis-sized integer marshal corrupts data silently rather than throwing,
        // so every width is bound at its documented extreme and read back.
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE I(id INT64, i8 INT8, i16 INT16, i32 INT32, i64 INT64, " +
                "u8 UINT8, u16 UINT16, u32 UINT32, u64 UINT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync(
                "CREATE (n:I {id: 1, i8: $i8, i16: $i16, i32: $i32, i64: $i64, " +
                "u8: $u8, u16: $u16, u32: $u32, u64: $u64})");
            stmt.Bind("i8", sbyte.MinValue);
            stmt.Bind("i16", short.MinValue);
            stmt.Bind("i32", int.MinValue);
            stmt.Bind("i64", long.MinValue);
            stmt.Bind("u8", byte.MaxValue);
            stmt.Bind("u16", ushort.MaxValue);
            stmt.Bind("u32", uint.MaxValue);
            stmt.Bind("u64", ulong.MaxValue);
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:I) RETURN n.i8, n.i16, n.i32, n.i64, n.u8, n.u16, n.u32, n.u64");
            await foreach (var row in r)
            {
                await Assert.That(row.GetValue(0).AsSByte()).IsEqualTo(sbyte.MinValue);
                await Assert.That(row.GetValue(1).AsInt16()).IsEqualTo(short.MinValue);
                await Assert.That(row.GetValue(2).AsInt32()).IsEqualTo(int.MinValue);
                await Assert.That(row.GetValue(3).AsInt64()).IsEqualTo(long.MinValue);
                await Assert.That(row.GetValue(4).AsByte()).IsEqualTo(byte.MaxValue);
                await Assert.That(row.GetValue(5).AsUInt16()).IsEqualTo(ushort.MaxValue);
                await Assert.That(row.GetValue(6).AsUInt32()).IsEqualTo(uint.MaxValue);
                await Assert.That(row.GetValue(7).AsUInt64()).IsEqualTo(ulong.MaxValue);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task PreparedStatement_IsReusableAcrossExecutions()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE R(id INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:R {id: $id})");
            for (var i = 0; i < 3; i++)
            {
                stmt.Bind("id", (long)i);
                await using var _ = await stmt.ExecuteAsync();
            }

            await using var r = await conn.QueryAsync("MATCH (n:R) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(3L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task BindingUnknownParameter_ThrowsLadybugException()
    {
        // Deviates from the brief's literal assertion (Bind() itself throwing synchronously).
        // Verified against the real engine via a raw probe directly against LbugNative, bypassing
        // this wrapper entirely: lbug_prepared_statement_bind_int64 (and, by construction, every
        // other lbug_prepared_statement_bind_* entry point) returns LbugSuccess unconditionally for
        // an unrecognized parameter name - it just records name/value pairs, with no validation
        // against the statement's own $-placeholders. The mismatch is caught only when the
        // statement is executed and the engine tries to resolve the query's actual placeholder
        // ($id here), which was never bound - lbug_connection_execute reports "Parameter id not
        // found." at that point, not at bind time. See task-5-report.md for the probe transcript.
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE U(id INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:U {id: $id})");
            stmt.Bind("nosuchparam", 1L);
            await Assert.ThrowsAsync<LadybugException>(async () => await stmt.ExecuteAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // The brief's Step 1 test file (EveryIntegerWidth_BindsAtItsExactBoundary,
    // PreparedStatement_IsReusableAcrossExecutions, BindingUnknownParameter_ThrowsLadybugException
    // above) only exercises 9 of the 20 binds (the 8 integer widths, plus INT64 a second time).
    // The remaining 12 - bool, float, double, string, DATE, INTERVAL, and the four TIMESTAMP
    // precisions, plus BindNull - are exercised below so every bind in the milestone's "all 20
    // typed parameter binds" scope actually has coverage, not just the integer-width boundaries.
    [Test]
    public async Task RemainingScalarStringAndTemporalBinds_RoundTrip()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, b BOOL, f FLOAT, dbl DOUBLE, s STRING, " +
                "dt DATE, iv INTERVAL, ts TIMESTAMP, tstz TIMESTAMP_TZ, " +
                "tssec TIMESTAMP_SEC, tsms TIMESTAMP_MS, tsns TIMESTAMP_NS, " +
                "PRIMARY KEY(id))")) { }

            var date = new DateOnly(2026, 7, 28);
            var interval = TimeSpan.FromSeconds(123456);
            var timestamp = new DateTime(2026, 7, 28, 12, 34, 56, 789, DateTimeKind.Utc);
            var timestampTz = new DateTimeOffset(2026, 7, 28, 12, 34, 56, 789, TimeSpan.Zero);
            var timestampSec = new DateTime(2026, 7, 28, 12, 34, 56, DateTimeKind.Utc);
            var timestampMs = new DateTime(2026, 7, 28, 12, 34, 56, 789, DateTimeKind.Utc);
            var timestampNs = new DateTime(2026, 7, 28, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1230);

            await using var stmt = await conn.PrepareAsync(
                "CREATE (n:T {id: 1, b: $b, f: $f, dbl: $dbl, s: $s, dt: $dt, iv: $iv, ts: $ts, " +
                "tstz: $tstz, tssec: $tssec, tsms: $tsms, tsns: $tsns})");
            stmt.Bind("b", true);
            stmt.Bind("f", 3.5f);
            stmt.Bind("dbl", 2.71828182845904);
            stmt.Bind("s", "ladybug");
            stmt.Bind("dt", date);
            stmt.Bind("iv", interval);
            stmt.Bind("ts", timestamp);
            stmt.Bind("tstz", timestampTz);
            stmt.BindTimestampSeconds("tssec", timestampSec);
            stmt.BindTimestampMilliseconds("tsms", timestampMs);
            stmt.BindTimestampNanoseconds("tsns", timestampNs);
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:T) RETURN n.b, n.f, n.dbl, n.s, n.dt, n.iv, n.ts, n.tstz, n.tssec, n.tsms, n.tsns");
            await foreach (var row in r)
            {
                await Assert.That(row.GetValue(0).AsBoolean()).IsTrue();
                await Assert.That(row.GetValue(1).AsSingle()).IsEqualTo(3.5f);
                await Assert.That(row.GetValue(2).AsDouble()).IsEqualTo(2.71828182845904);
                await Assert.That(row.GetValue(3).AsString()).IsEqualTo("ladybug");
                await Assert.That(row.GetValue(4).AsDateOnly()).IsEqualTo(date);
                await Assert.That(row.GetValue(5).AsTimeSpan()).IsEqualTo(interval);
                await Assert.That(row.GetValue(6).AsDateTime()).IsEqualTo(timestamp);
                await Assert.That(row.GetValue(7).AsDateTimeOffset()).IsEqualTo(timestampTz);
                await Assert.That(row.GetValue(8).AsDateTime()).IsEqualTo(timestampSec);
                await Assert.That(row.GetValue(9).AsDateTime()).IsEqualTo(timestampMs);
                await Assert.That(row.GetValue(10).AsDateTime()).IsEqualTo(timestampNs);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task BindNull_BindsATypedNullValue()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE N(id INT64, val INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:N {id: 1, val: $val})");
            stmt.BindNull("val");
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync("MATCH (n:N) RETURN n.val");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).IsNull).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Bind_NullOrWhitespaceParameterName_ThrowsArgumentException(string? name)
    {
        // Marshal.StringToCoTaskMemUTF8(null) silently produces a null pointer rather than
        // throwing, so without an explicit guard `Bind(null, ...)` would fail obscurely deep in
        // native code instead of at the call site - inconsistent with how `cypher` is validated
        // elsewhere in this client (e.g. LadybugConnection.QueryAsync/PrepareAsync).
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE V(id INT64, val INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:V {id: 1, val: $val})");
            Assert.Throws<ArgumentException>(() => stmt.Bind(name!, 1L));
            Assert.Throws<ArgumentException>(() => stmt.Bind(name!, "x"));
            Assert.Throws<ArgumentException>(() => stmt.Bind(name!, ExtendedNumerics.BigDecimal.One));
            Assert.Throws<ArgumentException>(() => stmt.BindNull(name!));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposingStatementBeforeConsumingResult_ResultRemainsUsable()
    {
        // Pins the ownership question directly: ExecuteAsync's LadybugQueryResult only leases the
        // database and its own handle chain (see LbugQueryResultHandle.ExecutePrepared) - it never
        // leases the prepared statement that produced it - so a result must stay fully usable after
        // the statement that created it has already been disposed, not merely "usually work."
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE D(id INT64, val INT64, PRIMARY KEY(id))")) { }

            var stmt = await conn.PrepareAsync("CREATE (n:D {id: 1, val: $val})");
            stmt.Bind("val", 42L);
            var result = await stmt.ExecuteAsync();
            await stmt.DisposeAsync();

            await using (result)
            {
                await foreach (var _ in result) { }
            }

            await using var r = await conn.QueryAsync("MATCH (n:D) RETURN n.val");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(42L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
