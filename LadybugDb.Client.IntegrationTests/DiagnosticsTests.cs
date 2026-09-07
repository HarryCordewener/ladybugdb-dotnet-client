using System.Diagnostics;
using System.Diagnostics.Metrics;
using LadybugDb.Client.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The client's traces and metrics, observed through the BCL listeners OpenTelemetry itself sits
/// on. Listeners are process-wide, so these tests run alone.
/// </summary>
[NotInParallel]
public class DiagnosticsTests
{
    private static ActivityListener ListenTo(List<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LadybugDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (sink) sink.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener ListenTo(List<(double Value, KeyValuePair<string, object?>[] Tags)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LadybugDiagnostics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (sink) sink.Add((value, tags.ToArray()));
        });
        listener.Start();
        return listener;
    }

    [Test]
    public async Task AStatement_ProducesASpanAndADurationMeasurement()
    {
        var path = TestDatabase.NewPath();
        var activities = new List<Activity>();
        var measurements = new List<(double, KeyValuePair<string, object?>[])>();
        using var activityListener = ListenTo(activities);
        using var meterListener = ListenTo(measurements);
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");

            const string cypher = "MATCH (t:T) WHERE t.id = $id RETURN t.id";
            await using (var r = await conn.QueryAsync(cypher, new { id = 1L })) { _ = r.HasNext; }

            var span = activities.Single(a => (string?)a.GetTagItem("db.query.text") == cypher);
            await Assert.That(span.DisplayName).IsEqualTo("MATCH");
            await Assert.That(span.Kind).IsEqualTo(ActivityKind.Client);
            await Assert.That((string?)span.GetTagItem("db.system.name")).IsEqualTo("ladybugdb");
            await Assert.That((string?)span.GetTagItem("db.namespace")).IsEqualTo(path);
            await Assert.That((string?)span.GetTagItem("db.operation.name")).IsEqualTo("MATCH");
            await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Ok);

            var (value, tags) = measurements.Last(m => m.Item2.Any(t => t.Key == "db.operation.name" && (string?)t.Value == "MATCH"));
            await Assert.That(value).IsGreaterThan(0);
            await Assert.That(value).IsLessThan(5);
            await Assert.That(tags.Single(t => t.Key == "db.system.name").Value).IsEqualTo("ladybugdb");
            await Assert.That(tags.Any(t => t.Key == "error.type")).IsFalse();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AFailingStatement_RecordsTheErrorType()
    {
        var path = TestDatabase.NewPath();
        var activities = new List<Activity>();
        var measurements = new List<(double, KeyValuePair<string, object?>[])>();
        using var activityListener = ListenTo(activities);
        using var meterListener = ListenTo(measurements);
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await Assert.ThrowsAsync<LadybugException>(async () => await conn.QueryAsync("MATCH (x:Missing) RETURN x"));

            var span = activities.Single(a => (string?)a.GetTagItem("db.query.text") == "MATCH (x:Missing) RETURN x");
            await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That((string?)span.GetTagItem("error.type")).IsEqualTo(typeof(LadybugException).FullName);

            var failed = measurements.Last(m => m.Item2.Any(t => t.Key == "error.type"));
            await Assert.That(failed.Item2.Single(t => t.Key == "error.type").Value).IsEqualTo(typeof(LadybugException).FullName);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task WithoutListeners_NoActivityIsCreated()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using var r = await conn.QueryAsync("RETURN 1");
            // The test runner keeps its own activity current; ours must not have started one.
            await Assert.That(Activity.Current?.Source.Name).IsNotEqualTo(LadybugDiagnostics.ActivitySourceName);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    [Arguments("MATCH (n) RETURN n", "MATCH")]
    [Arguments("  create (n:T)", "CREATE")]
    [Arguments("BEGIN TRANSACTION", "BEGIN")]
    [Arguments("$x", "QUERY")]
    public async Task OperationName_IsTheFirstKeyword(string cypher, string expected)
    {
        await Assert.That(LadybugDiagnostics.OperationName(cypher)).IsEqualTo(expected);
    }
}
