# NexNet Transport Headers and Message Formats

This document provides comprehensive internal documentation for NexNet's transport headers and message formats across all supported transport types.

## Universal Message Structure

All NexNet messages, regardless of transport, follow this fundamental structure:

### Basic Message Header Format

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     Type      |         Content Length        |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                    Message Header (Variable)                  |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                        Message Body (Variable)                |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

| Field          | Size (bytes) | Type     | Description                             |
|----------------|--------------|----------|-----------------------------------------|
| Type           | 1            | `byte`   | Message type from `MessageType` enum    |
| Content Length | 2            | `ushort` | Length of message body (little-endian)  |
| Message Header | Variable     | `byte[]` | Type-specific header data               |
| Message Body   | Variable     | `byte[]` | Serialized message payload              |

## Message Types

### Control Messages (1-19)

| Type | Value | Header Size | Description           |
|------|-------|-------------|-----------------------|
| Ping | 1     | 0           | Heartbeat message     |

### Disconnection Messages (20-39)

| Type                              | Value | Header Size | Description                 |
|-----------------------------------|-------|-------------|-----------------------------|
| DisconnectSocketError             | 20    | 0           | Socket disconnected         |
| DisconnectGraceful                | 21    | 0           | Graceful disconnection      |
| DisconnectProtocolError           | 22    | 0           | Transport protocol error    |
| DisconnectTimeout                 | 23    | 0           | Connection timed out        |
| DisconnectClientMismatch          | 24    | 0           | Client hub version mismatch |
| DisconnectServerMismatch          | 25    | 0           | Server hub version mismatch |
| DisconnectServerShutdown          | 28    | 0           | Server is shutting down     |
| DisconnectAuthentication          | 29    | 0           | Authentication failed       |
| DisconnectServerRestarting        | 30    | 0           | Server is restarting        |
| DisconnectSocketClosedWhenWriting | 32    | 0           | Socket closed during write  | 

### Duplex Pipe Messages (50-99)

#### DuplexPipeWrite (Type 50)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|      50       |         Data Length           |    Pipe ID    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|   Pipe ID     |                                               |
+-+-+-+-+-+-+-+-+                                               +
|                         Pipe Data                             |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

| Field       | Size (bytes) | Type     | Description                            |
|-------------|--------------|----------|----------------------------------------|
| Type        | 1            | `byte`   | 50 (DuplexPipeWrite)                   |
| Data Length | 2            | `ushort` | Length of pipe data (little-endian)    |
| Pipe ID     | 2            | `ushort` | Duplex pipe identifier (little-endian) |
| Pipe Data   | Variable     | `byte[]` | Raw pipe data payload                  |

### Handshake Messages (100-109)

#### ClientGreeting (Type 100)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     100       |        Greeting Length        |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                    Serialized ClientGreetingMessage          |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

| Field           | Size (bytes) | Type                    | Description                |
|-----------------|--------------|-------------------------|----------------------------|
| Type            | 1            | `byte`                  | 100 (ClientGreeting)       |
| Greeting Length | 2            | `ushort`                | Serialized message length  |
| Greeting Data   | Variable     | `ClientGreetingMessage` | MemoryPack serialized data |

#### ClientGreetingReconnection (Type 101)

Similar structure to ClientGreeting but indicates a reconnection attempt.

#### ServerGreeting (Type 105)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     105       |        Greeting Length        |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                    Serialized ServerGreetingMessage           |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Invocation Messages (110-119)

#### Invocation (Type 110)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     110       |       Message Length          |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                    Serialized InvocationMessage               |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

#### InvocationCancellation (Type 111)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     111       |       Message Length          |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                Serialized InvocationCancellationMessage       |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

#### InvocationResult (Type 112)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     112       |       Message Length          |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                Serialized InvocationResultMessage             |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Duplex Pipe Control Messages (120-129)

#### DuplexPipeUpdateState (Type 120)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     120       |       Message Length          |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|             Serialized DuplexPipeUpdateStateMessage           |
+                                                               +
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

## Transport-Specific Considerations

### TCP/TLS/UDS Transports

These transports send NexNet messages directly over the socket connection:

- **Framing**: NexNet provides its own message framing
- **Overhead**: 3 bytes minimum (Type + Content Length)
- **Ordering**: Guaranteed by underlying transport
- **Error Detection**: Relies on transport-level error detection

### WebSocket Transport

WebSocket messages encapsulate NexNet messages:

```
+------------------+
| WebSocket Frame  |
| (Binary, 4 bytes)|
+------------------+
| NexNet Message   |
| (Full message)   |
+------------------+
```

- **WebSocket Frame Overhead**: 4 bytes per message
- **Total Overhead**: 7 bytes minimum (4 WebSocket + 3 NexNet)
- **Message Boundary**: Each NexNet message = 1 WebSocket frame

### HttpSocket Transport

HttpSocket upgrades HTTP connection and sends NexNet messages directly:

```
+------------------+
| HTTP Upgrade     |
| Headers          |
+------------------+
| NexNet Messages  |
| (Stream of msgs) |
+------------------+
```

- **Upgrade Header**: `Upgrade: nexnet-httpsockets`
- **Post-Upgrade**: Identical to TCP transport
- **Overhead**: 3 bytes minimum (same as TCP)

### QUIC Transport

QUIC streams carry NexNet messages with additional reliability:

- **Stream Multiplexing**: Each duplex pipe can use separate QUIC stream
- **Flow Control**: QUIC-level congestion control
- **Connection Migration**: Automatic IP/port change handling
- **Overhead**: 3 bytes NexNet + QUIC stream overhead

## Implementation Notes

### Endianness

All multi-byte fields use **little-endian** byte order:
- `ushort` content length: low byte first
- `ushort` pipe ID: low byte first
- `int` invocation ID: low byte first


### Security Notes

- **Length Validation**: Content length is validated against maximum limits
- **Type Validation**: Message types are validated against known enum values
- **Buffer Bounds**: All buffer operations are bounds-checked
- **Resource Limits**: Pipe and invocation IDs are limited to prevent resource exhaustion