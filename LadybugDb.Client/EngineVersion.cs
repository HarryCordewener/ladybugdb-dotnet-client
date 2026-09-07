using System.Globalization;
using System.Reflection;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// Reconciles the engine this client was generated against with the engine actually loaded. The
/// binaries come from upstream's <c>LadybugDB.Native</c> packages, chosen by the consumer, so the two
/// can diverge; an older engine lacks entry points this client calls and would fail with an
/// <see cref="EntryPointNotFoundException"/> from whichever call happened to be missing, which is
/// why the check runs once, up front, in <see cref="LadybugDatabase"/>'s constructor.
/// </summary>
internal static class EngineVersion
{
    /// <summary>
    /// The pinned version (from <c>third-party/liblbug.version</c>, embedded at build time as
    /// assembly metadata), without its leading <c>v</c>.
    /// </summary>
    internal static string Pinned { get; } = ReadPinned();

    private static readonly Lazy<string> LoadedLazy = new(ReadLoaded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The version string the loaded library reports through <c>lbug_get_version</c>.</summary>
    internal static string Loaded => LoadedLazy.Value;

    private static string ReadPinned()
    {
        var value = typeof(EngineVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LadybugEngineVersion")?.Value;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("LadybugDb.Client was built without its LadybugEngineVersion metadata.");
        return value.TrimStart('v', 'V');
    }

    private static unsafe string ReadLoaded() => NativeString.TakeOwnership(LbugNative.lbug_get_version());

    /// <summary>
    /// Throws <see cref="LadybugException"/> if the loaded engine is older than <see cref="Pinned"/>.
    /// Compares major and minor only: patch releases have never changed the C API, and refusing a
    /// consumer's <c>0.19.0</c> against a <c>0.19.1</c> pin would be pedantry with no ABI behind it.
    /// </summary>
    internal static void EnsureCompatible()
    {
        var loaded = Loaded;
        if (IsCompatible(loaded, Pinned)) return;

        throw new LadybugException(
            $"The loaded LadybugDB engine is version {loaded}, but this build of LadybugDb.Client needs " +
            $"{Pinned} or newer (its interop was generated from that version's C header, and an older " +
            $"engine is missing entry points it calls). Update the LadybugDB.Native package to " +
            $"{Pinned} or later.");
    }

    /// <summary>
    /// <see langword="true"/> if <paramref name="loaded"/>'s major.minor is at least
    /// <paramref name="pinned"/>'s. A version that cannot be parsed is treated as compatible: the
    /// check exists to explain a predictable failure early, not to invent a new one when the engine
    /// reports something unexpected.
    /// </summary>
    internal static bool IsCompatible(string loaded, string pinned)
    {
        if (!TryParseMajorMinor(loaded, out var loadedMajor, out var loadedMinor)
            || !TryParseMajorMinor(pinned, out var pinnedMajor, out var pinnedMinor))
        {
            return true;
        }

        return loadedMajor > pinnedMajor || (loadedMajor == pinnedMajor && loadedMinor >= pinnedMinor);
    }

    private static bool TryParseMajorMinor(string version, out int major, out int minor)
    {
        major = minor = 0;
        var parts = version.TrimStart('v', 'V').Split('.', 3);
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }
}
