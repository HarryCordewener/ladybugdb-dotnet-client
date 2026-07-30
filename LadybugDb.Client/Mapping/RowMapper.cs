using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ExtendedNumerics;

namespace LadybugDb.Client.Mapping;

/// <summary>
/// Projects a <see cref="LadybugRow"/> into a caller-defined type: resolves either a constructor
/// whose parameters name the returned columns, or - for a scalar type against a single-column result
/// - a direct conversion of that one column.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam a streaming projection drives.</b> Resolution is separate from mapping on purpose:
/// resolve one <see cref="RowPlan{T}"/> from the result's column shape, then map every row with it.
/// Both halves are safe to call per row - resolution is cached by <see cref="MappingCache"/> - but
/// the shape below is the intended one, and is what keeps the reflection out of the row loop even if
/// the cache is ever removed:
/// </para>
/// <code>
/// await using var result = await connection.QueryAsync(cypher, parameters, ct);
/// var plan = RowMapper.ResolvePlan&lt;T&gt;(result.ColumnNames);
/// await foreach (var row in result.WithCancellation(ct))
/// {
///     yield return plan.Map(row);
/// }
/// </code>
/// <para>
/// <b>Resolve from the <em>result</em>'s column shape, not from the first row</b> - which is what
/// <see cref="LadybugConnection.Select{T}"/> does. The two are equivalent for every result that has a
/// first row; they differ for one that has none. Resolving from a row means a query returning
/// <b>zero rows</b> never resolves a plan at all, so a mismatched <c>T</c> - unmatched constructor,
/// ambiguity, unsupported target type - raises nothing whatsoever, and an empty result silently
/// "succeeds" against a <c>T</c> that could never have mapped its columns. Resolving from
/// <see cref="LadybugQueryResult.ColumnNames"/> reports that mismatch on an empty result too, which is
/// why that accessor exists.
/// </para>
/// <para>
/// <see cref="ResolvePlan{T}(LadybugRow)"/> remains for the single-row read, where a row is what the
/// caller has in hand; it is the same resolution against the same cache, just keyed off the row's
/// names.
/// </para>
/// <para>
/// This is reflective code, isolated in this folder alongside <see cref="ParameterBinder"/> so the
/// boundary a future source generator would replace stays clearly drawn. It is also, unavoidably,
/// an allocating path: one <c>object?[]</c> per row for the constructor arguments, and one box per
/// converted column. The typed <see cref="LadybugRow"/>/<see cref="LadybugValue"/> accessors remain
/// the allocation-free way to read a result.
/// </para>
/// </remarks>
internal static class RowMapper
{
    /// <summary>
    /// Every target type a column can be converted to, mapped to the <see cref="LadybugValue"/>
    /// accessor that does it. Membership in this table is also the definition of "scalar" for the
    /// single-column unwrap - one list serving both is deliberate, so the set of types that unwrap
    /// cannot drift from the set of types that convert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Conversion accepts lossless widening, and only widening.</b> A target reads the
    /// <see cref="LadybugType"/> that backs it exactly, plus every narrower type that is provably
    /// contained in it - so an <c>INT32</c> column reads into a <see cref="long"/>, a <c>FLOAT</c>
    /// into a <see cref="double"/>, and a <c>UINT32</c> into a <see cref="long"/> (whose range
    /// contains all of <c>UINT32</c>'s). Nothing that can lose a bit, a digit, or a sign is accepted:
    /// no narrowing (<c>INT64</c> into an <see cref="int"/>), no signed into unsigned (<c>INT8</c>
    /// into a <see cref="byte"/> - a negative value has nowhere to go), and no integer into a
    /// floating-point target. Those keep the error naming the column, its <see cref="LadybugType"/>
    /// and the target type, so the fix - widen the target, or <c>CAST</c> in the Cypher - is visible
    /// without a debugger.
    /// </para>
    /// <para>
    /// <b>Why widening rather than the exact match this originally shipped with.</b> Exactness makes a
    /// projection's target type track the schema's declared width, which is the opposite of what an
    /// ergonomics feature is for: a column declared <c>INT32</c> read into the <see cref="long"/> a
    /// record naturally declares is not a coercion a caller can be surprised by, because there is no
    /// value it can be wrong for. It is also what the bind side already does - measured, not assumed
    /// (see <see cref="ParameterBinder"/>'s remarks): the engine coerces a bound <c>INT32</c> into an
    /// <c>INT64</c> column and range-<em>checks</em> rather than truncating. The narrowing direction is
    /// where a silent coercion actually loses data, and that is precisely the direction still refused
    /// here.
    /// </para>
    /// <para>
    /// <b>Integer into floating-point is excluded deliberately, even where it would be lossless.</b>
    /// <c>INT32</c> into a <see cref="double"/> loses nothing (every 32-bit integer is exactly
    /// representable in 53 bits of mantissa), but <c>INT64</c> into a <see cref="double"/> loses
    /// integers above 2^53 and <c>INT32</c> into a <see cref="float"/> loses them above 2^24. A rule
    /// whose boundary is a mantissa width is one callers cannot hold in their heads, and admitting the
    /// safe half of it would make the unsafe half look like an oversight. The whole cross-family
    /// conversion stays out; <c>CAST</c> in the Cypher expresses it explicitly where it is wanted.
    /// </para>
    /// <para>
    /// <see cref="decimal"/> is present but is the one lossy-looking target, and it does not lose
    /// anything silently: <see cref="LadybugValue.AsDecimal"/> throws
    /// <see cref="LadybugException"/> rather than truncating when the engine's value needs more than
    /// the 28-29 significant digits <see cref="decimal"/> holds (the engine supports 38).
    /// <see cref="ConvertColumn"/> re-reports that with the column's name attached and points at
    /// <see cref="BigDecimal"/>, which is lossless for all 38.
    /// </para>
    /// <para>
    /// <see cref="string"/> inherits <see cref="LadybugValue.AsString"/>'s deliberate breadth: it
    /// reads any value whose payload is already a managed string, which includes <c>DECIMAL</c> (the
    /// engine's exact decimal string) and the engine's own rendering of an otherwise unmodelled type.
    /// It is the one target whose accepted set is decided by the payload's representation rather than
    /// by the widening rule above.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan"/> inherits <see cref="LadybugValue.AsTimeSpan"/>'s documented lossiness
    /// for <c>INTERVAL</c> (the engine converts months at a flat 30 days). There is no lossless
    /// managed target for an interval to offer instead, so this maps what the engine gives.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<Type, Func<LadybugValue, object?>> Converters =
        new Dictionary<Type, Func<LadybugValue, object?>>
        {
            [typeof(bool)] = static v => v.AsBoolean(),

            // The narrowest signed and unsigned widths, and float: nothing widens into them losslessly,
            // so each reads exactly its own type.
            [typeof(sbyte)] = static v => v.AsSByte(),
            [typeof(byte)] = static v => v.AsByte(),
            [typeof(float)] = static v => v.AsSingle(),

            // Every arm is cast to the target type explicitly, and the switch's own type is therefore
            // exactly that type. This is load-bearing, not tidiness: the returned object is later
            // unboxed to the target type (RowPlan<T>.Map for a scalar, ConstructorInvoker for a
            // parameter), and an unbox only succeeds against the exact boxed type - a `short` arm left
            // as its natural `sbyte` would box as sbyte and fail the unbox at runtime.
            [typeof(short)] = static v => v.Type switch
            {
                LadybugType.Int16 => v.AsInt16(),
                LadybugType.Int8 => (short)v.AsSByte(),
                LadybugType.UInt8 => (short)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.Int16),
            },
            [typeof(int)] = static v => v.Type switch
            {
                LadybugType.Int32 => v.AsInt32(),
                LadybugType.Int16 => (int)v.AsInt16(),
                LadybugType.Int8 => (int)v.AsSByte(),
                LadybugType.UInt16 => (int)v.AsUInt16(),
                LadybugType.UInt8 => (int)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.Int32),
            },
            [typeof(long)] = static v => v.Type switch
            {
                LadybugType.Int64 => v.AsInt64(),
                LadybugType.Int32 => (long)v.AsInt32(),
                LadybugType.Int16 => (long)v.AsInt16(),
                LadybugType.Int8 => (long)v.AsSByte(),
                LadybugType.UInt32 => (long)v.AsUInt32(),
                LadybugType.UInt16 => (long)v.AsUInt16(),
                LadybugType.UInt8 => (long)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.Int64),
            },
            [typeof(Int128)] = static v => v.Type switch
            {
                LadybugType.Int128 => v.AsInt128(),
                LadybugType.Int64 => (Int128)v.AsInt64(),
                LadybugType.Int32 => (Int128)v.AsInt32(),
                LadybugType.Int16 => (Int128)v.AsInt16(),
                LadybugType.Int8 => (Int128)v.AsSByte(),

                // UINT64 included: its whole range fits in Int128, unlike in long.
                LadybugType.UInt64 => (Int128)v.AsUInt64(),
                LadybugType.UInt32 => (Int128)v.AsUInt32(),
                LadybugType.UInt16 => (Int128)v.AsUInt16(),
                LadybugType.UInt8 => (Int128)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.Int128),
            },
            [typeof(ushort)] = static v => v.Type switch
            {
                LadybugType.UInt16 => v.AsUInt16(),
                LadybugType.UInt8 => (ushort)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.UInt16),
            },
            [typeof(uint)] = static v => v.Type switch
            {
                LadybugType.UInt32 => v.AsUInt32(),
                LadybugType.UInt16 => (uint)v.AsUInt16(),
                LadybugType.UInt8 => (uint)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.UInt32),
            },
            [typeof(ulong)] = static v => v.Type switch
            {
                LadybugType.UInt64 => v.AsUInt64(),
                LadybugType.UInt32 => (ulong)v.AsUInt32(),
                LadybugType.UInt16 => (ulong)v.AsUInt16(),
                LadybugType.UInt8 => (ulong)v.AsByte(),
                _ => throw NotConvertible(v, LadybugType.UInt64),
            },
            [typeof(double)] = static v => v.Type switch
            {
                LadybugType.Double => v.AsDouble(),
                LadybugType.Single => (double)v.AsSingle(),
                _ => throw NotConvertible(v, LadybugType.Double),
            },
            [typeof(decimal)] = static v => v.AsDecimal(),
            [typeof(BigDecimal)] = static v => v.AsBigDecimal(),
            [typeof(string)] = static v => v.AsString(),
            [typeof(byte[])] = static v => v.AsBlob(),
            [typeof(Guid)] = static v => v.AsGuid(),
            [typeof(DateOnly)] = static v => v.AsDateOnly(),
            [typeof(DateTime)] = static v => v.AsDateTime(),
            [typeof(DateTimeOffset)] = static v => v.AsDateTimeOffset(),
            [typeof(TimeSpan)] = static v => v.AsTimeSpan(),
        }.ToFrozenDictionary();

    /// <summary>
    /// The failure a widening converter raises for a column no widening reaches, in the same shape
    /// <see cref="LadybugValue"/>'s own accessors raise for an exact mismatch - so
    /// <see cref="ConvertColumn"/> can catch and re-report both identically.
    /// </summary>
    /// <param name="value">The column's value, for its <see cref="LadybugValue.Type"/>.</param>
    /// <param name="exact">The <see cref="LadybugType"/> the target reads exactly.</param>
    private static InvalidOperationException NotConvertible(LadybugValue value, LadybugType exact) =>
        new($"Value is {value.Type}, not {exact} nor a narrower type that widens losslessly into it.");

    /// <summary>
    /// Resolves the plan for projecting rows of <paramref name="row"/>'s shape into
    /// <typeparamref name="T"/>. Cached: calling this per row costs a dictionary lookup, not a
    /// reflection pass.
    /// </summary>
    /// <typeparam name="T">The type to project each row into.</typeparam>
    /// <param name="row">Any row of the result being projected - only its column names are read.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> cannot be mapped to this
    /// result's columns - see <see cref="BuildPlan{T}"/>.</exception>
    [RequiresUnreferencedCode(
        "Resolves T's constructor and its parameter types by reflection. Read columns through " +
        "LadybugRow's typed accessors when trimming.")]
    internal static RowPlan<T> ResolvePlan<T>(LadybugRow row) => ResolvePlan<T>(ColumnNamesOf(row));

    /// <summary>
    /// Resolves the plan for projecting a result with these columns into <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type to project each row into.</typeparam>
    /// <param name="columnNames">The result's column names, in result order.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> cannot be mapped to these
    /// columns - see <see cref="BuildPlan{T}"/>.</exception>
    [RequiresUnreferencedCode(
        "Resolves T's constructor and its parameter types by reflection. Read columns through " +
        "LadybugRow's typed accessors when trimming.")]
    internal static RowPlan<T> ResolvePlan<T>(IReadOnlyList<string> columnNames) =>
        MappingCache.GetOrAdd<T>(columnNames);

    /// <summary>
    /// Resolves a plan for <paramref name="row"/>'s shape and maps that one row with it. A
    /// convenience for a single-row read; a loop should resolve once and reuse the plan (see this
    /// type's remarks) rather than call this per row.
    /// </summary>
    /// <typeparam name="T">The type to project the row into.</typeparam>
    /// <param name="row">The row to map.</param>
    [RequiresUnreferencedCode(
        "Resolves T's constructor and its parameter types by reflection. Read columns through " +
        "LadybugRow's typed accessors when trimming.")]
    internal static T Map<T>(LadybugRow row) => ResolvePlan<T>(row).Map(row);

    /// <summary>Reads a row's column names, in order.</summary>
    internal static string[] ColumnNamesOf(LadybugRow row)
    {
        var names = new string[row.ColumnCount];
        for (var i = 0; i < names.Length; i++) names[i] = row.GetColumnName(i);
        return names;
    }

    /// <summary>
    /// Whether <paramref name="type"/> converts directly from a single column - i.e. whether it is
    /// one of the scalar types, or a <see cref="Nullable{T}"/> of one.
    /// </summary>
    internal static bool IsScalar(Type type) => TryGetConverter(type, out _, out _);

    /// <summary>
    /// Builds the plan for projecting a result with <paramref name="columnNames"/> into
    /// <typeparamref name="T"/>. Called once per (<typeparamref name="T"/>, column shape) by
    /// <see cref="MappingCache"/>; callers should go through <see cref="ResolvePlan{T}(IReadOnlyList{string})"/>.
    /// </summary>
    /// <typeparam name="T">The type to project each row into.</typeparam>
    /// <param name="columnNames">The result's column names, in result order.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is scalar and the result does not have exactly one column; or no
    /// public constructor's parameters all match the returned columns; or more than one does, making
    /// the choice ambiguous; or the matched constructor takes a parameter of a type no column can be
    /// converted to.
    /// </exception>
    [RequiresUnreferencedCode(
        "Resolves T's constructor and its parameter types by reflection. Read columns through " +
        "LadybugRow's typed accessors when trimming.")]
    internal static RowPlan<T> BuildPlan<T>(IReadOnlyList<string> columnNames)
    {
        var target = typeof(T);

        // Scalar unwrap is checked before any constructor is considered. `string` and `decimal` both
        // have public constructors whose parameters could in principle be name-matched; a scalar
        // target must never be built that way.
        if (TryGetConverter(target, out var converter, out var scalarAllowsNull))
        {
            if (columnNames.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Cannot project a result with {columnNames.Count} column(s) into the scalar type " +
                    $"{Describe(target)}: a scalar projection needs exactly one column. " +
                    $"{DescribeColumns(columnNames)} Project a type whose constructor parameters name " +
                    $"the columns instead, or return a single column.");
            }

            return new RowPlan<T>(
                constructor: null,
                [new ColumnBinding(columnNames[0], 0, target, scalarAllowsNull, converter)],
                columnNames.Count);
        }

        // Leftmost wins for a name that appears twice, matching LadybugRow's own indexer for
        // duplicate column names (legal Cypher: RETURN n.a AS x, n.b AS x). Case-insensitive, so a
        // record's PascalCase parameter matches a lower-case column without an AS alias per column.
        var columnIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columnNames.Count; i++)
        {
            columnIndexes.TryAdd(columnNames[i], i);
        }

        var matches = new List<ConstructorInfo>();
        var rejected = new List<string>();

        foreach (var candidate in target.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = candidate.GetParameters();

            // A parameterless constructor would "match" any result vacuously - every one of its zero
            // parameters has a column - and then produce an object holding none of the returned data,
            // silently. Rejecting it means the caller gets an error naming the constructor instead of
            // a stream of empty objects.
            if (parameters.Length == 0)
            {
                rejected.Add(
                    $"{Describe(candidate)} - takes no parameters, so it can receive no column. " +
                    $"A projected type needs a constructor whose parameters name the columns.");
                continue;
            }

            var unmatched = parameters
                .Where(p => !columnIndexes.ContainsKey(p.Name ?? string.Empty))
                .Select(p => $"'{p.Name}'")
                .ToArray();

            if (unmatched.Length == 0)
            {
                matches.Add(candidate);
            }
            else
            {
                rejected.Add(
                    $"{Describe(candidate)} - no returned column matches " +
                    $"{(unmatched.Length == 1 ? "parameter" : "parameters")} {string.Join(", ", unmatched)}.");
            }
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Cannot project into {Describe(target)}: {matches.Count} of its constructors match " +
                $"the returned columns, so the projection is ambiguous and this does not guess. " +
                $"{DescribeColumns(columnNames)} Matching constructors: " +
                $"{string.Join("; ", matches.Select(Describe))}. Return only the columns one of them " +
                $"names, or remove a constructor.");
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot project into {Describe(target)}: no public constructor's parameters all match " +
                $"the returned columns (matching is case-insensitive; extra columns are ignored). " +
                $"{DescribeColumns(columnNames)} " +
                (rejected.Count == 0
                    ? $"{Describe(target)} declares no public instance constructor."
                    : $"Candidate constructor(s): {string.Join(" ", rejected)}"));
        }

        var chosen = matches[0];
        var chosenParameters = chosen.GetParameters();
        var bindings = new ColumnBinding[chosenParameters.Length];
        for (var i = 0; i < chosenParameters.Length; i++)
        {
            var parameter = chosenParameters[i];

            // Reached only for the ONE constructor already chosen by name. Validating parameter types
            // earlier would let an unusable overload's parameter type reject a projection that a
            // different, perfectly good constructor would have served.
            if (!TryGetConverter(parameter.ParameterType, out var parameterConverter, out var allowsNull))
            {
                throw new InvalidOperationException(
                    $"Cannot project into {Describe(target)}: {Describe(chosen)} takes parameter " +
                    $"'{parameter.Name}' of type {Describe(parameter.ParameterType)}, which no column " +
                    $"can be converted to. Supported target types: {SupportedTargetTypes()} (each also " +
                    $"as a nullable value type).");
            }

            var index = columnIndexes[parameter.Name!];
            bindings[i] = new ColumnBinding(
                columnNames[index], index, parameter.ParameterType, allowsNull, parameterConverter);
        }

        return new RowPlan<T>(chosen, bindings, columnNames.Count);
    }

    /// <summary>
    /// Converts one column of <paramref name="row"/> per <paramref name="binding"/>, turning every
    /// failure into a <see cref="LadybugException"/> that names the column - the accessors' own
    /// messages name only the types, which is not enough to find the offending column in a
    /// twelve-column projection.
    /// </summary>
    /// <exception cref="LadybugException">The column is <c>NULL</c> and the target cannot hold
    /// <see langword="null"/>; or the column's <see cref="LadybugType"/> has no conversion to the
    /// target; or the column is a <c>DECIMAL</c> too precise for a <see cref="decimal"/>.</exception>
    internal static object? ConvertColumn(ColumnBinding binding, LadybugRow row)
    {
        var value = row.GetValue(binding.ColumnIndex);

        if (value.IsNull)
        {
            if (binding.AllowsNull) return null;

            throw new LadybugException(
                $"Column '{binding.ColumnName}' is NULL, which cannot be read as " +
                $"{Describe(binding.TargetType)} - a non-nullable value type. Declare the target as " +
                $"{Describe(binding.TargetType)}? to accept NULL.");
        }

        try
        {
            return binding.Convert(value);
        }
        catch (LadybugException ex)
        {
            // Only AsDecimal raises this: the engine's DECIMAL holds up to 38 significant digits and
            // decimal holds 28-29, so a value in that gap cannot be read into a decimal at all. It is
            // NOT truncated or rounded to fit - reporting the loss is the whole point, since a
            // silently rounded money value is the defect this project has already been bitten by
            // once. The caller's fix is a BigDecimal target, which is lossless for all 38.
            throw new LadybugException(
                $"Column '{binding.ColumnName}' holds a DECIMAL that cannot be read as " +
                $"{Describe(binding.TargetType)} without losing precision, so it was not read at " +
                $"all: {ex.Message} Declare the target as BigDecimal to read it losslessly.",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new LadybugException(
                $"Column '{binding.ColumnName}' is {value.Type}, which cannot be read as " +
                $"{Describe(binding.TargetType)}. Conversion accepts only lossless widening - never " +
                $"narrowing, never signed into unsigned, never integer into floating-point: declare " +
                $"the target as the type the column actually has (or a wider one of the same kind), " +
                $"or CAST the column in the Cypher.",
                ex);
        }
    }

    /// <summary>
    /// Finds the accessor for <paramref name="target"/>, unwrapping <see cref="Nullable{T}"/>.
    /// </summary>
    /// <param name="target">The type a column is to be converted to.</param>
    /// <param name="converter">The accessor to convert with, when one exists.</param>
    /// <param name="allowsNull">
    /// Whether a <c>NULL</c> column can be stored in <paramref name="target"/>: true for a reference
    /// type and for <see cref="Nullable{T}"/>, false for a bare value type. Nullable reference
    /// annotations are erased at runtime, so a <c>string</c> and a <c>string?</c> target are
    /// indistinguishable here and both accept <c>NULL</c>.
    /// </param>
    private static bool TryGetConverter(
        Type target,
        [NotNullWhen(true)] out Func<LadybugValue, object?>? converter,
        out bool allowsNull)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        allowsNull = underlying is not null || !target.IsValueType;
        return Converters.TryGetValue(underlying ?? target, out converter);
    }

    private static string DescribeColumns(IReadOnlyList<string> columnNames) =>
        columnNames.Count == 0
            ? "The result returned no columns."
            : $"Returned columns: {string.Join(", ", columnNames.Select(n => $"'{n}'"))}.";

    private static string SupportedTargetTypes() =>
        string.Join(", ", Converters.Keys.Select(Describe).Order(StringComparer.Ordinal));

    /// <summary>Renders a constructor the way it was declared, so the message can be matched against source by eye.</summary>
    private static string Describe(ConstructorInfo constructor) =>
        $"{Describe(constructor.DeclaringType!)}(" +
        string.Join(", ", constructor.GetParameters().Select(p => $"{Describe(p.ParameterType)} {p.Name}")) +
        ")";

    /// <summary>
    /// Renders a type as it would be written in C#, since every type named in these messages appears
    /// in the caller's own declaration: <c>long?</c>, not <c>Nullable`1[Int64]</c>.
    /// (<see cref="ParameterBinder"/> has its own renderer, which speaks a different vocabulary - CLR
    /// type names, and "an anonymous type" for the shapes a parameters object comes in.)
    /// </summary>
    private static string Describe(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return $"{Describe(underlying)}?";
        if (type.IsArray) return $"{Describe(type.GetElementType()!)}[]";

        return type switch
        {
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(sbyte) => "sbyte",
            _ when type == typeof(short) => "short",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(ushort) => "ushort",
            _ when type == typeof(uint) => "uint",
            _ when type == typeof(ulong) => "ulong",
            _ when type == typeof(float) => "float",
            _ when type == typeof(double) => "double",
            _ when type == typeof(decimal) => "decimal",
            _ when type == typeof(string) => "string",
            _ when type == typeof(object) => "object",
            _ => type.Name,
        };
    }
}

/// <summary>
/// One column of a resolved projection: where to read it, what to convert it to, and how.
/// </summary>
/// <param name="ColumnName">The column's name as the query returned it, for error messages.</param>
/// <param name="ColumnIndex">The column's position in the result, resolved once from the column shape.</param>
/// <param name="TargetType">The constructor parameter's (or scalar <c>T</c>'s) declared type.</param>
/// <param name="AllowsNull">Whether a <c>NULL</c> column can be stored in <paramref name="TargetType"/>.</param>
/// <param name="Convert">The <see cref="LadybugValue"/> accessor that reads this column's target type.</param>
internal sealed record ColumnBinding(
    string ColumnName,
    int ColumnIndex,
    Type TargetType,
    bool AllowsNull,
    Func<LadybugValue, object?> Convert);

/// <summary>
/// A resolved projection of one result shape into <typeparamref name="T"/>: the constructor to call
/// (or the single column to unwrap), and each column's index, target type and accessor.
/// </summary>
/// <typeparam name="T">The type each row is projected into.</typeparam>
/// <remarks>
/// <para>
/// Immutable and free-threaded once built, which is what makes it cacheable across every row of a
/// result and across concurrent queries of the same shape. All the reflection happens in
/// <see cref="RowMapper.BuildPlan{T}"/>; <see cref="Map"/> does a fixed amount of work per row.
/// </para>
/// <para>
/// Holds column <em>indices</em>, not names - which is why <see cref="MappingCache"/> must key on
/// the column shape and not on <typeparamref name="T"/> alone, and why <see cref="Map"/> re-checks
/// each row's column count.
/// </para>
/// </remarks>
internal sealed class RowPlan<T>
{
    /// <summary>
    /// Pre-bound invoker for <see cref="_constructor"/>, or <see langword="null"/> for the scalar
    /// unwrap. <see cref="ConstructorInvoker"/> rather than
    /// <see cref="ConstructorInfo.Invoke(object?[])"/> for two reasons: it hoists the per-call
    /// argument checking out of the row loop, and it does not wrap an exception thrown by the
    /// constructor itself in <see cref="TargetInvocationException"/> - so a record with a validating
    /// constructor surfaces its own <see cref="ArgumentException"/> to the caller rather than a
    /// reflection wrapper around it.
    /// </summary>
    private readonly ConstructorInvoker? _invoker;

    private readonly ConstructorInfo? _constructor;
    private readonly ColumnBinding[] _bindings;
    private readonly int _columnCount;

    internal RowPlan(ConstructorInfo? constructor, ColumnBinding[] bindings, int columnCount)
    {
        _constructor = constructor;
        _invoker = constructor is null ? null : ConstructorInvoker.Create(constructor);
        _bindings = bindings;
        _columnCount = columnCount;
    }

    /// <summary>
    /// <see langword="true"/> if this plan converts a single column directly into
    /// <typeparamref name="T"/> rather than calling a constructor.
    /// </summary>
    internal bool IsScalarUnwrap => _constructor is null;

    /// <summary>The constructor this plan calls, or <see langword="null"/> for a scalar unwrap.</summary>
    internal ConstructorInfo? Constructor => _constructor;

    /// <summary>Each column this plan reads, in constructor-parameter order.</summary>
    internal IReadOnlyList<ColumnBinding> Bindings => _bindings;

    /// <summary>Projects one row.</summary>
    /// <param name="row">A row of the same result this plan was resolved from.</param>
    /// <exception cref="LadybugException">A column could not be converted - see
    /// <see cref="RowMapper.ConvertColumn"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="row"/> has a different number of
    /// columns than the result this plan was resolved from.</exception>
    internal T Map(LadybugRow row)
    {
        // A plan reads columns by index, so mapping a row of a different shape with it would read
        // real values out of the wrong columns - a wrong answer, not an error. The column count is
        // the part of the shape checkable for the price of an int compare per row; the names are
        // not, so this catches the mis-wiring (a plan reused across two results) rather than
        // proving the shape.
        if (row.ColumnCount != _columnCount)
        {
            throw new InvalidOperationException(
                $"This projection was resolved against a result with {_columnCount} column(s) but is " +
                $"being used on a row with {row.ColumnCount}. Resolve one plan per query result: a " +
                $"plan reads columns by position, so reusing one across results of different shapes " +
                $"reads the wrong columns.");
        }

        if (_invoker is null)
        {
            // Scalar unwrap. The cast is an unbox to T (or to Nullable<T>, for which a null
            // reference is the null value) - ConvertColumn has already rejected a NULL that T
            // cannot hold, so this cannot be a null unboxed into a bare value type.
            return (T)RowMapper.ConvertColumn(_bindings[0], row)!;
        }

        var arguments = new object?[_bindings.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = RowMapper.ConvertColumn(_bindings[i], row);
        }

        return (T)_invoker.Invoke(arguments.AsSpan());
    }
}
