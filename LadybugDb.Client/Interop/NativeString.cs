using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// The single place a <c>char*</c> from the C API is converted and freed.
/// Every string the API returns must be released with <c>lbug_destroy_string</c>;
/// routing all of them through here is what keeps that guarantee checkable.
/// </summary>
internal static class NativeString
{
    /// <summary>Copies a native string into managed memory and frees the native buffer.</summary>
    internal static unsafe string TakeOwnership(sbyte* native) => TakeOwnershipOrNull(native) ?? string.Empty;

    /// <summary>
    /// Like <see cref="TakeOwnership"/>, but preserves the distinction some APIs document between
    /// "nothing to report" and "an empty message" - for example <c>lbug_get_last_error</c>, which
    /// returns null when no error has been recorded rather than an empty string. Collapsing that
    /// to <see cref="string.Empty"/> would make "no error" indistinguishable from "an error with
    /// a blank message", so this returns null for a null native pointer instead.
    /// </summary>
    internal static unsafe string? TakeOwnershipOrNull(sbyte* native)
    {
        if (native is null) return null;
        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)native);
        }
        finally
        {
            LbugNative.lbug_destroy_string(native);
        }
    }
}
