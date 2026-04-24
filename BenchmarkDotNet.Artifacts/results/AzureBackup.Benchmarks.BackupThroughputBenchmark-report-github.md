```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                       | Workload             | Mean        | Error         | StdDev    | Gen0       | Gen1      | Gen2      | Allocated   |
|--------------------------------------------- |--------------------- |------------:|--------------:|----------:|-----------:|----------:|----------:|------------:|
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-100**       |  **4,940.5 ms** |  **90,092.29 ms** | **200.13 ms** |  **4000.0000** | **3000.0000** | **2000.0000** |  **1433.48 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-200**       |  **5,525.1 ms** |   **3,830.09 ms** |   **8.51 ms** |  **4000.0000** | **3000.0000** | **2000.0000** |  **2097.91 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-100**  |  **1,571.6 ms** |   **4,841.52 ms** |  **10.76 ms** |  **1000.0000** | **1000.0000** | **1000.0000** |   **339.03 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-1000** |  **9,499.9 ms** |  **19,917.62 ms** |  **44.25 ms** |  **8000.0000** | **4000.0000** | **3000.0000** |  **6524.89 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-200**  | **15,178.9 ms** | **163,134.99 ms** | **362.40 ms** | **15000.0000** | **6000.0000** | **3000.0000** | **11295.98 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-50**   |  **3,259.8 ms** |  **20,440.57 ms** |  **45.41 ms** |  **3000.0000** | **2000.0000** | **2000.0000** |  **2796.18 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-100**      |    **290.2 ms** |   **2,781.49 ms** |   **6.18 ms** |  **1000.0000** | **1000.0000** | **1000.0000** |   **207.11 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-1000**     |  **2,785.8 ms** |      **56.53 ms** |   **0.13 ms** |  **4000.0000** | **4000.0000** | **4000.0000** |  **2066.68 MB** |
