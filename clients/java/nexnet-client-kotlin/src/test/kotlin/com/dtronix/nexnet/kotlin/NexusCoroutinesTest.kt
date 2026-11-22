package com.dtronix.nexnet.kotlin

import com.dtronix.nexnet.CancellationToken
import com.dtronix.nexnet.DisconnectReason
import com.dtronix.nexnet.NexusRuntime
import com.dtronix.nexnet.NexusTransportException
import com.dtronix.nexnet.client.ClientConfig
import com.dtronix.nexnet.client.ConnectionState
import com.dtronix.nexnet.client.NexusClient
import com.dtronix.nexnet.serialization.MsgPackReader
import com.dtronix.nexnet.spi.ClientNexusBase
import com.dtronix.nexnet.spi.HandshakeDescriptor
import com.dtronix.nexnet.spi.InvocationContext
import com.dtronix.nexnet.spi.ProxyBase
import com.dtronix.nexnet.spi.ProxyInvoker
import com.dtronix.nexnet.transport.Transport
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.net.ConnectException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.takeWhile
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class NexusCoroutinesTest {
    @Test
    fun nexusCallReturnsTheResult() = runBlocking {
        var seen: CancellationToken? = null
        val value = nexusCall { token ->
            seen = token
            CompletableFuture.completedFuture(42)
        }
        assertEquals(42, value)
        val token = seen!!
        assertTrue(token.canBeCanceled())
        assertFalse(token.isCancellationRequested)
    }

    @Test
    fun nexusCallThrowsTheCause() {
        val error = IllegalStateException("boom")
        val thrown = assertThrows<IllegalStateException> {
            runBlocking {
                nexusCall<Int> { CompletableFuture.supplyAsync { throw error } }
            }
        }
        // Coroutines stack-trace recovery may hand back a copy of the exception.
        assertEquals("boom", thrown.message)
    }

    @Test
    fun cancellingTheCoroutineCancelsTheTokenAndTheFuture() = runBlocking {
        val future = CompletableFuture<Int>()
        val tokenCanceled = CountDownLatch(1)
        val started = CountDownLatch(1)
        val job = launch(Dispatchers.Default) {
            nexusCall<Int> { token ->
                token.register { tokenCanceled.countDown() }
                started.countDown()
                future
            }
        }
        assertTrue(started.await(5, TimeUnit.SECONDS))
        job.cancelAndJoin()
        assertTrue(tokenCanceled.await(5, TimeUnit.SECONDS))
        assertTrue(future.isCancelled)
    }

    @Test
    fun connectAwaitFailsLikeConnect() {
        val client = client { _, _ -> throw ConnectException("refused") }
        client.use {
            assertThrows<NexusTransportException> { runBlocking { it.connectAwait() } }
            val result = runBlocking { it.tryConnectAwait() }
            assertFalse(result.isSuccess)
        }
    }

    @Test
    fun connectStatesAndDisconnect() = runBlocking {
        val client = client { _, _ -> GreetingTransport() }
        client.use {
            val states = async(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) {
                it.states().takeWhile { state -> state != ConnectionState.CONNECTED }.toList()
            }
            withTimeout(10_000) { it.connectAwait() }
            val before = withTimeout(10_000) { states.await() }
            assertTrue(before.contains(ConnectionState.CONNECTING), before.toString())
            assertEquals(ConnectionState.CONNECTED, withTimeout(10_000) { it.states().first() })

            withTimeout(10_000) { it.disconnectAwait() }
            withTimeout(10_000) { it.awaitDisconnected() }
            assertEquals(ConnectionState.DISCONNECTED, it.state())
        }
    }

    private class TestProxy(invoker: ProxyInvoker) : ProxyBase(invoker)

    private class TestNexus : ClientNexusBase<TestProxy>() {
        override fun dispatch(methodId: Int, arguments: MsgPackReader, context: InvocationContext): CompletableFuture<Void> =
            throw IllegalArgumentException("Unknown method id $methodId")
    }

    /** An in-memory transport whose server answers the handshake with the golden ServerGreeting. */
    private class GreetingTransport : Transport {
        private val toClient = PipedOutputStream()
        private val clientInput = PipedInputStream(toClient, 64 * 1024)
        private val fromClient = PipedInputStream(64 * 1024)
        private val clientOutput = PipedOutputStream(fromClient)

        init {
            val server = Thread {
                try {
                    val preamble = ByteArray(8)
                    readFully(fromClient, preamble)
                    val header = ByteArray(3)
                    readFully(fromClient, header)
                    val length = (header[1].toInt() and 0xFF) or ((header[2].toInt() and 0xFF) shl 8)
                    readFully(fromClient, ByteArray(length))
                    toClient.write(PREAMBLE + hex(SERVER_GREETING))
                    toClient.flush()
                    // Drain until the client closes.
                    val sink = ByteArray(1024)
                    while (fromClient.read(sink) >= 0) {
                        // Discard.
                    }
                } catch (ignored: IOException) {
                    // The client closed.
                }
            }
            server.isDaemon = true
            server.start()
        }

        override fun input(): InputStream = clientInput

        override fun output(): OutputStream = clientOutput

        override fun name(): String = "test"

        override fun close(reason: DisconnectReason) {
            try {
                toClient.close()
                clientOutput.close()
                fromClient.close()
            } catch (ignored: IOException) {
                // Closing never throws.
            }
        }

        companion object {
            val PREAMBLE = byteArrayOf(0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1)

            /** golden-frames.txt (nexnet-client tests) "server-greeting": version -987654321, clientId 42. */
            const val SERVER_GREETING = "690d0094d2c521974f2ace0040000020"

            fun hex(text: String): ByteArray = ByteArray(text.length / 2) { i ->
                text.substring(2 * i, 2 * i + 2).toInt(16).toByte()
            }

            fun readFully(input: InputStream, buffer: ByteArray) {
                var offset = 0
                while (offset < buffer.size) {
                    val n = input.read(buffer, offset, buffer.size - offset)
                    if (n < 0) {
                        throw IOException("End of stream")
                    }
                    offset += n
                }
            }
        }
    }

    private companion object {
        val HANDSHAKE = HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, -987654321, intArrayOf(0))

        fun client(connector: Transport.Connector): NexusClient<TestProxy> =
            NexusClient.create(ClientConfig.tcp("localhost", 1).build(), HANDSHAKE, TestNexus(), ::TestProxy, connector)
    }
}
