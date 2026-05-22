using NexNet.Pipes;

namespace NexNet.Internals;

/// <summary>
/// Optional internal hook that wraps duplex pipes at the boundary where they are returned to
/// user code. When installed via <see cref="NexNet.Transports.ConfigBase.PipeFactory"/>, the
/// pipe manager passes pipes through this factory before handing them to a handler, allowing
/// the caller (e.g., the test harness) to interpose tap wrappers that record bytes the handler
/// reads/writes and to track the lifetime of in-flight pipes for quiescence purposes.
/// </summary>
/// <remarks>
/// Wrappers are expected to compose the inner pipe (delegating all interface members) rather
/// than substitute for it: the pipe manager's internal active-pipe registry continues to hold
/// the underlying concrete pipe so incoming-data routing is unaffected. Only the reference
/// surfaced to user code is the wrapper.
/// </remarks>
internal interface IPipeFactory
{
    /// <summary>
    /// Wraps a pipe that the local side just rented via <see cref="NexusPipeManager.RentPipe"/>.
    /// </summary>
    /// <param name="inner">The freshly-rented pipe.</param>
    /// <returns>A wrapper to expose to user code, or <paramref name="inner"/> unchanged.</returns>
    IRentedNexusDuplexPipe WrapLocal(IRentedNexusDuplexPipe inner);

    /// <summary>
    /// Wraps a pipe that the local side just registered in response to a remote pipe request
    /// (see <see cref="NexusPipeManager.RegisterPipe"/>).
    /// </summary>
    /// <param name="inner">The pipe just made ready for the responder handler.</param>
    /// <returns>A wrapper to expose to user code, or <paramref name="inner"/> unchanged.</returns>
    INexusDuplexPipe WrapRemote(INexusDuplexPipe inner);
}
