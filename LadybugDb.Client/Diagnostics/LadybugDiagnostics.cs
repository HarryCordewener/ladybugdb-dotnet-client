using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LadybugDb.Client.Diagnostics;

/// <summary>
/// The names under which this client emits traces and metrics, for subscribing with OpenTelemetry
/// (<c>AddSource(LadybugDiagnostics.ActivitySourceName)</c>, <c>AddMeter(LadybugDiagnostics.MeterName)</c>)
/// or with <see cref="ActivityListener"/>/<see cref="MeterListener"/> directly.
/// </summary>
/// <remarks>
/// <para>
/// Follows the OpenTelemetry database semantic conventions, stable since 1.33: one span per
/// statement named after its operation, with <c>db.system.name</c> (<c>"ladybugdb"</c>),
/// <c>db.namespace</c> (the database path), <c>db.operation.name</c> (the statement's first
/// keyword), <c>db.query.text</c> (the statement, which carries <c>$name</c> placeholders rather
/// than values on the parameterized paths), and <c>error.type</c> (the exception's full type name)
/// on failure; and one histogram, <c>db.client.operation.duration</c> in seconds, tagged with the
/// same <c>db.system.name</c>, <c>db.operation.name</c> and <c>error.type</c>.
/// </para>
/// <para>
/// With no listener attached the cost per statement is one <see cref="ActivitySource.HasListeners"/>
/// check and one <see cref="Instrument.Enabled"/> check - no activity, no timestamp, no allocation.
/// The whole thing lives on <c>System.Diagnostics</c>, so the core package gains no dependency.
/// </para>
/// </remarks>
public static class LadybugDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> name: <c>LadybugDb.Client</c>.</summary>
    public const string ActivitySourceName = "LadybugDb.Client";

    /// <summary>The <see cref="Meter"/> name: <c>LadybugDb.Client</c>.</summary>
    public const string MeterName = "LadybugDb.Client";

    /// <summary>The value this client reports for <c>db.system.name</c>.</summary>
    public const string DbSystemName = "ladybugdb";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration", unit: "s", description: "Duration of database client operations.");

    /// <summary>
    /// Begins the span and the timer for one statement, or returns a scope that does nothing
    /// when nobody is listening. Complete it with <see cref="Scope.Succeed"/> or
    /// <see cref="Scope.Fail"/>; it is a struct so the disabled case allocates nothing.
    /// </summary>
    internal static Scope Start(string cypher, string databasePath)
    {
        var traced = Source.HasListeners();
        var measured = OperationDuration.Enabled;
        if (!traced && !measured) return default;

        var operation = OperationName(cypher);
        Activity? activity = null;
        if (traced)
        {
            activity = Source.StartActivity(operation, ActivityKind.Client);
            if (activity is not null)
            {
                activity.SetTag("db.system.name", DbSystemName);
                activity.SetTag("db.namespace", databasePath);
                activity.SetTag("db.operation.name", operation);
                activity.SetTag("db.query.text", cypher);
            }
        }
        return new Scope(activity, operation, measured ? Stopwatch.GetTimestamp() : 0);
    }

    /// <summary>The statement's first keyword, upper-cased: <c>MATCH</c>, <c>CREATE</c>, <c>BEGIN</c>, ...</summary>
    internal static string OperationName(string cypher)
    {
        var span = cypher.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && char.IsLetter(span[end])) end++;
        return end == 0 ? "QUERY" : span[..end].ToString().ToUpperInvariant();
    }

    /// <summary>One statement's span and timer. <see langword="default"/> is the no-listener case.</summary>
    internal readonly struct Scope(Activity? activity, string operation, long startTimestamp)
    {
        private readonly Activity? _activity = activity;
        private readonly string _operation = operation;
        private readonly long _start = startTimestamp;

        /// <summary>Records success: closes the span and records the duration.</summary>
        internal void Succeed()
        {
            if (_start != 0)
            {
                OperationDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds,
                    new KeyValuePair<string, object?>("db.system.name", DbSystemName),
                    new KeyValuePair<string, object?>("db.operation.name", _operation));
            }
            _activity?.SetStatus(ActivityStatusCode.Ok);
            _activity?.Dispose();
        }

        /// <summary>Records failure with <c>error.type</c>: closes the span and records the duration.</summary>
        internal void Fail(Exception exception)
        {
            var errorType = exception.GetType().FullName ?? exception.GetType().Name;
            if (_start != 0)
            {
                OperationDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds,
                    new KeyValuePair<string, object?>("db.system.name", DbSystemName),
                    new KeyValuePair<string, object?>("db.operation.name", _operation),
                    new KeyValuePair<string, object?>("error.type", errorType));
            }
            if (_activity is not null)
            {
                _activity.SetTag("error.type", errorType);
                _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
                _activity.Dispose();
            }
        }
    }
}
