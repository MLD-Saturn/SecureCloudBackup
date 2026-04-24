```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-HTFTLO : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  .NET 10.0  : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

Runtime=.NET 10.0  InvocationCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method                                           | Job        | Toolchain | IterationCount | RunStrategy | SimulatedLatencyMs | FileCount | SizeProfile     | Mean        | Error        | StdDev    | Gen0      | Gen1      | Gen2      | Allocated  |
|------------------------------------------------- |----------- |---------- |--------------- |------------ |------------------- |---------- |---------------- |------------:|-------------:|----------:|----------:|----------:|----------:|-----------:|
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **0**                  | **100**       | **large-skew**      |  **9,744.8 ms** |   **5,276.9 ms** | **289.24 ms** |         **-** |         **-** |         **-** |  **2547.5 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 0                  | 100       | large-skew      |  9,235.5 ms | 123,513.9 ms | 274.38 ms |         - |         - |         - | 2546.73 MB |
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **0**                  | **100**       | **mixed-realistic** |  **1,630.9 ms** |     **167.1 ms** |   **9.16 ms** | **1000.0000** | **1000.0000** | **1000.0000** |  **344.86 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 0                  | 100       | mixed-realistic |  1,613.3 ms |     566.1 ms |   1.26 ms | 1000.0000 | 1000.0000 | 1000.0000 |   338.9 MB |
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **0**                  | **100**       | **uniform-1MB**     |    **265.5 ms** |     **117.9 ms** |   **6.46 ms** | **1000.0000** | **1000.0000** | **1000.0000** |  **208.85 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 0                  | 100       | uniform-1MB     |    298.7 ms |   5,917.9 ms |  13.15 ms | 1000.0000 | 1000.0000 | 1000.0000 |   207.8 MB |
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **50**                 | **100**       | **large-skew**      | **11,670.0 ms** |     **643.3 ms** |  **35.26 ms** | **2000.0000** |         **-** |         **-** | **2546.92 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 50                 | 100       | large-skew      | 11,792.9 ms | 167,113.3 ms | 371.23 ms | 2000.0000 | 1000.0000 |         - | 2674.93 MB |
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **50**                 | **100**       | **mixed-realistic** |  **7,267.9 ms** |     **112.0 ms** |   **6.14 ms** | **2000.0000** | **1000.0000** | **1000.0000** |  **338.84 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 50                 | 100       | mixed-realistic |  7,287.2 ms |  13,709.7 ms |  30.46 ms | 3000.0000 | 1000.0000 | 1000.0000 |   338.9 MB |
| **&#39;End-to-end backup against in-memory blob store&#39;** | **Job-HTFTLO** | **Default**   | **3**              | **Throughput**  | **50**                 | **100**       | **uniform-1MB**     |  **1,830.6 ms** |     **652.5 ms** |  **35.76 ms** | **2000.0000** | **1000.0000** | **1000.0000** |  **206.93 MB** |
| &#39;End-to-end backup against in-memory blob store&#39; | .NET 10.0  | net10.0   | 2              | Default     | 50                 | 100       | uniform-1MB     |  1,830.2 ms |     602.2 ms |   1.34 ms | 2000.0000 | 1000.0000 | 1000.0000 |  207.19 MB |
