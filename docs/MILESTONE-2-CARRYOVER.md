# Milestone 2 carry-over

Items deferred from the foundation milestone, re-triaged at the end of Milestone 2. Most have
since been closed out — see below — leaving only what genuinely remains.

## Closed out in Milestone 2

- **`ReadStringAsync`'s untested failure branches** — moot: the method itself is gone (replaced by
  `IAsyncEnumerable<LadybugRow>` and typed column access).
- **The duplicated test cleanup helper** — replaced by the shared `TestDatabase` helper
  (`LadybugDb.Client.IntegrationTests/TestDatabase.cs`).
- **Post-dispose behaviour is memory-safe but not strictly deterministic** — this is no longer an
  open item, just a designed behaviour; it's fully documented on `LadybugDatabase.Dispose` and in
  [docs/USAGE.md](USAGE.md#disposal-and-lifetime).
- **`LbugQueryResultHandle.Execute` adopts storage unconditionally** — reviewed and judged correct;
  the reasoning is documented in full at the call site (`LbugQueryResultHandle.Execute`'s XML doc
  remarks), so it doesn't need to be duplicated here.
- **`HandleTests` verifies the CLR-level `SafeHandle` contract, not `Open()`** — checked against
  the current suite: integration tests (`DatabaseLifecycleTests`, `DisposalSafetyTests`, and
  others) now exercise every `Open()` path against the real library. `HandleTests`' own doc comment
  already scopes itself accurately ("these tests exercise `LbugStructHandle`'s ownership contract
  in isolation, with no native library involved") — it does not claim to cover `Open()`, so there
  was nothing left to trim.
- **No `.gitattributes` `linguist-generated` marker** — added; `LbugNative.g.cs` now collapses in
  GitHub diffs.

## Still open

- **`FindPackage` in `PackagingTests` uses `FirstOrDefault` with no tie-break** if two versions of
  the same package coexist in the tree. Enumeration order is filesystem dependent. Mitigated in
  practice because CI always packs clean first.
- **CI relies on `.slnx` auto-discovery** rather than naming the solution explicitly. Worth pinning
  once more projects accumulate.
- **12 `*_to_tm` / `*_from_tm` functions remain excluded** from the generated interop. There is no
  portable `struct tm` ABI across the six target RIDs (glibc and MSVCRT layouts differ). This is a
  standing design decision, not a gap: the client uses epoch-unit temporal marshalling instead
  (see [docs/USAGE.md](USAGE.md)), and every epoch-based entry point the client needs is present.
  Unlikely to ever be actionable without a portable `struct tm` shim.
