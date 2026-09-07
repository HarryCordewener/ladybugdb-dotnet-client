using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Extensions.Tests;

public class ServiceCollectionTests
{
    [Test]
    public async Task AddLadybugDb_ResolvesOneDatabaseForTheContainer()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();

            var first = provider.GetRequiredService<LadybugDatabase>();
            var second = provider.GetRequiredService<LadybugDatabase>();

            await Assert.That(second).IsSameReferenceAs(first);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The database is opened on first resolve, not at registration: registering must not touch
    /// the file system, so a host can build its container before the data directory exists.
    /// </summary>
    [Test]
    public async Task AddLadybugDb_OpensTheDatabaseOnFirstResolveNotAtRegistration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();
            await Assert.That(File.Exists(path)).IsFalse();

            _ = provider.GetRequiredService<LadybugDatabase>();
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AddLadybugDb_ConnectionIsOnePerScope()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();

            await using var scope1 = provider.CreateAsyncScope();
            await using var scope2 = provider.CreateAsyncScope();

            var a = scope1.ServiceProvider.GetRequiredService<LadybugConnection>();
            var aAgain = scope1.ServiceProvider.GetRequiredService<LadybugConnection>();
            var b = scope2.ServiceProvider.GetRequiredService<LadybugConnection>();

            await Assert.That(aAgain).IsSameReferenceAs(a);
            await Assert.That(b).IsNotSameReferenceAs(a);

            // Both are live connections to the same database.
            await a.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");
            await using var result = await b.QueryAsync("MATCH (t:T) RETURN count(*)");
            await foreach (var row in result)
                await Assert.That(row.GetInt64(0)).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposingAScope_DisposesItsConnection()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();

            LadybugConnection conn;
            await using (var scope = provider.CreateAsyncScope())
            {
                conn = scope.ServiceProvider.GetRequiredService<LadybugConnection>();
                await conn.ExecuteAsync("RETURN 1");
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await conn.ExecuteAsync("RETURN 1"));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposingTheProvider_DisposesTheDatabase()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var provider = new ServiceCollection().AddLadybugDb(path).BuildServiceProvider();
            var db = provider.GetRequiredService<LadybugDatabase>();
            await using (var conn = await db.ConnectAsync())
                await conn.ExecuteAsync("RETURN 1");

            await provider.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.ConnectAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AddLadybugDb_ConfigureCallbackShapesTheOptions()
    {
        var path = TestDatabase.NewPath();
        try
        {
            await using var provider = new ServiceCollection()
                .AddLadybugDb(path, o => o.Config = o.Config with { MaxThreads = 2, EnableCompression = false })
                .BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<LadybugDbOptions>>().Value;
            await Assert.That(options.DatabasePath).IsEqualTo(path);
            await Assert.That(options.Config.MaxThreads).IsEqualTo(2UL);
            await Assert.That(options.Config.EnableCompression).IsFalse();

            // And the database opens with them.
            var db = provider.GetRequiredService<LadybugDatabase>();
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("RETURN 1");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AddLadybugDb_BindsFromAConfigurationSection()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LadybugDb:DatabasePath"] = path,
                    ["LadybugDb:Config:MaxThreads"] = "3",
                    ["LadybugDb:Config:ReadOnly"] = "false",
                })
                .Build();

            await using var provider = new ServiceCollection()
                .AddLadybugDb(configuration.GetSection("LadybugDb"))
                .BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<LadybugDbOptions>>().Value;
            await Assert.That(options.DatabasePath).IsEqualTo(path);
            await Assert.That(options.Config.MaxThreads).IsEqualTo(3UL);
            await Assert.That(options.Config.EnableCompression).IsTrue(); // LadybugConfig's default, untouched

            var db = provider.GetRequiredService<LadybugDatabase>();
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("RETURN 1");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AddLadybugDb_EmptyPathInConfiguration_FailsAtRegistration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LadybugDb:Config:MaxThreads"] = "3" })
            .Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await Task.CompletedTask;
            new ServiceCollection().AddLadybugDb(configuration.GetSection("LadybugDb"));
        });
        await Assert.That(ex!.Message).Contains("DatabasePath");
    }

    [Test]
    public async Task AddLadybugDb_CallbackThatBlanksThePath_FailsAtRegistration()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await Task.CompletedTask;
            new ServiceCollection().AddLadybugDb("./somewhere", o => o.DatabasePath = " ");
        });
    }

    [Test]
    public async Task AddLadybugDb_NullOrBlankPathArgument_IsAnArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await Task.CompletedTask;
            new ServiceCollection().AddLadybugDb(" ");
        });
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await Task.CompletedTask;
            new ServiceCollection().AddLadybugDb((string)null!);
        });
    }

    /// <summary>Registering twice is not an error and does not register a second database.</summary>
    [Test]
    public async Task AddLadybugDb_Twice_KeepsOneRegistration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var services = new ServiceCollection().AddLadybugDb(path).AddLadybugDb(path);
            await Assert.That(services.Count(d => d.ServiceType == typeof(LadybugDatabase))).IsEqualTo(1);
            await Assert.That(services.Count(d => d.ServiceType == typeof(LadybugConnection))).IsEqualTo(1);

            // HealthCheckService rejects a duplicate name at its first run, so one registration matters here too.
            await using var provider = services.BuildServiceProvider();
            var registrations = provider.GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>().Value.Registrations;
            await Assert.That(registrations.Count(r => r.Name == "ladybugdb")).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
