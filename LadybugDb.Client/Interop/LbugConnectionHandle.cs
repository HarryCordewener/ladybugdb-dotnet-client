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

            lbug_state state;
            using (var lease = database.Acquire())
            {
                state = LbugNative.lbug_connection_init((lbug_database*)lease.Pointer, conn);
            }

            if (state != lbug_state.LbugSuccess)
            {
                var detail = NativeString.TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
                var message = detail is null
                    ? "Failed to open a LadybugDB connection."
                    : $"Failed to open a LadybugDB connection: {detail}";
                throw new LadybugException(message);
            }

            var handle = new LbugConnectionHandle();
            // See LbugDatabaseHandle.Open: set before Adopt so a failure here biases toward a
            // leak (storage never freed) rather than a double free (freed here, then again from
            // a handle that already believes it owns it).
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
            LbugNative.lbug_connection_destroy((lbug_connection*)handle);
        }
        catch
        {
            // See LbugDatabaseHandle.ReleaseHandle: this runs on the finalizer thread and must
            // never throw, however the native entry point resolves.
            return false;
        }
        finally
        {
            FreeStorage();
        }

        return true;
    }
}
