package com.dtronix.nexnet.serialization;

import java.util.concurrent.atomic.AtomicReferenceArray;

/**
 * A small shared pool of writer buffers, so short-lived writers do not allocate a new array each time. Lock-free and
 * bounded: at most {@link #SLOTS} buffers of at most {@link #MAX_POOLED} bytes are kept.
 */
final class BufferPool {
    /** The size of a new buffer. */
    static final int INITIAL_SIZE = 4096;

    /** Larger buffers are not kept. */
    static final int MAX_POOLED = 1024 * 1024;

    static final int SLOTS = 16;

    private static final AtomicReferenceArray<byte[]> POOL = new AtomicReferenceArray<>(SLOTS);

    private BufferPool() {
    }

    private static int startSlot() {
        return (System.identityHashCode(Thread.currentThread()) & 0x7FFFFFFF) % SLOTS;
    }

    /** A buffer of at least {@code minimum} bytes, from the pool when one fits. */
    static byte[] rent(int minimum) {
        if (minimum <= MAX_POOLED) {
            int start = startSlot();
            for (int i = 0; i < SLOTS; i++) {
                int slot = (start + i) % SLOTS;
                byte[] buffer = POOL.get(slot);
                if (buffer != null && buffer.length >= minimum && POOL.compareAndSet(slot, buffer, null)) {
                    return buffer;
                }
            }
        }
        return new byte[Math.max(minimum, INITIAL_SIZE)];
    }

    /** Gives a buffer back. The caller must not use it afterwards. */
    static void giveBack(byte[] buffer) {
        if (buffer.length < INITIAL_SIZE || buffer.length > MAX_POOLED) {
            return;
        }
        int start = startSlot();
        for (int i = 0; i < SLOTS; i++) {
            int slot = (start + i) % SLOTS;
            if (POOL.get(slot) == null && POOL.compareAndSet(slot, null, buffer)) {
                return;
            }
        }
    }
}
