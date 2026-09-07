using System.Runtime.InteropServices;
using System.Text;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Benchmarks.Prototypes;

/// <summary>
/// A PROTOTYPE reader over <c>lbug_query_result_get_next_arrow_chunk</c>: the engine hands back a
/// columnar chunk through the Arrow C Data Interface and this decodes INT64 and STRING columns
/// straight out of the buffers - one native call per chunk instead of one per cell.
/// </summary>
/// <remarks>
/// Handles exactly the buffer layouts the <c>Obj</c> scan produces: <c>l</c> (int64, buffers =
/// validity, data) and <c>u</c>/<c>U</c> (utf8 with int32/int64 offsets, buffers = validity,
/// offsets, data). Everything else throws. The schema and every chunk are released through their
/// <c>release</c> callbacks, which is the interface's ownership contract.
/// </remarks>
internal static unsafe class ArrowChunkReader
{
    internal static List<ObjRow> ReadObjRows(LadybugQueryResult result, int chunkSize)
    {
        var rows = new List<ObjRow>();
        using var lease = result.Handle.Acquire();
        var qr = (lbug_query_result*)lease.Pointer;

        ArrowSchema schema;
        if (LbugNative.lbug_query_result_get_arrow_schema(qr, &schema) != lbug_state.LbugSuccess)
            throw new LadybugException("arrow schema");
        try
        {
            if (schema.n_children != 3) throw new LadybugException("expected 3 columns");
            var nameFormat = Marshal.PtrToStringUTF8((IntPtr)schema.children[1]->format);
            var largeOffsets = nameFormat == "U";
            if (!largeOffsets && nameFormat != "u") throw new LadybugException($"unexpected string format {nameFormat}");

            while (true)
            {
                ArrowArray chunk;
                if (LbugNative.lbug_query_result_get_next_arrow_chunk(qr, chunkSize, &chunk) != lbug_state.LbugSuccess)
                    throw new LadybugException("arrow chunk");
                if (chunk.length == 0)
                {
                    if (chunk.release is not null) chunk.release(&chunk);
                    break;
                }
                try
                {
                    var dbrefCol = chunk.children[0];
                    var nameCol = chunk.children[1];
                    var locCol = chunk.children[2];
                    var dbrefs = (long*)dbrefCol->buffers[1] + dbrefCol->offset;
                    var locs = (long*)locCol->buffers[1] + locCol->offset;
                    var data = (byte*)nameCol->buffers[2];
                    for (var i = 0; i < chunk.length; i++)
                    {
                        string name;
                        if (largeOffsets)
                        {
                            var offsets = (long*)nameCol->buffers[1] + nameCol->offset;
                            name = Encoding.UTF8.GetString(data + offsets[i], (int)(offsets[i + 1] - offsets[i]));
                        }
                        else
                        {
                            var offsets = (int*)nameCol->buffers[1] + nameCol->offset;
                            name = Encoding.UTF8.GetString(data + offsets[i], offsets[i + 1] - offsets[i]);
                        }
                        rows.Add(new ObjRow(dbrefs[i], name, locs[i]));
                    }
                }
                finally
                {
                    if (chunk.release is not null) chunk.release(&chunk);
                }
            }
        }
        finally
        {
            if (schema.release is not null) schema.release(&schema);
        }
        return rows;
    }
}
