using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Extensions.Tests;

/// <summary>
/// The samples in docs/USAGE.md's "Extensions" chapter, run as written (a plain
/// <see cref="ServiceCollection"/> stands in for the host's), so the guide's claims about
/// lifetimes, validation and the health report stay true. Edit the guide and this file together.
/// </summary>
public class UsageGuideSamples
{
    [Test]
    public async Task ConfigurationSection_BindsAndServesAScopedConnection()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LadybugDb:DatabasePath"] = path,
                    ["LadybugDb:Config:MaxThreads"] = "4",
                    ["LadybugDb:Config:EnableCompression"] = "true",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLadybugDb(configuration.GetSection("LadybugDb"));
            await using var provider = services.BuildServiceProvider();

            await using var scope = provider.CreateAsyncScope();
            var conn = scope.ServiceProvider.GetRequiredService<LadybugConnection>();
            await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
            await conn.ExecuteAsync("CREATE (o:Object {dbref: 42, name: 'Limbo'})");

            // The endpoint body from the guide's Program.cs sample.
            var dbref = 42L;
            var name = await conn.Select<string>(
                "MATCH (o:Object) WHERE o.dbref = $dbref RETURN o.name", new { dbref }).FirstOrDefaultAsync();
            await Assert.That(name).IsEqualTo("Limbo");

            var options = provider.GetRequiredService<IOptions<LadybugDbOptions>>().Value;
            await Assert.That(options.Config.MaxThreads).IsEqualTo(4UL);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task PathOverload_WithACallback()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var services = new ServiceCollection();
            services.AddLadybugDb(path, o => o.Config = o.Config with { MaxThreads = 4 });
            await using var provider = services.BuildServiceProvider();

            await Assert.That(provider.GetRequiredService<IOptions<LadybugDbOptions>>().Value.Config.MaxThreads).IsEqualTo(4UL);
            await using var conn = await provider.GetRequiredService<LadybugDatabase>().ConnectAsync();
            await conn.ExecuteAsync("RETURN 1");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>The guide's table says a synchronous scope dispose throws; that is the container's rule, checked here.</summary>
    [Test]
    public async Task ASynchronousScopeDispose_ThrowsBecauseTheConnectionIsAsyncDisposableOnly()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();

            var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<LadybugConnection>();

            await Assert.That(() => scope.Dispose()).Throws<InvalidOperationException>();
            await ((IAsyncDisposable)scope).DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task MissingDatabasePath_FailsAtRegistrationWithTheDocumentedMessage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LadybugDb:Config:MaxThreads"] = "4" })
            .Build();
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddLadybugDb(configuration.GetSection("LadybugDb")))
            .Throws<OptionsValidationException>();

        await Assert.That(ex!.Message).IsEqualTo("DatabasePath must be set to the database file's path.");
    }

    [Test]
    public async Task HealthReport_HealthyEntryNamesTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLogging().AddLadybugDb(path).BuildServiceProvider();

            var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            var entry = report.Entries["ladybugdb"];

            await Assert.That(entry.Status).IsEqualTo(HealthStatus.Healthy);
            await Assert.That(entry.Description).IsEqualTo($"LadybugDB {LadybugDatabase.EngineVersion} answered.");
            await Assert.That(entry.Tags).Contains("db");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task HealthReport_AfterDispose_CarriesTheExceptionAndItsTypeName()
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
            await Assert.That(entry.Data["exception"]).IsEqualTo("System.ObjectDisposedException");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task TheCheck_ConstructedDirectly()
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
