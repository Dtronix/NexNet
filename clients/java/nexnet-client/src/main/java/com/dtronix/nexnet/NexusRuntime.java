package com.dtronix.nexnet;

/**
 * Information about the NexNet Java runtime.
 */
public final class NexusRuntime {
    /**
     * The runtime version. It matches the NexNet NuGet and npm versions (lockstep releases).
     */
    public static final String VERSION = NexusRuntimeVersion.VALUE;

    private NexusRuntime() {
    }
}
