using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: GeneratedCode("ClangSharp", "21.1.8.4")]

namespace LadybugDb.Client.Native
{
    internal unsafe partial struct ArrowSchema
    {
        [NativeTypeName("const char *")]
        internal sbyte* format;

        [NativeTypeName("const char *")]
        internal sbyte* name;

        [NativeTypeName("const char *")]
        internal sbyte* metadata;

        [NativeTypeName("int64_t")]
        internal long flags;

        [NativeTypeName("int64_t")]
        internal long n_children;

        [NativeTypeName("struct ArrowSchema **")]
        internal ArrowSchema** children;

        [NativeTypeName("struct ArrowSchema *")]
        internal ArrowSchema* dictionary;

        [NativeTypeName("void (*)(struct ArrowSchema *)")]
        internal delegate* unmanaged[Cdecl]<ArrowSchema*, void> release;

        internal void* private_data;
    }

    internal unsafe partial struct ArrowArray
    {
        [NativeTypeName("int64_t")]
        internal long length;

        [NativeTypeName("int64_t")]
        internal long null_count;

        [NativeTypeName("int64_t")]
        internal long offset;

        [NativeTypeName("int64_t")]
        internal long n_buffers;

        [NativeTypeName("int64_t")]
        internal long n_children;

        [NativeTypeName("const void **")]
        internal void** buffers;

        [NativeTypeName("struct ArrowArray **")]
        internal ArrowArray** children;

        [NativeTypeName("struct ArrowArray *")]
        internal ArrowArray* dictionary;

        [NativeTypeName("void (*)(struct ArrowArray *)")]
        internal delegate* unmanaged[Cdecl]<ArrowArray*, void> release;

        internal void* private_data;
    }

    internal partial struct lbug_system_config
    {
        [NativeTypeName("uint64_t")]
        internal ulong buffer_pool_size;

        [NativeTypeName("uint64_t")]
        internal ulong max_num_threads;

        [NativeTypeName("_Bool")]
        internal byte enable_compression;

        [NativeTypeName("_Bool")]
        internal byte read_only;

        [NativeTypeName("uint64_t")]
        internal ulong max_db_size;

        [NativeTypeName("_Bool")]
        internal byte auto_checkpoint;

        [NativeTypeName("uint64_t")]
        internal ulong checkpoint_threshold;

        [NativeTypeName("_Bool")]
        internal byte throw_on_wal_replay_failure;

        [NativeTypeName("_Bool")]
        internal byte enable_checksums;

        [NativeTypeName("_Bool")]
        internal byte enable_multi_writes;

        [NativeTypeName("_Bool")]
        internal byte enable_default_hash_index;
    }

    internal unsafe partial struct lbug_database
    {
        internal void* _database;
    }

    internal unsafe partial struct lbug_connection
    {
        internal void* _connection;
    }

    internal unsafe partial struct lbug_prepared_statement
    {
        internal void* _prepared_statement;

        internal void* _bound_values;
    }

    internal unsafe partial struct lbug_query_result
    {
        internal void* _query_result;

        [NativeTypeName("_Bool")]
        internal byte _is_owned_by_cpp;
    }

    internal unsafe partial struct lbug_flat_tuple
    {
        internal void* _flat_tuple;

        [NativeTypeName("_Bool")]
        internal byte _is_owned_by_cpp;
    }

    internal unsafe partial struct lbug_logical_type
    {
        internal void* _data_type;
    }

    internal unsafe partial struct lbug_value
    {
        internal void* _value;

        [NativeTypeName("_Bool")]
        internal byte _is_owned_by_cpp;
    }

    internal partial struct lbug_internal_id_t
    {
        [NativeTypeName("uint64_t")]
        internal ulong table_id;

        [NativeTypeName("uint64_t")]
        internal ulong offset;
    }

    internal partial struct lbug_date_t
    {
        [NativeTypeName("int32_t")]
        internal int days;
    }

    internal partial struct lbug_timestamp_ns_t
    {
        [NativeTypeName("int64_t")]
        internal long value;
    }

    internal partial struct lbug_timestamp_ms_t
    {
        [NativeTypeName("int64_t")]
        internal long value;
    }

    internal partial struct lbug_timestamp_sec_t
    {
        [NativeTypeName("int64_t")]
        internal long value;
    }

    internal partial struct lbug_timestamp_tz_t
    {
        [NativeTypeName("int64_t")]
        internal long value;
    }

    internal partial struct lbug_timestamp_t
    {
        [NativeTypeName("int64_t")]
        internal long value;
    }

    internal partial struct lbug_interval_t
    {
        [NativeTypeName("int32_t")]
        internal int months;

        [NativeTypeName("int32_t")]
        internal int days;

        [NativeTypeName("int64_t")]
        internal long micros;
    }

    internal unsafe partial struct lbug_query_summary
    {
        internal void* _query_summary;
    }

    internal partial struct lbug_int128_t
    {
        [NativeTypeName("uint64_t")]
        internal ulong low;

        [NativeTypeName("int64_t")]
        internal long high;
    }

    [NativeTypeName("unsigned int")]
    internal enum lbug_data_type_id : uint
    {
        LBUG_ANY = 0,
        LBUG_NODE = 10,
        LBUG_REL = 11,
        LBUG_RECURSIVE_REL = 12,
        LBUG_SERIAL = 13,
        LBUG_BOOL = 22,
        LBUG_INT64 = 23,
        LBUG_INT32 = 24,
        LBUG_INT16 = 25,
        LBUG_INT8 = 26,
        LBUG_UINT64 = 27,
        LBUG_UINT32 = 28,
        LBUG_UINT16 = 29,
        LBUG_UINT8 = 30,
        LBUG_INT128 = 31,
        LBUG_DOUBLE = 32,
        LBUG_FLOAT = 33,
        LBUG_DATE = 34,
        LBUG_TIMESTAMP = 35,
        LBUG_TIMESTAMP_SEC = 36,
        LBUG_TIMESTAMP_MS = 37,
        LBUG_TIMESTAMP_NS = 38,
        LBUG_TIMESTAMP_TZ = 39,
        LBUG_INTERVAL = 40,
        LBUG_DECIMAL = 41,
        LBUG_INTERNAL_ID = 42,
        LBUG_STRING = 50,
        LBUG_BLOB = 51,
        LBUG_LIST = 52,
        LBUG_ARRAY = 53,
        LBUG_STRUCT = 54,
        LBUG_MAP = 55,
        LBUG_UNION = 56,
        LBUG_POINTER = 58,
        LBUG_UUID = 59,
    }

    [NativeTypeName("unsigned int")]
    internal enum lbug_state : uint
    {
        LbugSuccess = 0,
        LbugError = 1,
    }

    internal static unsafe partial class LbugNative
    {
        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_database_init([NativeTypeName("const char *")] sbyte* database_path, lbug_system_config system_config, lbug_database* out_database);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_database_destroy(lbug_database* database);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_system_config lbug_default_system_config();

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_init(lbug_database* database, lbug_connection* out_connection);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_connection_destroy(lbug_connection* connection);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_set_max_num_thread_for_exec(lbug_connection* connection, [NativeTypeName("uint64_t")] ulong num_threads);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_get_max_num_thread_for_exec(lbug_connection* connection, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_query(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* query, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_prepare(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* query, lbug_prepared_statement* out_prepared_statement);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_execute(lbug_connection* connection, lbug_prepared_statement* prepared_statement, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_create_arrow_table(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* table_name, [NativeTypeName("struct ArrowSchema *")] ArrowSchema* schema, [NativeTypeName("struct ArrowArray *")] ArrowArray* arrays, [NativeTypeName("uint64_t")] ulong num_arrays, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_create_arrow_rel_table(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* table_name, [NativeTypeName("const char *")] sbyte* src_table_name, [NativeTypeName("const char *")] sbyte* dst_table_name, [NativeTypeName("struct ArrowSchema *")] ArrowSchema* schema, [NativeTypeName("struct ArrowArray *")] ArrowArray* arrays, [NativeTypeName("uint64_t")] ulong num_arrays, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_create_arrow_rel_table_csr(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* table_name, [NativeTypeName("const char *")] sbyte* src_table_name, [NativeTypeName("const char *")] sbyte* dst_table_name, [NativeTypeName("struct ArrowSchema *")] ArrowSchema* indices_schema, [NativeTypeName("struct ArrowArray *")] ArrowArray* indices_arrays, [NativeTypeName("uint64_t")] ulong num_indices_arrays, [NativeTypeName("struct ArrowSchema *")] ArrowSchema* indptr_schema, [NativeTypeName("struct ArrowArray *")] ArrowArray* indptr_arrays, [NativeTypeName("uint64_t")] ulong num_indptr_arrays, [NativeTypeName("const char *")] sbyte* dst_col_name, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_drop_arrow_table(lbug_connection* connection, [NativeTypeName("const char *")] sbyte* table_name, lbug_query_result* out_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_connection_interrupt(lbug_connection* connection);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_connection_set_query_timeout(lbug_connection* connection, [NativeTypeName("uint64_t")] ulong timeout_in_ms);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_prepared_statement_destroy(lbug_prepared_statement* prepared_statement);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_prepared_statement_is_success(lbug_prepared_statement* prepared_statement);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_prepared_statement_is_read_only(lbug_prepared_statement* prepared_statement);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_prepared_statement_get_error_message(lbug_prepared_statement* prepared_statement);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_bool(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("_Bool")] byte value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_int64(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("int64_t")] long value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_int32(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("int32_t")] int value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_int16(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("int16_t")] short value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_int8(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("int8_t")] sbyte value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_uint64(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("uint64_t")] ulong value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_uint32(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("uint32_t")] uint value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_uint16(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("uint16_t")] ushort value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_uint8(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("uint8_t")] byte value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_double(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, double value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_float(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, float value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_date(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_date_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_timestamp_ns(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_timestamp_ns_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_timestamp_sec(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_timestamp_sec_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_timestamp_tz(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_timestamp_tz_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_timestamp_ms(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_timestamp_ms_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_timestamp(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_timestamp_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_interval(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_interval_t value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_string(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, [NativeTypeName("const char *")] sbyte* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_prepared_statement_bind_value(lbug_prepared_statement* prepared_statement, [NativeTypeName("const char *")] sbyte* param_name, lbug_value* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_query_result_destroy(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_query_result_is_success(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_query_result_get_error_message(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("uint64_t")]
        internal static partial ulong lbug_query_result_get_num_columns(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_column_name(lbug_query_result* query_result, [NativeTypeName("uint64_t")] ulong index, [NativeTypeName("char **")] sbyte** out_column_name);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_column_data_type(lbug_query_result* query_result, [NativeTypeName("uint64_t")] ulong index, lbug_logical_type* out_column_data_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("uint64_t")]
        internal static partial ulong lbug_query_result_get_num_tuples(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_query_summary(lbug_query_result* query_result, lbug_query_summary* out_query_summary);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_query_result_has_next(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_next(lbug_query_result* query_result, lbug_flat_tuple* out_flat_tuple);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_query_result_has_next_query_result(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_next_query_result(lbug_query_result* query_result, lbug_query_result* out_next_query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_query_result_to_string(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_query_result_reset_iterator(lbug_query_result* query_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_arrow_schema(lbug_query_result* query_result, [NativeTypeName("struct ArrowSchema *")] ArrowSchema* out_schema);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_query_result_get_next_arrow_chunk(lbug_query_result* query_result, [NativeTypeName("int64_t")] long chunk_size, [NativeTypeName("struct ArrowArray *")] ArrowArray* out_arrow_array);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_flat_tuple_destroy(lbug_flat_tuple* flat_tuple);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_flat_tuple_get_value(lbug_flat_tuple* flat_tuple, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_flat_tuple_to_string(lbug_flat_tuple* flat_tuple);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_data_type_create(lbug_data_type_id id, lbug_logical_type* child_type, [NativeTypeName("uint64_t")] ulong num_elements_in_array, lbug_logical_type* out_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_data_type_clone(lbug_logical_type* data_type, lbug_logical_type* out_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_data_type_destroy(lbug_logical_type* data_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_data_type_equals(lbug_logical_type* data_type1, lbug_logical_type* data_type2);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_data_type_id lbug_data_type_get_id(lbug_logical_type* data_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_data_type_get_child_type(lbug_logical_type* data_type, lbug_logical_type* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_data_type_get_num_elements_in_array(lbug_logical_type* data_type, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_null();

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_null_with_data_type(lbug_logical_type* data_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("_Bool")]
        internal static partial byte lbug_value_is_null(lbug_value* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_value_set_null(lbug_value* value, [NativeTypeName("_Bool")] byte is_null);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_default(lbug_logical_type* data_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_bool([NativeTypeName("_Bool")] byte val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_int8([NativeTypeName("int8_t")] sbyte val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_int16([NativeTypeName("int16_t")] short val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_int32([NativeTypeName("int32_t")] int val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_int64([NativeTypeName("int64_t")] long val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_uint8([NativeTypeName("uint8_t")] byte val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_uint16([NativeTypeName("uint16_t")] ushort val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_uint32([NativeTypeName("uint32_t")] uint val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_uint64([NativeTypeName("uint64_t")] ulong val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_int128(lbug_int128_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_float(float val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_double(double val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_decimal([NativeTypeName("const char *")] sbyte* val_, [NativeTypeName("uint32_t")] uint precision, [NativeTypeName("uint32_t")] uint scale);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_internal_id(lbug_internal_id_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_date(lbug_date_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_timestamp_ns(lbug_timestamp_ns_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_timestamp_ms(lbug_timestamp_ms_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_timestamp_sec(lbug_timestamp_sec_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_timestamp_tz(lbug_timestamp_tz_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_timestamp(lbug_timestamp_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_interval(lbug_interval_t val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_string([NativeTypeName("const char *")] sbyte* val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_json([NativeTypeName("const char *")] sbyte* val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_create_uuid([NativeTypeName("const char *")] sbyte* val_);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_create_list([NativeTypeName("uint64_t")] ulong num_elements, lbug_value** elements, lbug_value** out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_create_struct([NativeTypeName("uint64_t")] ulong num_fields, [NativeTypeName("const char **")] sbyte** field_names, lbug_value** field_values, lbug_value** out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_create_map([NativeTypeName("uint64_t")] ulong num_fields, lbug_value** keys, lbug_value** values, lbug_value** out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_value* lbug_value_clone(lbug_value* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_value_copy(lbug_value* value, lbug_value* other);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_value_destroy(lbug_value* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_list_size(lbug_value* value, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_list_element(lbug_value* value, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_struct_num_fields(lbug_value* value, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_struct_field_name(lbug_value* value, [NativeTypeName("uint64_t")] ulong index, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_struct_field_index(lbug_value* value, [NativeTypeName("const char *")] sbyte* field_name, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_struct_field_value(lbug_value* value, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_map_size(lbug_value* value, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_map_key(lbug_value* value, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_key);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_map_value(lbug_value* value, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_recursive_rel_node_list(lbug_value* value, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_recursive_rel_rel_list(lbug_value* value, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_value_get_data_type(lbug_value* value, lbug_logical_type* out_type);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_bool(lbug_value* value, [NativeTypeName("_Bool *")] bool* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_int8(lbug_value* value, [NativeTypeName("int8_t *")] sbyte* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_int16(lbug_value* value, [NativeTypeName("int16_t *")] short* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_int32(lbug_value* value, [NativeTypeName("int32_t *")] int* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_int64(lbug_value* value, [NativeTypeName("int64_t *")] long* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_uint8(lbug_value* value, [NativeTypeName("uint8_t *")] byte* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_uint16(lbug_value* value, [NativeTypeName("uint16_t *")] ushort* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_uint32(lbug_value* value, [NativeTypeName("uint32_t *")] uint* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_uint64(lbug_value* value, [NativeTypeName("uint64_t *")] ulong* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_int128(lbug_value* value, lbug_int128_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_int128_t_from_string([NativeTypeName("const char *")] sbyte* str, lbug_int128_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_int128_t_to_string(lbug_int128_t val, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_float(lbug_value* value, float* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_double(lbug_value* value, double* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_internal_id(lbug_value* value, lbug_internal_id_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_date(lbug_value* value, lbug_date_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_timestamp(lbug_value* value, lbug_timestamp_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_timestamp_ns(lbug_value* value, lbug_timestamp_ns_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_timestamp_ms(lbug_value* value, lbug_timestamp_ms_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_timestamp_sec(lbug_value* value, lbug_timestamp_sec_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_timestamp_tz(lbug_value* value, lbug_timestamp_tz_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_interval(lbug_value* value, lbug_interval_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_decimal_as_string(lbug_value* value, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_string(lbug_value* value, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_blob(lbug_value* value, [NativeTypeName("uint8_t **")] byte** out_result, [NativeTypeName("uint64_t *")] ulong* out_length);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_value_get_uuid(lbug_value* value, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_value_to_string(lbug_value* value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_get_id_val(lbug_value* node_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_get_label_val(lbug_value* node_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_get_property_size(lbug_value* node_val, [NativeTypeName("uint64_t *")] ulong* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_get_property_name_at(lbug_value* node_val, [NativeTypeName("uint64_t")] ulong index, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_get_property_value_at(lbug_value* node_val, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_node_val_to_string(lbug_value* node_val, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_id_val(lbug_value* rel_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_src_id_val(lbug_value* rel_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_dst_id_val(lbug_value* rel_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_label_val(lbug_value* rel_val, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_property_size(lbug_value* rel_val, [NativeTypeName("uint64_t *")] ulong* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_property_name_at(lbug_value* rel_val, [NativeTypeName("uint64_t")] ulong index, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_get_property_value_at(lbug_value* rel_val, [NativeTypeName("uint64_t")] ulong index, lbug_value* out_value);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_rel_val_to_string(lbug_value* rel_val, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_destroy_string([NativeTypeName("char *")] sbyte* str);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_destroy_blob([NativeTypeName("uint8_t *")] byte* blob);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_query_summary_destroy(lbug_query_summary* query_summary);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial double lbug_query_summary_get_compiling_time(lbug_query_summary* query_summary);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial double lbug_query_summary_get_execution_time(lbug_query_summary* query_summary);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_date_to_string(lbug_date_t date, [NativeTypeName("char **")] sbyte** out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial lbug_state lbug_date_from_string([NativeTypeName("const char *")] sbyte* str, lbug_date_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_interval_to_difftime(lbug_interval_t interval, double* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void lbug_interval_from_difftime(double difftime, lbug_interval_t* out_result);

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_get_version();

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("uint64_t")]
        internal static partial ulong lbug_get_storage_version();

        [LibraryImport("lbug")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        [return: NativeTypeName("char *")]
        internal static partial sbyte* lbug_get_last_error();

        [NativeTypeName("#define ARROW_FLAG_DICTIONARY_ORDERED 1")]
        internal const int ARROW_FLAG_DICTIONARY_ORDERED = 1;

        [NativeTypeName("#define ARROW_FLAG_NULLABLE 2")]
        internal const int ARROW_FLAG_NULLABLE = 2;

        [NativeTypeName("#define ARROW_FLAG_MAP_KEYS_SORTED 4")]
        internal const int ARROW_FLAG_MAP_KEYS_SORTED = 4;
    }

    /// <summary>Defines the type of a member as it was used in the native signature.</summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
    [Conditional("DEBUG")]
    internal sealed partial class NativeTypeNameAttribute : Attribute
    {
        private readonly string _name;

        /// <summary>Initializes a new instance of the <see cref="NativeTypeNameAttribute" /> class.</summary>
        /// <param name="name">The name of the type that was used in the native signature.</param>
        public NativeTypeNameAttribute(string name)
        {
            _name = name;
        }

        /// <summary>Gets the name of the type that was used in the native signature.</summary>
        public string Name => _name;
    }

    /// <summary>Defines the annotation found in a native declaration.</summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    [Conditional("DEBUG")]
    internal sealed partial class NativeAnnotationAttribute : Attribute
    {
        private readonly string _annotation;

        /// <summary>Initializes a new instance of the <see cref="NativeAnnotationAttribute" /> class.</summary>
        /// <param name="annotation">The annotation that was used in the native declaration.</param>
        public NativeAnnotationAttribute(string annotation)
        {
            _annotation = annotation;
        }

        /// <summary>Gets the annotation that was used in the native declaration.</summary>
        public string Annotation => _annotation;
    }
}
