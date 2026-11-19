package com.dtronix.nexnet.diagnostics;

import org.junit.jupiter.api.extension.AfterEachCallback;
import org.junit.jupiter.api.extension.ExtensionContext;

/**
 * The suite-wide leak audit (D23): after every test, every leak-audit counter returns to 0 within 5 s. Registered for
 * every test class through {@code META-INF/services} and {@code junit-platform.properties}.
 */
public final class LeakAuditExtension implements AfterEachCallback {
    @Override
    public void afterEach(ExtensionContext context) throws InterruptedException {
        TestClients.awaitIdle();
    }
}
