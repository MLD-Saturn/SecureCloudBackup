```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                            | FileConcurrency | Workload             | Mean    | Error     | StdDev   | Gen0        | Gen1        | Gen2       | Allocated |
|-------------------------------------------------- |---------------- |--------------------- |--------:|----------:|---------:|------------:|------------:|-----------:|----------:|
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **8**               | **huge-outlier-mixed**   | **2.056 m** |  **0.1143 m** | **0.0003 m** |  **34000.0000** |  **30000.0000** | **28000.0000** |   **32.5 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **8**               | **media-library-500**    | **4.721 m** | **22.2826 m** | **0.0495 m** | **205000.0000** | **130000.0000** | **85000.0000** | **258.52 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **8**               | **produ(...)-3000 [21]** | **1.726 m** | **44.0305 m** | **0.0978 m** | **121000.0000** |  **52000.0000** | **24000.0000** |  **75.24 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **16**              | **huge-outlier-mixed**   | **2.044 m** |  **5.2216 m** | **0.0116 m** |  **38000.0000** |  **33000.0000** | **32000.0000** |  **32.52 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **16**              | **media-library-500**    | **3.431 m** |  **4.2125 m** | **0.0094 m** | **186000.0000** | **109000.0000** | **66000.0000** | **256.25 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **16**              | **produ(...)-3000 [21]** | **1.350 m** |  **4.2285 m** | **0.0094 m** | **124000.0000** |  **58000.0000** | **27000.0000** |  **75.24 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **32**              | **huge-outlier-mixed**   | **2.024 m** |  **3.2195 m** | **0.0072 m** |  **35000.0000** |  **31000.0000** | **30000.0000** |  **32.68 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **32**              | **media-library-500**    | **3.481 m** | **11.1505 m** | **0.0248 m** | **158000.0000** |  **82000.0000** | **42000.0000** | **256.59 GB** |
| **&#39;Big-scale backup at parametric file concurrency&#39;** | **32**              | **produ(...)-3000 [21]** | **1.287 m** |  **7.3818 m** | **0.0164 m** | **116000.0000** |  **53000.0000** | **20000.0000** |     **76 GB** |
