``` ini

BenchmarkDotNet=v0.13.5, OS=Windows 11 (10.0.26200.8246)
Unknown processor
.NET SDK=10.0.202
  [Host]     : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD


```
|         Method |     Mean |    Error |   StdDev |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 19.90 us | 0.784 us | 2.173 us | 6.3477 |  26.73 KB |
