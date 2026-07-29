using System.Reflection;
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class InteropSurfaceTests
{
    private static readonly Type Native =
        typeof(NativeLibraryResolver).Assembly.GetType("LadybugDb.Client.Native.LbugNative")!;

    [Test]
    public async Task LbugNative_ExposesCoreLifecycleEntryPoints()
    {
        string[] required =
        [
            "lbug_database_init", "lbug_database_destroy",
            "lbug_connection_init", "lbug_connection_destroy",
            "lbug_connection_query", "lbug_query_result_destroy",
            "lbug_query_result_is_success", "lbug_query_result_get_error_message",
            "lbug_query_result_has_next", "lbug_query_result_get_next",
            "lbug_destroy_string", "lbug_default_system_config",
        ];

        foreach (var name in required)
        {
            var m = Native.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            await Assert.That(m).IsNotNull();
        }
    }

    [Test]
    public async Task InteropTypes_AreNotPublic()
    {
        await Assert.That(Native.IsPublic).IsFalse();
    }
}
