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
