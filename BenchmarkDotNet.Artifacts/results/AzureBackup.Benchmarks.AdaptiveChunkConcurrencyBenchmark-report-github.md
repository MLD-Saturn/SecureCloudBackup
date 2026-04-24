```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-ADVOXT : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=1  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=0  

```
| Method                                              | ChunkConcurrency | Workload             | Mean        | Error | Gen0       | Gen1      | Gen2      | Allocated   |
|---------------------------------------------------- |----------------- |--------------------- |------------:|------:|-----------:|----------:|----------:|------------:|
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **large-skew-100**       |  **4,961.7 ms** |    **NA** |  **5000.0000** | **3000.0000** | **2000.0000** |  **1434.12 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **large-skew-200**       |  **5,402.8 ms** |    **NA** |  **5000.0000** | **3000.0000** | **2000.0000** |  **2097.69 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **mixed-realistic-100**  |  **1,599.4 ms** |    **NA** |  **2000.0000** | **2000.0000** | **1000.0000** |   **339.04 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **mixed-realistic-1000** |  **8,539.9 ms** |    **NA** |  **9000.0000** | **4000.0000** | **3000.0000** |  **6524.58 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **realistic-large-200**  | **14,869.0 ms** |    **NA** | **15000.0000** | **6000.0000** | **4000.0000** | **11298.76 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **realistic-large-50**   |  **3,602.6 ms** |    **NA** |  **3000.0000** | **2000.0000** | **2000.0000** |  **2796.24 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **uniform-1MB-100**      |    **284.4 ms** |    **NA** |  **1000.0000** | **1000.0000** | **1000.0000** |   **206.73 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **uniform-1MB-1000**     |  **2,990.2 ms** |    **NA** |  **4000.0000** | **4000.0000** | **4000.0000** |  **2066.13 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **large-skew-100**       |  **4,943.2 ms** |    **NA** |  **5000.0000** | **3000.0000** | **2000.0000** |  **1438.74 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **large-skew-200**       |  **5,516.8 ms** |    **NA** |  **5000.0000** | **3000.0000** | **2000.0000** |  **2097.61 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **mixed-realistic-100**  |  **1,765.4 ms** |    **NA** |  **2000.0000** | **2000.0000** | **1000.0000** |   **339.54 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **mixed-realistic-1000** |  **9,432.7 ms** |    **NA** |  **9000.0000** | **4000.0000** | **3000.0000** |   **6530.4 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **realistic-large-200**  | **15,699.3 ms** |    **NA** | **13000.0000** | **5000.0000** | **3000.0000** | **11298.01 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **realistic-large-50**   |  **3,350.2 ms** |    **NA** |  **3000.0000** | **2000.0000** | **2000.0000** |  **2799.13 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **uniform-1MB-100**      |    **277.8 ms** |    **NA** |  **1000.0000** | **1000.0000** | **1000.0000** |   **206.84 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **uniform-1MB-1000**     |  **2,824.4 ms** |    **NA** |  **4000.0000** | **4000.0000** | **4000.0000** |  **2066.87 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **large-skew-100**       |  **4,977.7 ms** |    **NA** |  **4000.0000** | **2000.0000** | **1000.0000** |  **1435.86 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **large-skew-200**       |  **5,564.3 ms** |    **NA** |  **6000.0000** | **3000.0000** | **2000.0000** |   **2100.3 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **mixed-realistic-100**  |  **1,630.0 ms** |    **NA** |  **2000.0000** | **2000.0000** | **1000.0000** |   **346.12 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **mixed-realistic-1000** |  **9,665.1 ms** |    **NA** |  **9000.0000** | **4000.0000** | **2000.0000** |  **6536.74 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **realistic-large-200**  | **14,120.1 ms** |    **NA** | **13000.0000** | **5000.0000** | **3000.0000** | **11307.85 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **realistic-large-50**   |  **3,511.6 ms** |    **NA** |  **3000.0000** | **3000.0000** | **2000.0000** |  **2798.92 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **uniform-1MB-100**      |    **298.2 ms** |    **NA** |  **1000.0000** | **1000.0000** | **1000.0000** |   **210.24 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **uniform-1MB-1000**     |  **2,908.8 ms** |    **NA** |  **4000.0000** | **4000.0000** | **4000.0000** |   **2070.4 MB** |
