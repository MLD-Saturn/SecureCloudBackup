```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-PSZTYW : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=5  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method  | TotalChunks | Backend | Mean         | Error       | StdDev     | Gen0         | Gen1        | Gen2       | Allocated      |
|-------- |------------ |-------- |-------------:|------------:|-----------:|-------------:|------------:|-----------:|---------------:|
| **Rebuild** | **10000**       | **LiteDB**  |    **404.67 ms** |    **66.07 ms** |  **17.159 ms** |   **53000.0000** |   **5000.0000** |  **3000.0000** |   **441230.77 KB** |
| **Rebuild** | **10000**       | **SQLite**  |     **81.18 ms** |    **11.64 ms** |   **3.023 ms** |            **-** |           **-** |          **-** |        **7.96 KB** |
| **Rebuild** | **100000**      | **LiteDB**  |  **5,213.67 ms** |   **390.07 ms** | **101.299 ms** |  **734000.0000** |  **17000.0000** |  **8000.0000** |  **5597372.05 KB** |
| **Rebuild** | **100000**      | **SQLite**  |    **707.76 ms** |    **56.49 ms** |   **8.742 ms** |            **-** |           **-** |          **-** |        **7.96 KB** |
| **Rebuild** | **500000**      | **LiteDB**  | **42,909.15 ms** | **1,522.37 ms** | **395.355 ms** | **4229000.0000** | **267000.0000** | **32000.0000** | **35425925.51 KB** |
| **Rebuild** | **500000**      | **SQLite**  |  **4,957.41 ms** |   **234.25 ms** |  **60.834 ms** |            **-** |           **-** |          **-** |        **7.96 KB** |
