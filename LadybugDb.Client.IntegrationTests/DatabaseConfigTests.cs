using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The checkpoint settings reach the engine: read back through <c>CALL current_setting(...)</c>,
/// which is the engine's own view of what it was configured with. <c>enable_checksums</c> and
/// <c>throw_on_wal_replay_failure</c> are not readable that way ("Invalid option name"), so they
/// are covered only by the struct mapping they share with the two that are.
/// </summary>
public class DatabaseConfigTests
{
    private static async Task<string> Setting(LadybugConnection conn, string name)
    {
        await using var r = await conn.QueryAsync($"CALL current_setting('{name}') RETURN *");
        await foreach (var row in r) return row.GetValue(0).ToString();
        throw new InvalidOperationException($"current_setting('{name}') returned no row");
    }

    [Test]
    public async Task Defaults_MatchTheEngineDefaults()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await Assert.That(await Setting(conn, "auto_checkpoint")).IsEqualTo("True");
            await Assert.That(await Setting(conn, "checkpoint_threshold")).IsEqualTo("16777216");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task CheckpointSettings_ReachTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path, new LadybugConfig
            {
                AutoCheckpoint = false,
                CheckpointThreshold = 1024 * 1024,
                EnableChecksums = false,
            });
            await using var conn = await db.ConnectAsync();
            await Assert.That(await Setting(conn, "auto_checkpoint")).IsEqualTo("False");
            await Assert.That(await Setting(conn, "checkpoint_threshold")).IsEqualTo("1048576");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
