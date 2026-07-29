using TUnit.Core;
using TUnit.Core.Interfaces;

// Bounds the whole assembly's parallelism - see DatabaseParallelLimit below for why.
[assembly: ParallelLimiter<LadybugDb.Client.IntegrationTests.DatabaseParallelLimit>]

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Caps how many tests in this assembly may run concurrently, because opening a
/// <see cref="LadybugDatabase"/> reserves roughly 8 TiB of *virtual* address space up front (not
/// resident memory - just a large address-space reservation the mmap call makes). TUnit's default
/// parallelism is effectively unbounded - one worker per available test, gated only by
/// <see cref="System.Environment.ProcessorCount"/> workers pulling from the queue, not by any
/// awareness of what those tests do. On a many-core box, enough concurrent
/// <c>new LadybugDatabase(...)</c> calls stack up their 8 TiB reservations to approach the 128 TiB
/// Linux per-process user virtual-address-space ceiling, at which point <c>lbug_database_init</c>'s
/// mmap starts failing with "Mmap for size 8796093022208 failed" (ENOMEM) - hitting whichever test
/// happens to be mid-open when the ceiling is crossed, so the failure is nondeterministic and
/// migrates between runs and between tests.
/// </summary>
/// <remarks>
/// Applied assembly-wide via <c>[assembly: ParallelLimiter&lt;DatabaseParallelLimit&gt;]</c> above
/// rather than per-class or per-method, because nearly every integration test opens at least one
/// database - a limit that only covered some classes would still let the untouched ones stack up
/// against the ones that are covered. A limit, not <c>[NotInParallel]</c>: fully serializing the
/// suite would also fix the crash, but at the cost of real wall-clock on every future task that
/// adds more database-opening tests (Milestone 2 Tasks 2-7 all do). 8 concurrent opens reserves at
/// most 64 TiB at any instant, comfortably under the 128 TiB ceiling with headroom left for the
/// runtime's own mappings and any database still draining a leased handle mid-close.
/// </remarks>
internal sealed class DatabaseParallelLimit : IParallelLimit
{
    /// <inheritdoc/>
    public int Limit => 8;
}
