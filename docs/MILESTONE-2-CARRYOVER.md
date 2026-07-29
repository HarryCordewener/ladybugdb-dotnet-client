# Milestone 2 carry-over

Items deferred from the foundation milestone. Each was reviewed and explicitly
triaged as safe to ship; none block use of the foundation. Recorded here so they
are not rediscovered from scratch.

## Behavioural

- **Post-dispose behaviour is memory-safe but not strictly deterministic.** `SafeHandle`
  only closes on the 1→0 refcount transition, so while other threads hold leases an
  in-flight operation can still complete after `Dispose()`. Never unsafe — destroy is
  deferred, so those calls run on live memory. Documented on `LadybugDatabase.Dispose`.

- **`ReadStringAsync` advances a row *and* reads a column.** A deliberate temporary seam
  proving the tuple/value ownership chain end to end. Milestone 2 replaces it with
  `IAsyncEnumerable<LadybugRow>` and typed column access; the dual responsibility should
  not survive into the final API.

## Interop coverage

- **12 `*_to_tm` / `*_from_tm` functions are excluded** from the generated interop. There
  is no portable `struct tm` ABI across the six target RIDs (glibc and MSVCRT layouts
  differ). The design spec commits to epoch-unit temporal marshalling instead, and every
  epoch-based entry point Milestone 2 needs is present.

- **`LbugQueryResultHandle.Execute` adopts storage unconditionally**, unlike every other
  handle factory, which adopts only on success. Reviewed and judged correct *for this
  function*: `lbug_query_result_get_error_message` is documented to return the error for a
  failed query, which only makes sense if the struct is populated on failure — skipping
  adoption would leak the captured error state. The divergence is intentional and
  documented at the call site.

## Test coverage

- **`ReadString`'s failure branches have no committed test.** They were verified correct
  by a reviewer's throwaway 3,000-iteration scratch tests, but only the success path ships
  with an assertion. The "failed to advance to the next row" branch is reachable only via
  a genuine data race, so it needs a deterministic seam rather than a timing-dependent test.

- **`HandleTests` verifies the CLR-level `SafeHandle` contract, not `Open()`.** No unit test
  exercises the `Open` methods (they need the real library); integration tests now cover
  them, which partly supersedes this.

## Infrastructure

- **`FindPackage` in `PackagingTests` uses `FirstOrDefault` with no tie-break** if two
  versions of the same package coexist in the tree. Enumeration order is filesystem
  dependent. Mitigated in practice because CI always packs clean first.

- **No `.gitattributes` `linguist-generated` marker** on `LbugNative.g.cs`, so GitHub does
  not collapse the 1,000-line generated file in diffs. Cosmetic.

- **CI relies on `.slnx` auto-discovery** rather than naming the solution explicitly. Worth
  pinning once more projects accumulate.
