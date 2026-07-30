using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using ExtendedNumerics;
using LadybugDb.Client.Mapping;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// <see cref="ParameterBinder"/> decides whether a caller's parameters object is a dictionary to
/// read by key or an object to reflect over. Getting that decision wrong does not throw - it binds
/// parameters the caller never wrote and runs the query anyway.
/// </summary>
/// <remarks>
/// The original implementation of this seam tested only
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c>. Generic interfaces are invariant in their value
/// type, so <c>Dictionary&lt;string, long&gt;</c> did not match, fell through to the reflection path,
/// and bound <c>Comparer</c>, <c>Count</c>, <c>Capacity</c>, <c>Keys</c>, <c>Values</c>, and
/// <c>Item</c> as parameter names. <see cref="DictionaryOfLong_BindsItsKeys_NotItsOwnProperties"/> is
/// that case; it fails if the non-generic <see cref="IDictionary"/> test is ever removed.
/// </remarks>
public class ParameterBinderTests
{
    /// <summary>The property names a dictionary exposes, which must never be read as parameters.</summary>
    private static readonly string[] DictionaryOwnProperties =
        ["Comparer", "Count", "Capacity", "Keys", "Values", "Item"];

    private static IReadOnlyList<KeyValuePair<string, object?>> Enumerate(object parameters) =>
        ParameterBinder.Enumerate(parameters);

    private static IReadOnlyList<string> NamesOf(object parameters) =>
        Enumerate(parameters).Select(p => p.Key).ToArray();

    // ---------------------------------------------------------------- the regression that matters

    /// <summary>
    /// The defect this seam exists to prevent. A <c>long</c>-valued dictionary is an entirely
    /// ordinary thing to write, and it must bind by its keys.
    /// </summary>
    [Test]
    public async Task DictionaryOfLong_BindsItsKeys_NotItsOwnProperties()
    {
        var parameters = new Dictionary<string, long> { ["dbref"] = 42L, ["parent"] = 7L };

        var pairs = Enumerate(parameters);

        await Assert.That(NamesOf(parameters)).IsEquivalentTo(new[] { "dbref", "parent" });
        await Assert.That(pairs.Single(p => p.Key == "dbref").Value).IsEqualTo(42L);
        await Assert.That(pairs.Single(p => p.Key == "parent").Value).IsEqualTo(7L);
    }

    /// <summary>
    /// States the failure mode directly, so the test names the symptom even if the assertion above
    /// is ever loosened: none of a dictionary's own property names may appear as a parameter.
    /// </summary>
    [Test]
    public async Task DictionaryOfLong_NeverBindsAnyDictionaryProperty()
    {
        var names = NamesOf(new Dictionary<string, long> { ["dbref"] = 42L });

        foreach (var own in DictionaryOwnProperties)
        {
            await Assert.That(names).DoesNotContain(own);
        }
    }

    // ------------------------------------------------------- every dictionary shape reads by key

    /// <summary>
    /// Each of these implements non-generic <see cref="IDictionary"/> but not
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c>, so each one depends on that second test.
    /// <c>FrozenDictionary</c> is included because its runtime type is an internal implementation
    /// class, which a type-name-based check would miss.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(DictionaryShapes))]
    public async Task EveryDictionaryShape_BindsByKey(string description, object parameters)
    {
        var pairs = Enumerate(parameters);

        await Assert.That(pairs.Count).IsEqualTo(1).Because(description);
        await Assert.That(pairs[0].Key).IsEqualTo("dbref").Because(description);
        await Assert.That(pairs[0].Value).IsEqualTo(42L).Because(description);
    }

    public static IEnumerable<Func<(string, object)>> DictionaryShapes()
    {
        yield return () => ("Dictionary<string, object?>", new Dictionary<string, object?> { ["dbref"] = 42L });
        yield return () => ("Dictionary<string, object>", new Dictionary<string, object> { ["dbref"] = 42L });
        yield return () => ("Dictionary<string, long>", new Dictionary<string, long> { ["dbref"] = 42L });
        yield return () => ("SortedDictionary<string, long>", new SortedDictionary<string, long> { ["dbref"] = 42L });
        yield return () => ("ConcurrentDictionary<string, long>",
            new ConcurrentDictionary<string, long>(new Dictionary<string, long> { ["dbref"] = 42L }));
        yield return () => ("ImmutableDictionary<string, long>",
            ImmutableDictionary.CreateRange(new Dictionary<string, long> { ["dbref"] = 42L }));
        yield return () => ("FrozenDictionary<string, long>",
            new Dictionary<string, long> { ["dbref"] = 42L }.ToFrozenDictionary());
        yield return () => ("ReadOnlyDictionary<string, long>",
            new Dictionary<string, long> { ["dbref"] = 42L }.AsReadOnly());
        yield return () => ("Hashtable", new Hashtable { ["dbref"] = 42L });
    }

    /// <summary>
    /// <c>Dictionary&lt;string, object&gt;</c> reaches the fast path even though the annotation says
    /// <c>object?</c>, because nullable reference annotations are erased at runtime. Recorded as a
    /// test so the fast path's real reach is documented rather than assumed.
    /// </summary>
    [Test]
    public async Task DictionaryOfNonNullableObject_ReachesTheSameResult()
    {
        var pairs = Enumerate(new Dictionary<string, object> { ["name"] = "Limbo" });

        await Assert.That(pairs.Single().Key).IsEqualTo("name");
        await Assert.That(pairs.Single().Value).IsEqualTo("Limbo");
    }

    [Test]
    public async Task NullDictionaryValue_IsPreservedAsNull()
    {
        var pairs = Enumerate(new Dictionary<string, object?> { ["name"] = null });

        await Assert.That(pairs.Single().Key).IsEqualTo("name");
        await Assert.That(pairs.Single().Value).IsNull();
    }

    [Test]
    public async Task EmptyDictionary_YieldsNoParameters()
    {
        await Assert.That(Enumerate(new Dictionary<string, object?>())).IsEmpty();
    }

    [Test]
    public async Task NonStringKeyedDictionary_ThrowsNamingTheKeyType()
    {
        var ex = Assert.Throws<ArgumentException>(() => Enumerate(new Dictionary<int, long> { [1] = 42L }));

        await Assert.That(ex!.Message).Contains("Int32");
        await Assert.That(ex.ParamName).IsEqualTo("parameters");
    }

    // ------------------------------------------------------------------------ the object path

    [Test]
    public async Task AnonymousObject_BindsItsProperties()
    {
        var pairs = Enumerate(new { dbref = 42L, name = "Limbo" });

        await Assert.That(pairs.Count).IsEqualTo(2);
        await Assert.That(pairs.Single(p => p.Key == "dbref").Value).IsEqualTo(42L);
        await Assert.That(pairs.Single(p => p.Key == "name").Value).IsEqualTo("Limbo");
    }

    private record ObjectParameters(long Dbref, string? Name);

    [Test]
    public async Task PositionalRecord_BindsItsProperties()
    {
        var pairs = Enumerate(new ObjectParameters(42L, null));

        await Assert.That(pairs.Count).IsEqualTo(2);
        await Assert.That(pairs.Single(p => p.Key == "Dbref").Value).IsEqualTo(42L);
        await Assert.That(pairs.Single(p => p.Key == "Name").Value).IsNull();
    }

    private class WriteOnlyAndIndexed
    {
        public long Dbref { get; set; }

        public string this[int index] => index.ToString();

        public static string Ignored => "static";

        private string Hidden => "private";

        public string WriteOnly { set { } }
    }

    /// <summary>
    /// An indexer is a property named <c>Item</c> that takes arguments; a static or private property
    /// belongs to no instance. Reading any of them would invent a parameter or throw.
    /// </summary>
    [Test]
    public async Task IndexerStaticPrivateAndWriteOnlyProperties_AreNotParameters()
    {
        var names = NamesOf(new WriteOnlyAndIndexed { Dbref = 42L });

        await Assert.That(names).IsEquivalentTo(new[] { "Dbref" });
    }

    private class Base
    {
        public long Inherited { get; set; }
    }

    private class Derived : Base
    {
        public string Own { get; set; } = "x";
    }

    [Test]
    public async Task InheritedProperties_AreParameters()
    {
        var names = NamesOf(new Derived { Inherited = 42L, Own = "y" });

        await Assert.That(names).IsEquivalentTo(new[] { "Inherited", "Own" });
    }

    private class ShadowBase
    {
        public object Value { get; set; } = 1L;
    }

    private class ShadowDerived : ShadowBase
    {
        public new string Value { get; set; } = "x";
    }

    /// <summary>
    /// A <c>new</c>-shadowed property appears twice. Silently picking one would repeat the exact
    /// mistake this class was written to prevent, so it is reported instead.
    /// </summary>
    [Test]
    public async Task ShadowedProperty_ThrowsAsAmbiguous()
    {
        var ex = Assert.Throws<ArgumentException>(() => Enumerate(new ShadowDerived()));

        await Assert.That(ex!.Message).Contains("Value");
        await Assert.That(ex.Message).Contains("ambiguous");
    }

    // -------------------------------------------------------------------- rejected, not guessed

    [Test]
    public async Task Null_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Enumerate(null!));

        await Assert.That(ex!.ParamName).IsEqualTo("parameters");
    }

    /// <summary>
    /// A bound value passed where a bag belongs. <see cref="string"/> is the dangerous one: left to
    /// reflection it would bind a parameter named <c>Length</c> and run.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(BoundValues))]
    public async Task SingleValue_ThrowsRatherThanBindingItsProperties(string description, object value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Enumerate(value));

        await Assert.That(ex!.Message).Contains("single value").Because(description);
        await Assert.That(ex.ParamName).IsEqualTo("parameters").Because(description);
    }

    public static IEnumerable<Func<(string, object)>> BoundValues()
    {
        yield return () => ("string", "Limbo");
        yield return () => ("long", 42L);
        yield return () => ("int", 42);
        yield return () => ("bool", true);
        yield return () => ("double", 1.5d);
        yield return () => ("decimal", 1.5m);
        yield return () => ("BigDecimal", new BigDecimal(42));
        yield return () => ("Int128", (Int128)42);
        yield return () => ("Guid", Guid.Empty);
        yield return () => ("DateTime", new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc));
        yield return () => ("DateTimeOffset", DateTimeOffset.UnixEpoch);
        yield return () => ("DateOnly", new DateOnly(2026, 7, 29));
        yield return () => ("TimeSpan", TimeSpan.FromHours(1));
        yield return () => ("enum", LadybugType.Int64);
    }

    /// <summary>
    /// A sequence is positional; parameters are named. Left to reflection, <c>List&lt;long&gt;</c>
    /// would bind <c>Capacity</c> and <c>Count</c>.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Sequences))]
    public async Task Sequence_ThrowsRatherThanBindingItsProperties(string description, object value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Enumerate(value));

        await Assert.That(ex!.Message).Contains("sequence").Because(description);
        await Assert.That(ex.ParamName).IsEqualTo("parameters").Because(description);
    }

    public static IEnumerable<Func<(string, object)>> Sequences()
    {
        yield return () => ("long[]", new[] { 1L, 2L });
        yield return () => ("List<long>", new List<long> { 1L, 2L });
        yield return () => ("HashSet<string>", new HashSet<string> { "a" });
        yield return () => ("byte[]", new byte[] { 1, 2 });
    }

    private class NoProperties
    {
        public long Field = 42L;
    }

    /// <summary>Fields are not properties; an object exposing only fields names no parameters.</summary>
    [Test]
    public async Task ObjectWithNoReadableProperties_ThrowsSayingSo()
    {
        var ex = Assert.Throws<ArgumentException>(() => Enumerate(new NoProperties()));

        await Assert.That(ex!.Message).Contains("no readable public properties");
    }

    /// <summary>
    /// Every rejection above must name the type it rejected. An error that says only "invalid
    /// parameters" sends the caller to a debugger.
    /// </summary>
    [Test]
    public async Task RejectionMessages_NameTheOffendingType()
    {
        await Assert.That(Assert.Throws<ArgumentException>(() => Enumerate("Limbo"))!.Message)
            .Contains("String");
        await Assert.That(Assert.Throws<ArgumentException>(() => Enumerate(new List<long>()))!.Message)
            .Contains("List<Int64>");
        await Assert.That(Assert.Throws<ArgumentException>(() => Enumerate(new NoProperties()))!.Message)
            .Contains(nameof(NoProperties));
    }
}
