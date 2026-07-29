using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ValueReadTests
{
    [Test]
    public async Task ReadString_ReturnsTheStoredValue()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 7, name: 'Master Room'})")) { }

            await using var result = await conn.QueryAsync(
                "MATCH (o:Obj) WHERE o.dbref = 7 RETURN o.name");

            string? name = null;
            await foreach (var row in result)
                name = row.GetValue(0).AsString();
            await Assert.That(name).IsEqualTo("Master Room");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Regresses the missing <c>LBUG_INTERNAL_ID</c> arm in <c>ValueReader</c>'s type switch:
    /// <c>RETURN id(n)</c> - a bare internal id, not one embedded in a node/rel value - used to fall
    /// through to <see cref="LadybugType.Unsupported"/> with a <see langword="null"/> payload, so
    /// even <see cref="LadybugValue.AsString"/> (which <see cref="LadybugType.Unsupported"/>'s own
    /// XML doc recommends as the fallback) threw. It now reads as <see cref="LadybugType.InternalId"/>,
    /// matching the same table/offset the node's own <see cref="LadybugNode.Id"/> reports for the
    /// identical row.
    /// </summary>
    [Test]
    public async Task ReadInternalId_FromBareIdFunction_ReturnsTheNodesId()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 7, name: 'Master Room'})")) { }

            await using var r = await conn.QueryAsync("MATCH (o:Obj) RETURN o, id(o)");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            var node = row.GetValue(0).AsNode();
            var idValue = row.GetValue(1);

            await Assert.That(idValue.Type).IsEqualTo(LadybugType.InternalId);
            await Assert.That(idValue.AsInternalId()).IsEqualTo(node.Id);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A DECIMAL(18,4) value comfortably within <see cref="decimal"/>'s 28-29 significant-digit
    /// range round-trips through <see cref="LadybugValue.AsDecimal"/> with the exact expected
    /// value, and reports <see cref="LadybugType.Decimal"/> as its <see cref="LadybugValue.Type"/>.
    /// </summary>
    [Test]
    public async Task ReadDecimal_WithinDecimalRange_RoundTripsThroughAsDecimal()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE D18(id INT64, amount DECIMAL(18,4), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:D18 {id: 1, amount: 12345.6789})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:D18) RETURN n.amount");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Decimal);
            await Assert.That(value.AsDecimal()).IsEqualTo(12345.6789m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A negative DECIMAL and one with trailing zeros in its scale, to pin the exact formatting
    /// the engine hands back (not just that some parseable string comes through).
    /// </summary>
    [Test]
    public async Task ReadDecimal_NegativeAndTrailingZeroScale_PreservesExactFormatting()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE D2(id INT64, neg DECIMAL(10,2), trailing DECIMAL(10,4), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:D2 {id: 1, neg: -123.45, trailing: 100.5000})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:D2) RETURN n.neg, n.trailing");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            await Assert.That(row.GetValue(0).AsDecimal()).IsEqualTo(-123.45m);
            await Assert.That(row.GetValue(1).AsDecimal()).IsEqualTo(100.5000m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A 38-digit DECIMAL(38,0) value - the engine's maximum precision, and one that genuinely
    /// exceeds <see cref="decimal"/>'s 28-29 significant digits. <see cref="LadybugValue.AsString"/>
    /// and <see cref="LadybugValue.AsBigDecimal"/> must still return the exact digits (the lossless
    /// paths), while <see cref="LadybugValue.AsDecimal"/> must throw <see cref="LadybugException"/>
    /// rather than truncate, round, or silently lose precision - and its message must point the
    /// caller at <c>AsBigDecimal()</c>. See <c>DecimalBidirectionalTests</c> for the write-side
    /// (bind) counterpart of this same 38-digit case.
    /// </summary>
    [Test]
    public async Task ReadDecimal_38Digits_AsStringExactAsDecimalThrows()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE D38(id INT64, big DECIMAL(38,0), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:D38 {id: 1, big: 12345678901234567890123456789012345678})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:D38) RETURN n.big");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Decimal);
            await Assert.That(value.AsString()).IsEqualTo("12345678901234567890123456789012345678");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(
                ExtendedNumerics.BigDecimal.Parse("12345678901234567890123456789012345678"));

            var ex = Assert.Throws<LadybugException>(() => value.AsDecimal());
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("AsBigDecimal");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
