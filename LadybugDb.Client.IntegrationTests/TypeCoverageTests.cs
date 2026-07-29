using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Closes the remaining type-coverage gaps documented in docs/USAGE.md's "Type coverage" section:
/// <c>RECURSIVE_REL</c> (variable-length path results, <see cref="LadybugValue.AsPath"/>),
/// <c>UUID</c> (<see cref="LadybugValue.AsGuid"/>), <c>INT128</c> (<see cref="LadybugValue.AsInt128"/>),
/// and the <c>UNION</c>/<c>POINTER</c> fallback (<see cref="LadybugType.Unsupported"/> now readable
/// via <see cref="LadybugValue.AsString"/> instead of throwing on every accessor).
/// </summary>
public class TypeCoverageTests
{
    /// <summary>
    /// A real multi-hop variable-length path query: a 3-node, 2-relationship chain
    /// <c>A-[:Knows]-&gt;B-[:Knows]-&gt;C</c>, matched with <c>[:Knows*1..3]</c> and endpoints pinned
    /// to <c>A</c> and <c>C</c> so exactly one path (the full 2-hop chain) can match. Asserts node
    /// and relationship counts, path ordering (start to end), and a property value on both a node
    /// and a relationship - not just that the value parses without throwing.
    /// </summary>
    [Test]
    public async Task ReadPath_MultiHopVariableLengthMatch_ReturnsOrderedNodesAndRelsWithProperties()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Person(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE REL TABLE Knows(FROM Person TO Person, since INT64)")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 1, name: 'Ada'})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 2, name: 'Grace'})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 3, name: 'Alan'})")) { }
            await using (var _ = await conn.QueryAsync(
                "MATCH (a:Person {id: 1}), (b:Person {id: 2}) CREATE (a)-[:Knows {since: 1990}]->(b)")) { }
            await using (var _ = await conn.QueryAsync(
                "MATCH (a:Person {id: 2}), (b:Person {id: 3}) CREATE (a)-[:Knows {since: 1995}]->(b)")) { }

            await using var r = await conn.QueryAsync(
                "MATCH p = (a:Person {id: 1})-[:Knows*1..3]->(c:Person {id: 3}) RETURN p");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Path);
            var result = value.AsPath();

            await Assert.That(result.Nodes.Count).IsEqualTo(3);
            await Assert.That(result.Relationships.Count).IsEqualTo(2);

            // Path order runs start to end: Ada -> Grace -> Alan.
            await Assert.That(result.Nodes[0].Properties["name"].AsString()).IsEqualTo("Ada");
            await Assert.That(result.Nodes[1].Properties["name"].AsString()).IsEqualTo("Grace");
            await Assert.That(result.Nodes[2].Properties["name"].AsString()).IsEqualTo("Alan");

            await Assert.That(result.Relationships[0].Properties["since"].AsInt64()).IsEqualTo(1990L);
            await Assert.That(result.Relationships[1].Properties["since"].AsInt64()).IsEqualTo(1995L);

            // The relationships' own endpoint ids line up with the path's node ids.
            await Assert.That(result.Relationships[0].SourceId).IsEqualTo(result.Nodes[0].Id);
            await Assert.That(result.Relationships[0].DestinationId).IsEqualTo(result.Nodes[1].Id);
            await Assert.That(result.Relationships[1].SourceId).IsEqualTo(result.Nodes[1].Id);
            await Assert.That(result.Relationships[1].DestinationId).IsEqualTo(result.Nodes[2].Id);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A single-hop match still returns a RECURSIVE_REL/<see cref="LadybugType.Path"/> value (not a
    /// plain REL) when the pattern uses variable-length syntax, even though only one hop matched -
    /// pins that the one-node-one-rel-shorter case is not silently mishandled.
    /// </summary>
    [Test]
    public async Task ReadPath_SingleHopMatch_ReturnsOneNodePairAndOneRel()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Person(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE REL TABLE Knows(FROM Person TO Person, since INT64)")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 1, name: 'Ada'})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 2, name: 'Grace'})")) { }
            await using (var _ = await conn.QueryAsync(
                "MATCH (a:Person {id: 1}), (b:Person {id: 2}) CREATE (a)-[:Knows {since: 2001}]->(b)")) { }

            await using var r = await conn.QueryAsync(
                "MATCH p = (a:Person {id: 1})-[:Knows*1..3]->(b:Person {id: 2}) RETURN p");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var result = e.Current.GetValue(0).AsPath();

            await Assert.That(result.Nodes.Count).IsEqualTo(2);
            await Assert.That(result.Relationships.Count).IsEqualTo(1);
            await Assert.That(result.Relationships[0].Properties["since"].AsInt64()).IsEqualTo(2001L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A UUID column accepts a string literal (implicitly cast by the engine) and reads back as
    /// <see cref="LadybugType.Uuid"/>/<see cref="LadybugValue.AsGuid"/> with the exact value.
    /// </summary>
    [Test]
    public async Task ReadUuid_FromStringLiteral_RoundTripsThroughAsGuid()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE U(id INT64, val UUID, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:U {id: 1, val: '3fa85f64-5717-4562-b3fc-2c963f66afa6'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:U) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Uuid);
            await Assert.That(value.AsGuid()).IsEqualTo(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The write side (<see cref="LadybugPreparedStatement.Bind(string, Guid)"/>) round-trips
    /// through the read side (<see cref="LadybugValue.AsGuid"/>) - an independent verification path
    /// from the literal-string test above, since both directions of the client's own code are
    /// exercised together, against the real engine, not just one.
    /// </summary>
    [Test]
    public async Task BindGuid_RoundTripsThroughAsGuid()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE U(id INT64, val UUID, PRIMARY KEY(id))")) { }

            var original = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");
            await using (var stmt = await conn.PrepareAsync("CREATE (n:U {id: 1, val: $val})"))
            {
                stmt.Bind("val", original);
                await using (var _ = await stmt.ExecuteAsync()) { }
            }

            await using var r = await conn.QueryAsync("MATCH (n:U) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Uuid);
            await Assert.That(value.AsGuid()).IsEqualTo(original);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An INT128 column accepts a literal at the engine's own maximum (<see cref="Int128.MaxValue"/>,
    /// 39 digits - too wide for any narrower integer literal) and reads back exactly.
    /// </summary>
    [Test]
    public async Task ReadInt128_FromMaxValueLiteral_RoundTripsThroughAsInt128()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE I(id INT64, val INT128, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:I {id: 1, val: 170141183460469231731687303715884105727})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:I) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Int128);
            await Assert.That(value.AsInt128()).IsEqualTo(Int128.MaxValue);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Boundary-value round trips for <see cref="LadybugPreparedStatement.Bind(string, Int128)"/> /
    /// <see cref="LadybugValue.AsInt128"/>, via the real engine - not a managed-only unit test. Every
    /// expected value is built with .NET's own <see cref="Int128"/> arithmetic/casts (never this
    /// client's <c>low</c>/<c>high</c> splitting logic), so a systematic sign or endianness bug in
    /// that splitting logic cannot cancel itself out between the bind and read sides and still pass.
    /// Covers exactly the cases verified during development: -1 (all bits set - would come back
    /// wrong under a naive unsigned reading of the high half), 2^64 (the low/high split boundary
    /// itself), and both <see cref="Int128.MinValue"/>/<see cref="Int128.MaxValue"/>.
    /// </summary>
    [Test]
    [Arguments("zero")]
    [Arguments("one")]
    [Arguments("negative_one")]
    [Arguments("two_pow_64")]
    [Arguments("min_value")]
    [Arguments("max_value")]
    public async Task BindInt128_BoundaryValues_RoundTripThroughAsInt128(string @case)
    {
        Int128 expected = @case switch
        {
            "zero" => Int128.Zero,
            "one" => Int128.One,
            "negative_one" => (Int128)(-1),
            "two_pow_64" => (Int128)1 << 64,
            "min_value" => Int128.MinValue,
            "max_value" => Int128.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE I(id INT64, val INT128, PRIMARY KEY(id))")) { }

            await using (var stmt = await conn.PrepareAsync("CREATE (n:I {id: 1, val: $val})"))
            {
                stmt.Bind("val", expected);
                await using (var _ = await stmt.ExecuteAsync()) { }
            }

            await using var r = await conn.QueryAsync("MATCH (n:I) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Int128);
            await Assert.That(value.AsInt128()).IsEqualTo(expected);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// UNION is reachable through ordinary Cypher DDL (<c>CREATE NODE TABLE ... UNION(...)</c>),
    /// unlike POINTER, which the engine rejects outright as neither an internal nor user-defined
    /// type ("POINTER is neither an internal type nor a user defined type" - confirmed empirically,
    /// there is no schema syntax that reaches it). A UNION value always resolves to exactly one
    /// concretely-typed value by read time (see <c>ValueReader.ReadUnion</c>'s remarks), so it reads
    /// as that value's own real <see cref="LadybugType"/> - here <see cref="LadybugType.Int64"/> and
    /// <see cref="LadybugType.String"/> respectively - not <see cref="LadybugType.Unsupported"/>,
    /// and every existing typed accessor (<see cref="LadybugValue.AsInt64"/>,
    /// <see cref="LadybugValue.AsString"/>) simply works on it.
    /// </summary>
    [Test]
    public async Task ReadUnion_ExplicitConstructor_ResolvesToActiveMemberRealType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE UN(id INT64, val UNION(a INT64, b STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:UN {id: 1, val: union_value(a := 42)})")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:UN {id: 2, val: union_value(b := 'hello')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:UN) RETURN n.id, n.val ORDER BY n.id");
            await using var e = r.GetAsyncEnumerator();

            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var first = e.Current.GetValue(1);
            await Assert.That(first.Type).IsEqualTo(LadybugType.Int64);
            await Assert.That(first.AsInt64()).IsEqualTo(42L);

            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var second = e.Current.GetValue(1);
            await Assert.That(second.Type).IsEqualTo(LadybugType.String);
            await Assert.That(second.AsString()).IsEqualTo("hello");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The explicit <c>union_value(member := value)</c> constructor stores the NAMED member's own
    /// declared type directly, bypassing the write-time coercion a bare literal goes through (see
    /// <see cref="ReadUnion_BareLiteralAssignment_CoercesToFirstMatchingMemberType"/>). Binding the
    /// digit string <c>"42"</c> to the STRING member of <c>UNION(num INT64, txt STRING)</c> - where
    /// INT64 is declared first and WOULD have matched a bare literal - genuinely stores a STRING,
    /// confirmed by reading it back as <see cref="LadybugType.String"/>, not
    /// <see cref="LadybugType.Int64"/>, even though both would render identically through
    /// <see cref="LadybugValue.AsString"/> alone (<c>"42"</c> either way) - which is exactly why
    /// reading the resolved member with its real type, not just <see cref="LadybugValue.AsString"/>,
    /// is what actually disambiguates this case.
    /// </summary>
    [Test]
    public async Task ReadUnion_ExplicitConstructorOnStringMemberHoldingDigits_StaysString()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE UN(id INT64, val UNION(num INT64, txt STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:UN {id: 1, val: union_value(txt := '42')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:UN) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.String);
            await Assert.That(value.AsString()).IsEqualTo("42");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A bare Cypher literal assigned to a UNION column (no <c>union_value()</c>) is coerced at
    /// WRITE time: the engine tries the declared member types in order and stores the value as the
    /// first one that accepts it. For <c>UNION(num INT64, txt STRING)</c>, the digit-string literal
    /// <c>'42'</c> is stored as the INT64 member (not the STRING one), and <c>'hello'</c> falls
    /// through to the STRING member since it does not parse as INT64. Confirmed empirically, not
    /// assumed - this is the opposite of the explicit-constructor case above, and is exactly why
    /// a UNION value is never genuinely ambiguous by the time it is read: coercion already resolved
    /// which member applies before storage.
    /// </summary>
    [Test]
    [Arguments("42", "num_int64")]
    [Arguments("'42'", "num_int64")]
    [Arguments("'hello'", "txt_string")]
    public async Task ReadUnion_BareLiteralAssignment_CoercesToFirstMatchingMemberType(string literal, string expectedCase)
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE UN(id INT64, val UNION(num INT64, txt STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync($"CREATE (n:UN {{id: 1, val: {literal}}})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:UN) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            if (expectedCase == "num_int64")
            {
                await Assert.That(value.Type).IsEqualTo(LadybugType.Int64);
                await Assert.That(value.AsInt64()).IsEqualTo(42L);
            }
            else
            {
                await Assert.That(value.Type).IsEqualTo(LadybugType.String);
                await Assert.That(value.AsString()).IsEqualTo("hello");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same write-time coercion holds for a UNION whose members cannot cross-coerce into each
    /// other (BOOL and STRING - a bare boolean literal cannot become a string and vice versa), so
    /// each literal resolves to its own matching member with no fallback ambiguity at all.
    /// </summary>
    [Test]
    public async Task ReadUnion_NonCoercibleMembers_EachLiteralResolvesToItsOwnMember()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE UN(id INT64, val UNION(flag BOOL, txt STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:UN {id: 1, val: true})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:UN {id: 2, val: 'hello'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:UN) RETURN n.id, n.val ORDER BY n.id");
            await using var e = r.GetAsyncEnumerator();

            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var boolValue = e.Current.GetValue(1);
            await Assert.That(boolValue.Type).IsEqualTo(LadybugType.Boolean);
            await Assert.That(boolValue.AsBoolean()).IsTrue();

            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var stringValue = e.Current.GetValue(1);
            await Assert.That(stringValue.Type).IsEqualTo(LadybugType.String);
            await Assert.That(stringValue.AsString()).IsEqualTo("hello");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
