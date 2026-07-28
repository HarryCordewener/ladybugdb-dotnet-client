using System.Runtime.InteropServices;
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class NativeLibraryResolverTests
{
    [Test]
    public async Task ProbePaths_IncludeRuntimesLayout()
    {
        var paths = NativeLibraryResolver.ProbePaths("linux-x64", "liblbug.so").ToList();
        var hasRuntimes = paths.Any(p =>
            p.Replace('\\', '/').Contains("runtimes/linux-x64/native/liblbug.so"));
        await Assert.That(hasRuntimes).IsTrue();
    }

    [Test]
    public async Task ProbePaths_IncludeAppLocalFallback()
    {
        var paths = NativeLibraryResolver.ProbePaths("win-x64", "lbug_shared.dll").ToList();
        var hasFlat = paths.Any(p => p.EndsWith("lbug_shared.dll", StringComparison.Ordinal)
                                     && !p.Contains("runtimes"));
        await Assert.That(hasFlat).IsTrue();
    }

    [Test]
    public async Task MissingLibrary_ThrowsMessageNamingTheNativePackage()
    {
        var ex = NativeLibraryResolver.CreateMissingLibraryException();
        await Assert.That(ex.Message).Contains("LadybugDb.Client.Native");
    }

    /// <summary>
    /// Nothing P/Invokes into liblbug yet (that starts in a later task), so this
    /// can't be proven end-to-end through a DllImport. What it can prove is that
    /// Task 2's confirmed filenames are real: load the actual binary for the
    /// current platform straight out of the runtimes/&lt;rid&gt;/native/ layout
    /// that ProbePaths targets, using the exact same NativeLibrary.TryLoad call
    /// the resolver makes.
    /// </summary>
    [Test]
    public async Task RealNativeLibrary_LoadsFromRuntimesLayout()
    {
        var rid = NativeLibraryResolver.CurrentRid();
        var fileName = NativeLibraryResolver.CurrentFileName();
        var path = Path.Combine(RepoRoot(), "LadybugDb.Client.Native", "runtimes", rid, "native", fileName);

        await Assert.That(File.Exists(path)).IsTrue();

        var loaded = NativeLibrary.TryLoad(path, out var handle);
        try
        {
            await Assert.That(loaded).IsTrue();
            await Assert.That(handle).IsNotEqualTo(IntPtr.Zero);
        }
        finally
        {
            if (loaded) NativeLibrary.Free(handle);
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LadybugDb.Client.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
