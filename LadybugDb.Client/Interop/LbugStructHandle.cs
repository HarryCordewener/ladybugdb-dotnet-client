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
/// <see cref="ReleaseHandle"/>, which calls the matching <c>*_destroy</c> on a struct that was
/// never proven to have been successfully built by the engine. Nothing in the C API documents
/// that <c>*_destroy</c> is safe to call on storage <c>*_init</c> never successfully populated
/// (contrast e.g. POSIX's documented "free(NULL) is a no-op" guarantee), so relying on that
/// would be an unverifiable assumption baked into every handle in this library.
///
/// Instead, storage from <see cref="AllocateUnowned"/> is "unowned" - a plain native allocation
/// this SafeHandle does not yet know about. Only after the caller's <c>*_init</c> reports success
/// does it call <see cref="Adopt"/>, which is the sole path to <c>SetHandle</c>. Until that call,
/// <see cref="SafeHandle.IsInvalid"/> (handle == 0) is true, and empirically (verified directly
/// against this runtime, not assumed) <c>SafeHandle</c> never invokes <see cref="ReleaseHandle"/>
/// while a handle is invalid - so a failed init leaves this object inert: no <c>*_destroy</c>
/// call, ever, for a struct the engine never finished constructing. The failure path instead
/// frees the raw allocation directly with <see cref="FreeUnowned"/>, which only ever undoes
/// <see cref="AllocateUnowned"/> and never touches the engine.
/// </remarks>
internal abstract class LbugStructHandle : SafeHandle
{
    protected LbugStructHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public unsafe void* Pointer => (void*)handle;

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
    /// <c>*_init</c> has reported success. From this point <see cref="ReleaseHandle"/> is
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
}
