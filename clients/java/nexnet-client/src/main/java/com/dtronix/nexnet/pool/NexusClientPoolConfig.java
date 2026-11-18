package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.client.ClientConfig;

/**
 * Configuration of a {@link NexusClientPool}. Defaults match C#: 10 connections, 2 minutes idle, 1 idle connection
 * kept.
 */
public final class NexusClientPoolConfig {
    private final ClientConfig clientConfig;
    private int maxConnections = 10;
    private long maxIdleTimeMillis = 120_000;
    private int minIdleConnections = 1;

    /**
     * Creates the configuration.
     *
     * @param clientConfig the configuration of each pooled client
     */
    public NexusClientPoolConfig(ClientConfig clientConfig) {
        if (clientConfig == null) {
            throw new NullPointerException("clientConfig");
        }
        this.clientConfig = clientConfig;
    }

    /**
     * The configuration of each pooled client.
     *
     * @return the configuration
     */
    public ClientConfig clientConfig() {
        return clientConfig;
    }

    /**
     * The maximum number of connections.
     *
     * @return the maximum
     */
    public int maxConnections() {
        return maxConnections;
    }

    /**
     * Sets the maximum number of connections.
     *
     * @param maxConnections at least 1
     * @return this configuration
     */
    public NexusClientPoolConfig maxConnections(int maxConnections) {
        if (maxConnections < 1) {
            throw new IllegalArgumentException("maxConnections must be at least 1.");
        }
        this.maxConnections = maxConnections;
        return this;
    }

    /**
     * How long an idle connection is kept.
     *
     * @return milliseconds
     */
    public long maxIdleTimeMillis() {
        return maxIdleTimeMillis;
    }

    /**
     * Sets how long an idle connection is kept.
     *
     * @param millis milliseconds, positive
     * @return this configuration
     */
    public NexusClientPoolConfig maxIdleTimeMillis(long millis) {
        if (millis <= 0) {
            throw new IllegalArgumentException("maxIdleTimeMillis must be positive.");
        }
        this.maxIdleTimeMillis = millis;
        return this;
    }

    /**
     * The number of idle connections kept regardless of idle time.
     *
     * @return the number
     */
    public int minIdleConnections() {
        return minIdleConnections;
    }

    /**
     * Sets the number of idle connections kept regardless of idle time.
     *
     * @param minIdleConnections 0 or more
     * @return this configuration
     */
    public NexusClientPoolConfig minIdleConnections(int minIdleConnections) {
        if (minIdleConnections < 0) {
            throw new IllegalArgumentException("minIdleConnections cannot be negative.");
        }
        this.minIdleConnections = minIdleConnections;
        return this;
    }
}
