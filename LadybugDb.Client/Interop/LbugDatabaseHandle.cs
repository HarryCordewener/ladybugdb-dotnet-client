using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugDatabaseHandle : LbugStructHandle
{
    internal static unsafe LbugDatabaseHandle Open(string path, in lbug_system_config config)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_database));
        var adopted = false;
        try
        {
            var db = (lbug_database*)storage;
            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            lbug_state state;
            try
            {
                state = LbugNative.lbug_database_init((sbyte*)utf8, config, db);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }

            if (state != lbug_state.LbugSuccess)
            {
                var detail = NativeString.TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
                var message = detail is null
                    ? $"Failed to open LadybugDB database at '{path}'."
                    : $"Failed to open LadybugDB database at '{path}': {detail}";
                throw new LadybugException(message);
            }

            var handle = new LbugDatabaseHandle();
            // Set before Adopt: if Adopt itself ever threw, biasing this flag "true" first
            // means the finally below skips FreeUnowned and leaks storage rather than freeing
            // it out from under a handle that might already consider itself the owner. A leak
            // is recoverable game-over-at-worst; a double free of the same native storage is not.
            adopted = true;
            handle.Adopt(storage);
            return handle;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        try
        {
            LbugNative.lbug_database_destroy((lbug_database*)handle);
        }
        catch
        {
            // ReleaseHandle runs on the finalizer thread; an unhandled exception here is
            // process-fatal. LibraryImport entry points bind lazily on first call, and that
            // first call can be this one - a library that loads but is missing or has renamed
            // this export throws EntryPointNotFoundException right here. Still free our own
            // storage below regardless; only report the release itself as failed.
            return false;
        }
        finally
        {
            FreeStorage();
        }

        return true;
    }
}
