```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                                   | Workload          | MemoryLimitParam | Mean    | Error     | StdDev   | Gen0        | Gen1        | Gen2        | Allocated |
|--------------------------------------------------------- |------------------ |----------------- |--------:|----------:|---------:|------------:|------------:|------------:|----------:|
| **&#39;Backup on media-library-500 at parametric MemoryBudget&#39;** | **media-library-500** | **0**                | **4.937 m** | **69.7621 m** | **0.1550 m** | **225000.0000** | **149000.0000** | **105000.0000** | **258.54 GB** |
| **&#39;Backup on media-library-500 at parametric MemoryBudget&#39;** | **media-library-500** | **4096**             | **4.726 m** |  **0.2185 m** | **0.0005 m** | **211000.0000** | **133000.0000** |  **91000.0000** | **258.27 GB** |
| **&#39;Backup on media-library-500 at parametric MemoryBudget&#39;** | **media-library-500** | **8192**             | **4.744 m** | **20.1477 m** | **0.0448 m** | **215000.0000** | **136000.0000** |  **95000.0000** | **257.39 GB** |
| **&#39;Backup on media-library-500 at parametric MemoryBudget&#39;** | **media-library-500** | **16384**            | **4.721 m** | **10.9402 m** | **0.0243 m** | **210000.0000** | **136000.0000** |  **90000.0000** |  **257.4 GB** |
