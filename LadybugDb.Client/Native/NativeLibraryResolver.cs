using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LadybugDb.Client.Native;

/// <summary>
/// Resolves the liblbug native library from the layout NuGet produces for
/// LadybugDb.Client.Native, so the managed package can ship no binaries of its own.
/// </summary>
internal static class NativeLibraryResolver
{
    internal const string LibraryName = "lbug";

    // CA2255 flags ModuleInitializer as unusual outside application/source-generator
    // code, but registering a DllImportResolver as early as possible - before any
    // P/Invoke into this assembly can run - is exactly the documented use case for
    // it here. Deliberate, so suppressed locally rather than project-wide.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
#pragma warning restore CA2255

    internal static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    internal static string CurrentFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "lbug_shared.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "liblbug.dylib";
        return "liblbug.so";
    }

    /// <summary>
    /// Candidate paths to probe for the native library, in priority order:
    /// the NuGet <c>runtimes/&lt;rid&gt;/native/</c> layout that
    /// LadybugDb.Client.Native copies next to the consuming app, then a
    /// flat app-local fallback for hand-placed binaries. Each is tried
    /// relative to both the resolver assembly's own directory and the
    /// app's base directory, since they can differ (e.g. plugin/probing
    /// scenarios) and <see cref="Assembly.Location"/> is empty for
    /// single-file published apps.
    /// </summary>
    internal static IEnumerable<string> ProbePaths(string rid, string fileName)
    {
        var roots = new List<string>();
        var asmDir = Path.GetDirectoryName(typeof(NativeLibraryResolver).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir)) roots.Add(asmDir);
        if (!string.IsNullOrEmpty(AppContext.BaseDirectory)) roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            yield return Path.Combine(root, "runtimes", rid, "native", fileName);
            yield return Path.Combine(root, fileName);
        }
    }

    internal static DllNotFoundException CreateMissingLibraryException() =>
        new($"""
             Could not load the LadybugDB native library ({CurrentFileName()}) for {CurrentRid()}.

             LadybugDb.Client ships no native binaries by design. Add the companion package:

                 dotnet add package LadybugDb.Client.Native

             If you supply the library yourself, place it at
             runtimes/{CurrentRid()}/native/{CurrentFileName()} next to the application,
             or alongside the application binary.
             """);

    // Invoked directly by the runtime for every unresolved P/Invoke against
    // this assembly (see Initialize()). Returning IntPtr.Zero here is the
    // documented "not found, fall through" signal, but falling through would
    // just repeat the very OS-default probing we already do below via the
    // NativeLibrary.TryLoad(name, assembly, searchPath, ...) overload -
    // ending in a generic, unhelpful DllNotFoundException. Throwing our own
    // DllNotFoundException instead is a supported pattern: exceptions raised
    // from a DllImportResolver callback propagate unmodified to the P/Invoke
    // call site (verified empirically), so this is how the actionable
    // "install LadybugDb.Client.Native" guidance actually reaches the caller
    // instead of being replaced by the runtime's own generic message.
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        foreach (var candidate in ProbePaths(CurrentRid(), CurrentFileName()))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Fall back to the OS loader (system install, LD_LIBRARY_PATH, etc.).
        // This overload performs the runtime's normal default-resolution
        // probing without re-entering this callback, so it cannot recurse.
        return NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out var sys)
            ? sys
            : throw CreateMissingLibraryException();
    }
}
