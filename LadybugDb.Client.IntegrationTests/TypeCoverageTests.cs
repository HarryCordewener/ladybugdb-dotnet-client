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
    /// there is no schema syntax that reaches it). A UNION value therefore gets a real
    /// against-the-engine test: it reads as <see cref="LadybugType.Unsupported"/>, but - unlike
    /// before this change, when every accessor including <see cref="LadybugValue.AsString"/> threw -
    /// <see cref="LadybugValue.AsString"/> now returns the engine's own string rendering of the
    /// value rather than throwing.
    /// </summary>
    [Test]
    public async Task ReadUnion_IsUnsupportedButReadableViaAsString()
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

            await using var r = await conn.QueryAsync("MATCH (n:UN) RETURN n.val");
            await using var e = r.GetAsyncEnumerator();
            await Assert.That(await e.MoveNextAsync()).IsTrue();
            var value = e.Current.GetValue(0);

            await Assert.That(value.Type).IsEqualTo(LadybugType.Unsupported);
            await Assert.That(value.AsString()).IsEqualTo("42");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
