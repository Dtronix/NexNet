# NexNet Protocol Specification

**Version:** 1.0

## Table of Contents

1. [Introduction](#1-introduction)
2. [Protocol Overview](#2-protocol-overview)
3. [Wire Format Conventions](#3-wire-format-conventions)
4. [Protocol Header](#4-protocol-header)
5. [Message Framing](#5-message-framing)
6. [Message Types](#6-message-types)
7. [Connection Handshake](#7-connection-handshake)
8. [Method Invocation Protocol](#8-method-invocation-protocol)
9. [Duplex Pipes Sub-Protocol](#9-duplex-pipes-sub-protocol)
10. [Collection Synchronization Sub-Protocol](#10-collection-synchronization-sub-protocol)
11. [Keep-Alive Mechanism](#11-keep-alive-mechanism)
12. [Disconnection Protocol](#12-disconnection-protocol)
13. [State Machines](#13-state-machines)
14. [Security Considerations & Threat Model](#14-security-considerations--threat-model)
15. [Reserved Ranges & Future Extensions](#15-reserved-ranges--future-extensions)

---

## 1. Introduction

### 1.1 Purpose

This document specifies the NexNet Protocol (NnP), a binary application-layer protocol for bidirectional remote procedure calls between networked endpoints. The protocol enables:

- Bidirectional method invocation between server and client
- Streaming data transfer via duplex pipes
- Synchronized distributed collections
- Session management with authentication

### 1.2 Scope

This specification covers the wire protocol only. Transport-layer concerns (TCP, Unix Domain Sockets, TLS) are out of scope. Implementations MAY use any reliable, ordered, byte-stream transport.

### 1.3 Terminology

| Term | Definition |
|------|------------|
| **Server** | The endpoint that listens for and accepts connections |
| **Client** | The endpoint that initiates connections |
| **Session** | A logical connection between one client and one server |
| **Nexus** | The interface definition exposed by an endpoint |
| **Proxy** | The interface definition for invoking methods on the remote endpoint |
| **Invocation** | A single method call from one endpoint to another |

### 1.4 Requirements Language

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

---

## 2. Protocol Overview

### 2.1 Architecture

```
┌────────────────────────────────────────────────────────────┐
│                    Application Layer                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│  │   Method    │  │   Duplex    │  │     Collection      │ │
│  │ Invocation  │  │   Pipes     │  │  Synchronization    │ │
│  └─────────────┘  └─────────────┘  └─────────────────────┘ │
├────────────────────────────────────────────────────────────┤
│                    Message Layer                           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │   Message Framing (Type + Length + Body)            │   │
│  └─────────────────────────────────────────────────────┘   │
├────────────────────────────────────────────────────────────┤
│                    Connection Layer                        │
│  ┌─────────────────────────────────────────────────────┐   │
│  │   Protocol Header + Handshake + Keep-Alive          │   │
│  └─────────────────────────────────────────────────────┘   │
├────────────────────────────────────────────────────────────┤
│                    Transport Layer                         │
│  ┌─────────────────────────────────────────────────────┐   │
│  │   TCP / Unix Domain Socket / Other Reliable Stream  │   │
│  └─────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────┘
```

### 2.2 Communication Model

- **Bidirectional:** Both endpoints can initiate method invocations
- **Asynchronous:** Multiple invocations may be in-flight simultaneously
- **Ordered:** Messages within a session are processed in order of receipt
- **Reliable:** The protocol assumes the transport provides reliable delivery

---

## 3. Wire Format Conventions

### 3.1 Byte Order

All multi-byte integer fields MUST be encoded in **little-endian** byte order.

### 3.2 Integer Types

| Type | Size | Range |
|------|------|-------|
| `byte` | 1 byte | 0 to 255 |
| `ushort` | 2 bytes | 0 to 65,535 |
| `int` | 4 bytes | -2,147,483,648 to 2,147,483,647 |
| `long` | 8 bytes | -9,223,372,036,854,775,808 to 9,223,372,036,854,775,807 |

### 3.3 Variable-Length Data

Variable-length fields are prefixed with their length. The protocol uses **MemoryPack** binary serialization format for structured data within message bodies.

### 3.4 String Encoding

Strings MUST be encoded as UTF-8 when serialized within message bodies.

---

## 4. Protocol Header

### 4.1 Format

The protocol header is sent by the client immediately after transport connection establishment.

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|      'N'      |      'n'      |      'P'      |     0x14      |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|   Reserved    |   Reserved    |   Reserved    |    Version    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

**Total Size:** 8 bytes

### 4.2 Field Definitions

| Offset | Field | Size | Value | Description |
|--------|-------|------|-------|-------------|
| 0 | Magic[0] | 1 | `0x4E` ('N') | Protocol identifier |
| 1 | Magic[1] | 1 | `0x6E` ('n') | Protocol identifier |
| 2 | Magic[2] | 1 | `0x50` ('P') | Protocol identifier |
| 3 | Magic[3] | 1 | `0x14` (DC4) | Protocol identifier |
| 4-6 | Reserved | 3 | `0x00` | Reserved for future use, MUST be zero |
| 7 | Version | 1 | `0x01` | Protocol version |

### 4.3 Validation Rules

The server MUST validate the protocol header:

1. Bytes 0-3 MUST equal `0x4E 0x6E 0x50 0x14`
2. Bytes 4-6 MUST equal `0x00 0x00 0x00`
3. Byte 7 MUST equal `0x01` (current protocol version)

If validation fails, the server MUST disconnect with reason `ProtocolError`.

---

## 5. Message Framing

### 5.1 Standard Message Format

All messages after the protocol header follow this structure:

```
 0                   1                   2
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     Type      |         Body Length           |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                               |
+                     Body                      +
|                   (variable)                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### 5.2 Header Fields

| Field | Size | Description |
|-------|------|-------------|
| Type | 1 byte | Message type identifier |
| Body Length | 2 bytes (ushort) | Length of body in bytes (0-65,535) |
| Body | Variable | Message-specific payload |

### 5.3 Header-Only Messages

Some message types have no body. For these messages:
- Body Length is `0x00 0x00`
- No body bytes follow the header

### 5.4 Maximum Message Size

- **Header:** 3 bytes
- **Maximum Body:** 65,535 bytes
- **Maximum Total:** 65,538 bytes per message

---

## 6. Message Types

### 6.1 Message Type Registry

| Value | Name | Direction | Body | Description |
|-------|------|-----------|------|-------------|
| 0x01 | Ping | Bidirectional | No | Keep-alive signal |
| 0x14 | DisconnectSocketError | Bidirectional | No | Socket error occurred |
| 0x15 | DisconnectGraceful | Bidirectional | No | Graceful shutdown |
| 0x16 | DisconnectProtocolError | Bidirectional | No | Protocol violation |
| 0x17 | DisconnectTimeout | Bidirectional | No | Connection timeout |
| 0x18 | DisconnectClientMismatch | Server→Client | No | Client API incompatible |
| 0x19 | DisconnectServerMismatch | Client→Server | No | Server API incompatible |
| 0x1C | DisconnectServerShutdown | Server→Client | No | Server shutting down |
| 0x1D | DisconnectAuthentication | Server→Client | No | Authentication failed |
| 0x1E | DisconnectServerRestarting | Server→Client | No | Server restarting |
| 0x20 | DisconnectSocketClosedWhenWriting | Bidirectional | No | Write failed |
| 0x32 | DuplexPipeWrite | Bidirectional | Yes | Streaming data segment |
| 0x64 | ClientGreeting | Client→Server | Yes | Initial handshake |
| 0x65 | ClientGreetingReconnection | Client→Server | Yes | Reconnection handshake (Reserved) |
| 0x69 | ServerGreeting | Server→Client | Yes | Handshake response |
| 0x6E | Invocation | Bidirectional | Yes | Method invocation request |
| 0x6F | InvocationCancellation | Bidirectional | Yes | Cancel pending invocation |
| 0x70 | InvocationResult | Bidirectional | Yes | Method invocation response |
| 0x78 | DuplexPipeUpdateState | Bidirectional | Yes | Pipe state transition |

### 6.2 Message Type Ranges

| Range | Purpose |
|-------|---------|
| 0x01 | Control (Ping) |
| 0x02-0x13 | Reserved |
| 0x14-0x27 | Disconnection reasons |
| 0x28-0x31 | Reserved |
| 0x32-0x3F | Duplex Pipe data |
| 0x40-0x63 | Reserved |
| 0x64-0x6D | Connection/Greeting |
| 0x6E-0x77 | Invocation |
| 0x78-0x7F | Duplex Pipe control |
| 0x80-0xFF | Reserved for future use |

---

## 7. Connection Handshake

### 7.1 Handshake Sequence

```
Client                                      Server
  |                                            |
  |-------- [Transport Connect] -------------->|
  |                                            |
  |-------- Protocol Header (8 bytes) -------->|
  |                                            | Validate header
  |                                            |
  |-------- ClientGreeting ------------------->|
  |                                            | Validate hashes
  |                                            | Authenticate (optional)
  |                                            |
  |<------- ServerGreeting --------------------|
  |                                            |
  | Validate hash                              |
  |                                            |
  |<======== Connection Established ==========>|
```

### 7.2 ClientGreeting Message (0x64)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | Version | string? | Requested API version identifier (null if unversioned) |
| 1 | ServerNexusHash | int | Expected server interface hash |
| 2 | ClientNexusHash | int | Client's own interface hash |
| 3 | AuthenticationToken | byte[] | Authentication credentials (may be empty) |

### 7.3 ServerGreeting Message (0x69)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | Version | int | Server's interface hash |
| 1 | ClientId | long | Assigned session identifier |

### 7.4 Hash Validation

**Server validates ClientGreeting:**
1. `ClientNexusHash` MUST match server's expected client interface hash
2. If server supports versioning:
   - `Version` MUST NOT be null
   - `Version` MUST exist in server's version table
   - `ServerNexusHash` MUST match the hash for that version
3. If server does not support versioning:
   - `Version` MUST be null
   - `ServerNexusHash` MUST match server's interface hash

**Client validates ServerGreeting:**
1. `Version` MUST match client's expected server interface hash

### 7.5 Authentication

If server requires authentication:
1. Server extracts `AuthenticationToken` from ClientGreeting
2. Server invokes authentication callback with token
3. If authentication fails, server disconnects with `DisconnectAuthentication`
4. If authentication succeeds, server proceeds with ServerGreeting

### 7.6 ClientGreetingReconnection (0x65) - RESERVED

This message type is reserved for future session reconnection functionality. Implementations MUST NOT send this message. Receivers SHOULD treat it as a protocol error.

### 7.7 Handshake Timeout

Both endpoints MUST implement a handshake timeout:
- If handshake is not completed within the configured timeout (default: 5000ms)
- The endpoint MUST disconnect with reason `Timeout`

---

## 8. Method Invocation Protocol

### 8.1 Invocation Message (0x6E)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | InvocationId | ushort | Request correlation identifier |
| 1 | MethodId | ushort | Target method identifier |
| 2 | Flags | byte | Invocation behavior flags |
| 3 | Arguments | byte[] | Serialized method arguments |

### 8.2 Invocation Flags

| Bit | Name | Description |
|-----|------|-------------|
| 0 | IgnoreReturn | Do not send InvocationResult (fire-and-forget) |
| 1 | DuplexPipe | Arguments contain a duplex pipe reference |
| 2-7 | Reserved | MUST be zero |

### 8.3 InvocationResult Message (0x70)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | InvocationId | ushort | Corresponding request identifier |
| 1 | State | byte | Result state code |
| 2 | Result | byte[]? | Serialized return value or error |

**State Values:**

| Value | Name | Description |
|-------|------|-------------|
| 0 | Unset | Invalid state |
| 1 | CompletedResult | Successful completion with result |
| 2 | Exception | Remote exception occurred |

### 8.4 InvocationCancellation Message (0x6F)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | InvocationId | int | Invocation to cancel |

### 8.5 Invocation Flow

**Request-Response Pattern:**

```
Invoker                                    Handler
  |                                           |
  |-------- Invocation ---------------------->|
  |         (InvocationId=N, Flags=0x00)      |
  |                                           | Execute method
  |                                           |
  |<------- InvocationResult ----------------|
  |         (InvocationId=N, State=1)         |
  |                                           |
```

**Fire-and-Forget Pattern:**

```
Invoker                                    Handler
  |                                           |
  |-------- Invocation ---------------------->|
  |         (InvocationId=N, Flags=0x01)      |
  |                                           | Execute method
  |         (no response)                     |
  |                                           |
```

### 8.6 Invocation ID Management

- InvocationId is a 16-bit unsigned integer (0-65,535)
- Each endpoint maintains its own ID counter
- IDs are unique per session, per direction
- IDs MAY wrap around after 65,535

### 8.7 Method ID Assignment

- MethodId values are assigned at compile time
- IDs are derived from the interface definition
- Manual ID assignment is supported for backward compatibility
- IDs MUST be unique within an interface

### 8.8 Maximum Argument Size

Maximum argument payload: **65,521 bytes**

Calculated as: `65,535 (max body) - 2 (InvocationId) - 2 (MethodId) - 1 (Flags) - 9 (serialization overhead)`

---

## 9. Duplex Pipes Sub-Protocol

### 9.1 Overview

Duplex Pipes enable bidirectional streaming within a method invocation. Each pipe provides independent read and write channels.

### 9.2 Pipe Identification

**Pipe ID Format (ushort):**

```
 0                   1
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|   Client ID   |   Server ID   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

- **Client ID (byte 0):** Local ID assigned by the client (1-255)
- **Server ID (byte 1):** Local ID assigned by the server (1-255)
- **Value 0:** Reserved for initial negotiation phase

### 9.3 DuplexPipeWrite Message (0x32)

**Wire Format:**

```
 0                   1                   2
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|     0x32      |         Data Length           |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|            Pipe ID            |               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+               +
|                  Pipe Data                    |
+                   (variable)                  +
|                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

| Field | Size | Description |
|-------|------|-------------|
| Type | 1 byte | `0x32` |
| Data Length | 2 bytes | Length of Pipe ID + Pipe Data |
| Pipe ID | 2 bytes | Duplex pipe identifier |
| Pipe Data | Variable | Raw streaming data |

### 9.4 DuplexPipeUpdateState Message (0x78)

**Body Format (MemoryPack serialized):**

| Order | Field | Type | Description |
|-------|-------|------|-------------|
| 0 | PipeId | ushort | Target pipe identifier |
| 1 | State | byte | State flags (see below) |

### 9.5 Pipe State Flags

| Bit | Value | Name | Description |
|-----|-------|------|-------------|
| 0 | 0x01 | ClientWriterServerReaderComplete | Client→Server direction closed |
| 1 | 0x02 | ClientReaderServerWriterComplete | Server→Client direction closed |
| 2 | 0x04 | Ready | Pipe is ready for data transfer |
| 3 | 0x08 | ClientWriterPause | Client requests server pause sending |
| 4 | 0x10 | ServerWriterPause | Server requests client pause sending |
| 5-7 | - | Reserved | MUST be zero |

**Composite States:**

| Value | Meaning |
|-------|---------|
| 0x00 | Unset (initial) |
| 0x04 | Ready for data transfer |
| 0x07 | Complete (both directions closed) |

### 9.6 Pipe Lifecycle

```
                ┌─────────────┐
                │   Unset     │
                └──────┬──────┘
                       │ First message sent/received
                       ▼
                ┌─────────────┐
       ┌────────│  Partial ID │────────┐
       │        └─────────────┘        │
       │ Initiator                     │ Receiver
       │ (has local ID only)           │ (receives remote ID)
       │                               │
       │        ┌─────────────┐        │
       └───────>│    Ready    │<───────┘
                │   (0x04)    │
                └──────┬──────┘
                       │
       ┌───────────────┼───────────────┐
       │               │               │
       ▼               ▼               ▼
┌────────────┐  ┌────────────┐  ┌────────────┐
│  Paused    │  │  Half-     │  │  Complete  │
│(0x0C/0x14) │  │  Closed    │  │   (0x07)   │
└────────────┘  │(0x05/0x06) │  └────────────┘
                └────────────┘
```

### 9.7 Flow Control

**High Water Mark System:**

| Threshold | Default | Description |
|-----------|---------|-------------|
| High Water Mark | 192 KB | Trigger back-pressure |
| Low Water Mark | 16 KB | Resume transmission |
| Hard Cutoff | 256 KB | Block sender completely |

**Back-Pressure Flow:**

1. Receiver buffer exceeds High Water Mark
2. Receiver sends `DuplexPipeUpdateState` with pause flag
3. Sender pauses transmission
4. Receiver drains buffer below Low Water Mark
5. Receiver sends `DuplexPipeUpdateState` clearing pause flag
6. Sender resumes transmission

### 9.8 Chunking

- Data larger than chunk size is split into multiple `DuplexPipeWrite` messages
- Default chunk size: 8 KB (TCP), 4 KB (Unix Domain Socket)
- Chunks maintain the same Pipe ID
- Receiver reassembles chunks in order

---

## 10. Collection Synchronization Sub-Protocol

### 10.1 Overview

The Collection Synchronization sub-protocol enables distributed collections that stay synchronized across multiple endpoints. Operations use Operational Transform (OT) for conflict resolution.

### 10.2 Collection Identification

Collections are identified by a 16-bit unsigned integer (`ushort`) unique within a Nexus definition.

### 10.3 Synchronization Modes

| Mode | Description |
|------|-------------|
| ServerToClient | Server is authoritative; clients are read-only |
| BiDirectional | Both endpoints can modify; conflicts resolved via OT |
| Relay | Proxy mode for hierarchical topologies |

### 10.4 Transport Mechanism

Collection messages are transmitted over a dedicated Duplex Pipe per collection. The pipe is established via a method invocation that returns `INexusDuplexPipe`.

### 10.5 Collection Message Types

Collection messages use a MemoryPack union type with discriminator byte:

| Discriminator | Message Type | Description |
|---------------|--------------|-------------|
| 0 | ResetStart | Begin full state synchronization |
| 1 | ResetValues | Batch of items during reset |
| 2 | ResetComplete | End of reset sequence |
| 3 | Clear | Remove all items |
| 4 | Insert | Add item at index |
| 5 | Replace | Modify item at index |
| 6 | Move | Reorder item |
| 7 | Remove | Delete item at index |
| 8 | Noop | Operation invalidated by OT |

### 10.6 Message Formats

#### 10.6.1 Common Fields

All collection messages contain:

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags (bit 0 = Ack) |

#### 10.6.2 ResetStart Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Server's current version |
| TotalValues | int | Expected total item count |

#### 10.6.3 ResetValues Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Values | byte[] | Serialized array of items (batch of ~40) |

#### 10.6.4 ResetComplete Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |

#### 10.6.5 Insert Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Base version for OT |
| Index | int | Position to insert (-1 = append) |
| Value | byte[] | Serialized item |

#### 10.6.6 Replace Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Base version for OT |
| Index | int | Position to replace |
| Value | byte[] | Serialized new value |

#### 10.6.7 Move Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Base version for OT |
| FromIndex | int | Source position |
| ToIndex | int | Destination position |

#### 10.6.8 Remove Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Base version for OT |
| Index | int | Position to remove |

#### 10.6.9 Clear Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |
| Version | int | Base version for OT |

#### 10.6.10 Noop Message

| Field | Type | Description |
|-------|------|-------------|
| Flags | byte | Message flags |

### 10.7 Version System

- Each collection maintains a monotonically-increasing 32-bit version counter
- Version increments with each successfully applied operation
- Server maintains operation history for OT rebasing (default: 1024 operations)

### 10.8 Operational Transform

When a client operation is based on an older version:

1. Server retrieves operations from client's version to current
2. Client operation is transformed against each historical operation
3. If transformation invalidates the operation, Noop is sent
4. If transformation succeeds, operation is applied and broadcast

**Transformation Rules:**

| Operation | Against Insert | Against Remove | Against Move |
|-----------|----------------|----------------|--------------|
| Insert | Adjust index if insert was before | Adjust index if remove was before | Recalculate index |
| Remove | Adjust index if insert was before | Noop if same index | Recalculate index |
| Replace | Adjust index if insert was before | Noop if same index | Recalculate index |
| Move | Adjust both indices | Recalculate indices | Recalculate indices |
| Clear | No transform needed | No transform needed | No transform needed |

### 10.9 Initial Synchronization

```
Server                                     Client
  |                                           |
  |-------- ResetStart ---------------------->|
  |         (Version=V, TotalValues=N)        |
  |                                           |
  |-------- ResetValues -------------------->|
  |         (batch 1 of ~40 items)            |
  |                                           |
  |-------- ResetValues -------------------->|
  |         (batch 2 of ~40 items)            |
  |         ...                               |
  |                                           |
  |-------- ResetComplete ------------------>|
  |                                           |
  |<======= Collection Synchronized =========>|
```

### 10.10 Acknowledgment

- When `Flags` bit 0 (Ack) is set, the message is an acknowledgment
- Server sends Ack back to the originating client for their operations
- Other clients receive the operation without the Ack flag

---

## 11. Keep-Alive Mechanism

### 11.1 Ping Message (0x01)

**Format:** Header-only (1 byte type, no body)

```
+-+-+-+-+-+-+-+-+
|     0x01      |
+-+-+-+-+-+-+-+-+
```

### 11.2 Ping Behavior

**Client:**
- Sends Ping at regular intervals (default: 10,000ms)
- Checks for inactivity timeout before each Ping

**Server:**
- Echoes received Ping back to client
- Does NOT initiate Pings

### 11.3 Inactivity Detection

Both endpoints track the timestamp of the last received message:

1. Timer fires at configured interval
2. Calculate: `threshold = now - timeout`
3. If `lastReceived < threshold`: disconnect with `Timeout`

**Default Values:**
- Ping Interval: 10,000ms (client only)
- Inactivity Timeout: 30,000ms (both endpoints)

---

## 12. Disconnection Protocol

### 12.1 Disconnect Messages

All disconnect messages are header-only (no body):

| Type | Name | Description |
|------|------|-------------|
| 0x14 | SocketError | Underlying transport error |
| 0x15 | Graceful | Intentional clean shutdown |
| 0x16 | ProtocolError | Protocol violation detected |
| 0x17 | Timeout | No activity within timeout period |
| 0x18 | ClientMismatch | Client interface incompatible |
| 0x19 | ServerMismatch | Server interface incompatible |
| 0x1C | ServerShutdown | Server is shutting down |
| 0x1D | Authentication | Authentication failed |
| 0x1E | ServerRestarting | Server is restarting |
| 0x20 | SocketClosedWhenWriting | Write operation failed |

### 12.2 Disconnection Sequence

1. Endpoint decides to disconnect
2. Send appropriate disconnect message (optional)
3. Optional delay for message delivery
4. Cancel pending operations
5. Close transport connection

### 12.3 Protocol Error Conditions

An endpoint MUST disconnect with `ProtocolError` when:

- Invalid protocol header received
- Message received before handshake complete
- Duplicate greeting message received
- Unknown message type received
- Invalid message structure detected
- Method invocation for unavailable method/version

---

## 13. State Machines

### 13.1 Connection State Machine

```
           ┌──────────────────┐
           │      Unset       │
           └────────┬─────────┘
                    │ Connect initiated
                    ▼
           ┌──────────────────┐
┌──────────│   Connecting     │──────────┐
│          └────────┬─────────┘          │
│                   │ Handshake          │ Timeout/Error
│                   │ complete           │
│                   ▼                    ▼
│          ┌──────────────────┐  ┌──────────────────┐
│          │    Connected     │  │   Disconnected   │
│          └────────┬─────────┘  └──────────────────┘
│                   │                    ▲
│                   │ Disconnect         │
│                   │ initiated          │
│                   ▼                    │
│          ┌──────────────────┐          │
└─────────>│  Disconnecting   │──────────┘
           └──────────────────┘
┌──────────────────┐
│   Reconnecting   │  (Reserved for future use)
└──────────────────┘
```

### 13.2 Handshake State Machine (Server)

```
┌──────────────────┐
│ AwaitingHeader   │
└────────┬─────────┘
         │ Valid header received
         ▼
┌──────────────────┐
│ AwaitingGreeting │
└────────┬─────────┘
         │ Valid ClientGreeting
         │ + Authentication passed
         ▼
┌──────────────────┐
│  SendGreeting    │
└────────┬─────────┘
         │ ServerGreeting sent
         ▼
┌──────────────────┐
│    Complete      │
└──────────────────┘
```

### 13.3 Invocation State Machine

```
                    ┌──────────────────┐
                    │      Idle        │
                    └────────┬─────────┘
                             │ Send Invocation
                             ▼
                    ┌──────────────────┐
         ┌──────────│    Pending       │──────────┐
         │          └────────┬─────────┘          │
         │                   │                    │
Cancellation                 │ Result             │ Timeout/
requested                    │ received           │ Disconnect
         │                   │                    │
         ▼                   ▼                    ▼
┌──────────────┐   ┌──────────────────┐  ┌──────────────────┐
│  Cancelling  │   │    Completed     │  │     Failed       │
└──────────────┘   └──────────────────┘  └──────────────────┘
```

### 13.4 Duplex Pipe State Machine

```
                    ┌──────────────────┐
                    │      Unset       │
                    └────────┬─────────┘
                             │ Pipe created
                             ▼
                    ┌──────────────────┐
                    │   Partial ID     │
                    └────────┬─────────┘
                             │ Ready state received
                             ▼
                    ┌──────────────────┐
         ┌──────────│      Ready       │──────────┐
         │          └────────┬─────────┘          │
         │                   │                    │
    Pause flag          Direction              Both directions
    received            completed              completed
         │                   │                    │
         ▼                   ▼                    ▼
┌──────────────┐   ┌──────────────────┐  ┌──────────────────┐
│    Paused    │   │   Half-Closed    │  │    Complete      │
└──────────────┘   └────────┬─────────┘  └──────────────────┘
                            │ Other direction
                            │ completed
                            ▼
                   ┌──────────────────┐
                   │    Complete      │
                   └──────────────────┘
```

---

## 14. Security Considerations & Threat Model

### 14.1 Threat Model Overview

This section identifies potential threats and recommended mitigations for NexNet protocol implementations.

### 14.2 Transport Security

**Threat:** Eavesdropping and man-in-the-middle attacks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Data interception | Confidentiality breach | Use TLS 1.2+ for transport encryption |
| Message tampering | Integrity violation | TLS provides authenticated encryption |
| Session hijacking | Unauthorized access | TLS mutual authentication |

**Recommendation:** Implementations SHOULD use TLS for all production deployments.

### 14.3 Authentication Threats

**Threat:** Unauthorized access to server resources

| Risk | Impact | Mitigation |
|------|--------|------------|
| Missing authentication | Unauthorized access | Require authentication token |
| Weak tokens | Token forgery | Use cryptographically secure tokens (JWT, etc.) |
| Token replay | Session hijacking | Include timestamp/nonce in tokens |
| Credential exposure | Account compromise | Transmit tokens only over encrypted transport |

**Recommendation:**
- Use secure token formats (JWT with appropriate algorithms)
- Validate token expiration
- Implement token revocation mechanisms

### 14.4 Protocol-Level Threats

**Threat:** Denial of service through protocol abuse

| Risk | Impact | Mitigation |
|------|--------|------------|
| Large message flood | Memory exhaustion | Enforce maximum message size (65KB) |
| Invocation flood | CPU exhaustion | Limit concurrent invocations per session |
| Pipe exhaustion | Resource exhaustion | Limit concurrent pipes (255 per endpoint) |
| Slow client attack | Connection pool exhaustion | Implement handshake timeout |
| Malformed messages | Parser crashes | Validate all input before processing |

**Recommended Limits:**

| Resource | Recommended Limit |
|----------|-------------------|
| Max concurrent invocations | 100 per session |
| Max concurrent pipes | 255 per endpoint |
| Handshake timeout | 5,000ms |
| Inactivity timeout | 30,000ms |
| Max message body | 65,535 bytes |

### 14.5 Collection Synchronization Threats

**Threat:** Data integrity attacks on synchronized collections

| Risk | Impact | Mitigation |
|------|--------|------------|
| Version manipulation | Operation ordering attacks | Server validates version ranges |
| Stale operation replay | Data corruption | Operation history with bounded window |
| Excessive history | Memory exhaustion | Limit operation history size |

**Recommendation:** Limit operation history to 1024 entries.

### 14.6 Information Disclosure

**Threat:** Leaking sensitive information through protocol messages

| Risk | Impact | Mitigation |
|------|--------|------------|
| Exception details | Internal information leak | Sanitize exception messages sent to clients |
| Method enumeration | API discovery | Hash-based validation prevents probing |
| Timing attacks | Information inference | Constant-time hash comparison |

### 14.7 Input Validation Requirements

Implementations MUST validate:

1. **Protocol header:** Magic bytes and version
2. **Message type:** Within valid range for current state
3. **Body length:** Does not exceed maximum
4. **Serialized data:** Well-formed MemoryPack structure
5. **InvocationId:** Within tracking capacity
6. **MethodId:** Exists in current version's method set
7. **Pipe ID:** Valid for current session
8. **Collection indices:** Within bounds

### 14.8 Secure Implementation Checklist

- Use TLS for transport encryption
- Implement authentication token validation
- Enforce message size limits
- Limit concurrent invocations
- Implement timeouts (handshake, inactivity)
- Validate all deserialized data
- Sanitize exception messages
- Use constant-time comparison for hashes
- Log security-relevant events
- Implement rate limiting

---

## 15. Reserved Ranges & Future Extensions

### 15.1 Reserved Message Type Ranges

| Range | Current Use | Future Purpose |
|-------|-------------|----------------|
| 0x02-0x13 | Unused | Additional control messages |
| 0x21-0x27 | Unused | Additional disconnect reasons |
| 0x28-0x31 | Unused | Reserved |
| 0x33-0x3F | Unused | Additional streaming message types |
| 0x40-0x63 | Unused | Reserved for application use |
| 0x66-0x68 | Unused | Additional greeting/connection messages |
| 0x71-0x77 | Unused | Additional invocation-related messages |
| 0x79-0x7F | Unused | Additional pipe control messages |
| 0x80-0xFF | Unused | Reserved for future protocol versions |

### 15.2 Reserved Features

#### 15.2.1 Reconnection (Reserved)

Message type `0x65` (ClientGreetingReconnection) is reserved for future session reconnection functionality. This would allow:

- Resuming sessions after brief disconnections
- Maintaining invocation state across reconnects
- Replaying missed messages

**Current Status:** Not implemented. Implementations MUST NOT use this message type.

#### 15.2.2 Protocol Version 2

Reserved byte values in the protocol header (bytes 4-6) and message types (0x80-0xFF) provide room for future protocol revisions.

### 15.3 Extension Guidelines

When extending the protocol:

1. Use reserved message type ranges
2. Maintain backward compatibility where possible
3. Increment protocol version for breaking changes
4. Document all extensions in this specification

---

## Appendix A: Wire Format Examples

### A.1 Protocol Header

```
4E 6E 50 14 00 00 00 01
│  │  │  │  │  │  │  └─ Version: 1
│  │  │  │  └──┴──┴──── Reserved: 0x000000
└──┴──┴──┴───────────── Magic: "NnP" + DC4
```

### A.2 ClientGreeting Message

```
64 1A 00 ...
│  │     └─ MemoryPack serialized body (26 bytes)
│  └─────── Body length: 0x001A (26)
└────────── Type: ClientGreeting (0x64)
```

### A.3 Invocation Message

```
6E 15 00 01 00 7B 00 00 ...
│  │     │     │     │  └─ Arguments (variable)
│  │     │     │     └──── Flags: 0x00 (None)
│  │     │     └────────── MethodId: 0x007B (123)
│  │     └──────────────── InvocationId: 0x0001
│  └────────────────────── Body length: 0x0015 (21)
└──────────────────────────Type: Invocation (0x6E)
```

### A.4 Ping Message

```
01
└─ Type: Ping (0x01)
```

### A.5 DuplexPipeWrite Message

```
32 66 00 01 05 ...
│  │     │     └─ Pipe data (100 bytes)
│  │     └─────── Pipe ID: 0x0501
│  └───────────── Data length: 0x0066 (102 = 2 + 100)
└──────────────── Type: DuplexPipeWrite (0x32)
```

---

## Appendix B: Glossary

| Term | Definition |
|------|------------|
| **Back-pressure** | Flow control mechanism to prevent buffer overflow |
| **Duplex Pipe** | Bidirectional streaming channel within a session |
| **Fire-and-forget** | Invocation pattern where response is not awaited |
| **Handshake** | Initial connection establishment sequence |
| **Hash** | Interface version identifier for compatibility checking |
| **Invocation** | Remote method call |
| **MemoryPack** | Binary serialization format used for message bodies |
| **Nexus** | Interface definition exposed by an endpoint |
| **OT (Operational Transform)** | Algorithm for resolving concurrent edit conflicts |
| **Ping** | Keep-alive message to detect connection health |
| **Proxy** | Interface for invoking methods on remote endpoint |
| **Session** | Logical connection between client and server |