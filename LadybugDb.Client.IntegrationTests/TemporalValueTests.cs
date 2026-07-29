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
            await Assert.That(row.Value.GetValue(1).AsDateTime())
                .IsEqualTo(new DateTime(2026, 7, 29, 13, 45, 30, DateTimeKind.Utc));
            await Assert.That(row.Value.GetValue(2).AsTimeSpan()).IsEqualTo(TimeSpan.FromDays(3));
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
