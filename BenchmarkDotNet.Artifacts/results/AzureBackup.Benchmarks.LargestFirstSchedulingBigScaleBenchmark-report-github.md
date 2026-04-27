```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                             | Workload             | Mean      | Error         | StdDev     | Ratio | RatioSD | Gen0        | Gen1        | Gen2        | Allocated | Alloc Ratio |
|----------------------------------- |--------------------- |----------:|--------------:|-----------:|------:|--------:|------------:|------------:|------------:|----------:|------------:|
| **&#39;Input-order (today&#39;s production)&#39;** | **huge-outlier-mixed**   |   **2.044 m** |       **2.126 m** |   **0.0047 m** |  **1.00** |    **0.00** |  **37000.0000** |  **32000.0000** |  **31000.0000** |  **32.38 GB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | huge-outlier-mixed   |   1.976 m |       3.913 m |   0.0087 m |  0.97 |    0.00 |  36000.0000 |  31000.0000 |  30000.0000 |  32.88 GB |        1.02 |
|                                    |                      |           |               |            |       |         |             |             |             |           |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **media-library-500**    | **279.992 m** | **175,284.973 m** | **389.3856 m** | **30.33** |   **59.32** | **233000.0000** | **158000.0000** | **113000.0000** | **258.82 GB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | media-library-500    |   5.072 m |      18.443 m |   0.0410 m |  0.55 |    0.62 | 342000.0000 | 273000.0000 | 223000.0000 | 258.82 GB |        1.00 |
|                                    |                      |           |               |            |       |         |             |             |             |           |             |
| **&#39;Input-order (today&#39;s production)&#39;** | **produ(...)-3000 [21]** |   **1.794 m** |      **26.084 m** |   **0.0579 m** |  **1.00** |    **0.04** | **122000.0000** |  **53000.0000** |  **24000.0000** |  **75.63 GB** |        **1.00** |
| &#39;Largest-first (LPT scheduling)&#39;   | produ(...)-3000 [21] |   1.751 m |      25.851 m |   0.0574 m |  0.98 |    0.04 | 141000.0000 |  79000.0000 |  46000.0000 |  75.76 GB |        1.00 |
