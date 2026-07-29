using ExtendedNumerics;
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Proves <see cref="LadybugPreparedStatement.Bind(string, BigDecimal)"/> and
/// <see cref="LadybugValue.AsBigDecimal"/> round-trip losslessly - the owner's stated bar for
/// taking a dependency on <c>ExtendedNumerics.BigDecimal</c> was "safely do bidirectional
/// conversions about decimals without causing problems", so every case here binds a value through
/// a prepared statement, reads it back, and asserts exact equality rather than a looser
/// approximation. Also pins the engine's actual behaviour - established empirically against a real
/// database, not assumed from the header - when a bound value's derived precision/scale differs
/// from the target column's declared <c>DECIMAL(p,s)</c>.
/// </summary>
public class DecimalBidirectionalTests
{
    private static async Task<(LadybugDatabase db, LadybugConnection conn)> OpenWithColumn(string path, string colType)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync($"CREATE NODE TABLE T(id INT64, v {colType}, PRIMARY KEY(id))")) { }
        return (db, conn);
    }

    private static async Task<LadybugValue> BindAndReadBack(LadybugConnection conn, BigDecimal value)
    {
        await using var stmt = await conn.PrepareAsync("CREATE (n:T {id: 1, v: $v})");
        stmt.Bind("v", value);
        await using (var _ = await stmt.ExecuteAsync()) { }

        await using var r = await conn.QueryAsync("MATCH (n:T) RETURN n.v");
        await using var e = r.GetAsyncEnumerator();
        await e.MoveNextAsync();
        return e.Current.GetValue(0);
    }

    /// <summary>A value comfortably inside <see cref="decimal"/>'s range round-trips exactly through <see cref="LadybugValue.AsBigDecimal"/>.</summary>
    [Test]
    public async Task WithinDecimalRange_RoundTripsExactlyThroughAsBigDecimal()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(18,4)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("12345.6789");
            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Decimal);
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original);
            await Assert.That(value.AsDecimal()).IsEqualTo(12345.6789m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A full 38-digit DECIMAL(38,0) value - the engine's maximum precision, and one
    /// <see cref="decimal"/> genuinely cannot hold (28-29 significant digits) - round-trips
    /// exactly through <see cref="LadybugValue.AsBigDecimal"/>. This is the case the whole
    /// BigDecimal dependency exists for.
    /// </summary>
    [Test]
    public async Task Full38Digits_ExceedingDecimalRange_RoundTripsExactlyThroughAsBigDecimal()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(38,0)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("12345678901234567890123456789012345678");
            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.AsString()).IsEqualTo("12345678901234567890123456789012345678");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original);

            // decimal genuinely cannot hold this - confirms AsBigDecimal isn't redundant with AsDecimal.
            var ex = Assert.Throws<LadybugException>(() => value.AsDecimal());
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("AsBigDecimal");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A negative value round-trips exactly, sign included.</summary>
    [Test]
    public async Task NegativeValue_RoundTripsExactly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(10,2)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("-123.45");
            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.AsString()).IsEqualTo("-123.45");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original);
            await Assert.That(value.AsDecimal()).IsEqualTo(-123.45m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Trailing zeros in the scale. Two distinct behaviours are pinned here, not assumed:
    /// (1) the engine's own storage preserves the column's declared scale on read - <c>AsString()</c>
    /// still reports "1.2300", matching the existing read-side coverage for a literal Cypher value.
    /// (2) <c>ExtendedNumerics.BigDecimal</c> itself normalizes trailing zeros out of its mantissa by
    /// default (<c>BigDecimal.AlwaysNormalize</c> defaults to <see langword="true"/>) - unlike
    /// <see cref="decimal"/>, which preserves them as part of its own representation. So
    /// <c>BigDecimal.Parse("1.2300")</c> and <c>BigDecimal.Parse("1.23")</c> are the identical value
    /// (mantissa 123, exponent -2) the moment they're parsed - not a lossy step introduced by this
    /// client, but the dependency's own canonical form. Since both the original bind and the value
    /// read back go through that same normalization, round-trip equality still holds exactly.
    /// </summary>
    [Test]
    public async Task TrailingZerosInScale_EngineStoragePreservesThem_ButBigDecimalNormalizes()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(18,4)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("1.2300");
            await Assert.That(original.ToString()).IsEqualTo("1.23"); // pins BigDecimal's own normalization

            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.AsString()).IsEqualTo("1.2300"); // the engine's own storage keeps the column's scale
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original); // still exactly equal once both sides normalize
            await Assert.That(value.AsDecimal()).IsEqualTo(1.2300m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>Zero round-trips exactly.</summary>
    [Test]
    public async Task Zero_RoundTripsExactly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(38,0)");
            using var _db = db;
            await using var _conn = conn;

            var value = await BindAndReadBack(conn, BigDecimal.Zero);

            await Assert.That(value.AsString()).IsEqualTo("0");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(BigDecimal.Zero);
            await Assert.That(value.AsDecimal()).IsEqualTo(0m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The maximum value representable at DECIMAL(38,0) round-trips exactly.</summary>
    [Test]
    public async Task MaximumAtDecimal38_0_RoundTripsExactly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(38,0)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("99999999999999999999999999999999999999");
            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.AsString()).IsEqualTo("99999999999999999999999999999999999999");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The minimum value representable at DECIMAL(38,0) round-trips exactly.</summary>
    [Test]
    public async Task MinimumAtDecimal38_0_RoundTripsExactly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(38,0)");
            using var _db = db;
            await using var _conn = conn;

            var original = BigDecimal.Parse("-99999999999999999999999999999999999999");
            var value = await BindAndReadBack(conn, original);

            await Assert.That(value.AsString()).IsEqualTo("-99999999999999999999999999999999999999");
            await Assert.That(value.AsBigDecimal()).IsEqualTo(original);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A value read via <see cref="LadybugValue.AsBigDecimal"/>, bound straight back via
    /// <see cref="LadybugPreparedStatement.Bind(string, BigDecimal)"/> to a second row, and read
    /// again - the full read-then-write-then-read loop, not just write-then-read - still compares
    /// exactly equal.
    /// </summary>
    [Test]
    public async Task AsBigDecimal_ThenBindAgain_ThenAsBigDecimal_StaysExactlyEqual()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, v DECIMAL(18,4), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:T {id: 1, v: 12345.6789})")) { }

            await using var r1 = await conn.QueryAsync("MATCH (n:T) WHERE n.id = 1 RETURN n.v");
            await using var e1 = r1.GetAsyncEnumerator();
            await e1.MoveNextAsync();
            var firstRead = e1.Current.GetValue(0).AsBigDecimal();

            await using var stmt = await conn.PrepareAsync("CREATE (n:T {id: 2, v: $v})");
            stmt.Bind("v", firstRead);
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r2 = await conn.QueryAsync("MATCH (n:T) WHERE n.id = 2 RETURN n.v");
            await using var e2 = r2.GetAsyncEnumerator();
            await e2.MoveNextAsync();
            var secondRead = e2.Current.GetValue(0).AsBigDecimal();

            await Assert.That(secondRead).IsEqualTo(firstRead);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A precision/scale below the target column's declared <c>DECIMAL(p,s)</c> is not rejected -
    /// established empirically (a native probe against liblbug, not assumed from the header, which
    /// documents <c>lbug_value_create_decimal</c>'s precision/scale parameters but not how a
    /// mismatch against the destination column is handled). The engine widens the bound value to the
    /// column's own precision/scale, zero-padding the scale as needed.
    /// </summary>
    [Test]
    public async Task BoundValue_WithLowerPrecisionAndScaleThanColumn_IsWidenedNotRejected()
    {
        var path = TestDatabase.NewPath();
        try
        {
            // "123" derives precision 3, scale 0 - both below the column's DECIMAL(18,4).
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(18,4)");
            using var _db = db;
            await using var _conn = conn;

            var value = await BindAndReadBack(conn, BigDecimal.Parse("123"));

            await Assert.That(value.AsString()).IsEqualTo("123.0000");
            await Assert.That(value.AsDecimal()).IsEqualTo(123m);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A bound scale above the column's declared scale is not rejected either - it's silently
    /// rounded (half-away-from-zero, confirmed separately) to fit, which is the one genuine sharp
    /// edge in this bind path: narrowing does not throw, it loses precision. A caller binding a
    /// BigDecimal with more fractional digits than the target column supports gets a rounded value
    /// back, not an exception - documented here and in docs/USAGE.md rather than left to be
    /// discovered.
    /// </summary>
    [Test]
    public async Task BoundValue_WithHigherScaleThanColumn_IsSilentlyRoundedNotRejected()
    {
        var path = TestDatabase.NewPath();
        try
        {
            // "123.456" derives scale 3, above the column's DECIMAL(18,2).
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(18,2)");
            using var _db = db;
            await using var _conn = conn;

            var value = await BindAndReadBack(conn, BigDecimal.Parse("123.456"));

            await Assert.That(value.AsString()).IsEqualTo("123.46");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Pins the bind path's rounding direction on an exact half-way value at the scale boundary -
    /// round-half-away-from-zero, not banker's rounding (round-half-to-even) and not the truncation
    /// a naive binary-float parse would produce. This is the behaviour this client controls and
    /// promises via <see cref="LadybugPreparedStatement.Bind(string, BigDecimal)"/>, which carries
    /// <paramref name="input"/>'s exact decimal digits through to the engine as a string - unlike a
    /// Cypher decimal literal, which the engine parses as a binary <c>double</c> first (so
    /// <c>1.005</c> - not exactly representable in binary, and actually
    /// <c>1.00499999999999989...</c> - rounds *down* to <c>1.00</c> as a literal, but *up* to
    /// <c>1.01</c> through this bind path). That literal-vs-bind divergence is real (confirmed
    /// separately, by binding through <c>CREATE (r:R {v: 1.005})</c> directly against this same
    /// column and comparing) and is documented in docs/USAGE.md, but deliberately isn't asserted
    /// here: the literal path's rounding is the engine's own double-parsing behaviour, not this
    /// client's, so pinning it in this suite would freeze an implementation detail this client
    /// doesn't own and can't promise.
    /// </summary>
    [Test]
    [Arguments("1.005", "1.01")]
    [Arguments("-1.005", "-1.01")]
    [Arguments("1.015", "1.02")]
    [Arguments("1.025", "1.03")]
    [Arguments("2.675", "2.68")]
    public async Task BoundValue_ExactHalfAtScaleBoundary_RoundsAwayFromZero(string input, string expected)
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(18,2)");
            using var _db = db;
            await using var _conn = conn;

            var value = await BindAndReadBack(conn, BigDecimal.Parse(input));

            await Assert.That(value.AsString()).IsEqualTo(expected);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Unlike a scale mismatch, a bound value whose integer part doesn't fit the column's
    /// precision/scale is rejected - the engine throws rather than truncating the whole number.
    /// </summary>
    [Test]
    public async Task BoundValue_ExceedingColumnsMagnitude_ThrowsLadybugException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            // "12345.67" needs 5 integer digits; DECIMAL(5,2) only has room for 3 (5 total - 2 scale).
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(5,2)");
            using var _db = db;
            await using var _conn = conn;

            await using var stmt = await conn.PrepareAsync("CREATE (n:T {id: 1, v: $v})");
            stmt.Bind("v", BigDecimal.Parse("12345.67"));

            var ex = Assert.Throws<LadybugException>(() => stmt.ExecuteAsync().AsTask().GetAwaiter().GetResult());
            await Assert.That(ex).IsNotNull();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A value needing more than 38 significant digits is rejected by this client before it ever
    /// reaches the native call - the engine's own DECIMAL maximum, confirmed at the DDL level
    /// (DECIMAL(39,0) is rejected column-creation time). Binding fails fast with a clear message
    /// rather than forwarding a 39-digit precision into <c>lbug_value_create_decimal</c> and
    /// relying on however the engine happens to react to that.
    /// </summary>
    [Test]
    public async Task BoundValue_NeedingMoreThan38Digits_ThrowsBeforeReachingTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithColumn(path, "DECIMAL(38,0)");
            using var _db = db;
            await using var _conn = conn;

            await using var stmt = await conn.PrepareAsync("CREATE (n:T {id: 1, v: $v})");
            var tooBig = BigDecimal.Parse("123456789012345678901234567890123456789"); // 39 digits

            var ex = Assert.Throws<LadybugException>(() => stmt.Bind("v", tooBig));
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.Message).Contains("38");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
