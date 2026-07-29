using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ScalarValueTests
{
    [Test]
    public async Task EveryScalarType_RoundTrips()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, b BOOL, i8 INT8, i16 INT16, i32 INT32, " +
                "u8 UINT8, u16 UINT16, u32 UINT32, u64 UINT64, f FLOAT, d DOUBLE, s STRING, " +
                "PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:S {id: 1, b: true, i8: -8, i16: -16, i32: -32, u8: 8, u16: 16, " +
                "u32: 32, u64: 64, f: 1.5, d: 2.25, s: 'hello'})")) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:S) RETURN n.b, n.i8, n.i16, n.i32, n.u8, n.u16, n.u32, n.u64, n.f, n.d, n.s");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsBoolean()).IsTrue();
            await Assert.That(row.Value.GetValue(1).AsSByte()).IsEqualTo((sbyte)-8);
            await Assert.That(row.Value.GetValue(2).AsInt16()).IsEqualTo((short)-16);
            await Assert.That(row.Value.GetValue(3).AsInt32()).IsEqualTo(-32);
            await Assert.That(row.Value.GetValue(4).AsByte()).IsEqualTo((byte)8);
            await Assert.That(row.Value.GetValue(5).AsUInt16()).IsEqualTo((ushort)16);
            await Assert.That(row.Value.GetValue(6).AsUInt32()).IsEqualTo(32u);
            await Assert.That(row.Value.GetValue(7).AsUInt64()).IsEqualTo(64ul);
            await Assert.That(row.Value.GetValue(8).AsSingle()).IsEqualTo(1.5f);
            await Assert.That(row.Value.GetValue(9).AsDouble()).IsEqualTo(2.25d);
            await Assert.That(row.Value.GetValue(10).AsString()).IsEqualTo("hello");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task TypeTag_ReportsTheDeclaredType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, s STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1, s: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN n.id, n.s");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).Type).IsEqualTo(LadybugType.Int64);
            await Assert.That(row.Value.GetValue(1).Type).IsEqualTo(LadybugType.String);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task WrongAccessor_ThrowsInvalidOperationNotGarbage()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE W(id INT64, s STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:W {id: 1, s: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:W) RETURN n.s");
            var row = await r.ReadRowAsync();

            Assert.Throws<InvalidOperationException>(() => row!.Value.GetValue(0).AsInt64());
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
