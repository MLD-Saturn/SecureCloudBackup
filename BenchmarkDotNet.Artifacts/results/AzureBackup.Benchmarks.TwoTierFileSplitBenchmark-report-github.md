```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-ADVOXT : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=1  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=0  

```
| Method                                             | FileConcurrency | Workload             | Mean        | Error | Gen0       | Gen1      | Gen2      | Allocated   |
|--------------------------------------------------- |---------------- |--------------------- |------------:|------:|-----------:|----------:|----------:|------------:|
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **large-skew-100**       |  **4,807.9 ms** |    **NA** |  **4000.0000** | **3000.0000** | **2000.0000** |   **1444.2 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **large-skew-200**       |  **5,331.3 ms** |    **NA** |  **4000.0000** | **3000.0000** | **2000.0000** |  **2100.05 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **mixed-realistic-100**  |  **1,675.8 ms** |    **NA** |  **2000.0000** | **2000.0000** | **1000.0000** |    **339.3 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **mixed-realistic-1000** |  **9,189.2 ms** |    **NA** | **10000.0000** | **5000.0000** | **3000.0000** |  **6527.08 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **realistic-large-200**  | **14,586.5 ms** |    **NA** | **15000.0000** | **6000.0000** | **3000.0000** | **11298.15 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **realistic-large-50**   |  **3,378.2 ms** |    **NA** |  **3000.0000** | **2000.0000** | **2000.0000** |   **2796.4 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **uniform-1MB-100**      |    **274.4 ms** |    **NA** |  **1000.0000** | **1000.0000** | **1000.0000** |   **206.81 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **uniform-1MB-1000**     |  **2,898.4 ms** |    **NA** |  **4000.0000** | **4000.0000** | **4000.0000** |  **2066.76 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **large-skew-100**       |  **4,847.3 ms** |    **NA** |  **4000.0000** | **2000.0000** | **1000.0000** |   **1439.7 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **large-skew-200**       |  **5,243.7 ms** |    **NA** |  **4000.0000** | **3000.0000** | **2000.0000** |  **2096.45 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **mixed-realistic-100**  |  **1,740.6 ms** |    **NA** |  **2000.0000** | **2000.0000** | **1000.0000** |   **340.14 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **mixed-realistic-1000** |  **8,719.4 ms** |    **NA** |  **8000.0000** | **4000.0000** | **2000.0000** |  **6522.99 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **realistic-large-200**  | **14,187.8 ms** |    **NA** | **13000.0000** | **5000.0000** | **3000.0000** | **11290.85 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **realistic-large-50**   |  **3,477.2 ms** |    **NA** |  **3000.0000** | **3000.0000** | **2000.0000** |  **2795.08 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **uniform-1MB-100**      |    **271.7 ms** |    **NA** |          **-** |         **-** |         **-** |   **207.61 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **uniform-1MB-1000**     |  **2,388.5 ms** |    **NA** |  **4000.0000** | **3000.0000** | **3000.0000** |  **2066.99 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **large-skew-100**       |  **5,298.7 ms** |    **NA** |  **4000.0000** | **2000.0000** | **1000.0000** |   **1436.3 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **large-skew-200**       |  **5,255.5 ms** |    **NA** |  **4000.0000** | **3000.0000** | **2000.0000** |  **2096.99 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **mixed-realistic-100**  |  **1,635.1 ms** |    **NA** |  **1000.0000** | **1000.0000** | **1000.0000** |   **340.22 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **mixed-realistic-1000** |  **8,643.0 ms** |    **NA** |  **8000.0000** | **4000.0000** | **2000.0000** |  **6544.34 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **realistic-large-200**  | **13,337.4 ms** |    **NA** | **12000.0000** | **4000.0000** | **2000.0000** | **11291.12 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **realistic-large-50**   |  **3,213.6 ms** |    **NA** |  **3000.0000** | **2000.0000** | **1000.0000** |  **2808.99 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **uniform-1MB-100**      |    **240.0 ms** |    **NA** |          **-** |         **-** |         **-** |   **208.78 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **uniform-1MB-1000**     |  **2,470.0 ms** |    **NA** |  **3000.0000** | **2000.0000** | **2000.0000** |  **2074.43 MB** |
