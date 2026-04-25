``` ini

BenchmarkDotNet=v0.13.5, OS=Windows 11 (10.0.26200.8246)
Unknown processor
.NET SDK=10.0.202
  [Host]     : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD
  Job-RBYSVL : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD

InvocationCount=25994  UnrollFactor=1


```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 18.21 us | 0.357 us | 0.704 us | 18.81 us | 19.16 us | 19.41 us | 6.5400 |  26.74 KB |
