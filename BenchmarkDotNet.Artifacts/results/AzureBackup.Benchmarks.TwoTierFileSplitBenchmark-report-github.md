```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                             | FileConcurrency | Workload             | Mean        | Error        | StdDev      | Gen0       | Gen1       | Gen2      | Allocated   |
|--------------------------------------------------- |---------------- |--------------------- |------------:|-------------:|------------:|-----------:|-----------:|----------:|------------:|
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **large-skew-100**       |  **3,568.8 ms** |   **3,546.5 ms** |     **7.88 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |  **1434.55 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **large-skew-200**       |  **4,119.5 ms** |  **26,357.4 ms** |    **58.55 ms** |  **8000.0000** |  **7000.0000** | **3000.0000** |  **2097.76 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **mixed-realistic-100**  |  **1,201.3 ms** |   **1,909.6 ms** |     **4.24 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **338.65 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **mixed-realistic-1000** |  **7,530.6 ms** |  **13,860.5 ms** |    **30.79 ms** | **25000.0000** | **24000.0000** | **5000.0000** |  **6530.54 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **realistic-large-200**  | **12,431.6 ms** | **123,815.1 ms** |   **275.05 ms** | **34000.0000** | **33000.0000** | **5000.0000** | **11316.57 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **realistic-large-50**   |  **2,677.1 ms** |  **20,733.2 ms** |    **46.06 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |  **2799.25 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **uniform-1MB-100**      |    **255.0 ms** |   **3,723.1 ms** |     **8.27 ms** |  **1000.0000** |  **1000.0000** | **1000.0000** |   **206.88 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **8**               | **uniform-1MB-1000**     |  **2,924.7 ms** |  **17,788.2 ms** |    **39.52 ms** | **11000.0000** |  **8000.0000** | **5000.0000** |  **2068.04 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **large-skew-100**       |  **3,466.8 ms** |     **509.0 ms** |     **1.13 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |   **1434.3 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **large-skew-200**       |  **3,797.6 ms** |   **1,488.8 ms** |     **3.31 ms** |  **7000.0000** |  **6000.0000** | **2000.0000** |  **2097.93 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **mixed-realistic-100**  |  **1,208.9 ms** |   **4,432.6 ms** |     **9.85 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **338.78 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **mixed-realistic-1000** |  **5,953.1 ms** |  **43,472.7 ms** |    **96.57 ms** | **25000.0000** | **24000.0000** | **5000.0000** |  **6529.61 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **realistic-large-200**  | **12,225.6 ms** | **498,560.8 ms** | **1,107.52 ms** | **34000.0000** | **33000.0000** | **5000.0000** | **11301.86 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **realistic-large-50**   |  **2,293.9 ms** |   **2,684.2 ms** |     **5.96 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |   **2797.3 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **uniform-1MB-100**      |    **247.1 ms** |  **11,654.6 ms** |    **25.89 ms** |          **-** |          **-** |         **-** |   **206.58 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **16**              | **uniform-1MB-1000**     |  **2,697.6 ms** |   **9,807.2 ms** |    **21.79 ms** | **10000.0000** |  **7000.0000** | **4000.0000** |  **2067.47 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **large-skew-100**       |  **3,528.8 ms** |   **5,920.7 ms** |    **13.15 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |  **1433.87 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **large-skew-200**       |  **3,982.6 ms** |  **13,648.3 ms** |    **30.32 ms** |  **7000.0000** |  **6000.0000** | **2000.0000** |  **2097.28 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **mixed-realistic-100**  |  **1,249.9 ms** |   **2,415.4 ms** |     **5.37 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **338.66 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **mixed-realistic-1000** |  **6,166.9 ms** |  **24,831.8 ms** |    **55.16 ms** | **23000.0000** | **22000.0000** | **3000.0000** |  **6526.86 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **realistic-large-200**  | **10,969.1 ms** |  **49,409.9 ms** |   **109.76 ms** | **34000.0000** | **33000.0000** | **5000.0000** | **11298.23 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **realistic-large-50**   |  **2,293.0 ms** |   **2,095.4 ms** |     **4.65 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |  **2795.69 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **uniform-1MB-100**      |    **249.4 ms** |  **12,131.5 ms** |    **26.95 ms** |          **-** |          **-** |         **-** |   **206.62 MB** |
| **&#39;End-to-end backup at parametric file concurrency&#39;** | **32**              | **uniform-1MB-1000**     |  **2,601.3 ms** |  **27,149.6 ms** |    **60.31 ms** |  **9000.0000** |  **7000.0000** | **3000.0000** |  **2066.55 MB** |
