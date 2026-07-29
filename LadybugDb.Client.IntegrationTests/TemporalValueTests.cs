using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class TemporalValueTests
{
    [Test]
    public async Task DateTimestampAndInterval_RoundTrip()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE E(id INT64, d DATE, ts TIMESTAMP, iv INTERVAL, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:E {id: 1, d: date('2026-07-29'), " +
                "ts: timestamp('2026-07-29 13:45:30'), iv: interval('3 days')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:E) RETURN n.d, n.ts, n.iv");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsDateOnly()).IsEqualTo(new DateOnly(2026, 7, 29));
            var ts = row.Value.GetValue(1).AsDateTime();
            await Assert.That(ts).IsEqualTo(new DateTime(2026, 7, 29, 13, 45, 30, DateTimeKind.Utc));
            // DateTime.Equals compares only Ticks, not Kind - a value equal-by-ticks but reported as
            // DateTimeKind.Unspecified (i.e. silently local-time) would pass the assertion above too,
            // so assert Kind explicitly.
            await Assert.That(ts.Kind).IsEqualTo(DateTimeKind.Utc);
            await Assert.That(row.Value.GetValue(2).AsTimeSpan()).IsEqualTo(TimeSpan.FromDays(3));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Interval_LargeEnoughToHaveOverflowedInt32Arithmetic_IsCorrectOrThrowsCleanly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE I(id INT64, iv INTERVAL, PRIMARY KEY(id))")) { }
            // 100,000,000 months. The old client-side conversion computed native.months * 30 in
            // checked int32 arithmetic - months this large overflow that multiplication
            // (int.MaxValue / 30 is ~71.6M) and would have silently wrapped to a small, wrong
            // TimeSpan. This value is large enough to trigger that wraparound: verify the fixed
            // implementation instead either returns a value or throws LadybugException cleanly,
            // never a silently wrong TimeSpan.
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:I {id: 1, iv: interval('100000000 months')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:I) RETURN n.iv");

            // Interval conversion happens eagerly while the row is materialized (ReadRowAsync), not
            // when AsTimeSpan() is later called - so the exception, if any, is thrown here.
            // The true value (100,000,000 months * 30 days/month) is ~8.2 million years, far beyond
            // TimeSpan's ~29,247-year range, so the only acceptable outcome is a clean
            // LadybugException (the value legitimately does not fit in a TimeSpan) - never a row
            // that materializes successfully with a silently wrong TimeSpan inside it.
            await Assert.ThrowsAsync<LadybugException>(async () => await r.ReadRowAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Blob_RoundTripsExactBytes()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE B(id INT64, data BLOB, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                @"CREATE (n:B {id: 1, data: BLOB('\xDE\xAD\xBE\xEF')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:B) RETURN n.data");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsBlob())
                .IsEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
