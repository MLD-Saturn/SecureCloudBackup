```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                              | ChunkConcurrency | Workload             | Mean        | Error        | StdDev      | Gen0       | Gen1       | Gen2      | Allocated   |
|---------------------------------------------------- |----------------- |--------------------- |------------:|-------------:|------------:|-----------:|-----------:|----------:|------------:|
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **large-skew-100**       |  **3,682.1 ms** |  **16,981.8 ms** |    **37.72 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |  **1433.65 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **large-skew-200**       |  **4,308.5 ms** |  **16,323.8 ms** |    **36.26 ms** |  **8000.0000** |  **7000.0000** | **3000.0000** |   **2097.1 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **mixed-realistic-100**  |  **1,252.0 ms** |   **3,376.3 ms** |     **7.50 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **338.39 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **mixed-realistic-1000** |  **8,243.3 ms** | **115,827.9 ms** |   **257.31 ms** | **25000.0000** | **24000.0000** | **5000.0000** |  **6526.51 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **realistic-large-200**  | **14,383.3 ms** | **281,976.5 ms** |   **626.39 ms** | **34000.0000** | **33000.0000** | **5000.0000** | **11299.96 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **realistic-large-50**   |  **3,215.1 ms** |  **32,923.4 ms** |    **73.14 ms** | **11000.0000** | **10000.0000** | **4000.0000** |  **2797.58 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **uniform-1MB-100**      |    **287.4 ms** |   **8,923.3 ms** |    **19.82 ms** |  **1000.0000** |  **1000.0000** | **1000.0000** |    **206.6 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **4**                | **uniform-1MB-1000**     |  **3,100.6 ms** |  **15,559.8 ms** |    **34.57 ms** | **11000.0000** |  **8000.0000** | **5000.0000** |  **2066.36 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **large-skew-100**       |  **3,765.1 ms** |  **34,792.3 ms** |    **77.29 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |   **1434.6 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **large-skew-200**       |  **4,526.5 ms** | **175,457.7 ms** |   **389.77 ms** |  **8000.0000** |  **7000.0000** | **3000.0000** |  **2098.32 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **mixed-realistic-100**  |  **1,218.4 ms** |   **2,018.4 ms** |     **4.48 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |    **338.9 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **mixed-realistic-1000** |  **8,060.8 ms** | **163,637.8 ms** |   **363.51 ms** | **23000.0000** | **22000.0000** | **3000.0000** |  **6531.95 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **realistic-large-200**  | **13,810.9 ms** | **455,898.0 ms** | **1,012.75 ms** | **34000.0000** | **33000.0000** | **5000.0000** |  **11310.4 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **realistic-large-50**   |  **2,854.0 ms** |  **65,008.5 ms** |   **144.41 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |   **2799.2 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **uniform-1MB-100**      |    **283.6 ms** |  **10,494.3 ms** |    **23.31 ms** |  **1000.0000** |  **1000.0000** | **1000.0000** |   **207.01 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **6**                | **uniform-1MB-1000**     |  **3,027.4 ms** |  **24,257.8 ms** |    **53.89 ms** | **11000.0000** |  **8000.0000** | **5000.0000** |  **2067.95 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **large-skew-100**       |  **3,785.5 ms** |   **7,336.0 ms** |    **16.30 ms** |  **5000.0000** |  **4000.0000** | **2000.0000** |  **1437.17 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **large-skew-200**       |  **4,260.3 ms** |  **21,751.7 ms** |    **48.32 ms** |  **8000.0000** |  **7000.0000** | **3000.0000** |  **2102.23 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **mixed-realistic-100**  |  **1,252.0 ms** |   **2,321.6 ms** |     **5.16 ms** |  **2000.0000** |  **1000.0000** | **1000.0000** |   **339.66 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **mixed-realistic-1000** |  **8,272.0 ms** |  **74,885.2 ms** |   **166.35 ms** | **26000.0000** | **25000.0000** | **5000.0000** |  **6545.79 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **realistic-large-200**  | **13,659.7 ms** | **190,392.4 ms** |   **422.95 ms** | **35000.0000** | **34000.0000** | **5000.0000** | **11326.18 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **realistic-large-50**   |  **2,996.5 ms** |  **49,288.5 ms** |   **109.49 ms** | **10000.0000** |  **9000.0000** | **3000.0000** |  **2804.04 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **uniform-1MB-100**      |    **288.1 ms** |   **2,149.1 ms** |     **4.77 ms** |  **1000.0000** |  **1000.0000** | **1000.0000** |   **207.24 MB** |
| **&#39;End-to-end backup at parametric chunk concurrency&#39;** | **12**               | **uniform-1MB-1000**     |  **3,012.2 ms** |  **39,367.4 ms** |    **87.45 ms** | **11000.0000** |  **8000.0000** | **5000.0000** |  **2076.95 MB** |
