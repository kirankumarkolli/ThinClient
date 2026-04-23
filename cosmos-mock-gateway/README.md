# Cosmos Mock Gateway

A lightweight Rust HTTPS server that mimics the Azure Cosmos DB Gateway (data-plane) just enough to satisfy `CosmosClient.ReadItemAsync()` through the .NET SDK. Designed for benchmarking SDK partition-routing at high partition counts (up to 50K+).

## What it does

Serves exactly **4 GET endpoints** — the minimal set the SDK calls during `new CosmosClient()` + `ReadItemAsync()`:

| # | Endpoint | Purpose |
|---|----------|---------|
| 1 | `GET /` | Account properties (called once on init) |
| 2 | `GET /dbs/{db}/colls/{coll}` | Container read |
| 3 | `GET /dbs/{rid}/colls/{rid}/pkranges` | Partition key range feed (supports `If-None-Match` → 304) |
| 4 | `GET /dbs/{db}/colls/{coll}/docs/{id}` | **ReadItem** (the primary use case) |

One test document is pre-seeded: partition key `"test-pk"`, id `"item-1"`.

---

## Prerequisites

### Rust toolchain

```powershell
# Install Rust (if not already installed)
winget install Rustlang.Rustup
# Or: https://rustup.rs

# Verify
rustup --version
cargo --version       # needs 1.70+
```

### MSVC build tools (Windows)

The `aws-lc-rs` crypto backend requires a C compiler. On Windows you need **Visual Studio Build Tools** (or full Visual Studio) with the **"Desktop development with C++"** workload.

Before building, set the MSVC environment variables in your PowerShell session:

```powershell
# Adjust paths to match your VS installation
$vsPath   = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise"  # or BuildTools, Community, etc.
$msvcVer  = "14.40.33807"                                                # find yours under VC\Tools\MSVC\
$sdkVer   = "10.0.22621.0"                                               # find yours under Windows Kits\10\Include\

$msvcPath  = "$vsPath\VC\Tools\MSVC\$msvcVer"
$sdkInclude = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib     = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"

# For x64:
$env:INCLUDE = "$msvcPath\include;$sdkInclude\ucrt;$sdkInclude\um;$sdkInclude\shared"
$env:LIB     = "$msvcPath\lib\x64;$sdkLib\ucrt\x64;$sdkLib\um\x64"
$env:PATH    = "$msvcPath\bin\Hostx64\x64;$env:PATH"

# For ARM64, replace x64 with arm64 and Hostx64 with Hostarm64 above.
```

> **Tip:** Alternatively, open a "Developer PowerShell for VS" which sets these automatically.

### .NET SDK (for the C# test client)

```powershell
dotnet --version      # needs 8.0+
```

---

## Build the mock server

```powershell
cd C:\src\cosmos-mock-gateway

# Debug build (faster compile, slower runtime)
cargo build

# Release build (slower compile, faster runtime — use for benchmarks)
cargo build --release
```

Binaries land in `target\debug\` or `target\release\`.

---

## Run the mock server

```powershell
# Start with 5 partitions (quick test)
.\target\release\cosmos-mock-gateway.exe --port 8901 --partitions 5

# Start with 50,000 partitions (benchmark scenario)
.\target\release\cosmos-mock-gateway.exe --port 8901 --partitions 50000

# All CLI options
.\target\release\cosmos-mock-gateway.exe --help
```

**CLI options:**

| Flag | Default | Description |
|------|---------|-------------|
| `--port` | `8901` | HTTPS listen port |
| `--partitions` | `1` | Number of partition key ranges to generate |
| `--db` | `testdb` | Database name |
| `--container` | `testcoll` | Container name |
| `--partition-key-path` | `/pk` | Partition key path |
| `--log-level` | `info` | Log level: `error`, `warn`, `info`, `debug`, `trace` |

The server generates a **self-signed TLS certificate** at startup (no files needed).

---

## Run the C# ReadItem test

With the mock server running in one terminal, open another and run:

```powershell
cd C:\src\cosmos-mock-e2e
dotnet run
```

**Expected output:**
```
=== Cosmos Mock Gateway E2E Test ===
Connecting to https://localhost:8901/...
Client created in ~100ms
Reading item (pk=test-pk, id=item-1)...
ReadItem succeeded in ~2000ms
  Status: OK
  RequestCharge: 1
  ActivityId: <guid>
  Item: { "id": "item-1", "pk": "test-pk", "data": "hello world", ... }

✅ E2E TEST PASSED
```

The test uses `ConnectionMode.Gateway` and bypasses TLS certificate validation (self-signed cert). It connects to `https://localhost:8901/`, creates a `CosmosClient`, and calls `ReadItemAsync("item-1", new PartitionKey("test-pk"))`.

---

## Quick smoke test with curl

You can also verify individual endpoints without the C# client:

```powershell
# Account properties
curl -sk https://localhost:8901/

# Container metadata
curl -sk https://localhost:8901/dbs/testdb/colls/testcoll

# Partition key ranges
curl -sk "https://localhost:8901/dbs/KwdHAA==/colls/KwdHANkV-KY=/pkranges"

# ReadItem
curl -sk "https://localhost:8901/dbs/testdb/colls/testcoll/docs/item-1" ^
  -H "x-ms-documentdb-partitionkey: [\"test-pk\"]"
```

---

## Project structure

```
cosmos-mock-gateway/
├── Cargo.toml                 # Dependencies and build config
├── README.md                  # This file
└── src/
    ├── main.rs                # CLI args, TLS cert gen, server startup
    ├── state.rs               # AppState (documents, PKRanges, RIDs)
    ├── partition.rs           # PKRange boundary generator (u128 arithmetic)
    ├── models.rs              # Serde structs for JSON responses
    ├── server.rs              # axum Router with TraceLayer middleware
    └── handlers/
        ├── mod.rs
        ├── account.rs         # GET /
        ├── container.rs       # GET /dbs/{db}/colls/{coll}
        ├── pkranges.rs        # GET /dbs/{rid}/colls/{rid}/pkranges
        └── document.rs        # GET /dbs/{db}/colls/{coll}/docs/{id}

cosmos-mock-e2e/
├── cosmos-mock-e2e.csproj     # .NET project with Microsoft.Azure.Cosmos NuGet
└── Program.cs                 # CosmosClient → ReadItemAsync test
```
