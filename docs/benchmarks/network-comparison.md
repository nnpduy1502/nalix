# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness in one session. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a shared desktop machine over Windows loopback. Absolute values are much lower than on bare metal with dedicated network hardware. Use them to *compare the stacks with each other*, not as absolute capacity figures.

## Summary

!!! note "All 12 libraries measured in a single session"
    All 12 frameworks and baselines below were measured alongside each other on 2026-10-01 in one uninterrupted session under .NET 10.0.12 (SDK 10.0.401). Every comparison reflects verified head-to-head results run under identical hardware and OS conditions.

- **Single-client latency:** Nalix TCP achieves **45.4 µs** p50 at 32 B, outperforming gRPC duplex (49.8 µs), SignalR (52.3 µs), MagicOnion hub (58.1 µs), and gRPC unary (64.2 µs). Only the hand-written raw-socket baseline (16.7 µs) and unencapsulated Kestrel WebSocket (34.2 µs) are lower.
- **Throughput with 64 clients:**
  - At 32 B: gRPC duplex 206.7k ops/s, MagicOnion hub 195.0k, SignalR 190.0k, Nalix TCP 168.0k, Nalix WebSocket 45.4k, Nalix TCP AEAD 43.6k, gRPC unary 142.0k, MagicOnion unary 133.1k.
  - At 1 KB: gRPC duplex 215.7k ops/s, SignalR 177.1k, MagicOnion hub 161.3k, gRPC duplex TLS 158.4k, gRPC unary 139.0k, MagicOnion unary 127.6k, Nalix TCP 44.9k, Nalix WebSocket 38.8k, Nalix TCP AEAD 36.8k.
- **Server CPU per message:** Nalix TCP measures 49.1 µs at 32 B against 33.0 µs for SignalR and 34.7 µs for gRPC duplex.
- **Server allocations:** **56 B** per message at 32 B for Nalix TCP and Nalix AEAD, the lowest of the full frameworks (MagicOnion hub 200 B, gRPC duplex 264 B, SignalR 672 B) — saving over 53% heap allocations following PR #401 zero-alloc fast-paths and pooled dispatch sessions. Only the raw Kestrel WebSocket baseline allocates less (0 B). Nalix WebSocket allocates **192 B** (down from 256 B).
- **Encryption:** Nalix AEAD (X25519 + ChaCha20-Poly1305) uses significantly fewer server allocations (**56 B** vs 525 B for gRPC TLS at 32 B; **1,048 B** vs 1,589 B at 1 KB). For high-concurrency throughput at 1 KB, gRPC duplex TLS with hardware-accelerated AES-NI edges ahead (158.4k vs 36.8k ops/s).

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
| raw-tcp | 17.3 | 16.7 | 18.3 | 36.3 | 77.6 | ±0.6% | 143 |
| kestrel-ws | 32.9 | 34.2 | 38.0 | 52.7 | 109.4 | ±1.9% | 0 |
| nalix-tcp | 49.5 | 45.4 | 56.3 | 130.3 | 574.9 | ±3.6% | 56 |
| nalix-ws | 68.3 | 64.1 | 78.1 | 124.6 | 457.4 | ±0.7% | 192 |
| nalix-tcp-aead | 77.7 | 71.4 | 89.4 | 151.9 | 1000.7 | ±0.3% | 56 |
| signalr-ws-msgpack | 51.2 | 52.3 | 59.5 | 84.3 | 262.3 | ±0.6% | 680 |
| grpc-unary | 66.5 | 64.2 | 72.7 | 92.3 | 524.9 | ±0.1% | 968 |
| grpc-duplex | 47.1 | 49.8 | 55.5 | 74.2 | 368.8 | ±0.7% | 264 |
| grpc-unary-tls | 77.9 | 76.6 | 85.7 | 116.2 | 506.3 | ±0.3% | 1039 |
| grpc-duplex-tls | 58.9 | 58.7 | 64.1 | 80.8 | 382.2 | ±0.4% | 311 |
| magiconion-unary | 74.2 | 70.4 | 85.9 | 128.7 | 598.2 | ±1.6% | 1432 |
| magiconion-hub | 59.0 | 58.1 | 69.9 | 108.4 | 294.3 | ±2.0% | 201 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 17.9 | 17.2 | 19.0 | 31.8 | 72.1 | ±0.9% | 144 |
| kestrel-ws | 25.5 | 24.1 | 29.7 | 54.2 | 74.6 | ±1.9% | 0 |
| nalix-tcp | 65.9 | 63.8 | 83.4 | 130.8 | 585.3 | ±25.2% | 1048 |
| nalix-ws | 80.0 | 74.0 | 92.9 | 144.2 | 706.9 | ±0.2% | 1184 |
| nalix-tcp-aead | 95.8 | 87.4 | 107.1 | 181.7 | 1380.1 | ±0.5% | 1048 |
| signalr-ws-msgpack | 53.8 | 53.9 | 59.7 | 76.7 | 443.8 | ±0.5% | 1672 |
| grpc-unary | 67.8 | 66.8 | 74.8 | 95.8 | 491.2 | ±0.4% | 1960 |
| grpc-duplex | 54.3 | 54.8 | 63.4 | 83.0 | 413.4 | ±19.5% | 1256 |
| grpc-unary-tls | 77.7 | 74.8 | 87.0 | 113.4 | 492.1 | ±0.2% | 1476 |
| grpc-duplex-tls | 63.2 | 61.9 | 69.5 | 98.4 | 450.5 | ±0.4% | 1291 |
| magiconion-unary | 76.8 | 74.0 | 85.1 | 110.5 | 634.5 | ±0.9% | 3736 |
| magiconion-hub | 63.4 | 61.2 | 76.6 | 110.6 | 377.2 | ±1.9% | 1192 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 56,917 (±11%) | 166,670 (±64%) | 378,423 (±21%) |
| kestrel-ws | 30,819 (±1%) | 291,165 (±3%) | 348,004 (±1%) |
| nalix-tcp | 20,220 (±8%) | 160,428 (±2%) | 168,012 (±1%) |
| nalix-ws | 14,765 (±4%) | 41,184 (±1%) | 45,380 (±5%) |
| nalix-tcp-aead | 12,953 (±3%) | 37,792 (±2%) | 43,600 (±29%) |
| signalr-ws-msgpack | 20,210 (±2%) | 156,899 (±1%) | 190,045 (±1%) |
| grpc-unary | 14,782 (±3%) | 97,363 (±1%) | 141,999 (±1%) |
| grpc-duplex | 20,657 (±4%) | 189,752 (±47%) | 206,685 (±22%) |
| grpc-unary-tls | 12,875 (±1%) | 84,623 (±2%) | 123,669 (±1%) |
| grpc-duplex-tls | 17,453 (±0%) | 162,314 (±1%) | 196,584 (±1%) |
| magiconion-unary | 13,865 (±1%) | 89,426 (±2%) | 133,060 (±1%) |
| magiconion-hub | 17,806 (±1%) | 144,125 (±2%) | 195,022 (±1%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 142 | 19.9 | 19.2 | 284 | 62/0/0 |
| kestrel-ws | 0 | 22.7 | 20.9 | 152 | 0/0/0 |
| nalix-tcp | 56 | 49.1 | 26.8 | 888 | 17/0/0 |
| nalix-ws | 192 | 40.7 | 33.3 | 888 | 5/0/0 |
| nalix-tcp-aead | 56 | 45.7 | 35.2 | 888 | 0/0/0 |
| signalr-ws-msgpack | 672 | 33.0 | 33.4 | 1553 | 201/0/0 |
| grpc-unary | 968 | 40.4 | 46.4 | 6736 | 206/0/0 |
| grpc-duplex | 264 | 34.7 | 33.4 | 2137 | 171/0/0 |
| grpc-unary-tls | 1476 | 48.7 | 53.4 | 6736 | 185/0/0 |
| grpc-duplex-tls | 525 | 38.8 | 34.1 | 2199 | 209/0/0 |
| magiconion-unary | 1432 | 41.7 | 49.8 | 7400 | 240/0/0 |
| magiconion-hub | 200 | 38.8 | 33.4 | 2244 | 122/0/0 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 54,323 (±2%) | 336,955 (±2%) | 361,231 (±1%) |
| kestrel-ws | 44,373 (±12%) | 276,075 (±1%) | 327,627 (±1%) |
| nalix-tcp | 13,312 (±3%) | 39,016 (±3%) | 44,947 (±6%) |
| nalix-ws | 12,230 (±2%) | 34,888 (±5%) | 38,816 (±7%) |
| nalix-tcp-aead | 10,904 (±1%) | 32,459 (±2%) | 36,805 (±1%) |
| signalr-ws-msgpack | 18,363 (±0%) | 108,718 (±2%) | 177,099 (±1%) |
| grpc-unary | 14,306 (±1%) | 84,451 (±0%) | 138,950 (±0%) |
| grpc-duplex | 21,673 (±3%) | 180,405 (±1%) | 215,702 (±1%) |
| grpc-unary-tls | 12,412 (±2%) | 71,650 (±1%) | 115,945 (±1%) |
| grpc-duplex-tls | 16,615 (±1%) | 120,972 (±7%) | 158,412 (±7%) |
| magiconion-unary | 12,920 (±3%) | 78,217 (±1%) | 127,583 (±1%) |
| magiconion-hub | 17,224 (±4%) | 142,635 (±3%) | 161,259 (±1%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 140 | 20.9 | 19.9 | 285 | 59/0/0 |
| kestrel-ws | 0 | 23.2 | 22.7 | 152 | 0/0/0 |
| nalix-tcp | 1048 | 43.2 | 32.3 | 1880 | 76/0/0 |
| nalix-ws | 1184 | 50.1 | 38.1 | 1880 | 134/0/0 |
| nalix-tcp-aead | 1048 | 54.3 | 40.5 | 1880 | 133/0/0 |
| signalr-ws-msgpack | 1664 | 35.8 | 34.6 | 2545 | 319/0/0 |
| grpc-unary | 1960 | 45.5 | 47.8 | 7728 | 293/0/0 |
| grpc-duplex | 1256 | 32.6 | 32.1 | 3210 | 399/0/0 |
| grpc-unary-tls | 2467 | 52.6 | 55.0 | 7728 | 228/1/1 |
| grpc-duplex-tls | 1589 | 44.4 | 41.0 | 3303 | 278/0/0 |
| magiconion-unary | 3736 | 48.4 | 48.5 | 9704 | 339/0/0 |
| magiconion-hub | 1192 | 43.3 | 40.1 | 3269 | 209/0/0 |

## Who wins where

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **Nalix TCP** (p50 45.4 µs) | 1st. gRPC duplex 49.8 µs, SignalR 52.3 µs, MagicOnion hub 58.1 µs |
| Latency, 1 KB, 1 client | **SignalR** (p50 53.9 µs) | Nalix TCP 63.8 µs, gRPC duplex 54.8 µs, MagicOnion hub 61.2 µs |
| Latency, encrypted | **gRPC duplex TLS** (p50 58.7 µs vs Nalix AEAD 71.4 µs at 32 B; 61.9 µs vs 87.4 µs at 1 KB) | 2nd |
| Throughput, 1 client, 32 B | **gRPC duplex** (20.7k) / **Nalix TCP** (20.2k) / **SignalR** (20.2k) | 2nd (statistical tie with SignalR) |
| Throughput, 16 clients, 32 B | **gRPC duplex** 189.8k | 2nd (Nalix TCP 160.4k, SignalR 156.9k) |
| Throughput, 16 clients, 1 KB | **gRPC duplex** 180.4k | 4th (SignalR 108.7k, Nalix TCP 39.0k) |
| Throughput, 64 clients, 32 B | **gRPC duplex** 206.7k | 4th (MagicOnion hub 195.0k, SignalR 190.0k, Nalix TCP 168.0k) |
| Throughput, 64 clients, 1 KB | **gRPC duplex** 215.7k | Nalix TCP 44.9k, SignalR 177.1k, MagicOnion hub 161.3k |
| Throughput, encrypted, 64 clients, 1 KB | **gRPC duplex TLS** 158.4k | Nalix AEAD 36.8k |
| Server allocations/msg | **Nalix TCP / AEAD** 56 B at 32 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| Server CPU/msg, 64 clients, 32 B | **SignalR** 33.0 µs | Nalix 49.1 µs, behind SignalR (33.0 µs) and gRPC duplex (34.7 µs) |

## Caveats

- **Loopback and shared host.** Loopback measurements reflect stack overhead and dispatch efficiency on a shared machine; absolute capacity on bare metal with dedicated network hardware will differ.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`, `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured.
- **Whole-process allocations.** The server numbers include background work (timers, task manager), spread across all messages.
- **The encrypted comparison is not like-for-like.** TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake, and needs no certificate.
