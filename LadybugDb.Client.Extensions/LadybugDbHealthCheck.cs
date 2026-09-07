using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LadybugDb.Client.Extensions;

/// <summary>
/// Reports <see cref="HealthStatus.Healthy"/> when a fresh connection to the registered
/// <see cref="LadybugDatabase"/> answers <c>RETURN 1</c>, and the registration's failure status
/// (<see cref="HealthStatus.Unhealthy"/> by default) with the exception otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Registered as <see cref="DefaultName"/> with the tags <c>db</c> and <c>ladybugdb</c> by
/// <see cref="LadybugDbServiceCollectionExtensions.AddLadybugDb(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, Action{LadybugDbOptions}?)"/>
/// unless <see cref="LadybugDbOptions.DisableHealthChecks"/>. A fresh connection, rather than
/// the request's scoped one, so the check never contends with a transaction the request holds
/// and so it works from a health endpoint that has no scope of its own.
/// </para>
/// <para>
/// The 5-second <see cref="Timeout"/> is honoured before the engine is entered, which is where the
/// client's cancellation currently takes effect (see the usage guide's "Connections"): the engine
/// runs the statement synchronously and in-process, and <c>RETURN 1</c> touches no data, so a
/// slow answer means the process itself is starved rather than the database.
/// </para>
/// </remarks>
/// <param name="database">The database to check.</param>
public sealed class LadybugDbHealthCheck(LadybugDatabase database) : IHealthCheck
{
    /// <summary>The name <c>AddLadybugDb</c> registers the check under: <c>ladybugdb</c>.</summary>
    public const string DefaultName = "ladybugdb";

    /// <summary>How long the check waits for the engine before reporting a failure.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    private readonly LadybugDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await using var connection = await _database.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await using var result = await connection.QueryAsync("RETURN 1", timeout.Token).ConfigureAwait(false);

            var answered = false;
            await foreach (var row in result.WithCancellation(timeout.Token).ConfigureAwait(false))
                answered = row.GetInt64(0) == 1;

            return answered
                ? HealthCheckResult.Healthy($"LadybugDB {LadybugDatabase.EngineVersion} answered.")
                : new HealthCheckResult(context.Registration.FailureStatus, "LadybugDB returned no row for RETURN 1.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A cancellation the caller asked for propagates; the check's own timeout, and every
            // other failure, is reported as the registration's failure status. The exception goes
            // both into the entry's Exception slot and, by type name, into Data, since health
            // endpoints commonly serialize Data and drop Exception.
            var description = ex is OperationCanceledException
                ? $"LadybugDB did not answer within {Timeout.TotalSeconds:0}s."
                : $"LadybugDB is unavailable: {ex.Message}";
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description,
                ex,
                new Dictionary<string, object> { ["exception"] = ex.GetType().FullName ?? ex.GetType().Name });
        }
    }
}
