# NexNet v0.20.0

*Unreleased*

## Behaviour changes

- **`MaxConcurrentConnectionInvocations` defaults to 32 with Trusted serializer options.** With `SerializerOptions = NexusSerializerOptions.Trusted` and no explicit value, up to 32 invocations from one connection now run at once on the receiving side (was 2). Untrusted configurations keep 2, and an explicit value always wins. Set `MaxConcurrentConnectionInvocations = 2` to keep the old behaviour.

## New features

- **`ConfigBase.InvocationQueueWarningThreshold`.** An invocation that waits longer than this (5000 ms default, 0 disables, 0 – 600000) for an execution slot logs a warning naming the method and the wait, at most once every 30 seconds per session, with the number of slow invocations since the last warning.

## Diagnostics

- **`nexnet.invocation.queue_wait` histogram** (`s`, tags `nexnet.role`, `nexnet.transport`): time each received invocation waited from being queued until it took an execution slot.
- **`nexnet.pipe.credit_wait` histogram** (`s`, tags `nexnet.role`, `nexnet.transport`): time a pipe writer waited for the peer to grant window credit, recorded when the wait ends.
- **`NexusInvocationSnapshot.OldestQueuedWait`**: how long the oldest queued invocation has waited for a slot, or null when none is queued.

## Documentation

- Getting Started has a section on invocation concurrency: what `MaxConcurrentConnectionInvocations` limits, its defaults and when to raise it.
- Hub Invocations explains that a caller's cancellation reaches the remote method only through its `CancellationToken` parameter, also while the invocation is queued.
