``` ini

BenchmarkDotNet=v0.13.5, OS=Windows 11 (10.0.26200.8246)
Unknown processor
.NET SDK=10.0.202
  [Host]     : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD
  Job-LHGVOH : .NET 8.0.26 (8.0.2626.16921), Arm64 RyuJIT AdvSIMD

InvocationCount=25994  UnrollFactor=1


```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 16.87 us | 0.333 us | 0.657 us | 17.81 us | 18.04 us | 18.29 us | 6.5400 |  26.73 KB |
