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
    /// Enumerates <paramref name="parameters"/> and binds every name/value pair it names to
    /// <paramref name="statement"/>.
    /// </summary>
    /// <param name="statement">The statement to bind onto.</param>
    /// <param name="parameters">A dictionary keyed by parameter name, or an object whose public
    /// instance properties name the parameters.</param>
    /// <param name="paramName">The name of the caller's parameter, used in thrown exceptions.</param>
    /// <remarks>
    /// Runs in two passes - every value's type is checked before <em>any</em> value is bound - so a
    /// parameters object carrying one unbindable value leaves the statement's bound values exactly
    /// as they were rather than partially overwritten. That matters on the reuse path
    /// (<c>statement.ExecuteAsync(parameters)</c>): a caller who catches the
    /// <see cref="ArgumentException"/> and executes the statement again with its previously bound
    /// values gets those values, not a half-applied mixture. Both passes run the same
    /// <see cref="BindOrValidate"/> switch, so the set of types the check accepts cannot drift from
    /// the set the bind pass handles.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameters"/> is not a usable parameter
    /// bag (see <see cref="Enumerate"/>), or names a value whose runtime type has no
    /// <c>Bind</c> overload.</exception>
    [RequiresUnreferencedCode(
        "Reads the parameters object's public properties by reflection. Use a dictionary, or the " +
        "typed Bind overloads, when trimming.")]
    internal static void BindAll(
        LadybugPreparedStatement statement, object parameters, string paramName = "parameters")
    {
        var pairs = Enumerate(parameters, paramName);

        foreach (var (name, value) in pairs)
        {
            if (!BindOrValidate(statement: null, name, value))
            {
                throw new ArgumentException(
                    UnsupportedValueMessage(name, value!.GetType()), paramName);
            }
        }

        // Goes through Bind rather than BindOrValidate directly so exactly one place turns an
        // unbindable value into an exception. Bind's own check cannot fire here - pass one already
        // cleared every value - which is the point: no second copy of the failure message to drift.
        foreach (var (name, value) in pairs)
        {
            Bind(statement, name, value, paramName);
        }
    }

    /// <summary>
    /// Binds one name/value pair to <paramref name="statement"/>, dispatching on the value's
    /// runtime type to the matching typed <c>Bind</c> overload.
    /// </summary>
    /// <param name="statement">The statement to bind onto.</param>
    /// <param name="name">The Cypher parameter name, without its leading <c>$</c>.</param>
    /// <param name="value">The value to bind. <see langword="null"/> binds a typed
    /// <c>NULL</c> via <see cref="LadybugPreparedStatement.BindNull"/>.</param>
    /// <param name="paramName">The name of the caller's parameter, used in thrown exceptions.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/>'s runtime type has no
    /// <c>Bind</c> overload.</exception>
    internal static void Bind(
        LadybugPreparedStatement statement, string name, object? value,
        string paramName = "parameters")
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (!BindOrValidate(statement, name, value))
        {
            throw new ArgumentException(UnsupportedValueMessage(name, value!.GetType()), paramName);
        }
    }

    /// <summary>
    /// The single dispatch table behind both <see cref="Bind"/> and <see cref="BindAll"/>, mapping a
    /// value's runtime type onto one of <see cref="LadybugPreparedStatement"/>'s typed <c>Bind</c>
    /// overloads. Returns <see langword="false"/>, rather than throwing, when the type has no
    /// overload, so the caller can attach its own <c>paramName</c>.
    /// </summary>
    /// <param name="statement">The statement to bind onto, or <see langword="null"/> to check
    /// whether <paramref name="value"/> <em>could</em> be bound without binding it. One switch
    /// serving both is deliberate: a separate <c>IsBindable</c> predicate would be a second copy of
    /// this type list, free to drift out of step with it.</param>
    /// <param name="name">The Cypher parameter name.</param>
    /// <param name="value">The value to bind or check.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/>'s runtime type is bindable.</returns>
    /// <remarks>
    /// <para>
    /// Every case matches its type <em>exactly</em> - a <see langword="switch"/> pattern on a boxed
    /// value tests the boxed type, with no implicit numeric conversion - so no value is ever bound
    /// at a width other than the one it was written at. An <see langword="enum"/> boxes as itself,
    /// not as its underlying integer, so it does not match <c>case int</c> and is reported
    /// unsupported rather than silently binding its ordinal.
    /// </para>
    /// <para>
    /// <b>The natural widths here are what the engine accepts, measured against it rather than
    /// assumed</b> - see <c>ParameterWidthCoercionTests</c>. A bound <c>INT32</c> is coerced into an
    /// <c>INT64</c> column (and likewise every narrower integer width, in both directions, and
    /// <c>FLOAT</c> into <c>DOUBLE</c>), so an <see langword="int"/> value need not be widened to
    /// <see cref="long"/> to reach a wider column. A value outside the target column's range is
    /// <em>rejected</em> by the engine with an overflow error surfaced as
    /// <see cref="LadybugException"/>, not silently truncated.
    /// </para>
    /// </remarks>
    private static bool BindOrValidate(LadybugPreparedStatement? statement, string name, object? value)
    {
        switch (value)
        {
            case null: statement?.BindNull(name); return true;
            case bool v: statement?.Bind(name, v); return true;
            case sbyte v: statement?.Bind(name, v); return true;
            case short v: statement?.Bind(name, v); return true;
            case int v: statement?.Bind(name, v); return true;
            case long v: statement?.Bind(name, v); return true;
            case byte v: statement?.Bind(name, v); return true;
            case ushort v: statement?.Bind(name, v); return true;
            case uint v: statement?.Bind(name, v); return true;
            case ulong v: statement?.Bind(name, v); return true;
            case float v: statement?.Bind(name, v); return true;
            case double v: statement?.Bind(name, v); return true;
            case string v: statement?.Bind(name, v); return true;
            case DateOnly v: statement?.Bind(name, v); return true;
            case DateTime v: statement?.Bind(name, v); return true;
            case DateTimeOffset v: statement?.Bind(name, v); return true;
            case TimeSpan v: statement?.Bind(name, v); return true;
            case Guid v: statement?.Bind(name, v); return true;
            case Int128 v: statement?.Bind(name, v); return true;
            case BigDecimal v: statement?.Bind(name, v); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Explains an unbindable value, naming both the parameter and its runtime type - either alone
    /// leaves the caller guessing which of several parameters is at fault, or what about it.
    /// </summary>
    private static string UnsupportedValueMessage(string name, Type type)
    {
        // decimal and BigDecimal are one keystroke apart at the call site and the client binds only
        // the latter (it holds all 38 digits the engine's DECIMAL does), so that specific mistake
        // gets told what to do about it instead of only what went wrong.
        var hint = type == typeof(decimal)
            ? " Convert it to BigDecimal, which binds all 38 digits the engine's DECIMAL holds, " +
              "rather than decimal's 28-29."
            : string.Empty;

        return $"The value supplied for parameter '{name}' is of type {Describe(type)}, which has no " +
               $"Bind overload.{hint}";
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
