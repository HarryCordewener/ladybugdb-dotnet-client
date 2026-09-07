using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using LadybugDb.Client.Benchmarks.Workload;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// Two modes. <c>--workload</c> runs the MUSH latency-percentile harness (the .NET twin of
/// <c>benchmarks/workload_bench.py</c>, emitting JSON <c>benchmarks/report.py</c> can render);
/// anything else is handed to BenchmarkDotNet, e.g. <c>--filter '*PointLookup*'</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--workload")
        {
            return MushWorkload.Run(args.Skip(1).ToArray());
        }

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(12).WithId("Bench"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(StatisticColumn.Median)
            .WithOptions(ConfigOptions.JoinSummary);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }
}
