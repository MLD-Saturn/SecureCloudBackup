```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                       | Workload             | Mean        | Error       | StdDev   | Gen0       | Gen1       | Gen2      | Allocated   |
|--------------------------------------------- |--------------------- |------------:|------------:|---------:|-----------:|-----------:|----------:|------------:|
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-100**       |  **3,542.6 ms** | **11,436.3 ms** | **25.41 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |  **1434.38 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-200**       |  **4,100.2 ms** | **25,531.3 ms** | **56.72 ms** |  **8000.0000** |  **7000.0000** | **3000.0000** |  **2098.39 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-100**  |  **1,217.0 ms** |  **2,832.2 ms** |  **6.29 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **339.82 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-1000** |  **7,370.6 ms** | **25,363.1 ms** | **56.34 ms** | **25000.0000** | **24000.0000** | **5000.0000** |  **6530.62 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-200**  | **12,346.3 ms** | **37,391.9 ms** | **83.06 ms** | **34000.0000** | **33000.0000** | **5000.0000** | **11306.83 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-50**   |  **2,724.2 ms** |    **934.6 ms** |  **2.08 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |  **2799.23 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-100**      |    **276.9 ms** |  **6,135.3 ms** | **13.63 ms** |  **1000.0000** |  **1000.0000** | **1000.0000** |   **206.72 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-1000**     |  **2,909.6 ms** | **27,250.1 ms** | **60.53 ms** | **11000.0000** |  **8000.0000** | **5000.0000** |  **2067.96 MB** |
