namespace LadybugDb.Client.Extensions;

/// <summary>
/// Settings for the <see cref="LadybugDatabase"/> that
/// <see cref="LadybugDbServiceCollectionExtensions.AddLadybugDb(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Action{LadybugDbOptions}?)"/>
/// registers. Bindable from configuration: a section such as
/// <c>"LadybugDb": { "DatabasePath": "./data/graph", "Config": { "MaxThreads": 4 } }</c>.
/// </summary>
public sealed class LadybugDbOptions
{
    /// <summary>
    /// The database file's path, passed to <see cref="LadybugDatabase(string, LadybugConfig?)"/>.
    /// Required; registration fails with an <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
    /// when it is empty.
    /// </summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Engine settings for the open. The default is the engine's defaults.</summary>
    public LadybugConfig Config { get; set; } = new();

    /// <summary>
    /// <see langword="true"/> to skip registering the <c>ladybugdb</c> health check. The default
    /// registers it whenever health checks are in use.
    /// </summary>
    public bool DisableHealthChecks { get; set; }
}
