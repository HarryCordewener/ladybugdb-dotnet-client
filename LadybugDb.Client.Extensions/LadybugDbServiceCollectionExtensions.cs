using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LadybugDb.Client.Extensions;

/// <summary>Registers LadybugDB with <see cref="IServiceCollection"/>.</summary>
/// <remarks>
/// <para>
/// What gets registered, and with which lifetime:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="LadybugDatabase"/>, singleton. Opened on first resolve (registration
/// touches nothing on disk) and disposed by the container, which then closes it to new work and
/// destroys the native database once the last dependent releases.</description></item>
/// <item><description><see cref="LadybugConnection"/>, scoped: one per scope, disposed with the
/// scope. It is <see cref="IAsyncDisposable"/> only, so scopes must be disposed asynchronously
/// (<see cref="ServiceProviderServiceExtensions.CreateAsyncScope(IServiceProvider)"/>, or ASP.NET
/// Core's request scope, which already is); a synchronous scope dispose throws.</description></item>
/// <item><description><see cref="IOptions{TOptions}"/> of <see cref="LadybugDbOptions"/>.</description></item>
/// <item><description>The <c>ladybugdb</c> health check, unless
/// <see cref="LadybugDbOptions.DisableHealthChecks"/>.</description></item>
/// </list>
/// <para>
/// Options are resolved once, at registration, the way Aspire client integrations do: the section
/// is bound (or the callback applied) and validated immediately, so a missing
/// <see cref="LadybugDbOptions.DatabasePath"/> fails here rather than at the first request. A
/// singleton database could not follow a configuration reload anyway.
/// </para>
/// </remarks>
public static class LadybugDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="LadybugDatabase"/> at <paramref name="databasePath"/>, a scoped
    /// <see cref="LadybugConnection"/>, <see cref="IOptions{TOptions}"/> of
    /// <see cref="LadybugDbOptions"/>, and the <c>ladybugdb</c> health check.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databasePath">The database file's path.</param>
    /// <param name="configure">Adjusts the options before they are validated and registered.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is empty or whitespace.</exception>
    /// <exception cref="OptionsValidationException"><paramref name="configure"/> left
    /// <see cref="LadybugDbOptions.DatabasePath"/> empty.</exception>
    public static IServiceCollection AddLadybugDb(
        this IServiceCollection services, string databasePath, Action<LadybugDbOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var options = new LadybugDbOptions { DatabasePath = databasePath };
        configure?.Invoke(options);
        return AddCore(services, options);
    }

    /// <summary>
    /// Registers LadybugDB from a configuration section bound to <see cref="LadybugDbOptions"/>,
    /// such as <c>configuration.GetSection("LadybugDb")</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The section to bind; its <c>DatabasePath</c> is required.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="OptionsValidationException">The section has no <c>DatabasePath</c>.</exception>
    public static IServiceCollection AddLadybugDb(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        var options = new LadybugDbOptions();
        section.Bind(options);
        return AddCore(services, options);
    }

    private static IServiceCollection AddCore(IServiceCollection services, LadybugDbOptions options)
    {
        Validate(options);

        // The resolved values are copied into the options pipeline rather than re-bound there, so
        // IOptions<LadybugDbOptions> reports exactly what the database was opened with.
        services.AddOptions<LadybugDbOptions>().Configure(o =>
        {
            o.DatabasePath = options.DatabasePath;
            o.Config = options.Config;
            o.DisableHealthChecks = options.DisableHealthChecks;
        });

        services.TryAddSingleton(static sp =>
        {
            var o = sp.GetRequiredService<IOptions<LadybugDbOptions>>().Value;
            return new LadybugDatabase(o.DatabasePath, o.Config);
        });

        services.TryAddScoped(static sp =>
        {
            var pending = sp.GetRequiredService<LadybugDatabase>().ConnectAsync();
            // ConnectAsync is async-shaped but completes synchronously (the engine is embedded), so
            // this never blocks a thread today. The general form is kept for the day it does not.
            return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
        });

        return services;
    }

    private static void Validate(LadybugDbOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new OptionsValidationException(
                Options.DefaultName, typeof(LadybugDbOptions),
                [$"{nameof(LadybugDbOptions.DatabasePath)} must be set to the database file's path."]);
        }
    }
}
