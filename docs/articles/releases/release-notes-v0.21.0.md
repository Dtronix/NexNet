# NexNet v0.21.0

*Unreleased*

## Behaviour changes

- **Behavior change: a call canceled before it is sent throws `TaskCanceledException`.** When the `CancellationToken` of a `ValueTask` or `ValueTask<T>` method is already canceled, or is canceled while the call waits for invocation credit, the call now throws `TaskCanceledException`. Before, `ValueTask` completed successfully and `ValueTask<T>` returned `default`, so a canceled call could not be told apart from a real result. As before, nothing is sent and no invocation ID is used. Fire-and-forget (`void`) methods and broadcasts are unchanged. See [Hub Invocations: Cancellation](../hub-invocations.md#cancellation).

## New features

## Fixes

## Documentation
