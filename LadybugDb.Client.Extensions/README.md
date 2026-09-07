# LadybugDb.Client.Extensions

Dependency injection, options binding and a health check for
[`LadybugDb.Client`](https://www.nuget.org/packages/LadybugDb.Client), the .NET client for the
[LadybugDB](https://github.com/LadybugDB/ladybug) embedded graph database.

```csharp
builder.Services.AddLadybugDb(builder.Configuration.GetSection("LadybugDb"));
// or
builder.Services.AddLadybugDb("./data/graph", o => o.Config = o.Config with { MaxThreads = 4 });
```

`AddLadybugDb` registers:

- `LadybugDatabase` as a singleton, opened on first resolve and disposed with the container;
- `LadybugConnection` as a scoped service, one per scope, disposed with the scope (the
  connection is both `IDisposable` and `IAsyncDisposable`, so scopes may be disposed either way);
- `IOptions<LadybugDbOptions>`;
- a health check named `ladybugdb` that runs `RETURN 1` on a fresh connection, unless
  `DisableHealthChecks` is set.

An empty `DatabasePath` fails at registration with `OptionsValidationException`.

The engine binaries come from upstream's `LadybugDB.Native` package, which you add alongside;
see the [LadybugDb.Client README](https://github.com/HarryCordewener/ladybugdb-dotnet-client#installation).
Full documentation: [docs/USAGE.md](https://github.com/HarryCordewener/ladybugdb-dotnet-client/blob/main/docs/USAGE.md#extensions-dependency-injection-and-health-checks).
