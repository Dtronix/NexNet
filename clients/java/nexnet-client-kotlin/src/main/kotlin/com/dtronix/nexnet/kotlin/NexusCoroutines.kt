@file:JvmName("NexusCoroutines")

package com.dtronix.nexnet.kotlin

import com.dtronix.nexnet.CancellationToken
import com.dtronix.nexnet.CancellationTokenSource
import com.dtronix.nexnet.client.ConnectionResult
import com.dtronix.nexnet.client.ConnectionState
import com.dtronix.nexnet.client.NexusClient
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.buffer
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.suspendCancellableCoroutine

/**
 * Runs a NexNet call that takes a [CancellationToken] and suspends until its future completes. Cancelling the
 * coroutine cancels the token, which cancels the invocation remotely (or fails it before it is sent, D17), and cancels
 * the future.
 *
 * ```kotlin
 * val value = nexusCall { token -> client.proxy().delay(1000, 42, token) }
 * ```
 *
 * For a method without a token parameter, `proxy.echo("x").await()` (kotlinx-coroutines) is enough: cancelling the
 * coroutine cancels the future, which cancels the invocation remotely too.
 *
 * @param block starts the call with the token
 * @return the result of the call
 */
public suspend fun <T> nexusCall(block: (CancellationToken) -> CompletableFuture<T>): T {
    val source = CancellationTokenSource()
    try {
        val future = block(source.token())
        return suspendCancellableCoroutine { continuation ->
            future.whenComplete { value, error ->
                if (error == null) {
                    continuation.resume(value)
                } else {
                    continuation.resumeWithException(unwrap(error))
                }
            }
            continuation.invokeOnCancellation {
                try {
                    source.cancel()
                } catch (ignored: RuntimeException) {
                    // A token callback failed; the future is canceled below regardless.
                }
                future.cancel(false)
            }
        }
    } finally {
        source.close()
    }
}

/**
 * Connects, suspending until connected. Cancelling the coroutine cancels the attempt.
 *
 * @throws com.dtronix.nexnet.NexusException when the connection fails, as [NexusClient.connect] does
 */
public suspend fun NexusClient<*>.connectAwait() {
    nexusCall { token -> connect(token) }
}

/**
 * Connects and reports the outcome instead of throwing. Cancelling the coroutine cancels the attempt.
 *
 * @return the result
 */
public suspend fun NexusClient<*>.tryConnectAwait(): ConnectionResult = nexusCall { token -> tryConnect(token) }

/**
 * Disconnects gracefully and suspends until disconnected. Cancelling the coroutine stops the wait, not the disconnect.
 */
public suspend fun NexusClient<*>.disconnectAwait() {
    disconnect().awaitShared()
}

/**
 * Suspends until the current connection closes ([NexusClient.disconnected]); returns at once when not connected.
 * Cancelling the coroutine stops the wait only.
 */
public suspend fun NexusClient<*>.awaitDisconnected() {
    disconnected().awaitShared()
}

/**
 * The connection state: the current state, then each change. Repeated values are dropped. The flow never completes;
 * it stops listening when its collector is cancelled.
 *
 * @return the states
 */
public fun NexusClient<*>.states(): Flow<ConnectionState> = callbackFlow {
    val registration = addStateListener { state -> trySend(state) }
    trySend(state())
    awaitClose { registration.close() }
}.buffer(Channel.UNLIMITED).distinctUntilChanged()

/** Awaits a future others may share: cancelling the coroutine stops the wait and leaves the future alone. */
internal suspend fun <T> CompletableFuture<T>.awaitShared(): T = suspendCancellableCoroutine { continuation ->
    whenComplete { value, error ->
        if (error == null) {
            continuation.resume(value)
        } else {
            continuation.resumeWithException(unwrap(error))
        }
    }
}

internal fun unwrap(error: Throwable): Throwable =
    if ((error is CompletionException || error is ExecutionException) && error.cause != null) error.cause!! else error
