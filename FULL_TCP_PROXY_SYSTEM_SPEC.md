# FULL TCP FAN-OUT / FAN-IN PROXY SYSTEM SPECIFICATION
# Production Engineering Design Document

Version: 1.0  
Target Framework: C# .NET Framework 4.7.2

---

# 1. System Overview

This document defines the complete architecture and implementation requirements for a production-grade TCP Fan-Out / Fan-In proxy system.

The system acts as a transparent TCP relay between:

- One upstream system
- Multiple downstream systems

The proxy duplicates upstream traffic to all downstream systems and forwards downstream traffic back to the upstream system.

The implementation must require:
- NO code changes in existing systems
- ONLY configuration changes

---

# 2. High-Level Network Architecture

```text
                    +-------------------+
                    |   1.1.1.1         |
                    |   Upstream        |
                    +-------------------+
                       |           |
                M&C :20000   DATA :25000
                       |           |
                       v           v

              +--------------------------------+
              |       3.3.3.3                 |
              | TCP Fan-Out/Fan-In Proxy      |
              +--------------------------------+
                  |        |        |       |
                  |        |        |       |
                  v        v        v       v

         +--------------+       +--------------+
         | 2.2.2.2      |       | 4.4.4.4      |
         | Downstream A |       | Downstream B |
         +--------------+       +--------------+
```

---

# 3. Functional Goals

The proxy must:

- Accept upstream TCP connections
- Duplicate traffic to multiple downstream systems
- Forward downstream traffic upstream
- Handle reconnects automatically
- Support high throughput
- Support long-running stable operation
- Operate transparently at byte-stream level

---

# 4. TCP Traffic Rules

# 4.1 Upstream -> Downstream

Traffic from:

```text
1.1.1.1:20000
```

Must be duplicated to:

```text
2.2.2.2:20000
4.4.4.4:20000
```

Traffic from:

```text
1.1.1.1:25000
```

Must be duplicated to:

```text
2.2.2.2:25000
4.4.4.4:25000
```

---

# 4.2 Downstream -> Upstream

Traffic from:
- 2.2.2.2
- 4.4.4.4

Must be forwarded to:
- 1.1.1.1

---

# 5. Important TCP Considerations

TCP is stream-based.

The implementation MUST:
- forward raw bytes exactly as received
- never assume message boundaries
- never modify payloads
- never parse protocol unless explicitly configured

The proxy is a transparent relay.

---

# 6. Core Features

## 6.1 Multi-Channel Support

Support multiple independent channels:

- M&C
- DATA

Each channel must have:
- separate listener
- separate socket management
- separate buffers
- separate logging context
- separate metrics

---

## 6.2 Fan-Out

One upstream packet stream must be duplicated to all downstream systems.

Implementation strategy:

```text
Read once
Write many
```

---

## 6.3 Fan-In

All downstream systems may send data upstream.

Requirements:
- independent receive loops
- simultaneous traffic support
- no blocking between downstreams

---

## 6.4 Full Duplex

All connections must support simultaneous:
- read
- write

Use async operations.

---

## 6.5 Automatic Reconnect

If downstream disconnects:
- reconnect automatically
- configurable retry interval
- no service interruption

If upstream disconnects:
- continue waiting for reconnect

---

# 7. Recommended Solution Structure

```text
TcpProxy.sln

/src
    /TcpProxy.Core
    /TcpProxy.Networking
    /TcpProxy.Routing
    /TcpProxy.Configuration
    /TcpProxy.Logging
    /TcpProxy.Metrics
    /TcpProxy.Service
    /TcpProxy.Console

/tests
    /TcpProxy.Tests
    /TcpProxy.IntegrationTests

/tools
    /UpstreamSimulator
    /DownstreamSimulator
    /TrafficGenerator
    /TcpTestCommon
```

---

# 8. Main Components

# 8.1 TcpChannelProxy

Represents one channel.

Examples:
- MC
- DATA

Responsibilities:
- accept upstream connection
- connect downstream targets
- traffic routing
- lifecycle management
- reconnect handling

---

# 8.2 TcpRelayConnection

Represents one TCP socket connection.

Responsibilities:
- receive loop
- send loop
- queue management
- disconnect detection
- metrics

---

# 8.3 TrafficRouter

Responsibilities:
- upstream -> all downstreams
- downstream -> upstream

---

# 8.4 ConnectionManager

Responsibilities:
- reconnect logic
- socket lifecycle
- health monitoring

---

# 8.5 MetricsManager

Responsibilities:
- throughput
- connection counts
- queue sizes
- reconnect counters

---

# 9. Recommended Technologies

Use:
- TcpListener
- TcpClient
- NetworkStream
- async/await

Do NOT use:
- blocking sockets
- synchronous I/O
- busy loops
- Thread.Sleep polling

---

# 10. Threading Model

Recommended model:

```text
1 receive task per socket
1 send queue per socket
```

Recommended primitives:
- ConcurrentQueue
- SemaphoreSlim
- CancellationTokenSource

---

# 11. Async Design Requirements

All socket operations must be asynchronous.

Use:
- ReadAsync
- WriteAsync

Never block receive loops.

---

# 12. Queue Management

Each connection must have:
- dedicated TX queue
- configurable limits

Requirements:
- prevent slow consumer blocking
- prevent memory explosion

Policies:
- disconnect slow client
OR
- drop oldest queued packets

Configurable behavior preferred.

---

# 13. Configuration System

Use JSON configuration.

Example:

```json
{
  "channels": [
    {
      "name": "MC",
      "listenIp": "0.0.0.0",
      "listenPort": 20000,
      "targets": [
        {
          "name": "Primary",
          "host": "2.2.2.2",
          "port": 20000
        },
        {
          "name": "Secondary",
          "host": "4.4.4.4",
          "port": 20000
        }
      ]
    },
    {
      "name": "DATA",
      "listenIp": "0.0.0.0",
      "listenPort": 25000,
      "targets": [
        {
          "name": "Primary",
          "host": "2.2.2.2",
          "port": 25000
        },
        {
          "name": "Secondary",
          "host": "4.4.4.4",
          "port": 25000
        }
      ]
    }
  ]
}
```

---

# 14. Logging Requirements

Use structured logging.

Recommended:
- Serilog

Log:
- connect/disconnect
- reconnect attempts
- socket exceptions
- bytes transferred
- queue overflows
- routing direction

Recommended sinks:
- Console
- Rolling file

---

# 15. Metrics

Track:
- bytes/sec
- packets/sec
- active sockets
- reconnect count
- queue sizes
- dropped packets

Optional:
- Prometheus metrics exporter

---

# 16. Socket Settings

Recommended:

```csharp
tcpClient.NoDelay = true;
tcpClient.ReceiveBufferSize = 1024 * 1024;
tcpClient.SendBufferSize = 1024 * 1024;
```

Enable KeepAlive if required.

---

# 17. Recommended Buffer Size

```text
64KB
```

---

# 18. Memory Management

Requirements:
- minimize allocations
- reuse buffers where possible
- avoid large temporary arrays

Optional:
- ArrayPool<byte>

---

# 19. Error Handling

The proxy must survive:
- remote disconnects
- half-open sockets
- connection resets
- network failures
- downstream failures

One failing downstream must NOT affect others.

---

# 20. Graceful Shutdown

On shutdown:
- stop listeners
- cancel receive loops
- flush queues if possible
- close sockets cleanly

---

# 21. Windows Service Support

The application must support:

## Console Mode
Used for:
- debugging
- development
- testing

## Windows Service Mode
Used for:
- production deployment

---

# 22. Deployment Requirements

Deliver:
- release build
- sample configuration
- installation instructions
- service registration instructions

Recommended:
- single deployment folder

---

# 23. Required NuGet Packages

```text
Serilog
Serilog.Sinks.Console
Serilog.Sinks.File
Microsoft.Extensions.Configuration
Microsoft.Extensions.Configuration.Json
```

Optional:
```text
Prometheus.Client
```

---

# 24. Simulation Utilities

The solution must include simulation tools.

Purpose:
- integration testing
- stress testing
- reconnect validation
- throughput validation
- corruption detection

---

# 25. Required Simulators

## 25.1 Upstream Simulator

Simulates:

```text
1.1.1.1
```

Capabilities:
- connect to proxy
- send MC traffic
- send DATA traffic
- receive responses
- validate responses

---

## 25.2 Downstream Simulator

Simulates:
- 2.2.2.2
- 4.4.4.4

Capabilities:
- receive traffic
- echo traffic
- generate custom traffic
- disconnect intentionally
- reconnect automatically

---

# 26. Simulator Modes

## Echo Mode

Any received bytes are immediately returned.

Used for:
- integrity testing

---

## Silent Mode

Receives traffic only.

Used for:
- mirror validation

---

## Traffic Generator Mode

Generate packets periodically.

Examples:
- every 100ms
- every 1 second
- random intervals

---

## Burst Mode

Generate:
- high throughput
- large payloads
- stress traffic

---

# 27. Structured Test Packet Format

Optional test packet format:

```text
[HEADER][COUNTER][TIMESTAMP][PAYLOAD]
```

Example:

```text
MC|000001|2026-05-07T10:00:00|HELLO
```

---

# 28. Simulator Console UI

Example:

```text
[MC]
CONNECTED
TX: 150 packets
RX: 149 packets

[DATA]
CONNECTED
TX RATE: 5 MB/s
```

---

# 29. Interactive Simulator Commands

Examples:

```text
send hello
burst 1000
disconnect
reconnect
stats
```

---

# 30. Scenario Runner

Support scripted JSON scenarios.

Example:

```json
{
  "steps": [
    {
      "action": "connect"
    },
    {
      "action": "send",
      "count": 1000,
      "size": 1024
    },
    {
      "action": "disconnect"
    }
  ]
}
```

---

# 31. Required Test Scenarios

## Scenario 1 — Single Downstream

Expected:
- normal relay

---

## Scenario 2 — Dual Downstream

Expected:
- upstream packets duplicated correctly

---

## Scenario 3 — Simultaneous Downstream Traffic

Expected:
- upstream receives both streams

---

## Scenario 4 — Downstream Disconnect

Expected:
- second downstream continues working

---

## Scenario 5 — Downstream Reconnect

Expected:
- traffic resumes automatically

---

## Scenario 6 — Stress Test

Expected:
- stable memory
- stable throughput
- no deadlocks

---

# 32. Packet Integrity Validation

Validate:
- byte-perfect forwarding
- ordering
- no corruption
- no unexpected duplication

---

# 33. Stress Test Goals

Target:
- millions of packets
- long-duration runtime
- stable memory usage

---

# 34. Production Hardening

Recommended:
- health monitoring
- watchdog restart
- memory monitoring
- log rotation
- connection metrics

---

# 35. Future Optional Features

## Traffic Recording

Binary dump format:

```text
timestamp + direction + raw bytes
```

---

## Web Dashboard

Optional dashboard:
- active connections
- throughput
- reconnects
- errors

---

## Protocol Plugins

Future support:
- protocol-aware routing
- filtering
- packet tagging

---

# 36. Performance Goals

Target:
- low latency
- stable long-term operation
- thousands of packets/sec
- minimal packet loss

---

# 37. Coding Standards

Requirements:
- SOLID principles
- dependency injection
- interface-driven design
- async best practices
- structured logging
- no swallowed exceptions

---

# 38. Recommended Interfaces

Examples:

```csharp
ITcpChannelProxy
ITrafficRouter
IConnectionManager
IRelayConnection
IMetricsCollector
```

---

# 39. Recommended Class Naming

Examples:

```text
TcpChannelProxy
TcpRelayConnection
SocketReceiveLoop
SocketSendLoop
TrafficDuplicator
ReconnectWorker
MetricsCollector
```

---

# 40. Deliverables

Required deliverables:

- production-ready source code
- Visual Studio solution
- sample configs
- simulator tools
- stress tools
- test scenarios
- README
- deployment instructions
- Windows Service instructions
- logging configuration

---

# 41. Final Engineering Goal

Build a stable, production-grade, high-throughput TCP relay platform capable of:

- transparent TCP forwarding
- traffic duplication
- multi-destination routing
- resiliency under failure
- long-running operation
- large-scale traffic simulation
