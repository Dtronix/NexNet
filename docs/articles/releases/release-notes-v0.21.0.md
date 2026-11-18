# NexNet v0.21.0

*Unreleased*

## Behaviour changes

- **Behavior change: a call canceled before it is sent throws `TaskCanceledException`.** When the `CancellationToken` of a `ValueTask` or `ValueTask<T>` method is already canceled, or is canceled while the call waits for invocation credit, the call now throws `TaskCanceledException`. Before, `ValueTask` completed successfully and `ValueTask<T>` returned `default`, so a canceled call could not be told apart from a real result. As before, nothing is sent and no invocation ID is used. Fire-and-forget (`void`) methods and broadcasts are unchanged. See [Hub Invocations: Cancellation](../hub-invocations.md#cancellation).

## New features

## Fixes

- **Duplicate nexus method names are reported as NEXNET042 instead of crashing the generator.** A nexus interface that overloads a method name, or declares a name that an inherited interface also declares, made the source generator throw, so the build reported only a generator failure (CS8785). It is now the error NEXNET042, reported at the duplicate method, and that nexus is not generated. Method names must be unique across a nexus interface and its inherited interfaces. A nexus class that overloads the name of an authorized method no longer crashes the generator either; the authorization of the overload that implements the interface method is used.
- **Generator diagnostics are reported at their source location.** Nexus validation and serialization diagnostics (NEXNET001 – NEXNET042) had no location, so the build and the IDE showed them without a file or line. They now point at the method, member, interface or class they are about.

## Documentation
