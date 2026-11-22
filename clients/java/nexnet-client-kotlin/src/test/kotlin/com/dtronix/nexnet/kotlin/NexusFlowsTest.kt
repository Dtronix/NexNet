package com.dtronix.nexnet.kotlin

import com.dtronix.nexnet.Registration
import com.dtronix.nexnet.collections.NexusList
import com.dtronix.nexnet.collections.NexusListChange
import com.dtronix.nexnet.pipes.ChannelReadResult
import com.dtronix.nexnet.pipes.NexusChannelReader
import com.dtronix.nexnet.pipes.NexusChannelWriter
import java.lang.reflect.Proxy
import java.util.Optional
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.function.Consumer
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class NexusFlowsTest {
    /** A reader over a fixed list of items, then the end of the channel. */
    private class ListReader<T>(items: List<T>) : NexusChannelReader<T> {
        private val remaining = ArrayDeque(items)

        override fun read(): CompletableFuture<Optional<T & Any>> = throw UnsupportedOperationException()

        override fun readItem(): CompletableFuture<ChannelReadResult<T>> = CompletableFuture.supplyAsync {
            if (remaining.isEmpty()) ChannelReadResult.completed() else ChannelReadResult.of(remaining.removeFirst())
        }

        override fun readBatch(max: Int): CompletableFuture<List<T>> = throw UnsupportedOperationException()

        override fun readUntilComplete(): CompletableFuture<List<T>> = throw UnsupportedOperationException()
    }

    private class ListWriter<T> : NexusChannelWriter<T> {
        val written = CopyOnWriteArrayList<T>()
        var completed = false

        override fun write(item: T): CompletableFuture<Void> {
            written.add(item)
            return CompletableFuture.completedFuture(null)
        }

        override fun writeAll(items: Iterable<T>): CompletableFuture<Void> = throw UnsupportedOperationException()

        override fun complete(): CompletableFuture<Void> {
            completed = true
            return CompletableFuture.completedFuture(null)
        }

        override fun writeAndComplete(items: Iterable<T>): CompletableFuture<Void> =
            throw UnsupportedOperationException()
    }

    @Test
    fun readerAsFlowEmitsUntilComplete() = runBlocking {
        assertEquals(listOf(1L, 2L, 3L), ListReader(listOf(1L, 2L, 3L)).asFlow().toList())
        assertEquals(listOf("a", null, "b"), ListReader(listOf("a", null, "b")).asFlow().toList())
        assertEquals(emptyList<Int>(), ListReader(emptyList<Int>()).asFlow().toList())
    }

    @Test
    fun writeAllWritesTheFlow() = runBlocking {
        val writer = ListWriter<Int>()
        writer.writeAll(flowOf(1, 2, 3))
        assertEquals(listOf(1, 2, 3), writer.written)
        assertEquals(false, writer.completed)
        writer.writeAll(flowOf(4), complete = true)
        assertEquals(listOf(1, 2, 3, 4), writer.written)
        assertTrue(writer.completed)
    }

    @Test
    fun listChangesFlowUntilCancelled() = runBlocking {
        val listeners = CopyOnWriteArrayList<Consumer<NexusListChange<String>>>()
        @Suppress("UNCHECKED_CAST")
        val list = Proxy.newProxyInstance(javaClass.classLoader, arrayOf(NexusList::class.java)) { _, method, args ->
            when (method.name) {
                "addChangeListener" -> {
                    val listener = args[0] as Consumer<NexusListChange<String>>
                    listeners.add(listener)
                    Registration { listeners.remove(listener) }
                }
                else -> throw UnsupportedOperationException(method.name)
            }
        } as NexusList<String>

        val collected = async(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) {
            list.changes().take(3).toList()
        }
        withTimeout(5_000) {
            while (listeners.isEmpty()) {
                yield()
            }
        }
        listeners.forEach { it.accept(NexusListChange(NexusListChange.Action.READY, -1, -1, null, null)) }
        listeners.forEach { it.accept(NexusListChange(NexusListChange.Action.ADD, 0, -1, "a", null)) }
        listeners.forEach { it.accept(NexusListChange(NexusListChange.Action.REMOVE, -1, 0, null, "a")) }

        val changes = withTimeout(5_000) { collected.await() }
        assertEquals(
            listOf(NexusListChange.Action.READY, NexusListChange.Action.ADD, NexusListChange.Action.REMOVE),
            changes.map { it.action() },
        )
        assertEquals("a", changes[1].item())
        withTimeout(5_000) {
            while (listeners.isNotEmpty()) {
                yield()
            }
        }
    }
}
