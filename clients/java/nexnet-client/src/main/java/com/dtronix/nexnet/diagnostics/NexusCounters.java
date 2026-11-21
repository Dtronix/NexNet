package com.dtronix.nexnet.diagnostics;

/**
 * The process-wide leak-audit counters, from {@link Diagnostics#counters()}. Every value is 0 once every client is
 * closed and every pool is closed (H05).
 */
public final class NexusCounters {
    private final int sessions;
    private final int threads;
    private final int pendingWrites;
    private final int timers;
    private final int pendingInvocations;
    private final int largeMessages;
    private final int pipes;
    private final int subscriptions;
    private final int pooledClients;

    NexusCounters(int sessions, int threads, int pendingWrites, int timers, int pendingInvocations,
                  int largeMessages, int pipes, int subscriptions, int pooledClients) {
        this.sessions = sessions;
        this.threads = threads;
        this.pendingWrites = pendingWrites;
        this.timers = timers;
        this.pendingInvocations = pendingInvocations;
        this.largeMessages = largeMessages;
        this.pipes = pipes;
        this.subscriptions = subscriptions;
        this.pooledClients = pooledClients;
    }

    /**
     * Sessions started and not yet closed.
     *
     * @return the count
     */
    public int sessions() {
        return sessions;
    }

    /**
     * Connection reader and writer threads that have not ended.
     *
     * @return the count
     */
    public int threads() {
        return threads;
    }

    /**
     * Frames queued for a writer thread.
     *
     * @return the count
     */
    public int pendingWrites() {
        return pendingWrites;
    }

    /**
     * Timers scheduled: session timers, reconnection delays, pool health checks and registered sources.
     *
     * @return the count
     */
    public int timers() {
        return timers;
    }

    /**
     * Calls to the server not yet completed plus server invocations queued or running.
     *
     * @return the count
     */
    public int pendingInvocations() {
        return pendingInvocations;
    }

    /**
     * Large arguments and results in transfer.
     *
     * @return the count
     */
    public int largeMessages() {
        return largeMessages;
    }

    /**
     * Pipes open, as reported by registered sources.
     *
     * @return the count
     */
    public int pipes() {
        return pipes;
    }

    /**
     * Listeners the runtime holds (metrics tracking and registered sources).
     *
     * @return the count
     */
    public int subscriptions() {
        return subscriptions;
    }

    /**
     * Clients owned by open pools.
     *
     * @return the count
     */
    public int pooledClients() {
        return pooledClients;
    }

    /**
     * Whether every counter is 0.
     *
     * @return true when nothing is open
     */
    public boolean isIdle() {
        return sessions == 0 && threads == 0 && pendingWrites == 0 && timers == 0 && pendingInvocations == 0
            && largeMessages == 0 && pipes == 0 && subscriptions == 0 && pooledClients == 0;
    }

    @Override
    public String toString() {
        return "NexusCounters[sessions=" + sessions + ", threads=" + threads + ", pendingWrites=" + pendingWrites
            + ", timers=" + timers + ", pendingInvocations=" + pendingInvocations + ", largeMessages=" + largeMessages
            + ", pipes=" + pipes + ", subscriptions=" + subscriptions + ", pooledClients=" + pooledClients + "]";
    }
}
