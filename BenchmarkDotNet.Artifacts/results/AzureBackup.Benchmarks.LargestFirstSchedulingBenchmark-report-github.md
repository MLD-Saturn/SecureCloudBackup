```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-MVDJLW : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=1  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                             | Workload             | Mean        | Error | Ratio | Gen0       | Gen1      | Gen2      | Allocated   | Alloc Ratio |
|----------------------------------- |--------------------- |------------:|------:|------:|-----------:|----------:|----------:|------------:|------------:|
| **&#39;Input-order (today&#39;s production)&#39;** | **large-skew-100**       |  **5,001.4 ms** |    **NA** |  **1.00** |  **5000.0000** | **3000.0000** | **2000.0000** |  **1436.85 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | large-skew-100       |  4,221.0 ms |    NA |  0.84 |  4000.0000 | 3000.0000 | 2000.0000 |  1439.61 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **large-skew-200**       |  **5,275.7 ms** |    **NA** |  **1.00** |  **5000.0000** | **3000.0000** | **2000.0000** |  **2097.47 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | large-skew-200       |  3,977.2 ms |    NA |  0.75 |  5000.0000 | 3000.0000 | 2000.0000 |  2097.26 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **mixed-realistic-100**  |  **1,611.8 ms** |    **NA** |  **1.00** |  **1000.0000** | **1000.0000** | **1000.0000** |      **339 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | mixed-realistic-100  |  1,566.1 ms |    NA |  0.97 |  2000.0000 | 2000.0000 | 1000.0000 |   338.97 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **mixed-realistic-1000** |  **8,575.4 ms** |    **NA** |  **1.00** |  **8000.0000** | **4000.0000** | **3000.0000** |  **6526.17 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | mixed-realistic-1000 | 10,037.6 ms |    NA |  1.17 | 10000.0000 | 5000.0000 | 3000.0000 |  6525.82 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **realistic-large-200**  | **13,930.3 ms** |    **NA** |  **1.00** | **13000.0000** | **5000.0000** | **3000.0000** | **11297.84 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | realistic-large-200  | 16,045.1 ms |    NA |  1.15 | 14000.0000 | 6000.0000 | 4000.0000 | 11297.27 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **realistic-large-50**   |  **3,313.5 ms** |    **NA** |  **1.00** |  **3000.0000** | **2000.0000** | **2000.0000** |  **2797.33 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | realistic-large-50   |  3,305.6 ms |    NA |  1.00 |  3000.0000 | 2000.0000 | 2000.0000 |   2796.2 MB |        1.00 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **uniform-1MB-100**      |    **282.2 ms** |    **NA** |  **1.00** |  **1000.0000** | **1000.0000** | **1000.0000** |   **210.28 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | uniform-1MB-100      |    270.6 ms |    NA |  0.96 |  1000.0000 | 1000.0000 | 1000.0000 |   206.91 MB |        0.98 |
|                                    |                      |             |       |       |            |           |           |             |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **uniform-1MB-1000**     |  **2,831.3 ms** |    **NA** |  **1.00** |  **4000.0000** | **4000.0000** | **4000.0000** |  **2066.82 MB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | uniform-1MB-1000     |  2,830.8 ms |    NA |  1.00 |  4000.0000 | 4000.0000 | 4000.0000 |  2066.91 MB |        1.00 |
