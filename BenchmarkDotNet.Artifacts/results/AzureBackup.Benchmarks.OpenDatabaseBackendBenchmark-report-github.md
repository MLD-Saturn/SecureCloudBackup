```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-CARYJT : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=5  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=2  

```
| Method         | Backend | Mean      | Error    | StdDev   | Gen0      | Gen1      | Gen2      | Allocated |
|--------------- |-------- |----------:|---------:|---------:|----------:|----------:|----------:|----------:|
| **OpenAndDispose** | **LiteDB**  |  **99.00 ms** | **5.162 ms** | **1.341 ms** | **2000.0000** | **2000.0000** | **2000.0000** |     **66 MB** |
| **OpenAndDispose** | **SQLite**  | **484.98 ms** | **5.192 ms** | **1.348 ms** | **2000.0000** | **2000.0000** | **2000.0000** |  **64.77 MB** |
