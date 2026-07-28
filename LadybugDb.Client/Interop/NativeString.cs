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
    internal static unsafe string TakeOwnership(sbyte* native)
    {
        if (native is null) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)native) ?? string.Empty;
        }
        finally
        {
            LbugNative.lbug_destroy_string(native);
        }
    }
}
