using System.Collections.ObjectModel;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// Regresses the fix-round-4 finding: <see cref="LadybugValue"/> relied on the inherited
/// <see cref="ValueType.Equals(object?)"/>, which boxes the payload and compares the box - fine
/// for scalar payloads, silently wrong for reference-typed container payloads (byte[]/list/
/// struct/map), where it degenerated to reference equality. These tests construct
/// <see cref="LadybugValue"/> directly via its <c>internal</c> constructor (this project has
/// <c>InternalsVisibleTo</c>) rather than through a live query, so they exercise the equality/
/// hashing/<c>ToString</c> logic in isolation from the native engine.
/// </summary>
public class LadybugValueEqualityTests
{
    private static LadybugValue Int64(long value) => new(LadybugType.Int64, value);

    private static LadybugValue List(params LadybugValue[] items) =>
        new(LadybugType.List, Array.AsReadOnly(items));

    private static LadybugValue Struct(params (string Name, LadybugValue Value)[] fields)
    {
        var dict = new Dictionary<string, LadybugValue>();
        foreach (var (name, value) in fields) dict[name] = value;
        return new LadybugValue(LadybugType.Struct, new ReadOnlyDictionary<string, LadybugValue>(dict));
    }

    private static LadybugValue Map(params (LadybugValue Key, LadybugValue Value)[] entries) =>
        new(LadybugType.Map, Array.AsReadOnly(entries
            .Select(e => new KeyValuePair<LadybugValue, LadybugValue>(e.Key, e.Value))
            .ToArray()));

    private static LadybugValue Blob(params byte[] bytes) => new(LadybugType.Blob, bytes);

    [Test]
    public async Task Equals_TwoScalarsWithTheSameValue_AreEqual()
    {
        await Assert.That(Int64(1).Equals(Int64(1))).IsTrue();
        await Assert.That(Int64(1).Equals(Int64(2))).IsFalse();
    }

    [Test]
    public async Task Equals_TwoListsWithTheSameElements_AreEqual()
    {
        var a = List(Int64(1), Int64(2), Int64(3));
        var b = List(Int64(1), Int64(2), Int64(3));

        // The bug this regresses: inherited ValueType.Equals compared the boxed
        // ReadOnlyCollection<LadybugValue> by reference, so two independently-built lists with
        // identical elements compared unequal.
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Equals_TwoListsWithDifferentElements_AreNotEqual()
    {
        var a = List(Int64(1), Int64(2), Int64(3));
        var b = List(Int64(1), Int64(2), Int64(4));
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Equals_StructsWithFieldsInDifferentOrder_AreStillEqual()
    {
        var a = Struct(("x", Int64(1)), ("y", Int64(2)));
        var b = Struct(("y", Int64(2)), ("x", Int64(1)));

        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Equals_MapsWithSameEntriesInDifferentOrder_AreNotEqual()
    {
        // Unlike STRUCT, MAP is documented as an ordered sequence (AsMap's remarks) - order is
        // significant, so this is intentionally the opposite assertion from the struct case above.
        var a = Map((Int64(1), Int64(10)), (Int64(2), Int64(20)));
        var b = Map((Int64(2), Int64(20)), (Int64(1), Int64(10)));
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Equals_BlobsWithSameContent_AreEqualByContentNotReference()
    {
        var a = Blob(1, 2, 3);
        var b = Blob(1, 2, 3);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task AsBlob_ReturnsAFreshArrayEveryCall()
    {
        var value = Blob(1, 2, 3);
        var first = value.AsBlob();
        var second = value.AsBlob();

        // The other fix-round-4 finding this file also covers: AsBlob() used to hand out the
        // live shared array while its own doc claimed "holding a copy".
        await Assert.That(ReferenceEquals(first, second)).IsFalse();

        first[0] = 99;
        await Assert.That(value.AsBlob()[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Distinct_OnAListOfContainerValues_DeduplicatesByStructuralEquality()
    {
        var values = new[]
        {
            List(Int64(1), Int64(2)),
            List(Int64(1), Int64(2)),
            List(Int64(3)),
        };

        // Distinct() relies on GetHashCode/Equals - the exact failure mode the finding called out.
        await Assert.That(values.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task ToString_RendersReadableContentForScalarsBlobsAndContainers()
    {
        await Assert.That(Int64(42).ToString()).IsEqualTo("42");
        await Assert.That(Blob(1, 2, 3).ToString()).IsEqualTo("Blob[3]");
        await Assert.That(List(Int64(1), Int64(2)).ToString()).IsEqualTo("[1, 2]");
        await Assert.That(Struct(("x", Int64(1))).ToString()).IsEqualTo("{x: 1}");
        await Assert.That(new LadybugValue(LadybugType.Null, null).ToString()).IsEqualTo("null");
    }

    [Test]
    public async Task DefaultLadybugRow_DoesNotThrowFromColumnCountOrToString()
    {
        var row = default(LadybugRow);
        await Assert.That(row.ColumnCount).IsEqualTo(0);
        await Assert.That(row.ToString()).IsEqualTo("{}");
    }

    [Test]
    public async Task DefaultLadybugRow_GetValueThrowsInvalidOperationNotNullReference()
    {
        var row = default(LadybugRow);
        Assert.Throws<InvalidOperationException>(() => row.GetValue(0));
        await Task.CompletedTask;
    }

    [Test]
    public async Task DefaultLadybugRow_GetColumnNameThrowsInvalidOperationNotNullReference()
    {
        var row = default(LadybugRow);
        Assert.Throws<InvalidOperationException>(() => row.GetColumnName(0));
        await Task.CompletedTask;
    }

    [Test]
    public async Task DefaultLadybugRow_IndexerThrowsInvalidOperationNotNullReference()
    {
        var row = default(LadybugRow);
        Assert.Throws<InvalidOperationException>(() => _ = row["anything"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LadybugNode_StructuralEquality_IgnoresPropertyOrder()
    {
        var a = new LadybugNode(new LadybugInternalId(1, 2), "Obj",
            new ReadOnlyDictionary<string, LadybugValue>(new Dictionary<string, LadybugValue>
            {
                ["name"] = new LadybugValue(LadybugType.String, "Master Room"),
                ["dbref"] = Int64(7),
            }));
        var b = new LadybugNode(new LadybugInternalId(1, 2), "Obj",
            new ReadOnlyDictionary<string, LadybugValue>(new Dictionary<string, LadybugValue>
            {
                ["dbref"] = Int64(7),
                ["name"] = new LadybugValue(LadybugType.String, "Master Room"),
            }));

        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.ToString()).Contains("Obj");
        await Assert.That(a.ToString()).Contains("Master Room");
    }

    private static LadybugNode Node(ulong offset, string label) =>
        new(new LadybugInternalId(0, offset), label, new ReadOnlyDictionary<string, LadybugValue>(
            new Dictionary<string, LadybugValue>()));

    private static LadybugRel Rel(ulong offset, LadybugNode from, LadybugNode to) =>
        new(new LadybugInternalId(1, offset), from.Id, to.Id, "Knows",
            new ReadOnlyDictionary<string, LadybugValue>(new Dictionary<string, LadybugValue>()));

    /// <summary>
    /// Unit-level coverage for <see cref="LadybugPath"/>'s equality/hashing/<c>ToString</c>,
    /// isolated from the native engine the same way <see cref="LadybugNode_StructuralEquality_IgnoresPropertyOrder"/>
    /// is for <see cref="LadybugNode"/> - constructed directly via the internal constructors this
    /// project's <c>InternalsVisibleTo</c> exposes, not through a live query.
    /// </summary>
    [Test]
    public async Task LadybugPath_StructuralEquality_ComparesNodesAndRelsNotReferences()
    {
        var a1 = Node(0, "Person");
        var a2 = Node(1, "Person");
        var pathA = new LadybugPath(new[] { a1, a2 }, new[] { Rel(0, a1, a2) });

        var b1 = Node(0, "Person");
        var b2 = Node(1, "Person");
        var pathB = new LadybugPath(new[] { b1, b2 }, new[] { Rel(0, b1, b2) });

        await Assert.That(ReferenceEquals(pathA, pathB)).IsFalse();
        await Assert.That(pathA.Equals(pathB)).IsTrue();
        await Assert.That(pathA == pathB).IsTrue();
        await Assert.That(pathA.GetHashCode()).IsEqualTo(pathB.GetHashCode());

        var value1 = new LadybugValue(LadybugType.Path, pathA);
        var value2 = new LadybugValue(LadybugType.Path, pathB);
        await Assert.That(value1.Equals(value2)).IsTrue();
        await Assert.That(value1.AsPath()).IsEqualTo(pathA);
    }

    [Test]
    public async Task LadybugPath_DifferentRelCount_AreNotEqual()
    {
        var n1 = Node(0, "Person");
        var n2 = Node(1, "Person");
        var n3 = Node(2, "Person");
        var shortPath = new LadybugPath(new[] { n1, n2 }, new[] { Rel(0, n1, n2) });
        var longPath = new LadybugPath(new[] { n1, n2, n3 }, new[] { Rel(0, n1, n2), Rel(1, n2, n3) });

        await Assert.That(shortPath.Equals(longPath)).IsFalse();
        await Assert.That(shortPath != longPath).IsTrue();
    }

    /// <summary>
    /// Regresses the <see cref="LadybugType.Unsupported"/> fallback fix directly: before this
    /// change, <see cref="LadybugValue.AsString"/> threw on every <see cref="LadybugType.Unsupported"/>
    /// value unconditionally, even though its own XML doc recommended <c>AsString()</c> as the way
    /// to read one. An <see cref="LadybugType.Unsupported"/> value now genuinely carries a string
    /// payload (the engine's own <c>lbug_value_to_string</c> rendering) for UNION/POINTER.
    /// </summary>
    [Test]
    public async Task Unsupported_WithStringPayload_AsStringReturnsIt()
    {
        var value = new LadybugValue(LadybugType.Unsupported, "42");
        await Assert.That(value.AsString()).IsEqualTo("42");
    }

    [Test]
    public async Task Unsupported_WithNullPayload_AsStringStillThrows()
    {
        var value = new LadybugValue(LadybugType.Unsupported, null);
        Assert.Throws<InvalidOperationException>(() => value.AsString());
        await Task.CompletedTask;
    }
}
