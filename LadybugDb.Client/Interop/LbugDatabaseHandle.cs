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
                throw new LadybugException($"Failed to open LadybugDB database at '{path}'.");

            var handle = new LbugDatabaseHandle();
            handle.Adopt(storage);
            adopted = true;
            return handle;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_database_destroy((lbug_database*)handle);
        FreeStorage();
        return true;
    }
}
