# Contributing

Thanks for considering a contribution. This project is young — the public API surface is
deliberately small (see the README's [Current status and limitations](README.md#current-status-and-limitations))
— so changes to it are worth discussing before you write code.

## Proposing a change

- **Bug fixes and small, self-contained improvements:** open a pull request directly.
- **Anything that changes the public API, adds a feature, or touches native resource lifetime
  management:** open an issue first describing what you want to do and why. Native handle
  ownership is the primary correctness risk in this codebase (see the design spec's Goals); a
  change that looks small in a diff can still introduce a leak or a use-after-free that only
  shows up under a stress test.

## Test-driven development

Write the failing test before the code that makes it pass. This isn't a style preference here —
the handle-lifetime and disposal-safety guarantees this client makes (see
[docs/USAGE.md](docs/USAGE.md#disposal-and-lifetime)) were each pinned down by a test that first
reproduced the bug (a segfault, an abort, a leak) before the fix landed. A fix without a test that
would have caught the original bug isn't done.

- Unit tests (`LadybugDb.Client.Tests`) run without the real engine and should cover anything that
  doesn't need it — marshalling logic, error classification, packaging contents.
- Integration tests (`LadybugDb.Client.IntegrationTests`) run against the real `liblbug` and
  should cover anything that does — actual query execution, actual disposal ordering, actual
  write conflicts.

See [docs/BUILDING.md](docs/BUILDING.md#running-tests) for how to run each, including the
`--treenode-filter` syntax you'll want while iterating on one test.

## Native binaries

Never commit native binaries. `LadybugDb.Client.Native/runtimes/` is fetched by
`scripts/fetch-liblbug.sh` from pinned, hash-verified upstream releases — see
[docs/BUILDING.md](docs/BUILDING.md#how-native-binaries-are-pinned-and-verified). If your change
needs a newer `liblbug`, bump `liblbug.version` and update the lockfile as described there; don't
hand-add a binary to the tree.

## Generated interop

`LadybugDb.Client/Native/LbugNative.g.cs` is generated from the pinned `lbug.h` header — see
[docs/BUILDING.md](docs/BUILDING.md#regenerating-interop). Don't hand-edit it. If the generated
shape needs to change, change `scripts/regen-interop.sh` (or the pinned generator version) and
regenerate, so the change survives the next regeneration instead of being silently reverted by it.
CI's `interop-drift` job fails any pull request where the committed file doesn't match a fresh
regeneration, so this is enforced, not just requested.

## Code style

Match what's already there: `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are both on
(`Directory.Build.props`), so `dotnet build` itself is your linter. XML doc comments are required
on public members (`GenerateDocumentationFile`); prefer a `<remarks>` explaining *why* a
non-obvious decision was made — ownership, lease timing, a benchmark result — over a comment
restating what the code already says.
