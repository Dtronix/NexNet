package com.dtronix.nexnet.android;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertTrue;

import android.os.Looper;
import android.util.Log;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import java.net.URI;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.junit.Test;
import org.junit.runner.RunWith;

/** Device checks of the Android integration that need no server. */
@RunWith(AndroidJUnit4.class)
public class NexusAndroidDeviceTest {
    @Test
    public void mainThreadExecutorRunsOnTheMainLooper() throws Exception {
        CompletableFuture<Looper> looper = new CompletableFuture<>();
        NexusAndroid.mainThreadExecutor().execute(() -> looper.complete(Looper.myLooper()));
        assertSame(Looper.getMainLooper(), looper.get(5, TimeUnit.SECONDS));
    }

    @Test
    public void configureSetsTheAndroidDefaults() {
        ClientConfig config = NexusAndroid.configure(ClientConfig.webSocket(URI.create("ws://10.0.2.2:1/ws"))).build();
        assertSame(NexusAndroid.mainThreadExecutor(), config.callbackExecutor());
        assertTrue(config.logger() instanceof AndroidNexusLogger);
    }

    @Test
    public void loggerMapsLevelsAndPaths() {
        AndroidNexusLogger logger = new AndroidNexusLogger("NexNetTest", NexusLogLevel.DEBUG);
        assertFalse(logger.isEnabled(NexusLogLevel.TRACE));
        assertTrue(logger.isEnabled(NexusLogLevel.DEBUG));
        assertFalse(logger.isEnabled(NexusLogLevel.NONE));
        assertEquals(Log.VERBOSE, AndroidNexusLogger.priority(NexusLogLevel.TRACE));
        assertEquals(Log.INFO, AndroidNexusLogger.priority(NexusLogLevel.INFORMATION));
        assertEquals(Log.ERROR, AndroidNexusLogger.priority(NexusLogLevel.CRITICAL));

        NexusLogger child = logger.createLogger("CL1").createLogger("S1");
        child.log(NexusLogLevel.WARNING, "Test", new IllegalStateException("test"), "A warning from the device test.");
        child.log(NexusLogLevel.TRACE, null, null, "Not written.");
    }
}
