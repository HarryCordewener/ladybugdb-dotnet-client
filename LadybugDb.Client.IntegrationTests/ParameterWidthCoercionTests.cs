using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <b>The record of a measurement, not a behavioural preference.</b> The parameter-object dispatch
/// (<c>ParameterBinder</c>) sends each value to the <c>Bind</c> overload matching its runtime type,
/// so a C# <see langword="int"/> binds <c>INT32</c> - and the design of that dispatch turned on one
/// question that had to be answered against the real engine rather than assumed: <b>does the engine
/// coerce a bound <c>INT32</c> into an <c>INT64</c> column, or reject it?</b> If it rejected,
/// <see langword="int"/>/<see langword="short"/>/<see langword="sbyte"/> would have had to be widened
/// to <c>Bind(long)</c> before dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured answer: the engine coerces.</b> Every narrower integer width reaches a wider column,
/// in both signed and unsigned families and across them, and <c>FLOAT</c> reaches a <c>DOUBLE</c>
/// column. Widening in the client was therefore unnecessary and values bind at their natural width.
/// </para>
/// <para>
/// <b>And the coercion is checked, not truncating.</b> A value outside the target column's range is
/// rejected with the engine's own <c>Overflow exception</c> surfaced as a
/// <see cref="LadybugException"/> - see
/// <see cref="OutOfRangeCoercion_IsRejected_NotSilentlyTruncated"/>. This is what makes binding at
/// natural width safe rather than merely convenient: the failure mode of a width mismatch is a loud
/// error, not a wrong number. (Silent numeric coercion has already produced one defect in this
/// client - literal-vs-bound DECIMAL rounding - which is why this was established empirically.)
/// </para>
/// <para>
/// The one genuinely lossy case measured is <see langword="double"/> into a <c>FLOAT</c> column,
/// which saturates to infinity rather than erroring - ordinary IEEE-754 narrowing, and not reachable
/// through the dispatch, which binds a <see langword="double"/> as <c>DOUBLE</c>. It is asserted
/// below anyway so the boundary of "coercion is checked" is written down rather than implied.
/// </para>
/// </remarks>
public class ParameterWidthCoercionTests
{
    private const string Schema =
        "CREATE NODE TABLE W(id INT64, i64 INT64, u64 UINT64, i32 INT32, dbl DOUBLE, flt FLOAT, " +
        "PRIMARY KEY(id))";

    /// <summary>
    /// The question Decision 2 of the design left open, answered directly: a bound <c>INT32</c>
    /// lands in an <c>INT64</c> column and reads back exactly.
    /// </summary>
    [Test]
    public async Task BoundInt32_IsCoercedIntoAnInt64Column_SoIntNeedNotBeWidened()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(Schema)) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:W {id: 1, i64: $v})");
            stmt.Bind("v", int.MaxValue); // Bind(int) -> INT32, into an INT64 column.
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync("MATCH (n:W) WHERE n.id = 1 RETURN n.i64");
            var rows = 0;
            await foreach (var row in r)
            {
                rows++;
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(int.MaxValue);
            }

            await Assert.That(rows).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same coercion across every other narrower width, in both integer families and across
    /// them, plus <c>FLOAT</c> into <c>DOUBLE</c> - so the conclusion above is not resting on the
    /// single <c>INT32</c> case.
    /// </summary>
    [Test]
    [Arguments("i64", "sbyte")]
    [Arguments("i64", "short")]
    [Arguments("i64", "int")]
    [Arguments("i64", "byte")]
    [Arguments("i64", "ushort")]
    [Arguments("i64", "uint")]
    [Arguments("u64", "byte")]
    [Arguments("u64", "ushort")]
    [Arguments("u64", "uint")]
    [Arguments("u64", "int")]
    [Arguments("u64", "long")]
    [Arguments("i32", "long")]
    [Arguments("dbl", "float")]
    [Arguments("dbl", "int")]
    public async Task EveryNarrowerWidth_IsCoercedIntoTheWiderColumn(string column, string boundAs)
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(Schema)) { }

            // 7 (7.5 for the floating widths) is representable in every width under test, so the
            // read-back below isolates "was it coerced" from any range question - which
            // OutOfRangeCoercion_IsRejected_NotSilentlyTruncated covers separately.
            await using var stmt = await conn.PrepareAsync($"CREATE (n:W {{id: 1, {column}: $v}})");
            switch (boundAs)
            {
                case "sbyte": stmt.Bind("v", (sbyte)7); break;
                case "short": stmt.Bind("v", (short)7); break;
                case "int": stmt.Bind("v", 7); break;
                case "long": stmt.Bind("v", 7L); break;
                case "byte": stmt.Bind("v", (byte)7); break;
                case "ushort": stmt.Bind("v", (ushort)7); break;
                case "uint": stmt.Bind("v", 7u); break;
                case "float": stmt.Bind("v", 7.5f); break;
                default: throw new ArgumentOutOfRangeException(nameof(boundAs), boundAs, null);
            }

            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync($"MATCH (n:W) WHERE n.id = 1 RETURN n.{column}");
            var rows = 0;
            await foreach (var row in r)
            {
                rows++;
                var value = row.GetValue(0);
                var actual = column switch
                {
                    "i64" => (object)value.AsInt64(),
                    "u64" => value.AsUInt64(),
                    "i32" => value.AsInt32(),
                    _ => value.AsDouble(),
                };
                var expected = column switch
                {
                    "i64" => (object)7L,
                    "u64" => 7ul,
                    "i32" => 7,
                    _ => boundAs == "float" ? 7.5d : 7d,
                };
                await Assert.That(actual).IsEqualTo(expected);
            }

            await Assert.That(rows).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The half of the measurement that makes natural-width binding safe: coercion is range-checked.
    /// Each case below binds a value the target column cannot hold and asserts the engine says so
    /// rather than storing a truncated number.
    /// </summary>
    [Test]
    [Arguments("i32", "long-too-big", "4294967303", "INT32")]
    [Arguments("u64", "long-negative", "-1", "UINT64")]
    [Arguments("i64", "ulong-too-big", "18446744073709551615", "INT64")]
    public async Task OutOfRangeCoercion_IsRejected_NotSilentlyTruncated(
        string column, string kind, string valueText, string range)
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(Schema)) { }

            await using var stmt = await conn.PrepareAsync($"CREATE (n:W {{id: 1, {column}: $v}})");
            switch (kind)
            {
                case "long-too-big": stmt.Bind("v", 4294967303L); break;
                case "long-negative": stmt.Bind("v", -1L); break;
                default: stmt.Bind("v", ulong.MaxValue); break;
            }

            var ex = await Assert.ThrowsAsync<LadybugException>(async () => await stmt.ExecuteAsync());

            // The engine's exact wording, e.g. "Overflow exception: Value 4294967303 is not within
            // INT32 range". Asserted rather than paraphrased so a future engine version quietly
            // switching to truncation cannot pass this test.
            await Assert.That(ex!.Message).Contains("Overflow exception");
            await Assert.That(ex!.Message).Contains(valueText);
            await Assert.That(ex!.Message).Contains(range);

            // And nothing was written.
            await using var r = await conn.QueryAsync("MATCH (n:W) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The measured exception to "coercion is checked": narrowing a <see langword="double"/> into a
    /// <c>FLOAT</c> column saturates to infinity instead of erroring. Not reachable through the
    /// parameter-object dispatch - a <see langword="double"/> binds <c>DOUBLE</c> - and recorded here
    /// only so the limit of the guarantee above is written down.
    /// </summary>
    [Test]
    public async Task DoubleNarrowedIntoAFloatColumn_SaturatesToInfinity_TheOneUncheckedCase()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(Schema)) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:W {id: 1, flt: $v})");
            stmt.Bind("v", 1e300);
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync("MATCH (n:W) WHERE n.id = 1 RETURN n.flt");
            await foreach (var row in r)
                await Assert.That(float.IsPositiveInfinity(row.GetValue(0).AsSingle())).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Calibrates the three tests above: the harness they share can see a rejection at all. Without
    /// this, "every width was accepted" would be indistinguishable from "the assertion never ran".
    /// A <see cref="Guid"/> into an <c>INT64</c> column is a conversion the engine genuinely refuses.
    /// </summary>
    [Test]
    public async Task Calibration_AGenuinelyUnconvertibleValue_IsRejected()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(Schema)) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:W {id: 1, i64: $v})");
            stmt.Bind("v", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<LadybugException>(async () => await stmt.ExecuteAsync());
            await Assert.That(ex!.Message).Contains("Unsupported casting function from UUID to INT64");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
