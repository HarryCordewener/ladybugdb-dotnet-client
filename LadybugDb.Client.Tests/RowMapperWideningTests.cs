using LadybugDb.Client.Mapping;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// The read-side conversion rule: a column reads into the target that backs it exactly, <b>and</b> into
/// any target that provably contains every value the column's <see cref="LadybugType"/> can hold - and
/// into nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Every widening case below uses the column type's <em>extreme</em> value rather than a small one.
/// A conversion that quietly truncated or reinterpreted would still produce the right answer for
/// <c>42</c>; it cannot for <see cref="sbyte.MinValue"/>, <see cref="ulong.MaxValue"/>, or
/// <see cref="float.MaxValue"/>. The refusals are the other half of the same rule, and are listed
/// exhaustively at each boundary (equal-width unsigned-into-signed, one-step narrowing,
/// signed-into-unsigned, integer-into-floating-point) because each of those is exactly where a
/// plausible "just widen it" implementation would be wrong.
/// </para>
/// <para>
/// Unit rather than integration tests: a hand-built <see cref="LadybugValue"/> can carry any
/// <see cref="LadybugType"/> at any extreme, whereas the set of column types the engine can be made to
/// return through Cypher is both narrower and beside the point here - what is under test is this
/// client's conversion table. <c>SelectWideningTests</c> covers the engine-facing half.
/// </para>
/// </remarks>
public class RowMapperWideningTests
{
    private static LadybugRow Row(LadybugType type, object? payload) =>
        new([new LadybugValue(type, payload)], ["c"]);

    /// <summary>A projected shape, so widening is exercised through a constructor and not only through the scalar unwrap.</summary>
    private record Widened(long C);

    // --------------------------------------------------------------------------- signed widening

    [Test]
    public async Task Int8_WidensIntoEveryWiderSignedTarget()
    {
        const sbyte min = sbyte.MinValue;

        await Assert.That(RowMapper.Map<sbyte>(Row(LadybugType.Int8, min))).IsEqualTo(min);
        await Assert.That(RowMapper.Map<short>(Row(LadybugType.Int8, min))).IsEqualTo((short)min);
        await Assert.That(RowMapper.Map<int>(Row(LadybugType.Int8, min))).IsEqualTo((int)min);
        await Assert.That(RowMapper.Map<long>(Row(LadybugType.Int8, min))).IsEqualTo((long)min);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.Int8, min))).IsEqualTo((Int128)min);
    }

    [Test]
    public async Task Int16_WidensIntoEveryWiderSignedTarget()
    {
        const short min = short.MinValue;

        await Assert.That(RowMapper.Map<int>(Row(LadybugType.Int16, min))).IsEqualTo((int)min);
        await Assert.That(RowMapper.Map<long>(Row(LadybugType.Int16, min))).IsEqualTo((long)min);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.Int16, min))).IsEqualTo((Int128)min);
    }

    [Test]
    public async Task Int32_WidensIntoEveryWiderSignedTarget()
    {
        const int min = int.MinValue;

        await Assert.That(RowMapper.Map<long>(Row(LadybugType.Int32, min))).IsEqualTo((long)min);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.Int32, min))).IsEqualTo((Int128)min);
    }

    [Test]
    public async Task Int64_WidensIntoInt128()
    {
        const long min = long.MinValue;

        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.Int64, min))).IsEqualTo((Int128)min);
    }

    // ------------------------------------------------------------------------- unsigned widening

    [Test]
    public async Task UnsignedColumns_WidenIntoEveryWiderUnsignedTarget()
    {
        await Assert.That(RowMapper.Map<ushort>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((ushort)byte.MaxValue);
        await Assert.That(RowMapper.Map<uint>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((uint)byte.MaxValue);
        await Assert.That(RowMapper.Map<ulong>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((ulong)byte.MaxValue);

        await Assert.That(RowMapper.Map<uint>(Row(LadybugType.UInt16, ushort.MaxValue))).IsEqualTo((uint)ushort.MaxValue);
        await Assert.That(RowMapper.Map<ulong>(Row(LadybugType.UInt16, ushort.MaxValue))).IsEqualTo((ulong)ushort.MaxValue);

        await Assert.That(RowMapper.Map<ulong>(Row(LadybugType.UInt32, uint.MaxValue))).IsEqualTo((ulong)uint.MaxValue);
    }

    /// <summary>
    /// An unsigned column reads into a signed target only where that target's range provably contains
    /// the whole unsigned range - so UINT8 reaches <see cref="short"/> but not <see cref="sbyte"/>, and
    /// UINT64 reaches <see cref="Int128"/> but not <see cref="long"/> (refusals below).
    /// </summary>
    [Test]
    public async Task UnsignedColumns_WidenIntoASignedTargetThatContainsThem()
    {
        await Assert.That(RowMapper.Map<short>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((short)byte.MaxValue);
        await Assert.That(RowMapper.Map<int>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((int)byte.MaxValue);
        await Assert.That(RowMapper.Map<long>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((long)byte.MaxValue);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.UInt8, byte.MaxValue))).IsEqualTo((Int128)byte.MaxValue);

        await Assert.That(RowMapper.Map<int>(Row(LadybugType.UInt16, ushort.MaxValue))).IsEqualTo((int)ushort.MaxValue);
        await Assert.That(RowMapper.Map<long>(Row(LadybugType.UInt16, ushort.MaxValue))).IsEqualTo((long)ushort.MaxValue);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.UInt16, ushort.MaxValue))).IsEqualTo((Int128)ushort.MaxValue);

        await Assert.That(RowMapper.Map<long>(Row(LadybugType.UInt32, uint.MaxValue))).IsEqualTo((long)uint.MaxValue);
        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.UInt32, uint.MaxValue))).IsEqualTo((Int128)uint.MaxValue);

        await Assert.That(RowMapper.Map<Int128>(Row(LadybugType.UInt64, ulong.MaxValue))).IsEqualTo((Int128)ulong.MaxValue);
    }

    // ---------------------------------------------------------------------- floating-point widening

    /// <summary>
    /// FLOAT into <see cref="double"/> is exact for every <see cref="float"/> - including the extremes
    /// and the non-finite values, which is where a conversion routed through a string or a
    /// <see cref="decimal"/> would come apart.
    /// </summary>
    [Test]
    public async Task Float_WidensIntoDouble()
    {
        await Assert.That(RowMapper.Map<double>(Row(LadybugType.Single, 1.5f))).IsEqualTo(1.5d);
        await Assert.That(RowMapper.Map<double>(Row(LadybugType.Single, float.MaxValue))).IsEqualTo((double)float.MaxValue);
        await Assert.That(RowMapper.Map<double>(Row(LadybugType.Single, float.Epsilon))).IsEqualTo((double)float.Epsilon);
        await Assert.That(RowMapper.Map<double>(Row(LadybugType.Single, float.NaN))).IsNaN();
    }

    // -------------------------------------------------------------------------- through a constructor

    /// <summary>
    /// Widening applies to a constructor parameter as well as to the scalar unwrap. This is not a
    /// duplicate of the scalar cases: a converted value is boxed, and the two paths unbox it
    /// differently (a direct cast to <c>T</c> versus <c>ConstructorInvoker</c>), so a converter that
    /// boxed as the <em>column's</em> type rather than the target's would fail here specifically.
    /// </summary>
    [Test]
    public async Task WideningWorksThroughAConstructorParameter()
    {
        await Assert.That(RowMapper.Map<Widened>(Row(LadybugType.Int32, 42)).C).IsEqualTo(42L);
        await Assert.That(RowMapper.Map<Widened>(Row(LadybugType.UInt32, uint.MaxValue)).C).IsEqualTo((long)uint.MaxValue);
    }

    /// <summary>A widened column reads into a <see cref="Nullable{T}"/> target too, and a NULL still reads as null.</summary>
    [Test]
    public async Task WideningWorksThroughANullableTarget()
    {
        await Assert.That(RowMapper.Map<long?>(Row(LadybugType.Int32, 7))).IsEqualTo(7L);
        await Assert.That(RowMapper.Map<long?>(Row(LadybugType.Null, null))).IsNull();
    }

    // ------------------------------------------------------------------------------------ refusals

    /// <summary>
    /// Narrowing is refused in every direction it could be attempted, one step at a time - the message
    /// names the column, the column's own <see cref="LadybugType"/>, and the target type.
    /// </summary>
    [Test]
    public async Task Narrowing_IsRefusedNamingColumnLadybugTypeAndTarget()
    {
        await AssertRefused<sbyte>(LadybugType.Int16, (short)1, "Int16", "sbyte");
        await AssertRefused<short>(LadybugType.Int32, 1, "Int32", "short");
        await AssertRefused<int>(LadybugType.Int64, 1L, "Int64", "int");
        await AssertRefused<long>(LadybugType.Int128, (Int128)1, "Int128", "long");
        await AssertRefused<byte>(LadybugType.UInt16, (ushort)1, "UInt16", "byte");
        await AssertRefused<ushort>(LadybugType.UInt32, 1u, "UInt32", "ushort");
        await AssertRefused<uint>(LadybugType.UInt64, 1ul, "UInt64", "uint");
        await AssertRefused<float>(LadybugType.Double, 1.5d, "Double", "float");
    }

    /// <summary>
    /// Signed into unsigned is refused however wide the target: a negative value has nowhere to go, and
    /// the alternative - reinterpreting the bits - is the silent corruption this rule exists to prevent.
    /// </summary>
    [Test]
    public async Task SignedIntoUnsigned_IsRefused()
    {
        await AssertRefused<byte>(LadybugType.Int8, (sbyte)1, "Int8", "byte");
        await AssertRefused<ushort>(LadybugType.Int8, (sbyte)1, "Int8", "ushort");
        await AssertRefused<uint>(LadybugType.Int32, 1, "Int32", "uint");
        await AssertRefused<ulong>(LadybugType.Int64, 1L, "Int64", "ulong");
        await AssertRefused<ulong>(LadybugType.Int128, (Int128)1, "Int128", "ulong");
    }

    /// <summary>
    /// An unsigned column does <b>not</b> read into a signed target of the same width, even though the
    /// value used here would fit: the rule is about what the column's type can hold, not what this row
    /// happens to hold, and UINT32's top half does not fit in an <see cref="int"/>. Pinned at every
    /// width because "unsigned widens into signed" is the plausible over-generalization.
    /// </summary>
    [Test]
    public async Task UnsignedIntoASignedTargetOfTheSameWidth_IsRefused()
    {
        await AssertRefused<sbyte>(LadybugType.UInt8, (byte)1, "UInt8", "sbyte");
        await AssertRefused<short>(LadybugType.UInt16, (ushort)1, "UInt16", "short");
        await AssertRefused<int>(LadybugType.UInt32, 1u, "UInt32", "int");
        await AssertRefused<long>(LadybugType.UInt64, 1ul, "UInt64", "long");
    }

    /// <summary>
    /// Integer into floating-point is refused as a family, including the cases that would be lossless
    /// (INT32 into <see cref="double"/>) - see <see cref="RowMapper"/>'s remarks for why the boundary is
    /// drawn at the family rather than at a mantissa width.
    /// </summary>
    [Test]
    public async Task IntegerIntoFloatingPoint_IsRefusedEvenWhereItWouldBeLossless()
    {
        await AssertRefused<double>(LadybugType.Int32, 1, "Int32", "double");
        await AssertRefused<double>(LadybugType.Int64, 1L, "Int64", "double");
        await AssertRefused<double>(LadybugType.UInt8, (byte)1, "UInt8", "double");
        await AssertRefused<float>(LadybugType.Int32, 1, "Int32", "float");
    }

    /// <summary>
    /// Widening does not open any target to an unrelated type: a BOOLEAN column is not an integer of
    /// width one, and a STRING column is not a number regardless of what it spells.
    /// </summary>
    [Test]
    public async Task UnrelatedTypes_AreStillRefused()
    {
        await AssertRefused<int>(LadybugType.Boolean, true, "Boolean", "int");
        await AssertRefused<long>(LadybugType.String, "42", "String", "long");
        await AssertRefused<int>(LadybugType.InternalId, new LadybugInternalId(0, 1), "InternalId", "int");
    }

    /// <summary>
    /// Asserts <typeparamref name="T"/> cannot read a column of <paramref name="type"/>, and that the
    /// refusal names the column, the column's <see cref="LadybugType"/>, and the target type - the three
    /// things a caller needs to fix it without a debugger.
    /// </summary>
    private static async Task AssertRefused<T>(
        LadybugType type, object? payload, string expectedLadybugType, string expectedTarget)
    {
        var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<T>(Row(type, payload)));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'c'");
        await Assert.That(ex.Message).Contains(expectedLadybugType);
        await Assert.That(ex.Message).Contains(expectedTarget);
    }
}
