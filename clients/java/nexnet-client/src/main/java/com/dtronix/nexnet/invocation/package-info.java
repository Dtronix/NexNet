/**
 * The invocation runtime: calls to the server ({@link com.dtronix.nexnet.invocation.InvocationRuntime} implements
 * {@link com.dtronix.nexnet.spi.ProxyInvoker}), invocations of the client nexus by the server, invocation credit,
 * cancellation and large (streamed) arguments and results. Protocol specification sections 4.3 to 4.10, 6 and 8.
 *
 * <p>Runtime-internal, apart from {@link com.dtronix.nexnet.invocation.NexusRemoteError},
 * {@link com.dtronix.nexnet.invocation.RemoteExceptionDetail} and
 * {@link com.dtronix.nexnet.invocation.InvocationCounters}. The pipe (K08) and collection (K09) runtimes attach
 * through {@link com.dtronix.nexnet.invocation.InvocationRuntime#setNextHandler},
 * {@link com.dtronix.nexnet.invocation.InvocationRuntime#invokeIgnoreReturn} and
 * {@link com.dtronix.nexnet.invocation.InvocationRuntime#budget()}.
 */
package com.dtronix.nexnet.invocation;
