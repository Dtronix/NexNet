package com.dtronix.nexnet.android;

import android.os.Handler;
import android.os.Looper;
import com.dtronix.nexnet.client.ClientConfig;
import java.util.concurrent.Executor;
import java.util.concurrent.RejectedExecutionException;

/**
 * Android defaults for a NexNet client: completions and listeners on the main thread, logging to
 * {@link android.util.Log}.
 *
 * <pre>{@code
 * ClientConfig config = NexusAndroid.configure(ClientConfig.webSocket(URI.create("wss://example.com/nexus")))
 *     .build();
 * }</pre>
 */
public final class NexusAndroid {
    private static final Executor MAIN = new MainThreadExecutor();

    private NexusAndroid() {
    }

    /**
     * An executor that runs tasks on the main (UI) thread. A task is always posted, never run inline, so a future
     * completed on the main thread does not run its callbacks re-entrantly.
     *
     * @return the shared executor
     */
    public static Executor mainThreadExecutor() {
        return MAIN;
    }

    /**
     * Sets the Android defaults on a configuration: {@link #mainThreadExecutor()} as the callback executor (every
     * user-visible future and listener) and an {@link AndroidNexusLogger} at {@code INFORMATION}. Incoming
     * invocations of the client nexus stay on the runtime's pool; set {@code invocationExecutor} to change that.
     *
     * @param builder the configuration builder
     * @return the builder
     */
    public static ClientConfig.Builder configure(ClientConfig.Builder builder) {
        return builder.callbackExecutor(MAIN).logger(new AndroidNexusLogger());
    }

    /** Posts to the main looper. */
    private static final class MainThreadExecutor implements Executor {
        private final Handler handler = new Handler(Looper.getMainLooper());

        @Override
        public void execute(Runnable command) {
            if (command == null) {
                throw new NullPointerException("command");
            }
            if (!handler.post(command)) {
                throw new RejectedExecutionException("The main looper is exiting.");
            }
        }
    }
}
