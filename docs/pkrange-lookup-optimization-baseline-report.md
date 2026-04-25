``` ini

BenchmarkDotNet=v0.13.5, OS=Windows 11 (10.0.26200.8246)
Unknown processor
.NET SDK=10.0.202
  [Host]     : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD


```
|         Method |     Mean |    Error |   StdDev |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 13.86 us | 0.223 us | 0.209 us | 6.4392 |  26.34 KB |
