using System.Globalization;
using System.Reflection;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// The engine version this client's interop was generated against versus the one actually loaded.
/// The binaries come from upstream's <c>LadybugDB.Native</c> packages, chosen by the consumer, so
/// they can diverge; an older engine lacks entry points this client calls and would fail with an
/// <see cref="EntryPointNotFoundException"/> from whichever call happened to be missing.
/// </summary>
internal static class EngineVersion
{
    /// <summary>From <c>third-party/liblbug.version</c>, embedded as assembly metadata at build; no leading <c>v</c>.</summary>
    internal static string Pinned { get; } = ReadPinned();

    private static readonly Lazy<string> LoadedLazy = new(ReadLoaded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>What <c>lbug_get_version</c> reports for the loaded library.</summary>
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

    /// <summary>Throws <see cref="LadybugException"/> if the loaded engine's major.minor is older than the pin.</summary>
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
    /// Major.minor comparison only: patch releases have not changed the C API. An unparseable
    /// version counts as compatible - the check explains a predictable failure early; it must not
    /// invent one.
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
