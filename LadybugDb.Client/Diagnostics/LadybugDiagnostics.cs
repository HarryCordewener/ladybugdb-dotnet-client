using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LadybugDb.Client.Diagnostics;

/// <summary>
/// The names this client emits traces and metrics under, for
/// <c>AddSource(LadybugDiagnostics.ActivitySourceName)</c> and <c>AddMeter(LadybugDiagnostics.MeterName)</c>.
/// </summary>
/// <remarks>
/// Per the OpenTelemetry database semantic conventions (stable since 1.33): one client span per
/// statement, named after its first keyword, with <c>db.system.name</c> = <c>"ladybugdb"</c>,
/// <c>db.namespace</c> = the database path, <c>db.operation.name</c>, <c>db.query.text</c> (the
/// statement as written - <c>$name</c> placeholders, never values) and <c>error.type</c> on failure;
/// and one histogram, <c>db.client.operation.duration</c> in seconds, with the same
/// <c>db.system.name</c>, <c>db.operation.name</c> and <c>error.type</c>. Without a listener a
/// statement pays one <see cref="ActivitySource.HasListeners"/> check and one
/// <see cref="Instrument.Enabled"/> check and allocates nothing.
/// </remarks>
public static class LadybugDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> name.</summary>
    public const string ActivitySourceName = "LadybugDb.Client";

    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "LadybugDb.Client";

    /// <summary>The value reported for <c>db.system.name</c>.</summary>
    public const string DbSystemName = "ladybugdb";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration", unit: "s", description: "Duration of database client operations.");

    /// <summary>Starts the span and timer for one statement; <see langword="default"/> when nobody listens.</summary>
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
            activity?.SetTag("db.system.name", DbSystemName);
            activity?.SetTag("db.namespace", databasePath);
            activity?.SetTag("db.operation.name", operation);
            activity?.SetTag("db.query.text", cypher);
        }
        return new Scope(activity, operation, measured ? Stopwatch.GetTimestamp() : 0);
    }

    /// <summary>The statement's first keyword, upper-cased; <c>QUERY</c> if it has none.</summary>
    internal static string OperationName(string cypher)
    {
        var span = cypher.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && char.IsLetter(span[end])) end++;
        return end == 0 ? "QUERY" : span[..end].ToString().ToUpperInvariant();
    }

    /// <summary>One statement's span and timer; a struct so the no-listener case allocates nothing.</summary>
    internal readonly struct Scope(Activity? activity, string operation, long startTimestamp)
    {
        private readonly Activity? _activity = activity;
        private readonly string _operation = operation;
        private readonly long _start = startTimestamp;

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
