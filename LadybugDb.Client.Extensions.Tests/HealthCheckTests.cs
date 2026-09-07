using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Extensions.Tests;

public class HealthCheckTests
{
    [Test]
    public async Task AddLadybugDb_RegistersTheCheckUnderItsName()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();

            var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
            var registration = registrations.Single(r => r.Name == LadybugDbHealthCheck.DefaultName);
            await Assert.That(registration.Name).IsEqualTo("ladybugdb");
            await Assert.That(registration.Tags).Contains("db");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task HealthCheck_IsHealthyAgainstAnOpenDatabase()
    {
        var path = TestDatabase.NewPath();
        try
        {
            // AddLogging as any host does: HealthCheckService's implementation takes an ILogger.
            await using var provider = new ServiceCollection().AddLogging().AddLadybugDb(path).BuildServiceProvider();

            var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

            var entry = report.Entries["ladybugdb"];
            await Assert.That(entry.Status).IsEqualTo(HealthStatus.Healthy);
            await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The failure the check exists to report: the database is gone (here disposed underneath the
    /// container, the closest an in-process engine gets to "the server went away"). The report
    /// entry carries the exception so the failure is diagnosable from the health endpoint.
    /// </summary>
    [Test]
    public async Task HealthCheck_IsUnhealthyWithTheExceptionAfterTheDatabaseIsDisposed()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLogging().AddLadybugDb(path).BuildServiceProvider();
            provider.GetRequiredService<LadybugDatabase>().Dispose();

            var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

            var entry = report.Entries["ladybugdb"];
            await Assert.That(entry.Status).IsEqualTo(HealthStatus.Unhealthy);
            await Assert.That(entry.Exception).IsTypeOf<ObjectDisposedException>();
            await Assert.That(entry.Data.Keys).Contains("exception");
            await Assert.That(entry.Description ?? string.Empty).Contains("LadybugDB");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisableHealthChecks_SkipsTheRegistration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection()
                .AddLadybugDb(path, o => o.DisableHealthChecks = true)
                .BuildServiceProvider();

            var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
            await Assert.That(registrations.Where(r => r.Name == "ladybugdb")).IsEmpty();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The check can also be used directly, outside the registration, against any database.</summary>
    [Test]
    public async Task HealthCheck_RunsStandalone()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            var check = new LadybugDbHealthCheck(db);
            var context = new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("ladybugdb", check, failureStatus: null, tags: null),
            };

            var result = await check.CheckHealthAsync(context);

            await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
