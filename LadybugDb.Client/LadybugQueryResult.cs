using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>The result of a Cypher statement.</summary>
public sealed class LadybugQueryResult : IAsyncDisposable
{
    private readonly LbugQueryResultHandle _handle;

    internal LadybugQueryResult(LbugQueryResultHandle handle) => _handle = handle;

    internal LbugQueryResultHandle Handle => _handle;

    public unsafe bool IsSuccess
    {
        get
        {
            using var lease = _handle.Acquire();
            return LbugNative.lbug_query_result_is_success((lbug_query_result*)lease.Pointer) != 0;
        }
    }

    public unsafe bool HasNext
    {
        get
        {
            using var lease = _handle.Acquire();
            return LbugNative.lbug_query_result_has_next((lbug_query_result*)lease.Pointer) != 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
