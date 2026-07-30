using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ExtendedNumerics;

namespace LadybugDb.Client.Mapping;

/// <summary>
/// Turns a caller-supplied parameters object - a dictionary, or an object whose public properties
/// name the parameters - into the name/value pairs to bind.
/// </summary>
/// <remarks>
/// <para>
/// This is the only reflective code path in the client, isolated here so the boundary a future
/// source generator would replace stays clearly drawn.
/// </para>
/// <para>
/// <b>The order of the type tests in <see cref="Enumerate"/> is load-bearing, and was established by
/// measurement rather than assumption.</b> Testing only
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> of <see cref="string"/> to
/// <see cref="object"/> misses <c>Dictionary&lt;string, long&gt;</c> - generic interfaces are
/// invariant in their value type - which then reaches the reflection path, where the "parameters"
/// become that dictionary's own public properties: <c>Comparer</c>, <c>Count</c>, <c>Capacity</c>,
/// <c>Keys</c>, <c>Values</c>, <c>Item</c>. No exception is raised; the query simply runs with
/// parameters the caller never wrote. The non-generic <see cref="IDictionary"/> test exists to close
/// that hole, and covers every dictionary shape measured: <c>Dictionary</c>,
/// <c>SortedDictionary</c>, <c>ConcurrentDictionary</c>, <c>ImmutableDictionary</c>,
/// <c>FrozenDictionary</c>, and <c>ReadOnlyDictionary</c>, at any value type. Anonymous types
/// implement none of these interfaces, so they still reach the reflection path.
/// </para>
/// <para>
/// Validation is eager and the result materialized, so an invalid parameters object throws from the
/// call the caller made rather than partway through binding. The 19 typed <c>Bind</c> overloads
/// remain the allocation-free path; this one trades a list allocation for predictable failure.
/// </para>
/// </remarks>
internal static class ParameterBinder
{
    /// <summary>
    /// Extracts the parameter names and values from <paramref name="parameters"/>.
    /// </summary>
    /// <param name="parameters">A dictionary keyed by parameter name, or an object whose public
    /// instance properties name the parameters.</param>
    /// <param name="paramName">The name of the caller's parameter, used in thrown exceptions.</param>
    /// <returns>The parameter names and values, in no guaranteed order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameters"/> is a dictionary with a
    /// non-<see cref="string"/> key, or is a value or sequence rather than a parameter bag, or
    /// exposes no usable properties, or exposes two properties with the same name.</exception>
    [RequiresUnreferencedCode(
        "Reads the parameters object's public properties by reflection. Use a dictionary, or the " +
        "typed Bind overloads, when trimming.")]
    internal static IReadOnlyList<KeyValuePair<string, object?>> Enumerate(
        object parameters, string paramName = "parameters")
    {
        ArgumentNullException.ThrowIfNull(parameters, paramName);

        // Fast path: the common case, read without boxing the values. Nullable reference
        // annotations are erased at runtime, so Dictionary<string, object> lands here too.
        if (parameters is IReadOnlyDictionary<string, object?> typed)
        {
            var pairs = new List<KeyValuePair<string, object?>>(typed.Count);
            foreach (var pair in typed)
            {
                pairs.Add(pair);
            }

            return pairs;
        }

        // Every other dictionary shape, at any value type. Values box on the way through.
        if (parameters is IDictionary untyped)
        {
            var pairs = new List<KeyValuePair<string, object?>>(untyped.Count);
            foreach (DictionaryEntry entry in untyped)
            {
                if (entry.Key is not string key)
                {
                    throw new ArgumentException(
                        $"Parameter names must be strings, but the supplied " +
                        $"{Describe(parameters.GetType())} is keyed by " +
                        $"{Describe(entry.Key.GetType())}.",
                        paramName);
                }

                pairs.Add(new KeyValuePair<string, object?>(key, entry.Value));
            }

            return pairs;
        }

        return FromProperties(parameters, paramName);
    }

    /// <summary>
    /// Reads parameters from an object's public instance properties, rejecting the shapes whose
    /// properties would silently stand in for parameters the caller never wrote.
    /// </summary>
    [RequiresUnreferencedCode("Reads the parameters object's public properties by reflection.")]
    private static IReadOnlyList<KeyValuePair<string, object?>> FromProperties(
        object parameters, string paramName)
    {
        var type = parameters.GetType();

        // A bound value passed where a parameter bag belongs. Left to reflection, `string` would
        // bind a parameter named `Length` and every other scalar would report "no properties",
        // both of which describe the symptom rather than the mistake.
        if (IsBoundValue(type))
        {
            throw new ArgumentException(
                $"Expected a dictionary or an object whose properties name the parameters, but got " +
                $"{Describe(type)}, which is a single value. Pass an object such as " +
                $"`new {{ name = value }}`, or bind it by name with the typed Bind overloads.",
                paramName);
        }

        // Likewise a sequence: List<T> would bind `Capacity` and `Count`. Dictionaries have
        // already returned above, so nothing legitimate reaches here.
        if (parameters is IEnumerable)
        {
            throw new ArgumentException(
                $"Expected a dictionary or an object whose properties name the parameters, but got " +
                $"{Describe(type)}, which is a sequence. Parameters are named, not positional; " +
                $"pass an object such as `new {{ name = value }}`.",
                paramName);
        }

        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        if (properties.Length == 0)
        {
            throw new ArgumentException(
                $"{Describe(type)} exposes no readable public properties, so it names no " +
                $"parameters.",
                paramName);
        }

        // A `new`-shadowed property appears twice. Picking one silently would be the same class of
        // mistake as the Dictionary<string, long> hole above, so say so instead.
        var duplicate = properties
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"{Describe(type)} declares more than one property named '{duplicate.Key}', so the " +
                $"parameter it names is ambiguous.",
                paramName);
        }

        var pairs = new List<KeyValuePair<string, object?>>(properties.Length);
        foreach (var property in properties)
        {
            pairs.Add(new KeyValuePair<string, object?>(property.Name, property.GetValue(parameters)));
        }

        return pairs;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a value the client binds directly, and so is a parameter
    /// <em>value</em> rather than a bag of them. Mirrors the typed <c>Bind</c> overloads.
    /// </summary>
    private static bool IsBoundValue(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(BigDecimal)
        || type == typeof(Int128)
        || type == typeof(UInt128)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan);

    /// <summary>Names a type readably, since anonymous types have unspeakable names.</summary>
    private static string Describe(Type type)
    {
        if (type.Name.StartsWith("<>", StringComparison.Ordinal))
        {
            return "an anonymous type";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        var args = string.Join(", ", type.GetGenericArguments().Select(Describe));
        return $"{name}<{args}>";
    }
}
