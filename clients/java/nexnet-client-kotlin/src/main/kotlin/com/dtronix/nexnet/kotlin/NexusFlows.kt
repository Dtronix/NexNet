@file:JvmName("NexusFlows")

package com.dtronix.nexnet.kotlin

import com.dtronix.nexnet.collections.NexusList
import com.dtronix.nexnet.collections.NexusListChange
import com.dtronix.nexnet.pipes.NexusChannelReader
import com.dtronix.nexnet.pipes.NexusChannelWriter
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.buffer
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.future.await

/**
 * The items of a channel reader as a cold [Flow] that completes when the channel completes. Collect it once: reading
 * consumes the items. Cancelling the collector cancels the pending read; an item that arrives for it is dropped.
 * Null items (a nullable item type) are emitted as null.
 *
 * ```kotlin
 * channel.reader().await().asFlow().collect { item -> println(item) }
 * ```
 *
 * @return the items
 */
public fun <T> NexusChannelReader<T>.asFlow(): Flow<T> = flow {
    while (true) {
        val result = readItem().await()
        if (result.isCompleted) {
            break
        }
        @Suppress("UNCHECKED_CAST")
        emit(result.value() as T)
    }
}

/**
 * Writes every item of a flow to the channel, each write suspending until the channel accepts it (flow control), and
 * completes the channel afterwards when [complete] is true.
 *
 * @param items the items
 * @param complete whether to complete the channel after the last item
 */
public suspend fun <T> NexusChannelWriter<T>.writeAll(items: Flow<T>, complete: Boolean = false) {
    items.collect { item -> write(item).await() }
    if (complete) {
        complete().await()
    }
}

/**
 * The changes of a synchronized list as a hot [Flow]: the changes after collection starts, in order, never dropped.
 * The flow never completes; it stops listening when its collector is cancelled. The list itself is the current state:
 * read it when [NexusListChange.Action.READY] or [NexusListChange.Action.RESET] arrives.
 *
 * @return the changes
 */
public fun <T> NexusList<T>.changes(): Flow<NexusListChange<T>> = callbackFlow {
    val registration = addChangeListener { change -> trySend(change) }
    awaitClose { registration.close() }
}.buffer(Channel.UNLIMITED)
