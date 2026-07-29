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

    /// <summary>
    /// Folds the engine's own error detail (if any) into <paramref name="message"/>. Shared by
    /// every failure path in the client that wants to enrich a message with
    /// <c>lbug_get_last_error()</c> - originally duplicated between <c>LbugDatabaseHandle.Open</c>,
    /// <c>LbugConnectionHandle.Open</c>, and <c>LadybugQueryResult</c>; centralized here so a new
    /// failure path (for example <c>ValueReader</c>) picks it up by calling this rather than by
    /// copy-pasting the pattern and risking a path that forgets to consume it.
    /// </summary>
    /// <remarks>
    /// Consumes <c>lbug_get_last_error()</c> unconditionally, even when it turns out there is
    /// nothing recorded (<see cref="TakeOwnershipOrNull"/> returns <see langword="null"/>).
    /// Leaving it unconsumed is the hazard: a message recorded by this call and never read would
    /// otherwise still be sitting there for an unrelated later call to pick up and misreport.
    /// </remarks>
    internal static unsafe string WithErrorDetail(string message)
    {
        var detail = TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
        return detail is null ? message : $"{message} {detail}";
    }
}
