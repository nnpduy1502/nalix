# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness in one session. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a shared desktop machine over Windows loopback. Absolute values are much lower than on bare metal with dedicated network hardware. Use them to *compare the stacks with each other*, not as absolute capacity figures.

## Summary

!!! note "All 12 libraries measured in a single session"
    All 12 frameworks and baselines below were measured alongside each other on 2026-10-02 in one uninterrupted session under .NET 10.0.12 (SDK 10.0.401). Every comparison reflects verified head-to-head results run under identical hardware and OS conditions.

- **Single-client latency:** Nalix TCP achieves **63.4 µs** p50 at 32 B, outperforming all other application-layer frameworks: gRPC duplex (79.7 µs), SignalR (83.6 µs), MagicOnion hub (84.4 µs), gRPC duplex TLS (91.8 µs), gRPC unary (103.4 µs), and MagicOnion unary (108.6 µs). At 1 KB, Nalix TCP achieves **72.6 µs** p50, also leading gRPC duplex (74.2 µs), SignalR (87.2 µs), and MagicOnion hub (90.2 µs).
- **Server allocations:** **56 B** per message at 32 B for Nalix TCP and Nalix AEAD, the lowest of all full frameworks (MagicOnion hub 200 B, gRPC duplex 264 B, SignalR 672 B, gRPC duplex TLS 869 B, gRPC unary 968 B, MagicOnion unary 1,432 B, gRPC unary TLS 1,573 B) — saving >53% heap allocations following PR #401 zero-alloc fast-paths and pooled dispatch sessions. At 1 KB, Nalix TCP and AEAD allocate **1,048 B** (vs 1,192 B for MagicOnion hub, 1,256 B for gRPC duplex, 1,664 B for SignalR, and 1,961 B for gRPC unary).
- **GC Pressure:** At 32 B, Nalix TCP triggered only **1** Gen0 collection per 8-second run and Nalix AEAD had **0** Gen0 collections, compared to 65 for SignalR, 45 for gRPC duplex, 75 for gRPC unary, and 118 for MagicOnion unary.
- **Encryption:** Nalix AEAD (X25519 + ChaCha20-Poly1305) outperforms gRPC duplex TLS in latency at both payload sizes (32 B p50 **75.6 µs** vs 91.8 µs; 1 KB **95.0 µs** vs 97.1 µs) while using a fraction of the server allocations (**56 B** vs 869 B at 32 B; **1,048 B** vs 1,862 B at 1 KB).

## Environment

| Item | Value |
|:--|:--|
| CPU | 13th Gen Intel Core i7-13620H @ 2.40 GHz (10 physical cores, 16 logical cores) |
| Memory | 16 GB |
| OS | Windows 11 (10.0.26300) |
| Runtime | .NET 10.0.12 (SDK 10.0.401), Release, x64 RyuJIT, TieredPGO on |
| GC | Server GC + Concurrent GC for **every** process (from `benchmarks/Directory.Build.props`) |
| Nalix | source at `master` (`Version.props` 14.2.16), project references |
| ASP.NET Core / SignalR | 10.0.12 (`Microsoft.AspNetCore.SignalR.Client`, `.Protocols.MessagePack` → MessagePack 2.5.302) |
| gRPC | `Grpc.AspNetCore` 2.84.0, `Grpc.Net.Client` 2.84.0, Google.Protobuf 3.35.1 |
| MagicOnion | `MagicOnion.Server` / `.Client` 7.11.0 (MessagePack 3.1.7) |
| Date | 2026-10-02 (Refreshed for PR #401 zero-alloc send pipeline optimizations) |
| Machine state | Clean idle state before the run; one uninterrupted session executing all 12 libraries |

## Methodology

Harness: `benchmarks/Nalix.Comparison.Benchmarks` (runner `benchmarks/run-comparison.ps1` / `run-comparison.sh`). MagicOnion is in a
separate executable, `benchmarks/Nalix.Comparison.MagicOnion`, because it needs MessagePack v3 while the SignalR
MessagePack protocol is built against v2. Both executables share the same harness source file.

- **Topology:** the driver starts the library's **server as a separate child process** on `127.0.0.1`,
  then runs the clients inside the driver process. The server process reports its own
  `GC.GetTotalAllocatedBytes(precise: true)`, Gen0/1/2 collection counts and process CPU time between
  `reset` and `report` commands sent over stdin. So the allocation and CPU numbers are server-only, and they cover the
  **whole process** (I/O threads, timers and background tasks included).
- **Workload:** echo. The client sends an opaque byte payload of **32 B** or **1024 B**, and the server returns the same
  bytes in a response message. Each library uses its idiomatic request/response API:

| Label | What runs |
|:--|:--|
| `raw-tcp` | Baseline: hand-written `Socket` echo with a 4-byte length prefix. No framework. |
| `kestrel-ws` | Baseline: ASP.NET Core `UseWebSockets` binary echo, `ClientWebSocket` client. |
| `nalix-tcp` | Nalix `ListenTcp<DefaultProtocol>` with a `[PacketHandler]` that echoes via a pooled response (`PacketFactory<T>.Acquire()` + `context.Sender.SendAsync`). The client uses `TcpSession.RequestAsync<T>`. **Default options**. |
| `nalix-ws` | Same as above over `ListenWebSocket`, with a `WebSocketSession` client. |
| `nalix-tcp-aead` | `UseSecureConnections()` + `UseSystemControl()`. The client runs `HandshakeAsync()` (X25519), then requests go out with `WithEncrypt()` and the handler has `[PacketEncryption(true)]`, so both directions are ChaCha20-Poly1305. |
| `signalr-ws-msgpack` | SignalR hub `byte[] Echo(byte[])`, WebSockets transport only, `SkipNegotiation`, MessagePack protocol, `InvokeAsync<byte[]>`. |
| `grpc-unary` / `grpc-unary-tls` | Unary `rpc Unary(EchoMessage) returns (EchoMessage)` with a `bytes` field, over h2c or HTTP/2+TLS (self-signed ECDSA P-256). |
| `grpc-duplex` / `grpc-duplex-tls` | One long-lived bidirectional stream per client, with one write and one read per operation. |
| `magiconion-unary` / `magiconion-hub` | `IService<T>` `UnaryResult<byte[]>`, or a `StreamingHub` method returning `ValueTask<byte[]>`, over h2c. |

- **Every client owns its own connection** (for gRPC and MagicOnion, its own `GrpcChannel` / `SocketsHttpHandler`), so
  "N clients" means N TCP connections for every library.
- **Latency:** one client, sequential calls. 20 000 warm-up calls, then **100 000 timed calls**. Every call is timed
  with `Stopwatch` and the samples are sorted to read exact percentiles.
- **Throughput:** N ∈ {1, 16, 64} clients in a **closed loop** (each client keeps one request in flight). After 2 s of warm-up,
  requests are counted over an 8 s window.
- **Runs:** 3 runs of each cell. The tables show the **median** of the runs, and `±x%` is half of the (max − min)/median spread.
- ASP.NET Core servers use `WebApplication.CreateBuilder`, with logging cleared and the minimum level set to Warning, and Kestrel bound only to loopback. No
  competitor was tuned beyond its documented default setup. The same goes for Nalix: no tuning at all (loopback is exempt from the default per-IP connection quota since #365).
- Raw data (JSON lines, one row per run) is in
  [`data/network-comparison-results.jsonl`](data/network-comparison-results.jsonl). The tables below are produced from it by
  `benchmarks/Nalix.Comparison.Benchmarks/aggregate.py`.

Reproduce with:

```powershell
.\benchmarks\run-comparison.ps1
```

## Results

### Latency — 32 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 56.0 | 50.5 | 72.6 | 117.8 | 491.6 | ±6.3% | 144 |
| kestrel-ws | 56.7 | 51.8 | 69.1 | 105.1 | 311.0 | ±1.9% | 0 |
| nalix-tcp | 68.8 | 63.4 | 82.3 | 133.1 | 903.5 | ±2.0% | 56 |
| nalix-ws | 90.1 | 74.0 | 108.2 | 283.7 | 2415.9 | ±1.6% | 192 |
| nalix-tcp-aead | 86.5 | 75.6 | 103.0 | 193.0 | 1980.7 | ±1.4% | 56 |
| signalr-ws-msgpack | 92.8 | 83.6 | 110.9 | 180.8 | 1793.9 | ±1.7% | 680 |
| grpc-unary | 114.0 | 103.4 | 134.3 | 215.2 | 1753.5 | ±2.0% | 968 |
| grpc-duplex | 87.7 | 79.7 | 101.8 | 177.6 | 1374.2 | ±4.4% | 264 |
| grpc-unary-tls | 139.4 | 128.6 | 162.0 | 248.7 | 1691.9 | ±0.5% | 997 |
| grpc-duplex-tls | 98.6 | 91.8 | 113.8 | 175.2 | 751.3 | ±0.9% | 305 |
| magiconion-unary | 118.9 | 108.6 | 137.0 | 229.7 | 1796.9 | ±1.1% | 1432 |
| magiconion-hub | 90.4 | 84.4 | 103.8 | 161.1 | 629.3 | ±0.4% | 200 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 58.0 | 51.9 | 70.2 | 121.8 | 1054.2 | ±5.1% | 143 |
| kestrel-ws | 61.2 | 56.2 | 73.2 | 122.7 | 701.6 | ±2.7% | 0 |
| nalix-tcp | 81.9 | 72.6 | 97.1 | 189.4 | 1476.9 | ±1.1% | 1048 |
| nalix-ws | 93.2 | 79.6 | 109.4 | 229.5 | 2205.0 | ±2.2% | 1184 |
| nalix-tcp-aead | 106.2 | 95.0 | 123.9 | 219.6 | 2030.0 | ±0.6% | 1048 |
| signalr-ws-msgpack | 101.3 | 87.2 | 121.5 | 248.3 | 2358.9 | ±2.6% | 1672 |
| grpc-unary | 117.6 | 107.2 | 139.6 | 217.6 | 1871.8 | ±2.3% | 1960 |
| grpc-duplex | 81.0 | 74.2 | 95.6 | 153.6 | 933.9 | ±0.7% | 1256 |
| grpc-unary-tls | 142.7 | 131.0 | 164.5 | 231.3 | 1900.7 | ±1.2% | 1986 |
| grpc-duplex-tls | 104.5 | 97.1 | 120.7 | 177.3 | 1039.4 | ±0.5% | 1297 |
| magiconion-unary | 123.8 | 112.9 | 142.2 | 226.8 | 1381.0 | ±0.2% | 3736 |
| magiconion-hub | 102.0 | 90.2 | 118.0 | 233.1 | 2029.3 | ±9.8% | 1225 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 17,558 (±2%) | 60,243 (±6%) | 72,143 (±3%) |
| kestrel-ws | 18,069 (±3%) | 62,340 (±6%) | 70,085 (±6%) |
| nalix-tcp | 12,948 (±4%) | 35,118 (±2%) | 41,785 (±1%) |
| nalix-ws | 11,214 (±6%) | 33,385 (±5%) | 37,171 (±4%) |
| nalix-tcp-aead | 12,242 (±3%) | 31,982 (±4%) | 37,529 (±2%) |
| signalr-ws-msgpack | 10,497 (±3%) | 42,871 (±3%) | 53,252 (±44%) |
| grpc-unary | 8,591 (±2%) | 35,655 (±1%) | 41,181 (±3%) |
| grpc-duplex | 12,838 (±2%) | 47,622 (±3%) | 55,920 (±1%) |
| grpc-unary-tls | 7,258 (±1%) | 29,957 (±5%) | 35,856 (±4%) |
| grpc-duplex-tls | 10,303 (±1%) | 40,459 (±0%) | 47,608 (±1%) |
| magiconion-unary | 8,517 (±1%) | 35,619 (±1%) | 42,905 (±1%) |
| magiconion-hub | 9,946 (±32%) | 46,030 (±7%) | 50,308 (±12%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 22.7 | 23.8 | 288 | 13/2/0 |
| kestrel-ws | 0 | 23.5 | 23.9 | 152 | 0/0/0 |
| nalix-tcp | 56 | 44.5 | 31.5 | 888 | 1/0/0 |
| nalix-ws | 192 | 46.4 | 35.1 | 888 | 3/0/0 |
| nalix-tcp-aead | 56 | 49.3 | 38.9 | 888 | 0/0/0 |
| signalr-ws-msgpack | 672 | 29.4 | 29.3 | 591 | 65/1/1 |
| grpc-unary | 968 | 35.9 | 43.2 | 6737 | 75/0/0 |
| grpc-duplex | 264 | 27.8 | 30.9 | 2327 | 45/1/0 |
| grpc-unary-tls | 1573 | 44.3 | 50.5 | 6737 | 88/0/0 |
| grpc-duplex-tls | 869 | 34.5 | 36.2 | 2328 | 45/0/0 |
| magiconion-unary | 1432 | 32.4 | 45.6 | 7401 | 118/0/0 |
| magiconion-hub | 203 | 28.8 | 33.7 | 2254 | 7/2/1 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 17,185 (±1%) | 62,375 (±3%) | 67,304 (±5%) |
| kestrel-ws | 16,627 (±1%) | 54,094 (±3%) | 58,219 (±9%) |
| nalix-tcp | 12,877 (±3%) | 37,045 (±3%) | 42,735 (±27%) |
| nalix-ws | 11,495 (±5%) | 30,442 (±3%) | 36,352 (±2%) |
| nalix-tcp-aead | 9,725 (±2%) | 28,123 (±1%) | 32,007 (±3%) |
| signalr-ws-msgpack | 10,765 (±2%) | 42,118 (±1%) | 50,972 (±4%) |
| grpc-unary | 8,021 (±3%) | 33,704 (±1%) | 36,984 (±2%) |
| grpc-duplex | 12,255 (±2%) | 43,668 (±12%) | 51,520 (±7%) |
| grpc-unary-tls | 7,089 (±1%) | 29,342 (±1%) | 33,567 (±9%) |
| grpc-duplex-tls | 9,474 (±1%) | 36,778 (±0%) | 42,828 (±1%) |
| magiconion-unary | 7,659 (±4%) | 27,526 (±11%) | 32,896 (±10%) |
| magiconion-hub | 9,355 (±4%) | 140,846 (±0%) | 152,102 (±0%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 22.9 | 24.6 | 288 | 10/0/0 |
| kestrel-ws | 0 | 24.7 | 27.6 | 152 | 0/0/0 |
| nalix-tcp | 1048 | 46.4 | 33.8 | 1880 | 142/0/0 |
| nalix-ws | 1184 | 51.1 | 38.0 | 1880 | 113/0/0 |
| nalix-tcp-aead | 1048 | 55.0 | 46.1 | 1880 | 111/0/0 |
| signalr-ws-msgpack | 1664 | 31.4 | 33.3 | 2547 | 270/0/0 |
| grpc-unary | 1961 | 39.9 | 46.2 | 7730 | 119/1/0 |
| grpc-duplex | 1256 | 28.3 | 33.6 | 3348 | 215/0/0 |
| grpc-unary-tls | 2566 | 46.1 | 51.5 | 7744 | 129/0/0 |
| grpc-duplex-tls | 1862 | 38.5 | 40.2 | 3349 | 124/0/0 |
| magiconion-unary | 3737 | 39.9 | 46.9 | 9752 | 248/0/0 |
| magiconion-hub | 1192 | 40.7 | 37.5 | 3270 | 178/0/0 |

## Who wins where

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **Nalix TCP** (p50 63.4 µs) | 1st. gRPC duplex 79.7 µs, SignalR 83.6 µs, MagicOnion hub 84.4 µs |
| Latency, 1 KB, 1 client | **Nalix TCP** (p50 72.6 µs) | 1st. gRPC duplex 74.2 µs, SignalR 87.2 µs, MagicOnion hub 90.2 µs |
| Latency, encrypted, 32 B | **Nalix AEAD** (p50 75.6 µs) | 1st. gRPC duplex TLS 91.8 µs, gRPC unary TLS 128.6 µs |
| Latency, encrypted, 1 KB | **Nalix AEAD** (p50 95.0 µs) | 1st. gRPC duplex TLS 97.1 µs, gRPC unary TLS 131.0 µs |
| Throughput, 1 client, 32 B | **Nalix TCP** (12.9k) / **gRPC duplex** (12.8k) / **Nalix AEAD** (12.2k) | 1st |
| Throughput, 16 clients, 32 B | **gRPC duplex** 47.6k | 4th (MagicOnion hub 46.0k, SignalR 42.9k, Nalix TCP 35.1k) |
| Throughput, 16 clients, 1 KB | **MagicOnion hub** 140.8k | gRPC duplex 43.7k, SignalR 42.1k, Nalix TCP 37.0k |
| Throughput, 64 clients, 32 B | **gRPC duplex** 55.9k | SignalR 53.3k, MagicOnion hub 50.3k, Nalix TCP 41.8k |
| Throughput, 64 clients, 1 KB | **MagicOnion hub** 152.1k | gRPC duplex 51.5k, SignalR 51.0k, Nalix TCP 42.7k |
| Server allocations/msg, 32 B | **Nalix TCP / AEAD** 56 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| Server allocations/msg, 1 KB | **Nalix TCP / AEAD** 1,048 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| GC Gen0 collections, 32 B | **Nalix AEAD** 0 / **Nalix TCP** 1 | 1st (SignalR 65, gRPC duplex 45, gRPC unary 75, MagicOnion unary 118) |
## Caveats

- **Loopback and shared host.** Loopback measurements reflect stack overhead and dispatch efficiency on a shared machine; absolute capacity on bare metal with dedicated network hardware will differ.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`, `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured.
- **Whole-process allocations.** The server numbers include background work (timers, task manager), spread across all messages.
- **The encrypted comparison is not like-for-like.** TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake, and needs no certificate.
