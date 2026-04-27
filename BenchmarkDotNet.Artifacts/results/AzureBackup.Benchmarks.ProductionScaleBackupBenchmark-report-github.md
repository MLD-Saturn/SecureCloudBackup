```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                                       | Workload             | Mean    | Error   | StdDev   | Gen0        | Gen1        | Gen2       | Allocated |
|------------------------------------------------------------- |--------------------- |--------:|--------:|---------:|------------:|------------:|-----------:|----------:|
| **&#39;Production-scale backup, 16 GB memory budget, no overrides&#39;** | **huge-outlier-mixed**   | **2.036 m** | **1.842 m** | **0.0041 m** |  **33000.0000** |  **28000.0000** | **27000.0000** |  **32.76 GB** |
| **&#39;Production-scale backup, 16 GB memory budget, no overrides&#39;** | **media-library-500**    | **4.764 m** | **8.401 m** | **0.0187 m** | **202000.0000** | **129000.0000** | **82000.0000** | **256.38 GB** |
| **&#39;Production-scale backup, 16 GB memory budget, no overrides&#39;** | **produ(...)-3000 [21]** | **1.653 m** | **2.080 m** | **0.0046 m** | **130000.0000** |  **61000.0000** | **33000.0000** |  **75.13 GB** |
