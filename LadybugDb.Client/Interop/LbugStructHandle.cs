using System.Runtime.InteropServices;

namespace LadybugDb.Client.Interop;

/// <summary>
/// Owns a natively allocated C API struct (for example <c>lbug_database</c>).
/// The C API allocates nothing for these structs itself: the caller provides storage,
/// passes its address to the matching <c>*_init</c>, and passes the same address to
/// <c>*_destroy</c>. Releasing therefore has two obligations - destroy, then free.
/// </summary>
/// <remarks>
/// Allocation and ownership are deliberately two separate steps (<see cref="AllocateUnowned"/>
/// then <see cref="Adopt"/>), not one. A naive implementation would call <c>SetHandle</c> as
/// soon as storage is allocated, before <c>*_init</c> has run. That creates a failure window:
/// if <c>*_init</c> then reports an error, <c>Dispose</c>/finalization would still invoke
/// <see cref="SafeHandle.ReleaseHandle"/>, which calls the matching <c>*_destroy</c> on a struct that was
/// never proven to have been successfully built by the engine. Nothing in the C API documents
/// that <c>*_destroy</c> is safe to call on storage <c>*_init</c> never successfully populated
/// (contrast e.g. POSIX's documented "free(NULL) is a no-op" guarantee), so relying on that
/// would be an unverifiable assumption baked into every handle in this library.
///
/// Instead, storage from <see cref="AllocateUnowned"/> is "unowned" - a plain native allocation
/// this SafeHandle does not yet know about. Only after the caller's <c>*_init</c> reports success
/// does it call <see cref="Adopt"/>, which is the sole path to <c>SetHandle</c>. Until that call,
/// <see cref="SafeHandle.IsInvalid"/> (handle == 0) is true, and (verified directly against this
/// runtime, not assumed - see HandleTests) <c>SafeHandle</c> never invokes <see cref="SafeHandle.ReleaseHandle"/>
/// while a handle is invalid - so a failed init leaves this object inert: no <c>*_destroy</c>
/// call, ever, for a struct the engine never finished constructing. The failure path instead
/// frees the raw allocation directly with <see cref="FreeUnowned"/>, which only ever undoes
/// <see cref="AllocateUnowned"/> and never touches the engine.
/// </remarks>
internal abstract class LbugStructHandle : SafeHandle
{
    protected LbugStructHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// <see langword="true"/> from the moment <c>Dispose()</c> (or finalization) first runs on
    /// this handle - unlike <see cref="SafeHandle.IsClosed"/>, which does NOT become
    /// <see langword="true"/> while any <see cref="SafeHandle.DangerousAddRef(ref bool)"/>-based
    /// reference against this handle is still outstanding, whether that reference is a short-lived
    /// <see cref="Acquire"/> lease or one of this handle's own long-lived
    /// <see cref="AcquireParentHolds"/> holds ON some OTHER handle. See <see cref="Dispose(bool)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Verified directly, not assumed, and the reason this field exists at all.</b> The natural
    /// assumption - that a <see cref="SafeHandle"/> reports itself closed the instant
    /// <c>Dispose()</c> runs, regardless of how many references are still outstanding elsewhere -
    /// is wrong. A small standalone probe (a bare <see cref="SafeHandle"/> subclass, one
    /// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> taken and left outstanding, then
    /// <c>Dispose()</c> called) showed <see cref="SafeHandle.IsClosed"/> still reading
    /// <see langword="false"/> immediately afterward, and - more consequentially - a SECOND, brand
    /// new <see cref="SafeHandle.DangerousAddRef(ref bool)"/> attempt at that point SUCCEEDING
    /// rather than throwing <see cref="ObjectDisposedException"/>. <see cref="SafeHandle"/> packs
    /// "closed" and the reference count into one shared state word, and only actually flips
    /// "closed" once the count reaches zero - i.e. once <c>ReleaseHandle</c> is about to run, the
    /// same event <see cref="SafeHandle.IsClosed"/> was always documented to reflect, just not the
    /// event this library's disposal-safety contract actually needs a signal for.
    /// </para>
    /// <para>
    /// <b>Why that matters here specifically.</b> Once a handle can hold a long-lived reference on
    /// a parent for its own entire lifetime (<see cref="AcquireParentHolds"/> - the fix this field
    /// is part of), a parent almost always HAS an outstanding reference on it whenever it is
    /// disposed while any child is still alive - a live <see cref="LadybugDb.Client.LadybugConnection"/>
    /// on a <see cref="LadybugDb.Client.LadybugDatabase"/>, for example. Relying on
    /// <see cref="SafeHandle.IsClosed"/> for "reject new work immediately" would then mean
    /// <see cref="LadybugDb.Client.LadybugDatabase.Dispose"/> stops rejecting new work through any
    /// connection opened before it, for as long as that connection remains alive - silently
    /// resurrecting exactly the "looks alive after Dispose" failure mode this whole fix exists to
    /// close, just moved from a memory-safety bug to a contract-violation one. This field is set
    /// synchronously, unconditionally, the moment <c>Dispose()</c> (or finalization) first runs -
    /// independent of the underlying reference count - and is what <see cref="Lease"/> actually
    /// checks, so "closed for new work" and "actually destroyed" are two separate, independently
    /// correct signals instead of one conflated one.
    /// </para>
    /// </remarks>
    private volatile bool _closedForNewWork;

    /// <summary>See <see cref="_closedForNewWork"/>.</summary>
    internal bool IsClosedForNewWork => _closedForNewWork;

    /// <summary>
    /// Sets <see cref="_closedForNewWork"/> before delegating to <see cref="SafeHandle"/>'s own
    /// <c>Dispose(bool)</c>, which performs the actual reference-count decrement and (once/if the
    /// count reaches zero) invokes <c>ReleaseHandle</c> - see <see cref="_closedForNewWork"/>'s
    /// remarks for why these need to be two separate steps rather than relying on
    /// <see cref="SafeHandle.IsClosed"/> alone. Runs identically whether reached via an explicit
    /// <c>Dispose()</c> or finalization - both route through this override - which is correct here:
    /// nothing can still be calling <see cref="Acquire"/> against a handle that is being finalized
    /// (finalization only happens once nothing holds a reachable reference to this managed object),
    /// so which path triggered this makes no difference to what <see cref="_closedForNewWork"/>
    /// needs to reflect.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        _closedForNewWork = true;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Allocates zeroed native storage of <paramref name="size"/> bytes. The result is not yet
    /// owned by any <see cref="LbugStructHandle"/>: pass it to the matching native <c>*_init</c>,
    /// then either <see cref="Adopt"/> it on success or <see cref="FreeUnowned"/> it on failure.
    /// </summary>
    protected static unsafe void* AllocateUnowned(nuint size) => NativeMemory.AllocZeroed(size);

    /// <summary>Frees storage from <see cref="AllocateUnowned"/> that was never adopted, e.g. after a failed init.</summary>
    protected static unsafe void FreeUnowned(void* storage) => NativeMemory.Free(storage);

    /// <summary>
    /// Takes ownership of storage from <see cref="AllocateUnowned"/> after the matching
    /// <c>*_init</c> has reported success. From this point <see cref="SafeHandle.ReleaseHandle"/> is
    /// responsible for destroying and freeing it.
    /// </summary>
    protected unsafe void Adopt(void* storage) => SetHandle((IntPtr)storage);

    /// <summary>Frees the struct storage this handle owns. Call only after the matching <c>*_destroy</c> has run.</summary>
    protected unsafe void FreeStorage()
    {
        if (handle != IntPtr.Zero)
        {
            NativeMemory.Free((void*)handle);
            SetHandle(IntPtr.Zero);
        }
    }

    /// <summary>
    /// Leases the struct's address for the duration of exactly one synchronous native call.
    /// </summary>
    /// <remarks>
    /// There is deliberately no plain pointer property on this type: a raw pointer handed out
    /// without a reference held against it could be dereferenced after a concurrent
    /// <c>Dispose</c>/finalize has already run <see cref="SafeHandle.ReleaseHandle"/> - the C struct
    /// destroyed, its storage freed - which is a use-after-free, not merely a leak. Single
    /// threaded call sites are not exempt either: after release, the address is gone, so a
    /// "just check IsClosed first" pattern still races and can dereference freed memory or
    /// segfault instead of throwing a managed exception.
    ///
    /// <see cref="Acquire"/> instead calls <see cref="SafeHandle.DangerousAddRef(ref bool)"/> before handing out the
    /// pointer and <see cref="SafeHandle.DangerousRelease"/> when the lease is disposed. SafeHandle only
    /// ever invokes <see cref="SafeHandle.ReleaseHandle"/> once its reference count reaches zero, so a
    /// concurrent <c>Dispose</c> while a lease is outstanding is deferred, not lost: it decrements
    /// the handle's own baseline reference but the actual destroy-and-free waits for this lease's
    /// <see cref="SafeHandle.DangerousRelease"/> (verified empirically, not assumed). Always scope a lease
    /// tightly around one synchronous native call - never hold it across an <c>await</c>; the C#
    /// compiler enforces this for us since <see cref="Lease"/> is a <c>ref struct</c> and cannot
    /// be stored across an async boundary.
    ///
    /// Nesting leases from different handles is safe, and in this library is required whenever a
    /// native call needs a descendant's storage (e.g. a query result) plus an ancestor's (e.g. the
    /// database it was obtained from): the ancestor's lease must be the outermost one, acquired
    /// before and released after the descendant's. That ordering is what keeps a child from ever
    /// outliving its parent's storage for the duration of the call - the descendant's native call
    /// runs only while the ancestor is also proven live. Nesting cannot deadlock: <c>Acquire</c>
    /// never blocks a thread waiting on another thread's lease, because <see cref="SafeHandle.DangerousAddRef"/>
    /// is an interlocked increment on a refcount, not a lock acquisition - two leases, nested or
    /// concurrent, on the same or different handles, never contend for anything a thread could be
    /// suspended waiting on.
    /// </remarks>
    internal Lease Acquire() => new(this);

    /// <summary>
    /// Parents this handle holds a long-lived <see cref="SafeHandle.DangerousAddRef(ref bool)"/>
    /// hold on, taken by <see cref="AcquireParentHolds"/> and undone by
    /// <see cref="ReleaseParentHolds"/>. <see langword="null"/> until (and unless)
    /// <see cref="AcquireParentHolds"/> succeeds; never partially populated - see that method.
    /// </summary>
    private SafeHandle[]? _heldParents;

    /// <summary>
    /// Attempts to take a long-lived hold on every handle in <paramref name="parents"/>, all-or-
    /// nothing. On success (<see langword="true"/>), every parent is held from this point until
    /// <see cref="ReleaseParentHolds"/> undoes it - the same <see cref="SafeHandle.DangerousAddRef(ref bool)"/>/
    /// <see cref="SafeHandle.DangerousRelease"/> mechanism <see cref="Acquire"/> already uses for a
    /// single native call, just not scoped to one call. On failure (<see langword="false"/>), every
    /// hold this same invocation managed to take before hitting the one that failed is released
    /// again before returning - nothing is left partially held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The general form of a pattern this project used to treat as bespoke to one type.</b> A
    /// child native handle - a connection, a query result, a prepared statement - depends on its
    /// parent's native storage staying allocated for its own ENTIRE lifetime, not merely for the
    /// duration of the call that created it: an ancestor's own eventual native destroy call can
    /// reach into memory the child's destroy needs (a materialized query result's factorized table
    /// lives in the database's own buffer manager, for example), so the ancestor must not actually
    /// be torn down while any such child is still alive. <see cref="Acquire"/>'s per-call lease
    /// protects the call that PRODUCES a child handle; it does nothing for that handle's remaining
    /// lifetime once the call returns. This method is what closes that gap, generally, for every
    /// handle that needs it - not a special case for one relationship. This is purely about
    /// deferring the ACTUAL native destroy (<c>ReleaseHandle</c>, which only runs once every
    /// reference - including a hold taken here - has been released); it has no bearing on whether
    /// the parent still accepts NEW work, which <see cref="_closedForNewWork"/> tracks separately
    /// and immediately, precisely because a hold taken here would otherwise make that signal lag.
    /// </para>
    /// <para>
    /// <b>Call this only once per handle, before that handle's own storage is adopted</b> (see
    /// <see cref="Adopt"/>) - and, for a handle whose underlying native call needs those same
    /// parents to run at all, only while a short-lived <see cref="Acquire"/> lease is independently
    /// held on each of them too. Every call site in this library follows that shape (see
    /// <see cref="LbugConnectionHandle.Open"/>, <see cref="LbugQueryResultHandle.Execute"/>, etc.),
    /// which has a useful consequence: since the caller's own lease already guarantees each
    /// parent's reference count is at least 1 at the moment this method runs, and
    /// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> only ever throws
    /// <see cref="ObjectDisposedException"/> once a handle's count has ACTUALLY reached zero (not
    /// merely once <c>Dispose()</c> has been called on it while other references remain - see
    /// <see cref="_closedForNewWork"/>'s remarks for how that was verified), the attempt below is
    /// structurally guaranteed to succeed at every current call site. The per-attempt
    /// <see langword="try"/>/<see langword="catch"/> below exists anyway, for the general contract
    /// this method documents (a caller need not hold its own lease first) and as defense against a
    /// future call site that does not follow this shape - not because it is expected to trigger
    /// today.
    /// </para>
    /// <para>
    /// This is also what makes a failed acquisition leak-free: catching
    /// <see cref="ObjectDisposedException"/> around each individual
    /// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> attempt (rather than one try/catch around
    /// the whole loop) means a failure on parent N still lets every already-acquired parent
    /// (0..N-1) get rolled back below - nothing here uses a separate counter that could drift out
    /// of sync with what was actually acquired, unlike the earlier, bespoke single-parent version
    /// of this idea, which used exactly such a counter and leaked it past this same throw.
    /// </para>
    /// </remarks>
    internal bool AcquireParentHolds(params SafeHandle[] parents)
    {
        var acquiredUpTo = 0;
        for (; acquiredUpTo < parents.Length; acquiredUpTo++)
        {
            var acquired = false;
            try
            {
                parents[acquiredUpTo].DangerousAddRef(ref acquired);
            }
            catch (ObjectDisposedException)
            {
                // acquired stays false - handled identically to the ordinary "AddRef declined"
                // case below. See this method's remarks for why this must be caught per-attempt.
            }
            if (!acquired) break;
        }

        if (acquiredUpTo < parents.Length)
        {
            for (var i = 0; i < acquiredUpTo; i++) parents[i].DangerousRelease();
            return false;
        }

        _heldParents = parents;
        return true;
    }

    /// <summary>
    /// Releases every hold <see cref="AcquireParentHolds"/> took, if any (a no-op if it was never
    /// called, or never succeeded). Call from this handle's own <c>ReleaseHandle</c> override,
    /// always AFTER the matching native destroy has run - never before, or a parent could be freed
    /// while the native destroy call that still needs it is executing.
    /// </summary>
    internal void ReleaseParentHolds()
    {
        var parents = _heldParents;
        if (parents is null) return;
        _heldParents = null;
        foreach (var parent in parents) parent.DangerousRelease();
    }

    internal readonly unsafe ref struct Lease
    {
        private readonly LbugStructHandle _handle;

        internal Lease(LbugStructHandle handle)
        {
            // Authoritative, not merely a fast path: SafeHandle.IsClosed itself does NOT become
            // true immediately once Dispose() runs while some OTHER reference on this handle
            // (e.g. a child's AcquireParentHolds hold) is still outstanding - verified directly,
            // not assumed; see IsClosedForNewWork's remarks. This is the check that gives "closed
            // for new work" its immediate, hold-independent meaning.
            ObjectDisposedException.ThrowIf(handle.IsClosedForNewWork, handle);

            // Still checked explicitly rather than trusted to have been implied by the line
            // above: DangerousAddRef can independently fail (throw) once this handle's own
            // reference count has ACTUALLY reached zero, which is a real, if narrower, race
            // against the check above (Dispose() completing, refcount hitting zero, and
            // ReleaseHandle running, all between that check and this call).
            var acquired = false;
            handle.DangerousAddRef(ref acquired);
            if (!acquired)
                throw new ObjectDisposedException(handle.GetType().Name);

            _handle = handle;
        }

        internal void* Pointer => (void*)_handle.DangerousGetHandle();

        public void Dispose() => _handle.DangerousRelease();
    }
}
