using ExtendedNumerics;
using LadybugDb.Client.Mapping;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// <see cref="RowMapper"/> resolves a projection by matching constructor parameter names against
/// returned column names, then converts each column through the matching
/// <see cref="LadybugValue"/> accessor. Every failure mode here is one that would otherwise be
/// silent - a plan that reads the right value from the wrong column, a projection that ignores every
/// column it was given, a DECIMAL rounded to fit - so each is pinned to a named exception with the
/// column in its message.
/// </summary>
/// <remarks>
/// Rows are constructed directly through <see cref="LadybugRow"/>'s and
/// <see cref="LadybugValue"/>'s <c>internal</c> constructors (this project has
/// <c>InternalsVisibleTo</c>), the same technique <see cref="LadybugValueEqualityTests"/> uses, so
/// the mapping logic is exercised without a live engine. <c>RowMapperIntegrationTests</c> covers the
/// same seam against real query results, where the <see cref="LadybugType"/>s are the engine's own
/// rather than this file's assertions about them.
/// </remarks>
public class RowMapperTests
{
    // ------------------------------------------------------------------------------- row builders

    private static LadybugRow Row(params (string Name, LadybugValue Value)[] columns) =>
        new([.. columns.Select(c => c.Value)], [.. columns.Select(c => c.Name)]);

    private static LadybugValue Int64(long value) => new(LadybugType.Int64, value);

    private static LadybugValue Int32(int value) => new(LadybugType.Int32, value);

    private static LadybugValue Str(string value) => new(LadybugType.String, value);

    private static LadybugValue Dec(string exact) => new(LadybugType.Decimal, exact);

    private static readonly LadybugValue Null = new(LadybugType.Null, null);

    // -------------------------------------------------------------------- the projected shapes

    private record Person(long Dbref, string Name);

    private record Reversed(string Name, long Dbref);

    private record Nullables(long? Dbref, string? Name);

    private record Money(BigDecimal Balance);

    private record Truncatable(decimal Balance);

    private record Text(string Balance);

    private enum Kind
    {
        None = 0,
    }

    private record Unsupported(long Dbref, Kind Kind);

    /// <summary>Two constructors, only one of which names the columns this test file returns.</summary>
    private class OneUsableConstructor
    {
        public OneUsableConstructor(long Dbref, string Name)
        {
            this.Dbref = Dbref;
            this.Name = Name;
        }

        /// <summary>Matches no column named <c>Kind</c>, so it is rejected before its parameter type is ever looked at.</summary>
        public OneUsableConstructor(long Dbref, Kind Kind)
        {
            this.Dbref = Dbref;
            this.Name = Kind.ToString();
        }

        public long Dbref { get; }

        public string Name { get; }
    }

    /// <summary>Both constructors match <c>(Dbref, Name)</c>, so a projection into this is ambiguous.</summary>
    private class TwoMatchingConstructors
    {
        public TwoMatchingConstructors(long Dbref, string Name) => Description = $"{Dbref}:{Name}";

        public TwoMatchingConstructors(string Name, long Dbref) => Description = $"{Name}:{Dbref}";

        public string Description { get; }
    }

    private class ParameterlessOnly
    {
        public long Dbref { get; set; }
    }

    private class NoPublicConstructor
    {
        private NoPublicConstructor()
        {
        }
    }

    private record Validating(long Dbref)
    {
        public long Dbref { get; } = Dbref >= 0
            ? Dbref
            : throw new ArgumentOutOfRangeException(nameof(Dbref), "A dbref is never negative.");
    }

    // ---------------------------------------------------------------------- constructor matching

    [Test]
    public async Task PositionalRecord_MapsByParameterName()
    {
        var person = RowMapper.Map<Person>(Row(("Dbref", Int64(42)), ("Name", Str("Limbo"))));

        await Assert.That(person.Dbref).IsEqualTo(42L);
        await Assert.That(person.Name).IsEqualTo("Limbo");
    }

    /// <summary>
    /// Case-insensitive matching is what makes a PascalCase record usable against a query that
    /// returns the engine's own lower-case property names without an <c>AS</c> alias per column.
    /// </summary>
    [Test]
    public async Task ColumnNames_MatchParametersCaseInsensitively()
    {
        var person = RowMapper.Map<Person>(Row(("dbref", Int64(42)), ("NAME", Str("Limbo"))));

        await Assert.That(person.Dbref).IsEqualTo(42L);
        await Assert.That(person.Name).IsEqualTo("Limbo");
    }

    /// <summary>
    /// Column ORDER is irrelevant to matching - a parameter is bound to the column that shares its
    /// name, not to the column in its position. Reading by position would compile, run, and return
    /// values from the wrong columns whenever a query's RETURN order differed from the record's.
    /// </summary>
    [Test]
    public async Task ColumnOrder_DoesNotDecideWhichParameterGetsWhichColumn()
    {
        var person = RowMapper.Map<Person>(Row(("Name", Str("Limbo")), ("Dbref", Int64(42))));

        await Assert.That(person.Dbref).IsEqualTo(42L);
        await Assert.That(person.Name).IsEqualTo("Limbo");
    }

    [Test]
    public async Task ExtraColumns_AreIgnored()
    {
        var person = RowMapper.Map<Person>(
            Row(("Dbref", Int64(42)), ("Ignored", Int64(7)), ("Name", Str("Limbo"))));

        await Assert.That(person.Dbref).IsEqualTo(42L);
        await Assert.That(person.Name).IsEqualTo("Limbo");
    }

    /// <summary>
    /// A parameter with no column is the error case, and the message has to be usable without a
    /// debugger: it names the returned columns AND every rejected constructor with its parameter
    /// names, because the mistake is almost always a typo in one of the two lists.
    /// </summary>
    [Test]
    public async Task ParameterWithNoColumn_ThrowsNamingColumnsAndCandidates()
    {
        var row = Row(("Dbref", Int64(42)), ("Nmae", Str("Limbo")));

        var ex = Assert.Throws<InvalidOperationException>(() => RowMapper.Map<Person>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Person");
        await Assert.That(ex.Message).Contains("'Dbref'");
        await Assert.That(ex.Message).Contains("'Nmae'");
        await Assert.That(ex.Message).Contains("Person(long Dbref, string Name)");
        await Assert.That(ex.Message).Contains("'Name'");
    }

    [Test]
    public async Task MoreThanOneMatchingConstructor_ThrowsAsAmbiguousWithoutGuessing()
    {
        var row = Row(("Dbref", Int64(42)), ("Name", Str("Limbo")));

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowMapper.Map<TwoMatchingConstructors>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("ambiguous");
        await Assert.That(ex.Message).Contains("TwoMatchingConstructors(long Dbref, string Name)");
        await Assert.That(ex.Message).Contains("TwoMatchingConstructors(string Name, long Dbref)");
    }

    /// <summary>
    /// A parameterless constructor vacuously "matches" every result - all zero of its parameters
    /// have a column - and would then hand back objects holding none of the returned data, silently.
    /// It is rejected as a candidate, and the message says why rather than leaving a caller to
    /// wonder where their columns went.
    /// </summary>
    [Test]
    public async Task ParameterlessConstructor_IsNotAMatch()
    {
        var row = Row(("Dbref", Int64(42)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowMapper.Map<ParameterlessOnly>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("ParameterlessOnly()");
        await Assert.That(ex.Message).Contains("takes no parameters");
    }

    [Test]
    public async Task NoPublicConstructor_SaysSoRatherThanListingNothing()
    {
        var row = Row(("Dbref", Int64(42)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowMapper.Map<NoPublicConstructor>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("declares no public instance constructor");
    }

    /// <summary>
    /// Parameter TYPES are validated only for the constructor already chosen by name. Validating
    /// them while filtering candidates would let an unusable overload's parameter type reject a
    /// projection a perfectly good sibling constructor serves - which is exactly this shape.
    /// </summary>
    [Test]
    public async Task UnsupportedParameterType_OnARejectedOverload_DoesNotBlockTheMatchingOne()
    {
        var mapped = RowMapper.Map<OneUsableConstructor>(
            Row(("Dbref", Int64(42)), ("Name", Str("Limbo"))));

        await Assert.That(mapped.Dbref).IsEqualTo(42L);
        await Assert.That(mapped.Name).IsEqualTo("Limbo");
    }

    [Test]
    public async Task UnsupportedParameterType_OnTheMatchingConstructor_NamesTheParameterAndItsType()
    {
        var row = Row(("Dbref", Int64(42)), ("Kind", Int64(0)));

        var ex = Assert.Throws<InvalidOperationException>(() => RowMapper.Map<Unsupported>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'Kind'");
        await Assert.That(ex.Message).Contains("Kind");
        await Assert.That(ex.Message).Contains("no column can be converted to");
    }

    /// <summary>
    /// A constructor that validates its own arguments reports its own exception, not a
    /// <see cref="System.Reflection.TargetInvocationException"/> wrapped around it - the reason
    /// <see cref="System.Reflection.ConstructorInvoker"/> is used rather than
    /// <c>ConstructorInfo.Invoke</c>.
    /// </summary>
    [Test]
    public async Task ConstructorThatThrows_SurfacesItsOwnException()
    {
        var row = Row(("Dbref", Int64(-1)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RowMapper.Map<Validating>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("A dbref is never negative.");
    }

    // --------------------------------------------------------------------------- scalar unwrap

    [Test]
    public async Task ScalarType_WithOneColumn_ConvertsThatColumnDirectly()
    {
        await Assert.That(RowMapper.Map<long>(Row(("count(*)", Int64(3))))).IsEqualTo(3L);
        await Assert.That(RowMapper.Map<string>(Row(("n.name", Str("Limbo"))))).IsEqualTo("Limbo");
    }

    [Test]
    public async Task NullableScalar_ReadsNullAsNull()
    {
        await Assert.That(RowMapper.Map<long?>(Row(("n.parent", Null)))).IsNull();
        await Assert.That(RowMapper.Map<long?>(Row(("n.parent", Int64(7))))).IsEqualTo(7L);
    }

    /// <summary>Every scalar target, converted from the <see cref="LadybugType"/> that backs it.</summary>
    [Test]
    public async Task EveryScalarTarget_ConvertsFromItsOwnLadybugType()
    {
        var guid = Guid.NewGuid();

        await Assert.That(RowMapper.Map<bool>(Row(("c", new LadybugValue(LadybugType.Boolean, true))))).IsTrue();
        await Assert.That(RowMapper.Map<sbyte>(Row(("c", new LadybugValue(LadybugType.Int8, (sbyte)-8))))).IsEqualTo((sbyte)-8);
        await Assert.That(RowMapper.Map<short>(Row(("c", new LadybugValue(LadybugType.Int16, (short)-16))))).IsEqualTo((short)-16);
        await Assert.That(RowMapper.Map<int>(Row(("c", Int32(-32))))).IsEqualTo(-32);
        await Assert.That(RowMapper.Map<long>(Row(("c", Int64(-64))))).IsEqualTo(-64L);
        await Assert.That(RowMapper.Map<byte>(Row(("c", new LadybugValue(LadybugType.UInt8, (byte)8))))).IsEqualTo((byte)8);
        await Assert.That(RowMapper.Map<ushort>(Row(("c", new LadybugValue(LadybugType.UInt16, (ushort)16))))).IsEqualTo((ushort)16);
        await Assert.That(RowMapper.Map<uint>(Row(("c", new LadybugValue(LadybugType.UInt32, 32u))))).IsEqualTo(32u);
        await Assert.That(RowMapper.Map<ulong>(Row(("c", new LadybugValue(LadybugType.UInt64, 64ul))))).IsEqualTo(64ul);
        await Assert.That(RowMapper.Map<Int128>(Row(("c", new LadybugValue(LadybugType.Int128, (Int128)128))))).IsEqualTo((Int128)128);
        await Assert.That(RowMapper.Map<float>(Row(("c", new LadybugValue(LadybugType.Single, 1.5f))))).IsEqualTo(1.5f);
        await Assert.That(RowMapper.Map<double>(Row(("c", new LadybugValue(LadybugType.Double, 2.25d))))).IsEqualTo(2.25d);
        await Assert.That(RowMapper.Map<decimal>(Row(("c", Dec("12345.6789"))))).IsEqualTo(12345.6789m);
        await Assert.That(RowMapper.Map<BigDecimal>(Row(("c", Dec("12345.6789"))))).IsEqualTo(BigDecimal.Parse("12345.6789"));
        await Assert.That(RowMapper.Map<string>(Row(("c", Str("hello"))))).IsEqualTo("hello");
        await Assert.That(RowMapper.Map<byte[]>(Row(("c", new LadybugValue(LadybugType.Blob, new byte[] { 1, 2, 3 }))))).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(RowMapper.Map<Guid>(Row(("c", new LadybugValue(LadybugType.Uuid, guid))))).IsEqualTo(guid);
        await Assert.That(RowMapper.Map<DateOnly>(Row(("c", new LadybugValue(LadybugType.Date, new DateOnly(2026, 7, 29)))))).IsEqualTo(new DateOnly(2026, 7, 29));
        await Assert.That(RowMapper.Map<DateTime>(Row(("c", new LadybugValue(LadybugType.Timestamp, new DateTime(2026, 7, 29, 1, 2, 3, DateTimeKind.Utc)))))).IsEqualTo(new DateTime(2026, 7, 29, 1, 2, 3, DateTimeKind.Utc));
        await Assert.That(RowMapper.Map<DateTimeOffset>(Row(("c", new LadybugValue(LadybugType.TimestampTz, DateTimeOffset.UnixEpoch))))).IsEqualTo(DateTimeOffset.UnixEpoch);
        await Assert.That(RowMapper.Map<TimeSpan>(Row(("c", new LadybugValue(LadybugType.Interval, TimeSpan.FromHours(3)))))).IsEqualTo(TimeSpan.FromHours(3));
    }

    /// <summary>
    /// A scalar target against a multi-column result is a mistake in the query or the target, not
    /// something to resolve by picking the first column. The count is in the message because the
    /// caller's next question is "how many did I return, then?".
    /// </summary>
    [Test]
    public async Task ScalarTarget_WithMoreThanOneColumn_ThrowsNamingTheColumnCount()
    {
        var row = Row(("Dbref", Int64(42)), ("Name", Str("Limbo")));

        var ex = Assert.Throws<InvalidOperationException>(() => RowMapper.Map<long>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("2 column(s)");
        await Assert.That(ex.Message).Contains("long");
        await Assert.That(ex.Message).Contains("exactly one column");
    }

    /// <summary>
    /// <c>string</c> and <c>decimal</c> both have public constructors whose parameters could in
    /// principle be name-matched, so the scalar check must run before constructor matching or a
    /// one-column <c>string</c> projection would resolve to <c>string(char, int)</c>.
    /// </summary>
    [Test]
    public async Task ScalarUnwrap_TakesPrecedenceOverAScalarTypesOwnConstructors()
    {
        var plan = RowMapper.ResolvePlan<string>(Row(("c", Str("x"))));

        await Assert.That(plan.IsScalarUnwrap).IsTrue();
        await Assert.That(plan.Constructor).IsNull();
    }

    // ------------------------------------------------------------------------------- conversion

    /// <summary>
    /// Conversion is exact, not widening: an INT32 column read as <c>long</c> is an error naming
    /// both, not a silent widening. Silent numeric coercion has produced a defect in this project
    /// before, and the error message carries the fix.
    /// </summary>
    [Test]
    public async Task ColumnOfADifferentWidth_ThrowsNamingColumnLadybugTypeAndTarget()
    {
        var row = Row(("Dbref", Int32(42)), ("Name", Str("Limbo")));

        var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Person>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'Dbref'");
        await Assert.That(ex.Message).Contains("Int32");
        await Assert.That(ex.Message).Contains("long");
    }

    [Test]
    public async Task WrongTypeEntirely_ThrowsNamingColumnLadybugTypeAndTarget()
    {
        var row = Row(("Dbref", Str("forty-two")), ("Name", Str("Limbo")));

        var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Person>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'Dbref'");
        await Assert.That(ex.Message).Contains("String");
        await Assert.That(ex.Message).Contains("long");
    }

    [Test]
    public async Task NullIntoANonNullableValueType_ThrowsNamingTheColumn()
    {
        var row = Row(("Dbref", Null), ("Name", Str("Limbo")));

        var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Person>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'Dbref'");
        await Assert.That(ex.Message).Contains("NULL");
        await Assert.That(ex.Message).Contains("long?");
    }

    [Test]
    public async Task NullIntoAReferenceOrNullableTarget_IsNull()
    {
        var mapped = RowMapper.Map<Nullables>(Row(("Dbref", Null), ("Name", Null)));

        await Assert.That(mapped.Dbref).IsNull();
        await Assert.That(mapped.Name).IsNull();
    }

    /// <summary>
    /// The DECIMAL precision decision, stated as a test: a value the engine holds but
    /// <see cref="decimal"/> cannot is NOT truncated or rounded to fit. It throws, names the column,
    /// and points at <see cref="BigDecimal"/> - which reads the same value losslessly.
    /// </summary>
    [Test]
    public async Task Decimal38Digits_IntoADecimalTarget_ThrowsRatherThanTruncating()
    {
        const string exact = "12345678901234567890123456789012345678";
        var row = Row(("Balance", Dec(exact)));

        var ex = Assert.Throws<LadybugException>(() => RowMapper.Map<Truncatable>(row));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'Balance'");
        await Assert.That(ex.Message).Contains("losing precision");
        await Assert.That(ex.Message).Contains("BigDecimal");

        // The same column, read losslessly - so the exception above is a real choice between two
        // available behaviours, not the only thing the client can do with this value.
        await Assert.That(RowMapper.Map<Money>(row).Balance).IsEqualTo(BigDecimal.Parse(exact));
    }

    /// <summary>
    /// A DECIMAL that fits is read into a <c>decimal</c> target exactly, so the throw above is
    /// scoped to genuine precision loss rather than to DECIMAL columns in general.
    /// </summary>
    [Test]
    public async Task DecimalWithinRange_IntoADecimalTarget_IsExact()
    {
        var mapped = RowMapper.Map<Truncatable>(Row(("Balance", Dec("12345.6789"))));

        await Assert.That(mapped.Balance).IsEqualTo(12345.6789m);
    }

    /// <summary>
    /// A <c>string</c> target inherits <see cref="LadybugValue.AsString"/>'s deliberate breadth - it
    /// reads any string-backed value, DECIMAL included. Pinned here because it is the one target
    /// that accepts more than one <see cref="LadybugType"/>, and a future "tighten AsString" change
    /// would otherwise silently change what a projection accepts.
    /// </summary>
    [Test]
    public async Task StringTarget_ReadsAnyStringBackedValue_IncludingDecimal()
    {
        var mapped = RowMapper.Map<Text>(Row(("Balance", Dec("12345.6789"))));

        await Assert.That(mapped.Balance).IsEqualTo("12345.6789");
    }

    /// <summary>
    /// Duplicate column names are legal Cypher (<c>RETURN n.a AS x, n.b AS x</c>). Leftmost wins,
    /// matching <see cref="LadybugRow"/>'s own indexer rather than inventing a second rule.
    /// </summary>
    [Test]
    public async Task DuplicateColumnName_ResolvesToTheLeftmostColumn()
    {
        var mapped = RowMapper.Map<Person>(
            Row(("Dbref", Int64(1)), ("Name", Str("first")), ("Name", Str("second"))));

        await Assert.That(mapped.Name).IsEqualTo("first");
    }

    // -------------------------------------------------------------------------- plan reuse guard

    /// <summary>
    /// A plan reads columns by index. Using one on a row of a different shape would read real values
    /// out of the wrong columns - a wrong answer with no exception - so the count is re-checked per
    /// row, which is what catches a plan accidentally shared across two results.
    /// </summary>
    [Test]
    public async Task PlanUsedOnARowOfADifferentWidth_Throws()
    {
        var plan = RowMapper.ResolvePlan<Person>(Row(("Dbref", Int64(1)), ("Name", Str("a"))));

        var ex = Assert.Throws<InvalidOperationException>(
            () => plan.Map(Row(("Dbref", Int64(1)), ("Name", Str("a")), ("Extra", Int64(2)))));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("2 column(s)");
        await Assert.That(ex.Message).Contains("3");
    }

    /// <summary>
    /// The shape the streaming projection drives: resolve once, map many. Also the shape that
    /// exposes an index-vs-name mix-up, since the two records below name the same columns in
    /// opposite orders.
    /// </summary>
    [Test]
    public async Task OnePlan_MapsEveryRowOfTheResult()
    {
        var columns = new[] { "Dbref", "Name" };
        var plan = RowMapper.ResolvePlan<Reversed>(columns);

        var rows = Enumerable.Range(1, 5)
            .Select(i => Row(("Dbref", Int64(i)), ("Name", Str($"n{i}"))))
            .Select(plan.Map)
            .ToArray();

        await Assert.That(rows.Select(r => r.Dbref)).IsEquivalentTo(new[] { 1L, 2L, 3L, 4L, 5L });
        await Assert.That(rows.Select(r => r.Name)).IsEquivalentTo(new[] { "n1", "n2", "n3", "n4", "n5" });
    }
}
