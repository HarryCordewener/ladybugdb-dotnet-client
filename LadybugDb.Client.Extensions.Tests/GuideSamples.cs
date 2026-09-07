using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Extensions.Tests;

/// <summary>The dependency-injection block of docs/GUIDE.md section 11, as written.</summary>
public class GuideSamples
{
    [Test]
    public async Task Section11_RegistersWithOptionsAndFromConfiguration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var services = new ServiceCollection();
            services.AddLadybugDb(path, o => o.Config = o.Config with { MaxThreads = 1, EnableMultiWrites = true });
            services.AddHealthChecks();
            await using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<LadybugDbOptions>>().Value;
            await Assert.That(options.Config.MaxThreads).IsEqualTo(1UL);
            await Assert.That(options.Config.EnableMultiWrites).IsTrue();

            await using var scope = provider.CreateAsyncScope();
            var conn = scope.ServiceProvider.GetRequiredService<LadybugConnection>();
            await conn.ExecuteAsync("RETURN 1");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LadybugDb:DatabasePath"] = path })
                .Build();
            var fromConfig = new ServiceCollection();
            fromConfig.AddLadybugDb(configuration.GetSection("LadybugDb"));
            await using var provider2 = fromConfig.BuildServiceProvider();
            await Assert.That(provider2.GetRequiredService<IOptions<LadybugDbOptions>>().Value.DatabasePath).IsEqualTo(path);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
