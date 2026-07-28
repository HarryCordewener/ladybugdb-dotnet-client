using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugConnectionHandle : LbugStructHandle
{
    internal static unsafe LbugConnectionHandle Open(LbugDatabaseHandle database)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_connection));
        var adopted = false;
        try
        {
            var conn = (lbug_connection*)storage;
            var state = LbugNative.lbug_connection_init((lbug_database*)database.Pointer, conn);
            if (state != lbug_state.LbugSuccess)
                throw new LadybugException("Failed to open a LadybugDB connection.");

            var handle = new LbugConnectionHandle();
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
        LbugNative.lbug_connection_destroy((lbug_connection*)handle);
        FreeStorage();
        return true;
    }
}
