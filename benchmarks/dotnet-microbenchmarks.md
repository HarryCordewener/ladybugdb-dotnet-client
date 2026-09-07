# BenchmarkDotNet results, 2026-09-06

Engine v0.18.3, LadybugDb.Client at branch `feat/api-ergonomics` plus the 2026-09-06 fixes. Host: Intel Core Ultra 7 265F (20 cores), NVMe, CachyOS, .NET 10.0.8, BenchmarkDotNet 0.15.8, 3 warm-up and 12 measured iterations, MemoryDiagnoser. The analysis is in [`docs/2026-09-06-production-readiness.md`](../docs/2026-09-06-production-readiness.md); the classes are in `LadybugDb.Client.Benchmarks/`.

`Ratio` is against the row marked as the baseline within each class; `?` cells are parameters that do not apply to that class (BenchmarkDotNet joins every class into one table).

## Point lookups, writes, traversals (run 1, 19:38)

| Type                  | Method                         | Rows  | Size   | Mean             | Error            | StdDev           | Median           | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|---------------------- |------------------------------- |------ |------- |-----------------:|-----------------:|-----------------:|-----------------:|------:|--------:|---------:|---------:|---------:|-----------:|------------:|
| PointLookupBenchmarks | Interpolated                   | ?     | 10000  |    101,095.88 ns |     2,179.550 ns |     1,441.638 ns |    100,952.96 ns |  0.10 |    0.02 |        - |        - |        - |      880 B |        0.24 |
| TraversalBenchmarks   | RoomContents                   | ?     | 10000  |  1,067,740.10 ns |   330,387.008 ns |   257,944.444 ns |    909,940.63 ns |  1.05 |    0.33 |        - |        - |        - |     3601 B |        1.00 |
| WriteBenchmarks       | SetAutoCommit_ParametersObject | ?     | 10000  |    265,627.56 ns |    54,984.107 ns |    42,927.974 ns |    264,455.28 ns |  0.26 |    0.07 |        - |        - |        - |     1148 B |        0.32 |
| PointLookupBenchmarks | ParametersObject               | ?     | 10000  |    122,006.26 ns |     2,022.824 ns |     1,579.288 ns |    121,734.16 ns |  0.12 |    0.02 |        - |        - |        - |     1305 B |        0.36 |
| TraversalBenchmarks   | RoomContentsCount              | ?     | 10000  |    275,431.95 ns |     4,719.143 ns |     3,121.421 ns |    275,332.51 ns |  0.27 |    0.05 |        - |        - |        - |      544 B |        0.15 |
| WriteBenchmarks       | SetAutoCommit_Prepared         | ?     | 10000  |    536,963.80 ns |   281,528.740 ns |   219,799.122 ns |    506,311.90 ns |  0.53 |    0.24 |        - |        - |        - |      219 B |        0.06 |
| PointLookupBenchmarks | PreparedTypedBind              | ?     | 10000  |     59,232.73 ns |       551.227 ns |       398.573 ns |     59,164.77 ns |  0.06 |    0.01 |        - |        - |        - |      480 B |        0.13 |
| TraversalBenchmarks   | ObjectWithAllAttributes        | ?     | 10000  |    565,978.56 ns |   143,295.349 ns |   103,611.926 ns |    513,879.64 ns |  0.56 |    0.15 |        - |        - |        - |     3695 B |        1.03 |
| WriteBenchmarks       | SetInManagedTransaction        | ?     | 10000  |    371,533.45 ns |   188,390.975 ns |   147,083.281 ns |    342,377.09 ns |  0.36 |    0.16 |        - |        - |        - |      587 B |        0.16 |
| PointLookupBenchmarks | PreparedParametersObject       | ?     | 10000  |     59,559.20 ns |       631.013 ns |       492.654 ns |     59,382.50 ns |  0.06 |    0.01 |        - |        - |        - |     1128 B |        0.31 |
| TraversalBenchmarks   | TwoHop                         | ?     | 10000  |    944,219.93 ns |    13,841.944 ns |    10,806.880 ns |    941,698.80 ns |  0.93 |    0.19 |        - |        - |        - |      536 B |        0.15 |
| WriteBenchmarks       | SetInRawTransaction            | ?     | 10000  |    250,348.86 ns |   113,938.870 ns |    82,385.268 ns |    216,261.39 ns |  0.25 |    0.09 |        - |        - |        - |      851 B |        0.24 |
| PointLookupBenchmarks | PreparedSelectScalar           | ?     | 10000  |     60,944.90 ns |     2,536.729 ns |     1,834.222 ns |     60,328.77 ns |  0.06 |    0.01 |        - |        - |        - |     1464 B |        0.41 |
| WriteBenchmarks       | Batch100InOneTransaction       | ?     | 10000  |    168,393.53 ns |    40,418.700 ns |    31,556.262 ns |    163,162.74 ns |  0.17 |    0.05 |        - |        - |        - |      223 B |        0.06 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun      | ?     | 10000  |     66,893.75 ns |     3,114.987 ns |     2,431.977 ns |     67,655.67 ns |  0.07 |    0.01 |        - |        - |        - |      752 B |        0.21 |
| PointLookupBenchmarks | AttrByPrimaryKey               | ?     | 10000  |     61,565.88 ns |       561.983 ns |       334.427 ns |     61,704.67 ns |  0.06 |    0.01 |        - |        - |        - |      523 B |        0.15 |
| PointLookupBenchmarks | AttrByTraversalHop             | ?     | 10000  |    459,201.65 ns |     9,178.385 ns |     7,165.879 ns |    460,448.55 ns |  0.45 |    0.09 |        - |        - |        - |      480 B |        0.13 |
| PointLookupBenchmarks | Interpolated                   | ?     | 100000 |    100,152.09 ns |     1,236.350 ns |       965.261 ns |     99,995.93 ns |  0.80 |    0.02 |        - |        - |        - |      887 B |        0.68 |
| PointLookupBenchmarks | ParametersObject               | ?     | 100000 |    125,359.62 ns |     3,892.935 ns |     2,574.936 ns |    124,673.33 ns |  1.00 |    0.03 |        - |        - |        - |     1305 B |        1.00 |
| PointLookupBenchmarks | PreparedTypedBind              | ?     | 100000 |     60,425.82 ns |       694.560 ns |       542.267 ns |     60,296.78 ns |  0.48 |    0.01 |        - |        - |        - |      480 B |        0.37 |
| PointLookupBenchmarks | PreparedParametersObject       | ?     | 100000 |     61,513.53 ns |     1,335.140 ns |       883.112 ns |     61,204.61 ns |  0.49 |    0.01 |        - |        - |        - |     1128 B |        0.86 |
| PointLookupBenchmarks | PreparedSelectScalar           | ?     | 100000 |     62,225.85 ns |       578.212 ns |       451.430 ns |     62,151.98 ns |  0.50 |    0.01 |        - |        - |        - |     1464 B |        1.12 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun      | ?     | 100000 |     70,186.69 ns |     6,809.521 ns |     5,316.426 ns |     68,421.59 ns |  0.56 |    0.04 |        - |        - |        - |      752 B |        0.58 |
| PointLookupBenchmarks | AttrByPrimaryKey               | ?     | 100000 |     88,320.27 ns |    20,071.877 ns |    15,670.801 ns |     83,425.19 ns |  0.70 |    0.12 |        - |        - |        - |      527 B |        0.40 |
| PointLookupBenchmarks | AttrByTraversalHop             | ?     | 100000 |    648,552.98 ns |   315,664.965 ns |   228,246.452 ns |    517,160.55 ns |  5.18 |    1.75 |        - |        - |        - |      480 B |        0.37 |

## Read path and thread cap (run 2, 19:53, after the RowMapper change so `SelectRecord` runs)

| Type                 | Method                    | MaxThreads | Rows  | Mean               | Error             | StdDev            | Median             | Ratio  | RatioSD | Gen0     | Gen1     | Gen2    | Allocated  | Alloc Ratio |
|--------------------- |-------------------------- |----------- |------ |-------------------:|------------------:|------------------:|-------------------:|-------:|--------:|---------:|---------:|--------:|-----------:|------------:|
| ThreadCapBenchmarks  | PointLookup               | 0          | ?     |     58,909.1462 ns |     1,535.2544 ns |     1,015.4758 ns |     58,627.4560 ns |   1.00 |    0.02 |        - |        - |       - |      480 B |        1.00 |
| ThreadCapBenchmarks  | RoomContents              | 0          | ?     |    881,397.9365 ns |    32,535.5638 ns |    23,525.3444 ns |    871,686.5000 ns |  14.97 |    0.45 |        - |        - |       - |     3601 B |        7.50 |
| ThreadCapBenchmarks  | ObjectWithAllAttributes   | 0          | ?     |    538,288.3924 ns |    38,524.6032 ns |    30,077.4761 ns |    526,831.5635 ns |   9.14 |    0.51 |        - |        - |       - |     3695 B |        7.70 |
| ThreadCapBenchmarks  | TwoHop                    | 0          | ?     |  1,308,211.7855 ns |    72,778.9822 ns |    56,821.0421 ns |  1,325,779.9463 ns |  22.21 |    1.00 |        - |        - |       - |      536 B |        1.12 |
| ThreadCapBenchmarks  | Scan10kRows               | 0          | ?     |  9,109,065.2487 ns |   123,295.8378 ns |    96,261.2802 ns |  9,107,565.6719 ns | 154.67 |    2.96 | 265.6250 |        - |       - |  4399632 B |    9,165.90 |
| ThreadCapBenchmarks  | PointLookup               | 1          | ?     |     60,387.6482 ns |     5,192.0983 ns |     4,053.6488 ns |     59,958.9452 ns |   1.00 |    0.09 |        - |        - |       - |      480 B |        1.00 |
| ThreadCapBenchmarks  | RoomContents              | 1          | ?     |    731,456.4463 ns |    52,687.9354 ns |    41,135.2743 ns |    721,064.7363 ns |  12.16 |    1.05 |        - |        - |       - |     3601 B |        7.50 |
| ThreadCapBenchmarks  | ObjectWithAllAttributes   | 1          | ?     |    397,477.5812 ns |    20,101.1620 ns |    15,693.6651 ns |    394,207.5000 ns |   6.61 |    0.51 |        - |        - |       - |     3695 B |        7.70 |
| ThreadCapBenchmarks  | TwoHop                    | 1          | ?     |    630,251.5474 ns |    46,883.3213 ns |    33,899.7131 ns |    637,890.3892 ns |  10.48 |    0.88 |        - |        - |       - |      536 B |        1.12 |
| ThreadCapBenchmarks  | Scan10kRows               | 1          | ?     |  8,567,927.2409 ns |   273,096.8435 ns |   213,216.0519 ns |  8,519,993.1172 ns | 142.49 |   10.11 | 265.6250 |        - |       - |  4399632 B |    9,165.90 |
| ThreadCapBenchmarks  | PointLookup               | 4          | ?     |     63,653.0702 ns |     1,420.9201 ns |     1,109.3609 ns |     63,842.9317 ns |   1.00 |    0.02 |        - |        - |       - |      480 B |        1.00 |
| ThreadCapBenchmarks  | RoomContents              | 4          | ?     |    785,176.4568 ns |    29,303.0044 ns |    22,877.8584 ns |    787,433.8667 ns |  12.34 |    0.40 |        - |        - |       - |     3601 B |        7.50 |
| ThreadCapBenchmarks  | ObjectWithAllAttributes   | 4          | ?     |    415,037.6472 ns |    14,689.7123 ns |    11,468.7611 ns |    415,001.7795 ns |   6.52 |    0.20 |        - |        - |       - |     3695 B |        7.70 |
| ThreadCapBenchmarks  | TwoHop                    | 4          | ?     |    667,087.7078 ns |    36,155.4939 ns |    28,227.8314 ns |    660,902.8062 ns |  10.48 |    0.46 |        - |        - |       - |      536 B |        1.12 |
| ThreadCapBenchmarks  | Scan10kRows               | 4          | ?     |  8,765,651.6576 ns |   299,595.2187 ns |   233,904.2403 ns |  8,834,447.2891 ns | 137.75 |    4.21 | 265.6250 |        - |       - |  4399632 B |    9,165.90 |
| ReadPathBenchmarks   | CountOnly                 | ?          | 10000 |    181,326.3513 ns |     4,007.2320 ns |     2,384.6395 ns |    181,497.5256 ns |   0.02 |    0.00 |        - |        - |       - |      536 B |       0.000 |
| ReadPathBenchmarks   | RowAccessors              | ?          | 10000 |  9,520,247.8346 ns |   622,972.9384 ns |   486,376.2930 ns |  9,303,516.3203 ns |   1.00 |    0.07 | 265.6250 |        - |       - |  4399632 B |       1.000 |
| ReadPathBenchmarks   | SelectRecord              | ?          | 10000 |  9,495,837.9812 ns |   595,373.8753 ns |   393,802.9554 ns |  9,451,784.7344 ns |   1.00 |    0.06 | 359.3750 |        - |       - |  5760000 B |       1.309 |
| ReadPathBenchmarks   | RowsToListAsync           | ?          | 10000 | 13,916,250.1146 ns | 1,355,725.2257 ns | 1,058,461.0807 ns | 13,699,942.3438 ns |   1.47 |    0.13 | 343.7500 | 218.7500 | 93.7500 |  4924302 B |       1.119 |
| ReadPathBenchmarks   | NodeValues                | ?          | 10000 | 20,572,745.0369 ns |   356,607.1410 ns |   257,850.3277 ns | 20,644,172.9375 ns |   2.17 |    0.11 | 625.0000 |        - |       - | 10239528 B |       2.327 |
| ReadPathBenchmarks   | Prototype_FastPath_Values | ?          | 10000 |  2,797,048.0023 ns |    85,014.0325 ns |    66,373.3646 ns |  2,787,639.1953 ns |   0.29 |    0.02 | 125.0000 | 117.1875 | 50.7813 |  1862154 B |       0.423 |
| ReadPathBenchmarks   | Prototype_FastPath_Typed  | ?          | 10000 |  1,890,035.2838 ns |    67,385.7163 ns |    48,724.2880 ns |  1,877,818.8193 ns |   0.20 |    0.01 |  82.0313 |  80.0781 | 41.0156 |  1062060 B |       0.241 |
| ReadPathBenchmarks   | Prototype_ArrowChunks     | ?          | 10000 |  1,772,168.1717 ns |   141,311.3587 ns |   102,177.3710 ns |  1,718,341.5000 ns |   0.19 |    0.01 |  82.0313 |  80.0781 | 41.0156 |  1062084 B |       0.241 |

## Transaction-statement classifier (run 3, after the allocation fix)

| Method           | Mean      | Error     | StdDev    | Median    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------:|----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| TypicalQuery     |  1.081 ns | 0.0140 ns | 0.0109 ns |  1.079 ns |  1.00 |    0.01 |      - |         - |          NA |
| BeginTransaction | 48.986 ns | 0.7225 ns | 0.5224 ns | 48.778 ns | 45.30 |    0.64 | 0.0107 |     168 B |          NA |

Before the fix (run 1): `TypicalQuery` 117.88 ns, 528 B; `BeginTransaction` 38.08 ns, 168 B.

## Engine 0.19.1 re-run (run 4, 20:10, after switching to upstream's `LadybugDB.Native` 0.19.1)

Same host, same code as run 2. Point lookups are 8–12% slower than on 0.18.3 across every dispatch shape; the read path is unchanged within noise.

| Type                  | Method                    | Rows  | Size   | Mean         | Error      | StdDev     | Median       | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|---------------------- |-------------------------- |------ |------- |-------------:|-----------:|-----------:|-------------:|------:|--------:|---------:|---------:|---------:|-----------:|------------:|
| ReadPathBenchmarks    | CountOnly                 | 10000 | ?      |    181.06 μs |   2.112 μs |   1.257 μs |    181.54 μs |  0.02 |    0.00 |        - |        - |        - |      536 B |       0.000 |
| ReadPathBenchmarks    | RowAccessors              | 10000 | ?      |  8,854.34 μs | 247.920 μs | 193.559 μs |  8,886.55 μs |  1.00 |    0.03 | 265.6250 |        - |        - |  4399632 B |       1.000 |
| ReadPathBenchmarks    | SelectRecord              | 10000 | ?      |  9,026.27 μs | 194.134 μs | 151.567 μs |  9,066.13 μs |  1.02 |    0.03 | 359.3750 |        - |        - |  5760000 B |       1.309 |
| ReadPathBenchmarks    | RowsToListAsync           | 10000 | ?      | 11,804.15 μs | 210.193 μs | 139.029 μs | 11,778.88 μs |  1.33 |    0.03 | 359.3750 | 234.3750 | 109.3750 |  4924282 B |       1.119 |
| ReadPathBenchmarks    | NodeValues                | 10000 | ?      | 20,153.15 μs | 426.768 μs | 333.193 μs | 20,266.73 μs |  2.28 |    0.06 | 625.0000 |        - |        - | 10239528 B |       2.327 |
| ReadPathBenchmarks    | Prototype_FastPath_Values | 10000 | ?      |  3,047.33 μs |  61.433 μs |  47.963 μs |  3,044.00 μs |  0.34 |    0.01 | 125.0000 | 117.1875 |  50.7813 |  1862178 B |       0.423 |
| ReadPathBenchmarks    | Prototype_FastPath_Typed  | 10000 | ?      |  1,892.43 μs |  18.515 μs |  14.456 μs |  1,893.03 μs |  0.21 |    0.00 |  82.0313 |  80.0781 |  41.0156 |  1062060 B |       0.241 |
| ReadPathBenchmarks    | Prototype_ArrowChunks     | 10000 | ?      |  1,725.19 μs |  25.585 μs |  19.975 μs |  1,728.60 μs |  0.19 |    0.00 |  82.0313 |  80.0781 |  41.0156 |  1062084 B |       0.241 |
| PointLookupBenchmarks | Interpolated              | ?     | 10000  |    111.58 μs |   6.327 μs |   4.575 μs |    111.57 μs |  0.83 |    0.03 |        - |        - |        - |      592 B |        0.45 |
| PointLookupBenchmarks | ParametersObject          | ?     | 10000  |    134.51 μs |   2.581 μs |   1.867 μs |    134.39 μs |  1.00 |    0.02 |        - |        - |        - |     1305 B |        1.00 |
| PointLookupBenchmarks | PreparedTypedBind         | ?     | 10000  |     66.20 μs |   0.845 μs |   0.503 μs |     66.14 μs |  0.49 |    0.01 |        - |        - |        - |      480 B |        0.37 |
| PointLookupBenchmarks | PreparedParametersObject  | ?     | 10000  |     65.42 μs |   1.119 μs |   0.809 μs |     65.77 μs |  0.49 |    0.01 |        - |        - |        - |     1128 B |        0.86 |
| PointLookupBenchmarks | PreparedSelectScalar      | ?     | 10000  |     66.86 μs |   2.725 μs |   1.802 μs |     66.56 μs |  0.50 |    0.01 |        - |        - |        - |     1464 B |        1.12 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun | ?     | 10000  |     73.11 μs |   2.938 μs |   2.124 μs |     73.32 μs |  0.54 |    0.02 |        - |        - |        - |      752 B |        0.58 |
| PointLookupBenchmarks | AttrByPrimaryKey          | ?     | 10000  |     67.57 μs |   0.804 μs |   0.478 μs |     67.69 μs |  0.50 |    0.01 |        - |        - |        - |      523 B |        0.40 |
| PointLookupBenchmarks | AttrByTraversalHop        | ?     | 10000  |    502.94 μs |  15.806 μs |  12.340 μs |    503.14 μs |  3.74 |    0.10 |        - |        - |        - |      480 B |        0.37 |
| PointLookupBenchmarks | Interpolated              | ?     | 100000 |    108.30 μs |   5.208 μs |   3.766 μs |    107.94 μs |  0.74 |    0.04 |        - |        - |        - |      592 B |        0.45 |
| PointLookupBenchmarks | ParametersObject          | ?     | 100000 |    146.97 μs |   9.756 μs |   7.054 μs |    145.47 μs |  1.00 |    0.06 |        - |        - |        - |     1305 B |        1.00 |
| PointLookupBenchmarks | PreparedTypedBind         | ?     | 100000 |     69.03 μs |   1.693 μs |   1.224 μs |     69.03 μs |  0.47 |    0.02 |        - |        - |        - |      480 B |        0.37 |
| PointLookupBenchmarks | PreparedParametersObject  | ?     | 100000 |     71.73 μs |   5.465 μs |   4.267 μs |     69.77 μs |  0.49 |    0.04 |        - |        - |        - |     1128 B |        0.86 |
| PointLookupBenchmarks | PreparedSelectScalar      | ?     | 100000 |     73.48 μs |   1.946 μs |   1.519 μs |     73.66 μs |  0.50 |    0.02 |        - |        - |        - |     1464 B |        1.12 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun | ?     | 100000 |     79.61 μs |   3.317 μs |   2.590 μs |     79.60 μs |  0.54 |    0.03 |        - |        - |        - |      752 B |        0.58 |
| PointLookupBenchmarks | AttrByPrimaryKey          | ?     | 100000 |     73.81 μs |   1.409 μs |   0.932 μs |     73.42 μs |  0.50 |    0.02 |        - |        - |        - |      527 B |        0.40 |
| PointLookupBenchmarks | AttrByTraversalHop        | ?     | 100000 |    580.96 μs |   5.949 μs |   4.644 μs |    579.97 μs |  3.96 |    0.18 |        - |        - |        - |      480 B |        0.37 |

## Re-run after the PR #3 review fixes (2026-09-07 01:35)

Two benchmark corrections from the review are in this run: `RowsToListAsync` now reads the same
three columns as its neighbours (it read one, which made the comparison uneven), and
`BenchDatabase` rejects an invalid size or model.

**Read the allocation column, not the timing column, from this run.** The host was running an
unrelated Unity session at roughly five cores throughout, and the timings show it: `PreparedTypedBind`
measures 142 µs at 10,000 objects and 97 µs at 100,000 in the same run, for an operation that is
size-independent and measured 63-69 µs on a quiet machine. Allocations are load-independent and
exact, and they corroborate the read-path change on their own: `RowAccessors` allocates 1,599,680 B
per 10,000 three-column rows against 4,399,904 B before it, and `RowsToListAsync` 2,124,329 B
against 4,924,587 B. The timing figures quoted elsewhere in this repository come from the quieter
runs earlier the same day; re-measure on an idle host before quoting these.

| Type                  | Method                         | MaxThreads | Rows  | Size   | Mean              | Error           | StdDev          | Median            | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|---------------------- |------------------------------- |----------- |------ |------- |------------------:|----------------:|----------------:|------------------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **ClassifierBenchmarks**  | **TypicalQuery**                   | **?**          | **?**     | **?**      |          **1.111 ns** |       **0.0014 ns** |       **0.0009 ns** |          **1.111 ns** |  **1.00** |    **0.00** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ClassifierBenchmarks  | BeginTransaction               | ?          | ?     | ?      |         58.759 ns |       1.1570 ns |       0.9033 ns |         58.673 ns | 52.87 |    0.78 |   0.0106 |        - |        - |     168 B |          NA |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **ThreadCapBenchmarks**   | **PointLookup**                    | **0**          | **?**     | **?**      |     **95,721.759 ns** |   **2,393.3605 ns** |   **1,868.5784 ns** |     **95,491.895 ns** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |     **400 B** |        **1.00** |
| ThreadCapBenchmarks   | RoomContents                   | 0          | ?     | ?      |    987,153.938 ns |  15,131.8604 ns |  11,813.9613 ns |    985,412.473 ns | 10.32 |    0.22 |        - |        - |        - |    1635 B |        4.09 |
| ThreadCapBenchmarks   | ObjectWithAllAttributes        | 0          | ?     | ?      |    733,454.414 ns |  53,125.6597 ns |  41,477.0206 ns |    734,235.981 ns |  7.66 |    0.44 |        - |        - |        - |    1735 B |        4.34 |
| ThreadCapBenchmarks   | TwoHop                         | 0          | ?     | ?      |  1,722,786.243 ns |  39,906.9679 ns |  31,156.7356 ns |  1,711,562.574 ns | 18.00 |    0.46 |        - |        - |        - |     456 B |        1.14 |
| ThreadCapBenchmarks   | Scan10kRows                    | 0          | ?     | ?      |  3,350,821.010 ns |  30,059.4855 ns |  21,734.9775 ns |  3,353,589.287 ns | 35.02 |    0.68 | 101.5625 |        - |        - | 1599680 B |    3,999.20 |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **ThreadCapBenchmarks**   | **PointLookup**                    | **1**          | **?**     | **?**      |     **76,447.873 ns** |   **1,409.7163 ns** |   **1,100.6137 ns** |     **76,703.417 ns** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |     **400 B** |        **1.00** |
| ThreadCapBenchmarks   | RoomContents                   | 1          | ?     | ?      |    750,582.325 ns |   5,778.0743 ns |   4,511.1403 ns |    750,358.170 ns |  9.82 |    0.15 |        - |        - |        - |    1640 B |        4.10 |
| ThreadCapBenchmarks   | ObjectWithAllAttributes        | 1          | ?     | ?      |    462,534.390 ns |   7,094.3497 ns |   5,538.8016 ns |    463,248.861 ns |  6.05 |    0.11 |        - |        - |        - |    1735 B |        4.34 |
| ThreadCapBenchmarks   | TwoHop                         | 1          | ?     | ?      |    735,096.183 ns |  13,689.6631 ns |  10,687.9885 ns |    736,081.251 ns |  9.62 |    0.19 |        - |        - |        - |     456 B |        1.14 |
| ThreadCapBenchmarks   | Scan10kRows                    | 1          | ?     | ?      |  3,386,079.524 ns |  35,279.2323 ns |  27,543.7040 ns |  3,383,693.725 ns | 44.30 |    0.70 | 101.5625 |        - |        - | 1599680 B |    3,999.20 |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **ThreadCapBenchmarks**   | **PointLookup**                    | **4**          | **?**     | **?**      |     **85,345.192 ns** |     **793.3072 ns** |     **619.3621 ns** |     **85,483.200 ns** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |     **400 B** |        **1.00** |
| ThreadCapBenchmarks   | RoomContents                   | 4          | ?     | ?      |    941,653.312 ns |  10,757.9503 ns |   7,778.7029 ns |    942,302.548 ns | 11.03 |    0.12 |        - |        - |        - |    1635 B |        4.09 |
| ThreadCapBenchmarks   | ObjectWithAllAttributes        | 4          | ?     | ?      |    571,256.089 ns |  18,669.2745 ns |  13,499.1087 ns |    567,268.992 ns |  6.69 |    0.16 |        - |        - |        - |    1735 B |        4.34 |
| ThreadCapBenchmarks   | TwoHop                         | 4          | ?     | ?      |    904,162.995 ns |  73,565.3486 ns |  57,434.9853 ns |    904,666.167 ns | 10.59 |    0.65 |        - |        - |        - |     456 B |        1.14 |
| ThreadCapBenchmarks   | Scan10kRows                    | 4          | ?     | ?      |  3,310,266.749 ns |  15,418.9377 ns |  12,038.0923 ns |  3,317,051.082 ns | 38.79 |    0.30 | 101.5625 |        - |        - | 1599680 B |    3,999.20 |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **ReadPathBenchmarks**    | **CountOnly**                      | **?**          | **10000** | **?**      |    **327,184.728 ns** |  **25,799.7725 ns** |  **20,142.7654 ns** |    **329,063.416 ns** |  **0.10** |    **0.01** |        **-** |        **-** |        **-** |     **456 B** |       **0.000** |
| ReadPathBenchmarks    | RowAccessors                   | ?          | 10000 | ?      |  3,357,691.137 ns |  58,605.0199 ns |  45,754.9447 ns |  3,346,920.719 ns |  1.00 |    0.02 | 101.5625 |        - |        - | 1599680 B |       1.000 |
| ReadPathBenchmarks    | SelectRecord                   | ?          | 10000 | ?      |  2,789,370.285 ns |  21,016.3251 ns |  16,408.1642 ns |  2,788,930.713 ns |  0.83 |    0.01 | 187.5000 |        - |        - | 2960048 B |       1.850 |
| ReadPathBenchmarks    | RowsToListAsync                | ?          | 10000 | ?      |  2,855,796.473 ns | 137,349.5332 ns |  99,312.7116 ns |  2,908,408.537 ns |  0.85 |    0.03 | 121.0938 | 121.0938 | 121.0938 | 2124329 B |       1.328 |
| ReadPathBenchmarks    | NodeValues                     | ?          | 10000 | ?      | 16,539,059.409 ns |  78,734.9093 ns |  61,471.0383 ns | 16,512,786.312 ns |  4.93 |    0.07 | 562.5000 |        - |        - | 9039568 B |       5.651 |
| ReadPathBenchmarks    | Prototype_FastPath_Values      | ?          | 10000 | ?      |  3,072,825.387 ns |  38,494.7445 ns |  30,054.1645 ns |  3,071,497.693 ns |  0.92 |    0.01 | 125.0000 | 117.1875 |  50.7813 | 1862214 B |       1.164 |
| ReadPathBenchmarks    | Prototype_FastPath_Typed       | ?          | 10000 | ?      |  1,948,963.177 ns |  15,856.2569 ns |  10,487.9322 ns |  1,948,557.525 ns |  0.58 |    0.01 |  78.1250 |  74.2188 |  39.0625 | 1062106 B |       0.664 |
| ReadPathBenchmarks    | Prototype_ArrowChunks          | ?          | 10000 | ?      |  1,824,594.716 ns |  60,972.5768 ns |  47,603.3774 ns |  1,810,055.037 ns |  0.54 |    0.02 |  82.0313 |  80.0781 |  41.0156 | 1062132 B |       0.664 |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **PointLookupBenchmarks** | **Interpolated**                   | **?**          | **?**     | **10000**  |    **150,339.871 ns** |   **9,510.5430 ns** |   **7,425.2064 ns** |    **146,368.028 ns** |  **0.15** |    **0.01** |        **-** |        **-** |        **-** |     **512 B** |        **0.31** |
| TraversalBenchmarks   | RoomContents                   | ?          | ?     | 10000  |  1,005,335.012 ns |  10,595.0867 ns |   7,007.9939 ns |  1,005,535.839 ns |  1.00 |    0.01 |        - |        - |        - |    1635 B |        1.00 |
| WriteBenchmarks       | SetAutoCommit_ParametersObject | ?          | ?     | 10000  |    429,564.034 ns | 114,389.9616 ns |  89,308.1578 ns |    430,640.980 ns |  0.43 |    0.09 |        - |        - |        - |     619 B |        0.38 |
| PointLookupBenchmarks | ParametersObject               | ?          | ?     | 10000  |    100,876.779 ns |   4,100.6611 ns |   3,201.5265 ns |     99,914.089 ns |  0.10 |    0.00 |        - |        - |        - |     696 B |        0.43 |
| TraversalBenchmarks   | RoomContentsCount              | ?          | ?     | 10000  |    490,374.723 ns |   5,604.0677 ns |   4,375.2874 ns |    490,069.714 ns |  0.49 |    0.01 |        - |        - |        - |     464 B |        0.28 |
| WriteBenchmarks       | SetAutoCommit_Prepared         | ?          | ?     | 10000  |    444,261.642 ns | 116,136.9628 ns |  90,672.1014 ns |    470,933.526 ns |  0.44 |    0.09 |        - |        - |        - |     251 B |        0.15 |
| PointLookupBenchmarks | PreparedTypedBind              | ?          | ?     | 10000  |    141,945.968 ns |  32,197.0121 ns |  25,137.3092 ns |    140,808.915 ns |  0.14 |    0.02 |        - |        - |        - |     400 B |        0.24 |
| TraversalBenchmarks   | ObjectWithAllAttributes        | ?          | ?     | 10000  |    715,860.624 ns |  55,040.6023 ns |  42,972.0818 ns |    696,112.363 ns |  0.71 |    0.04 |        - |        - |        - |    1735 B |        1.06 |
| WriteBenchmarks       | SetInManagedTransaction        | ?          | ?     | 10000  |    508,320.255 ns | 111,967.6712 ns |  87,416.9928 ns |    511,985.611 ns |  0.51 |    0.08 |        - |        - |        - |     683 B |        0.42 |
| PointLookupBenchmarks | PreparedParametersObject       | ?          | ?     | 10000  |    136,068.167 ns |  44,206.8080 ns |  34,513.7679 ns |    129,318.601 ns |  0.14 |    0.03 |        - |        - |        - |     616 B |        0.38 |
| TraversalBenchmarks   | TwoHop                         | ?          | ?     | 10000  |  1,819,037.109 ns | 100,312.0997 ns |  78,317.0892 ns |  1,813,532.547 ns |  1.81 |    0.08 |        - |        - |        - |     456 B |        0.28 |
| WriteBenchmarks       | SetInRawTransaction            | ?          | ?     | 10000  |    547,992.434 ns | 142,948.5251 ns | 111,604.8057 ns |    561,471.018 ns |  0.55 |    0.11 |        - |        - |        - |     947 B |        0.58 |
| PointLookupBenchmarks | PreparedSelectScalar           | ?          | ?     | 10000  |    113,808.884 ns |  10,219.2459 ns |   7,978.5150 ns |    116,191.989 ns |  0.11 |    0.01 |        - |        - |        - |     952 B |        0.58 |
| WriteBenchmarks       | Batch100InOneTransaction       | ?          | ?     | 10000  |    332,334.198 ns |  83,239.1793 ns |  64,987.6760 ns |    335,466.999 ns |  0.33 |    0.06 |        - |        - |        - |     255 B |        0.16 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun      | ?          | ?     | 10000  |    190,804.188 ns |  80,351.1294 ns |  62,732.8765 ns |    171,031.990 ns |  0.19 |    0.06 |        - |        - |        - |     659 B |        0.40 |
| PointLookupBenchmarks | AttrByPrimaryKey               | ?          | ?     | 10000  |    117,501.308 ns |  18,638.8472 ns |  14,551.9859 ns |    114,520.621 ns |  0.12 |    0.01 |        - |        - |        - |     443 B |        0.27 |
| PointLookupBenchmarks | AttrByTraversalHop             | ?          | ?     | 10000  |    749,771.565 ns |  81,834.3151 ns |  63,890.8504 ns |    757,967.384 ns |  0.75 |    0.06 |        - |        - |        - |     400 B |        0.24 |
|                       |                                |            |       |        |                   |                 |                 |                   |       |         |          |          |          |           |             |
| **PointLookupBenchmarks** | **Interpolated**                   | **?**          | **?**     | **100000** |    **176,987.254 ns** |  **12,103.6462 ns** |   **9,449.7308 ns** |    **173,749.800 ns** |  **1.73** |    **0.11** |        **-** |        **-** |        **-** |     **512 B** |        **0.74** |
| PointLookupBenchmarks | ParametersObject               | ?          | ?     | 100000 |    102,386.988 ns |   5,000.8952 ns |   3,904.3700 ns |    103,570.813 ns |  1.00 |    0.05 |        - |        - |        - |     696 B |        1.00 |
| PointLookupBenchmarks | PreparedTypedBind              | ?          | ?     | 100000 |     96,894.700 ns |   6,427.2380 ns |   5,017.9647 ns |     94,821.923 ns |  0.95 |    0.06 |        - |        - |        - |     400 B |        0.57 |
| PointLookupBenchmarks | PreparedParametersObject       | ?          | ?     | 100000 |     99,841.632 ns |   6,993.9178 ns |   5,057.0608 ns |     99,517.652 ns |  0.98 |    0.06 |        - |        - |        - |     616 B |        0.89 |
| PointLookupBenchmarks | PreparedSelectScalar           | ?          | ?     | 100000 |     99,184.363 ns |   6,623.4833 ns |   4,381.0241 ns |     98,805.569 ns |  0.97 |    0.05 |        - |        - |        - |     952 B |        1.37 |
| PointLookupBenchmarks | PreparedTypedBind_TaskRun      | ?          | ?     | 100000 |    109,057.346 ns |   3,819.7841 ns |   2,982.2361 ns |    108,146.696 ns |  1.07 |    0.05 |        - |        - |        - |     672 B |        0.97 |
| PointLookupBenchmarks | AttrByPrimaryKey               | ?          | ?     | 100000 |    106,477.456 ns |   3,767.3925 ns |   2,724.0716 ns |    105,463.334 ns |  1.04 |    0.05 |        - |        - |        - |     447 B |        0.64 |
| PointLookupBenchmarks | AttrByTraversalHop             | ?          | ?     | 100000 |    757,589.129 ns |  30,967.1199 ns |  22,391.2566 ns |    751,844.967 ns |  7.41 |    0.35 |        - |        - |        - |     400 B |        0.57 |
