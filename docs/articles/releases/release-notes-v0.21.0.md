# NexNet v0.21.0

*Unreleased*

## Behaviour changes

- **Behavior change: a call canceled before it is sent throws `TaskCanceledException`.** When the `CancellationToken` of a `ValueTask` or `ValueTask<T>` method is already canceled, or is canceled while the call waits for invocation credit, the call now throws `TaskCanceledException`. Before, `ValueTask` completed successfully and `ValueTask<T>` returned `default`, so a canceled call could not be told apart from a real result. As before, nothing is sent and no invocation ID is used. Fire-and-forget (`void`) methods and broadcasts are unchanged. See [Hub Invocations: Cancellation](../hub-invocations.md#cancellation).
- **An unauthorized pipe, channel or collection connect refuses the caller's pipe.** When `OnAuthorize` returns `AuthorizeResult.Unauthorized` for a method with an `INexusDuplexPipe` or `INexusDuplexChannel<T>` parameter, or for a synchronized collection connect, the server now sends `Refused` for the caller's pipe, as it does when the pipe limit is reached. The caller's `ReadyTask` (for a channel, `BasePipe.ReadyTask`; for a collection, `EnableAsync()`) fails at once with `NexusBusyException` and the session stays up. Before, nothing was sent and `ReadyTask` waited until the session ended. The wire protocol is unchanged, so the client cannot tell an authorization refusal from a limit refusal. `AuthorizeResult.Disconnect` still disconnects the session. See [Authorization](../authorization.md#authorization-results).

## New features

## Fixes

## Documentation