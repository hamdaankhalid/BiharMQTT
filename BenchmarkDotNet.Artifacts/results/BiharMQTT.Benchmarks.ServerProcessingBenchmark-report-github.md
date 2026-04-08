```

BenchmarkDotNet v0.15.4, macOS 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]    : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  .NET 10.0 : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=.NET 10.0  Runtime=.NET 10.0  

```
| Method                            | Mean     | Error    | StdDev   | Rank | Gen0     | Allocated |
|---------------------------------- |---------:|---------:|---------:|-----:|---------:|----------:|
| Publish_And_Deliver_1000_Messages | 61.81 ms | 1.195 ms | 1.467 ms |    1 | 750.0000 |   6.68 MB |
