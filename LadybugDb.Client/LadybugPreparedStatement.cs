using System.Runtime.InteropServices;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// A parameterized Cypher statement, prepared once and executed - with different bound values -
/// as many times as needed, so the engine does not have to re-plan the same query on every call.
/// </summary>
/// <remarks>
/// Leases only its own <see cref="LbugPreparedStatementHandle"/> for every <c>Bind*</c> call: none
/// of the <c>lbug_prepared_statement_bind_*</c> entry points take a connection or database
/// pointer, so - unlike <see cref="ExecuteAsync"/>, which calls <c>lbug_connection_execute</c> and
/// therefore leases the parent database and connection too, exactly like
/// <see cref="LadybugConnection.QueryAsync"/> - there is no ancestor storage a bind call could
/// dereference after it was freed.
/// </remarks>
public sealed class LadybugPreparedStatement : IAsyncDisposable
{
    private static readonly DateOnly Epoch = new(1970, 1, 1);

    private readonly LbugDatabaseHandle _database;
    private readonly LbugConnectionHandle _connection;
    private readonly LbugPreparedStatementHandle _handle;
    private readonly string _cypher;

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
    /// <remarks>Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_t.value</c> is microseconds since 1970-01-01T00:00:00Z.</remarks>
    public unsafe void Bind(string name, DateTime value)
    {
        var micros = ToMicros(name, value.Ticks);
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
    /// <remarks>Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_sec_t.value</c> is seconds since 1970-01-01T00:00:00Z.</remarks>
    public unsafe void BindTimestampSeconds(string name, DateTime value)
    {
        long seconds;
        try
        {
            seconds = checked((value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);
        }
        catch (OverflowException ex)
        {
            throw BindConversionFailed(name, "timestamp_sec", ex);
        }
        BindScalar(name, new lbug_timestamp_sec_t { value = seconds }, LbugNative.lbug_prepared_statement_bind_timestamp_sec);
    }

    /// <summary>Binds a <c>TIMESTAMP_MS</c> parameter.</summary>
    /// <remarks>Inverts <see cref="ValueReader"/>'s read side: <c>lbug_timestamp_ms_t.value</c> is milliseconds since 1970-01-01T00:00:00Z.</remarks>
    public unsafe void BindTimestampMilliseconds(string name, DateTime value)
    {
        long millis;
        try
        {
            millis = checked((value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond);
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
    /// </remarks>
    public unsafe void BindTimestampNanoseconds(string name, DateTime value)
    {
        long nanos;
        try
        {
            nanos = checked((value.Ticks - DateTime.UnixEpoch.Ticks) * 100);
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
        ArgumentNullException.ThrowIfNull(value);
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        var utf8Value = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            lbug_state state;
            using (var lease = _handle.Acquire())
            {
                state = LbugNative.lbug_prepared_statement_bind_string(
                    (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, (sbyte*)utf8Value);
            }
            ThrowIfBindFailed(state, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
            Marshal.FreeCoTaskMem(utf8Value);
        }
    }

    /// <summary>
    /// Binds a <c>NULL</c> value of the parameter's own type, via <c>lbug_value_create_null</c> and
    /// <c>lbug_prepared_statement_bind_value</c> - the C API has no dedicated <c>bind_null</c> entry
    /// point, so a typed-null <c>lbug_value</c> is built and bound generically instead.
    /// </summary>
    public unsafe void BindNull(string name)
    {
        using var valueHandle = LbugValueHandle.CreateNull();
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            lbug_state state;
            using (var lease = _handle.Acquire())
            using (var valueLease = valueHandle.Acquire())
            {
                state = LbugNative.lbug_prepared_statement_bind_value(
                    (lbug_prepared_statement*)lease.Pointer, (sbyte*)utf8Name, (lbug_value*)valueLease.Pointer);
            }
            ThrowIfBindFailed(state, name);
        }
        finally
        {
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
        var utf8Name = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            lbug_state state;
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
