using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ExtendedNumerics;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// A parameterized Cypher statement, prepared once and executed - with different bound values -
/// as many times as needed, so the engine does not have to re-plan the same query on every call.
/// </summary>
/// <remarks>
/// <para>
/// Leases only its own <see cref="LbugPreparedStatementHandle"/> for every <c>Bind*</c> call: none
/// of the <c>lbug_prepared_statement_bind_*</c> entry points take a connection or database
/// pointer, so - unlike <see cref="ExecuteAsync"/>, which calls <c>lbug_connection_execute</c> and
/// therefore leases the parent database and connection too, exactly like
/// <see cref="LadybugConnection.QueryAsync"/> - there is no ancestor storage a bind call could
/// dereference after it was freed.
/// </para>
/// <para>
/// <b>Concurrent <c>Bind</c> calls on the SAME instance are not safe without <see cref="_bindGate"/>.</b>
/// <c>lbug_prepared_statement</c> carries a <c>_bound_values</c> pointer the engine mutates in
/// place on every bind - unlike <c>lbug_connection</c>, the C header documents no thread-safety
/// guarantee for a prepared statement at all, and reproduced directly: two threads calling
/// <c>Bind</c> on one <see cref="LadybugPreparedStatement"/> concurrently, with no synchronization,
/// corrupted the native heap and crashed the process (SIGABRT/SIGSEGV, <c>free(): invalid
/// pointer</c> on stderr - not a catchable managed exception) on effectively every attempt. Every
/// <c>Bind*</c>/<see cref="BindNull"/> overload below takes <see cref="_bindGate"/> around its own
/// native bind call - not merely its own <see cref="Interop.LbugStructHandle.Acquire"/> lease,
/// which only protects against a concurrent <em>disposal</em>, a separate concern from concurrent
/// re-entry into the same mutable native state. This is not a hot path relative to the native call
/// itself, so a plain <see cref="Lock"/> is the right tool - no need for anything fancier.
/// <see cref="ExecuteAsync"/> deliberately does NOT take this lock: it does not touch
/// <c>_bound_values</c> itself (the engine reads whatever was bound most recently, whenever
/// <c>lbug_connection_execute</c> runs), so serializing it against binds would only add contention
/// without closing any actual gap - see that method's remarks. What <see cref="ExecuteAsync"/> gets
/// concurrently with a <c>Bind</c> in flight, if a caller races the two, is a query result that
/// reflects the bound values as of whenever the engine happened to read them - a correctness
/// question for the CALLER to avoid by not doing that, not a memory-safety one.
/// </para>
/// </remarks>
public sealed class LadybugPreparedStatement : IAsyncDisposable
{
    private static readonly DateOnly Epoch = new(1970, 1, 1);

    private readonly LbugDatabaseHandle _database;
    private readonly LbugConnectionHandle _connection;
    private readonly LbugPreparedStatementHandle _handle;
    private readonly string _cypher;

    /// <summary>
    /// Serializes every <c>Bind*</c>/<see cref="BindNull"/> call on this instance against every
    /// other one - see this type's remarks for why concurrent binds are unsafe without it.
    /// </summary>
    private readonly Lock _bindGate = new();

    /// <summary>
    /// Runs <c>lbug_connection_prepare</c> and checks <c>lbug_prepared_statement_is_success</c>,
    /// throwing on failure - the compile-time counterpart of how <see cref="LadybugConnection.QueryAsync"/>
    /// checks <c>lbug_query_result_is_success</c> after a plain query.
    /// </summary>
    internal static unsafe LadybugPreparedStatement Prepare(
        LbugDatabaseHandle database, LbugConnectionHandle connection, string cypher)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(cypher);
        try
        {
            var handle = LbugPreparedStatementHandle.Prepare(database, connection, (sbyte*)utf8, out var state);

            // Non-null only on failure - see LadybugConnection.Execute for why this doubles as the
            // success/failure flag without a separate bool.
            string? failureMessage = null;
            using (var lease = handle.Acquire())
            {
                var prepared = (lbug_prepared_statement*)lease.Pointer;
                var success = state == lbug_state.LbugSuccess
                    && LbugNative.lbug_prepared_statement_is_success(prepared) != 0;
                if (!success)
                    failureMessage = NativeString.TakeOwnership(
                        LbugNative.lbug_prepared_statement_get_error_message(prepared));
            }

            if (failureMessage is not null)
            {
                handle.Dispose();
                throw new LadybugException(failureMessage, cypher);
            }

            return new LadybugPreparedStatement(database, connection, handle, cypher);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private LadybugPreparedStatement(
        LbugDatabaseHandle database, LbugConnectionHandle connection, LbugPreparedStatementHandle handle, string cypher)
    {
        _database = database;
        _connection = connection;
        _handle = handle;
        _cypher = cypher;
    }

    /// <summary>Binds a boolean parameter.</summary>
    /// <remarks><c>lbug_prepared_statement_bind_bool</c> takes a <c>byte</c>, not a native bool - converted explicitly here.</remarks>
    public unsafe void Bind(string name, bool value) => BindScalar(name, (byte)(value ? 1 : 0), LbugNative.lbug_prepared_statement_bind_bool);

    /// <summary>Binds an <c>INT8</c> parameter.</summary>
    public unsafe void Bind(string name, sbyte value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_int8);

    /// <summary>Binds an <c>INT16</c> parameter.</summary>
    public unsafe void Bind(string name, short value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_int16);

    /// <summary>Binds an <c>INT32</c> parameter.</summary>
    public unsafe void Bind(string name, int value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_int32);

    /// <summary>Binds an <c>INT64</c> parameter.</summary>
    public unsafe void Bind(string name, long value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_int64);

    /// <summary>Binds a <c>UINT8</c> parameter.</summary>
    public unsafe void Bind(string name, byte value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_uint8);

    /// <summary>Binds a <c>UINT16</c> parameter.</summary>
    public unsafe void Bind(string name, ushort value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_uint16);

    /// <summary>Binds a <c>UINT32</c> parameter.</summary>
    public unsafe void Bind(string name, uint value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_uint32);

    /// <summary>Binds a <c>UINT64</c> parameter.</summary>
    public unsafe void Bind(string name, ulong value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_uint64);

    /// <summary>Binds a <c>FLOAT</c> parameter.</summary>
    public unsafe void Bind(string name, float value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_float);

    /// <summary>Binds a <c>DOUBLE</c> parameter.</summary>
    public unsafe void Bind(string name, double value) => BindScalar(name, value, LbugNative.lbug_prepared_statement_bind_double);

    /// <summary>Binds a <c>DATE</c> parameter.</summary>
    /// <remarks>Inverts <see cref="ValueReader"/>'s read side: <c>lbug_date_t.days</c> is days since 1970-01-01.</remarks>
    public unsafe void Bind(string name, DateOnly value)
    {
        int days;
        try
        {
            days = checked(value.DayNumber - Epoch.DayNumber);
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(name, "date", ex);
        }
        BindScalar(name, new lbug_date_t { days = days }, LbugNative.lbug_prepared_statement_bind_date);
    }

    /// <summary>Binds an <c>INTERVAL</c> parameter.</summary>
    /// <remarks>
    /// Built via the engine's own <c>lbug_interval_from_difftime</c> - the exact inverse of
    /// <c>lbug_interval_to_difftime</c>, which <see cref="ValueReader"/> uses on the read
    /// side - rather than re-deriving the months-to-days convention by hand.
    /// </remarks>
    public unsafe void Bind(string name, TimeSpan value)
    {
        lbug_interval_t interval;
        LbugNative.lbug_interval_from_difftime(value.TotalSeconds, &interval);
        BindScalar(name, interval, LbugNative.lbug_prepared_statement_bind_interval);
    }

    /// <summary>Binds a <c>TIMESTAMP</c> (microsecond) parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_t.value</c> is microseconds
    /// since 1970-01-01T00:00:00Z. <paramref name="value"/> is normalized to UTC first - see
    /// <see cref="NormalizeToUtcTicks"/> - so a <see cref="DateTimeKind.Local"/> value binds the
    /// same instant it represents, not its raw wall-clock reading; <see cref="DateTimeKind.Unspecified"/>
    /// is assumed to already be UTC.
    /// </remarks>
    public unsafe void Bind(string name, DateTime value)
    {
        var micros = ToMicros(name, NormalizeToUtcTicks(value));
        BindScalar(name, new lbug_timestamp_t { value = micros }, LbugNative.lbug_prepared_statement_bind_timestamp);
    }

    /// <summary>Binds a <c>TIMESTAMP_TZ</c> parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_tz_t.value</c> is
    /// microseconds since 1970-01-01T00:00:00Z, using <see cref="DateTimeOffset.UtcTicks"/> - the
    /// engine does not retain a distinct source offset, matching how the read side always reports a
    /// zero UTC offset back.
    /// </remarks>
    public unsafe void Bind(string name, DateTimeOffset value)
    {
        var micros = ToMicros(name, value.UtcTicks);
        BindScalar(name, new lbug_timestamp_tz_t { value = micros }, LbugNative.lbug_prepared_statement_bind_timestamp_tz);
    }

    /// <summary>Binds a <c>TIMESTAMP_SEC</c> parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_sec_t.value</c> is seconds
    /// since 1970-01-01T00:00:00Z. <paramref name="value"/> is normalized to UTC first - see
    /// <see cref="NormalizeToUtcTicks"/> - so a <see cref="DateTimeKind.Local"/> value binds the
    /// same instant it represents, not its raw wall-clock reading; <see cref="DateTimeKind.Unspecified"/>
    /// is assumed to already be UTC.
    /// </remarks>
    public unsafe void BindTimestampSeconds(string name, DateTime value)
    {
        long seconds;
        try
        {
            seconds = checked((NormalizeToUtcTicks(value) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(name, "timestamp_sec", ex);
        }
        BindScalar(name, new lbug_timestamp_sec_t { value = seconds }, LbugNative.lbug_prepared_statement_bind_timestamp_sec);
    }

    /// <summary>Binds a <c>TIMESTAMP_MS</c> parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_ms_t.value</c> is
    /// milliseconds since 1970-01-01T00:00:00Z. <paramref name="value"/> is normalized to UTC first
    /// - see <see cref="NormalizeToUtcTicks"/> - so a <see cref="DateTimeKind.Local"/> value binds
    /// the same instant it represents, not its raw wall-clock reading;
    /// <see cref="DateTimeKind.Unspecified"/> is assumed to already be UTC.
    /// </remarks>
    public unsafe void BindTimestampMilliseconds(string name, DateTime value)
    {
        long millis;
        try
        {
            millis = checked((NormalizeToUtcTicks(value) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond);
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(name, "timestamp_ms", ex);
        }
        BindScalar(name, new lbug_timestamp_ms_t { value = millis }, LbugNative.lbug_prepared_statement_bind_timestamp_ms);
    }

    /// <summary>Binds a <c>TIMESTAMP_NS</c> parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_ns_t.value</c> is
    /// nanoseconds since 1970-01-01T00:00:00Z; a <see cref="DateTime"/> tick is 100ns, so this is
    /// exact (no truncation) unlike the microsecond and lower-precision binds above.
    /// <paramref name="value"/> is normalized to UTC first - see <see cref="NormalizeToUtcTicks"/> -
    /// so a <see cref="DateTimeKind.Local"/> value binds the same instant it represents, not its raw
    /// wall-clock reading; <see cref="DateTimeKind.Unspecified"/> is assumed to already be UTC.
    /// </remarks>
    public unsafe void BindTimestampNanoseconds(string name, DateTime value)
    {
        long nanos;
        try
        {
            nanos = checked((NormalizeToUtcTicks(value) - DateTime.UnixEpoch.Ticks) * 100);
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(name, "timestamp_ns", ex);
        }
        BindScalar(name, new lbug_timestamp_ns_t { value = nanos }, LbugNative.lbug_prepared_statement_bind_timestamp_ns);
    }

    /// <summary>Binds a <c>STRING</c> parameter.</summary>
    public unsafe void Bind(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            // Nested, not two flat allocations before one shared try: if this second allocation
            // itself throws (e.g. OutOfMemoryException on a very large value), a flat
            // "both-then-try" shape would leak utf8Name - it was assigned successfully but sits
            // outside any try that could free it. Nesting means utf8Name's own finally always
            // runs, on every path, including this one.
            var utf8Value = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                lbug_state state;
                lock (_bindGate)
                using (var lease = _handle.Acquire())
                {
                    state = LbugNative.lbug_prepared_statement_bind_string(
                        (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, (sbyte*)utf8Value);
                }
                ThrowIfBindFailed(state, name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8Value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>
    /// Binds a <c>DECIMAL</c> parameter losslessly, for all 38 significant digits the engine
    /// supports - the write-side counterpart to <see cref="LadybugValue.AsBigDecimal"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="value"/>'s precision and scale are derived from the value itself (its
    /// <see cref="BigDecimal.Mantissa"/>/<see cref="BigDecimal.Exponent"/>), not taken from the
    /// target column's declared <c>DECIMAL(p,s)</c> - the C API gives a prepared statement no way
    /// to ask what a parameter's declared column type is, only <c>lbug_value_create_decimal(val,
    /// precision, scale)</c> to build the value being bound. Empirically (see
    /// docs/USAGE.md's DECIMAL binding section), the engine accepts a bound precision/scale that
    /// differs from the target column's - lower precision and lower scale are both widened to the
    /// column's own DECIMAL(p,s) on write, not rejected or silently truncated. A derived precision
    /// above 38 - the engine's DECIMAL maximum - throws here before the native call, rather than
    /// letting the engine reject it with a less specific message.
    /// </para>
    /// <para>
    /// Follows the same short-lived-value discipline as <see cref="BindNull"/>, for the same
    /// reason: <c>lbug_value_create_decimal</c> returns an engine-owned <c>lbug_value*</c> directly
    /// (per its "caller is responsible for destroying the returned value"), the same ownership
    /// shape that caused a double-free via <see cref="Interop.LbugValueHandle"/>/
    /// <c>NativeMemory.Free</c> elsewhere in this client, so it is destroyed via
    /// <c>lbug_value_destroy</c> directly in a <c>finally</c> rather than wrapped in a handle.
    /// </para>
    /// </remarks>
    /// <exception cref="LadybugException">
    /// <paramref name="value"/> needs more than 38 significant digits to represent, exceeding what
    /// the engine's DECIMAL type can hold.
    /// </exception>
    public unsafe void Bind(string name, BigDecimal value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var digitString = DecimalStringOf(value, out var precision, out var scale);
        if (precision > 38)
            throw new LadybugException(
                $"The BigDecimal value bound to parameter '{name}' needs {precision} significant " +
                "digits to represent exactly, exceeding the engine's DECIMAL(38) maximum.");

        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var utf8Value = Marshal.StringToCoTaskMemUTF8(digitString);
            try
            {
                var nativeValue = LbugNative.lbug_value_create_decimal((sbyte*)utf8Value, precision, scale);
                try
                {
                    lbug_state state;
                    lock (_bindGate)
                    using (var lease = _handle.Acquire())
                    {
                        state = LbugNative.lbug_prepared_statement_bind_value(
                            (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, nativeValue);
                    }
                    ThrowIfBindFailed(state, name);
                }
                finally
                {
                    LbugNative.lbug_value_destroy(nativeValue);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8Value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>
    /// Renders a <see cref="BigDecimal"/> as the plain (never scientific-notation), always
    /// invariant, always-dot-separated digit string <c>lbug_value_create_decimal</c> expects,
    /// alongside the precision/scale that string implies. Deliberately does not use
    /// <see cref="BigDecimal.ToString()"/>: that formatter defaults to
    /// <see cref="CultureInfo.CurrentCulture"/> (wrong on a comma-decimal-separator machine, the
    /// same hazard <see cref="LadybugValue.AsDecimal"/> guards against on the read side) and pads
    /// or trims digits according to its own <c>NumberFormatInfo.NumberDecimalDigits</c>, neither of
    /// which this bind path wants - it needs the exact digits <paramref name="value"/> carries, no
    /// more and no fewer. Building the string directly from <see cref="BigDecimal.Mantissa"/>/
    /// <see cref="BigDecimal.Exponent"/> sidesteps both.
    /// </summary>
    private static string DecimalStringOf(BigDecimal value, out uint precision, out uint scale)
    {
        var negative = value.Mantissa.Sign < 0;
        var digits = BigInteger.Abs(value.Mantissa).ToString(CultureInfo.InvariantCulture);

        string text;
        if (value.Exponent >= 0)
        {
            // An integer with trailing zeros the mantissa doesn't store, e.g. mantissa=123,
            // exponent=2 -> "12300". No fractional part, so scale is always 0 here.
            text = digits + new string('0', value.Exponent);
            scale = 0;
        }
        else
        {
            // A fractional value: -exponent digits belong after the decimal point. If the mantissa
            // has fewer digits than that (e.g. mantissa=5, exponent=-3 -> "0.005"), left-pad with
            // zeros first so there's always at least one digit left of the point.
            var fractionDigits = -value.Exponent;
            if (digits.Length <= fractionDigits)
                digits = digits.PadLeft(fractionDigits + 1, '0');

            var splitAt = digits.Length - fractionDigits;
            text = string.Concat(digits.AsSpan(0, splitAt), ".", digits.AsSpan(splitAt));
            scale = (uint)fractionDigits;
        }

        // Precision is the significant-digit count of the formatted text actually being sent
        // (excluding the sign, added below, and the decimal point) - not digits.Length, which
        // undercounts whenever Exponent >= 0 appended trailing zeros text carries but the mantissa
        // string never did (e.g. mantissa "999...9" (38 nines), exponent 1 -> text "999...990",
        // 39 significant digits, but digits.Length is still 38). Using digits.Length there let a
        // 39-digit value slip past the precision > 38 guard below and bind a longer digit string
        // than its own reported precision claimed.
        var decimalPointIndex = text.IndexOf('.');
        precision = (uint)(decimalPointIndex >= 0 ? text.Length - 1 : text.Length);
        return negative ? "-" + text : text;
    }

    /// <summary>Binds a <c>UUID</c> parameter.</summary>
    /// <remarks>
    /// <paramref name="value"/> is bound via its string rendering (<c>lbug_value_create_uuid</c>
    /// takes <c>const char*</c>) - the write-side counterpart of <see cref="LadybugValue.AsGuid"/>,
    /// which reads a UUID back the same way. Follows the same short-lived-value discipline as
    /// <see cref="BindNull"/>: <c>lbug_value_create_uuid</c> returns an engine-owned <c>lbug_value*</c>
    /// directly (per its "caller is responsible for destroying the returned value"), so it is
    /// destroyed via <c>lbug_value_destroy</c> directly in a <c>finally</c> rather than wrapped in
    /// an <see cref="Interop.LbugValueHandle"/>.
    /// </remarks>
    public unsafe void Bind(string name, Guid value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var utf8Value = Marshal.StringToCoTaskMemUTF8(value.ToString());
            try
            {
                var nativeValue = LbugNative.lbug_value_create_uuid((sbyte*)utf8Value);
                try
                {
                    lbug_state state;
                    lock (_bindGate)
                    using (var lease = _handle.Acquire())
                    {
                        state = LbugNative.lbug_prepared_statement_bind_value(
                            (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, nativeValue);
                    }
                    ThrowIfBindFailed(state, name);
                }
                finally
                {
                    LbugNative.lbug_value_destroy(nativeValue);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8Value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>Binds an <c>INT128</c> parameter.</summary>
    /// <remarks>
    /// Inverts <see cref="ValueReader"/>'s read side (<see cref="LadybugValue.AsInt128"/>): the
    /// <see cref="Int128"/> value itself never crosses the native boundary - only the blittable
    /// <c>lbug_int128_t{low,high}</c> pair does, split out of <paramref name="value"/> purely in
    /// managed code via truncating numeric conversions (the exact inverse of
    /// <c>new Int128((ulong)high, low)</c> on the read side). See <see cref="LadybugValue.AsInt128"/>
    /// for why <see cref="Int128"/> itself must never be marshalled across the boundary. Follows the
    /// same short-lived-value discipline as <see cref="BindNull"/> for the same reason as
    /// <see cref="Bind(string, Guid)"/>.
    /// </remarks>
    public unsafe void Bind(string name, Int128 value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var native = new lbug_int128_t { low = (ulong)value, high = (long)(value >> 64) };

        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        var nativeValue = LbugNative.lbug_value_create_int128(native);
        try
        {
            lbug_state state;
            lock (_bindGate)
            using (var lease = _handle.Acquire())
            {
                state = LbugNative.lbug_prepared_statement_bind_value(
                    (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, nativeValue);
            }
            ThrowIfBindFailed(state, name);
        }
        finally
        {
            LbugNative.lbug_value_destroy(nativeValue);
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>
    /// Binds a <c>NULL</c> value of the parameter's own type, via <c>lbug_value_create_null</c> and
    /// <c>lbug_prepared_statement_bind_value</c> - the C API has no dedicated <c>bind_null</c> entry
    /// point, so a typed-null <c>lbug_value</c> is built and bound generically instead.
    /// </summary>
    /// <remarks>
    /// Deliberately does not route the transient <c>lbug_value*</c> through an
    /// <see cref="Interop.LbugValueHandle"/>/<see cref="System.Runtime.InteropServices.SafeHandle"/>
    /// the way every value read elsewhere in this client does. An earlier version did, and a
    /// measured 41-57 bytes/call of unreclaimed growth under a 300k/600k-call stress test traced
    /// back entirely to that extra managed allocation - not to the native library: calling
    /// <c>lbug_value_create_null</c>/<c>lbug_prepared_statement_bind_value</c>/<c>lbug_value_destroy</c>
    /// directly, with no <c>SafeHandle</c> in between, measured at noise-floor (~3 bytes/call) in
    /// the same harness, both in isolation and against a real prepared statement. The value here
    /// never outlives this one synchronous call - it is created, bound, and destroyed without ever
    /// being exposed to a caller or another thread - so the lease/dispose machinery
    /// <see cref="Interop.LbugValueHandle"/> exists for (protecting a handle that can outlive the
    /// call that created it, possibly racing a concurrent <c>Dispose</c>) buys nothing here and
    /// only added allocation churn. The <c>try</c>/<c>finally</c> below is what actually matters:
    /// it guarantees <c>lbug_value_destroy</c> still runs if <c>bind_value</c> throws or returns
    /// <see cref="lbug_state.LbugError"/>.
    /// </remarks>
    public unsafe void BindNull(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        var value = LbugNative.lbug_value_create_null();
        try
        {
            lbug_state state;
            lock (_bindGate)
            using (var lease = _handle.Acquire())
            {
                state = LbugNative.lbug_prepared_statement_bind_value(
                    (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, value);
            }
            ThrowIfBindFailed(state, name);
        }
        finally
        {
            LbugNative.lbug_value_destroy(value);
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>
    /// Executes this prepared statement with its currently bound parameters and returns the
    /// result. May be called more than once - each call reuses the same compiled statement with
    /// whatever values the most recent <c>Bind</c> calls set.
    /// </summary>
    /// <remarks>
    /// No <c>IsClosed</c> pre-check here, for the same reason as <see cref="LadybugConnection.QueryAsync"/>:
    /// <see cref="Execute"/> leases this statement's handle and both its ancestor connection's and
    /// database's handles internally (via <see cref="LbugQueryResultHandle.ExecutePrepared"/>), and
    /// those leases already throw <see cref="ObjectDisposedException"/> if any of the three has been
    /// disposed.
    /// </remarks>
    public ValueTask<LadybugQueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Execute());
    }

    private unsafe LadybugQueryResult Execute()
    {
        var handle = LbugQueryResultHandle.ExecutePrepared(_database, _connection, _handle, out var state);

        // Non-null only on failure - see LadybugConnection.Execute.
        string? failureMessage = null;
        using (var lease = handle.Acquire())
        {
            var result = (lbug_query_result*)lease.Pointer;
            var success = state == lbug_state.LbugSuccess && LbugNative.lbug_query_result_is_success(result) != 0;
            if (!success)
                failureMessage = NativeString.TakeOwnership(LbugNative.lbug_query_result_get_error_message(result));
        }

        if (failureMessage is not null)
        {
            handle.Dispose();
            throw QueryFailureClassifier.Classify(failureMessage, _cypher);
        }

        return LadybugQueryResult.Create(_database, handle);
    }

    private unsafe delegate lbug_state NativeBind<T>(lbug_prepared_statement* statement, sbyte* paramName, T value) where T : unmanaged;

    private unsafe void BindScalar<T>(string name, T value, NativeBind<T> nativeBind) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            lbug_state state;
            lock (_bindGate)
            using (var lease = _handle.Acquire())
            {
                state = nativeBind((lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, value);
            }
            ThrowIfBindFailed(state, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    /// <summary>
    /// Normalizes <paramref name="value"/>'s ticks to UTC before any of the four TIMESTAMP* binds
    /// (<see cref="Bind(string, DateTime)"/>, <see cref="BindTimestampSeconds"/>,
    /// <see cref="BindTimestampMilliseconds"/>, <see cref="BindTimestampNanoseconds"/>) convert them
    /// to engine units - one shared missing normalization step, not four independent bugs.
    /// <see cref="DateTimeKind.Local"/> is converted via <see cref="DateTime.ToUniversalTime"/> so
    /// the bound value represents the same <em>instant</em> <paramref name="value"/> does, not its
    /// raw wall-clock reading - matching the read side (<see cref="ValueReader"/>), which always
    /// returns <see cref="DateTimeKind.Utc"/>. Without this, a <see cref="DateTimeKind.Local"/>
    /// value's wall-clock reading was persisted as if it were already UTC, silently shifting the
    /// represented instant by the zone's UTC offset on every round trip.
    /// <see cref="DateTimeKind.Unspecified"/> is assumed to already be UTC - its
    /// <see cref="DateTime.Ticks"/> pass through unchanged, exactly like <see cref="DateTimeKind.Utc"/>
    /// itself - because <see cref="DateTime.ToUniversalTime"/> treats Unspecified as local (not
    /// UTC) and would apply an unwanted shift here if called unconditionally.
    /// </summary>
    private static long NormalizeToUtcTicks(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks;

    private long ToMicros(string paramName, long ticksSinceDotNetEpoch)
    {
        try
        {
            return checked((ticksSinceDotNetEpoch - DateTime.UnixEpoch.Ticks) / (TimeSpan.TicksPerMillisecond / 1000));
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(paramName, "timestamp", ex);
        }
    }

    private void ThrowIfBindFailed(lbug_state state, string paramName)
    {
        if (state != lbug_state.LbugSuccess)
            throw new LadybugException(NativeString.WithErrorDetail($"Failed to bind parameter '{paramName}'."), _cypher);
    }

    private LadybugException BindConversionFailed(string paramName, string kind, Exception inner) =>
        new($"The value bound to parameter '{paramName}' could not be represented as a {kind}.", inner);

    /// <summary>Destroys this prepared statement. Safe to call even if the parent connection or database was disposed first.</summary>
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
