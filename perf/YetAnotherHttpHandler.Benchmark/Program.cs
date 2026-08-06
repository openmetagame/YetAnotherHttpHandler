using BenchmarkDotNet.Running;
using YahaBenchmark;

// Usage:
//   dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --filter '*'
//   dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --filter '*SmallResponse*'
//   dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --list flat
//
// See README.md in this directory for what each benchmark measures and how to read the results.
BenchmarkSwitcher.FromAssembly(typeof(SmallResponseBenchmark).Assembly).Run(args);
