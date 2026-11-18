package com.dtronix.nexnet.spi;

import java.util.Arrays;
import org.jspecify.annotations.Nullable;

/**
 * The greeting values of one exported nexus pair, emitted by the generator as a constant
 * ({@code <Export>Handshake.DESCRIPTOR}) from the IR's {@code handshake} and {@code clientAcceptedIds}. These are
 * opaque constants: the runtime sends them as they are and never recomputes a hash.
 */
public final class HandshakeDescriptor {
    /** The longest server API version string the protocol allows. */
    public static final int MAX_VERSION_LENGTH = 16;

    private final String runtimeVersion;
    private final @Nullable String version;
    private final int serverNexusHash;
    private final int clientNexusHash;
    private final int[] clientAcceptedMethodIds;

    /**
     * Creates a descriptor.
     *
     * @param runtimeVersion the runtime version the code was generated for ({@code NexusRuntime.VERSION} at
     *                       generation time); the runtime refuses a different major.minor
     * @param version the server API version string, or null when the server API is not versioned
     * @param serverNexusHash the server nexus hash (of that version)
     * @param clientNexusHash the client nexus hash
     * @param clientAcceptedMethodIds the method and collection ids the client nexus accepts from the server
     * @throws IllegalArgumentException when the version is longer than 16 characters or an id is not 0–65535
     */
    public HandshakeDescriptor(String runtimeVersion, @Nullable String version, int serverNexusHash,
                               int clientNexusHash, int[] clientAcceptedMethodIds) {
        if (runtimeVersion == null) {
            throw new NullPointerException("runtimeVersion");
        }
        if (version != null && version.length() > MAX_VERSION_LENGTH) {
            throw new IllegalArgumentException("The version is longer than " + MAX_VERSION_LENGTH + " characters: " + version);
        }
        int[] ids = clientAcceptedMethodIds.clone();
        Arrays.sort(ids);
        for (int id : ids) {
            if (id < 0 || id > 0xFFFF) {
                throw new IllegalArgumentException("Method ids are 0 to 65535: " + id);
            }
        }
        this.runtimeVersion = runtimeVersion;
        this.version = version;
        this.serverNexusHash = serverNexusHash;
        this.clientNexusHash = clientNexusHash;
        this.clientAcceptedMethodIds = ids;
    }

    /**
     * The runtime version the code was generated for.
     *
     * @return the version
     */
    public String runtimeVersion() {
        return runtimeVersion;
    }

    /**
     * The server API version string sent in the greeting.
     *
     * @return the version, or null when not versioned
     */
    public @Nullable String version() {
        return version;
    }

    /**
     * The server nexus hash sent in the greeting.
     *
     * @return the hash
     */
    public int serverNexusHash() {
        return serverNexusHash;
    }

    /**
     * The client nexus hash sent in the greeting.
     *
     * @return the hash
     */
    public int clientNexusHash() {
        return clientNexusHash;
    }

    /**
     * Whether the client nexus accepts an incoming invocation of this id.
     *
     * @param methodId the method id
     * @return true when accepted
     */
    public boolean acceptsMethod(int methodId) {
        return Arrays.binarySearch(clientAcceptedMethodIds, methodId) >= 0;
    }

    /**
     * The accepted ids, ascending.
     *
     * @return a copy of the ids
     */
    public int[] clientAcceptedMethodIds() {
        return clientAcceptedMethodIds.clone();
    }
}
