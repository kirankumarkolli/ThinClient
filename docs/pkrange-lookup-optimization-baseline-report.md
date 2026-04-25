``` ini

BenchmarkDotNet=v0.13.5, OS=Windows 11 (10.0.26200.8246)
Unknown processor
.NET SDK=10.0.202
  [Host]     : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD


```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 23.57 us | 1.135 us | 3.293 us | 27.42 us | 29.92 us | 32.53 us | 6.3477 |  27.05 KB |
