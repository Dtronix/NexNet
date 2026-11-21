package com.dtronix.nexnet.interop.host;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * The entry point of the JVM language host: {@code --control <host>:<port> --token <token>} (control protocol §2).
 * Dials the runner, sends {@code hello} and serves requests until the control connection closes.
 */
public final class Main {
    private Main() {
    }

    /**
     * Runs the host.
     *
     * @param args the command line
     */
    public static void main(String[] args) {
        String control = null;
        String token = null;
        for (int i = 0; i + 1 < args.length; i++) {
            if ("--control".equals(args[i])) {
                control = args[++i];
            } else if ("--token".equals(args[i])) {
                token = args[++i];
            }
        }
        if (control == null || token == null) {
            System.err.println("usage: interop-host --control <host>:<port> --token <token>");
            System.exit(2);
            return;
        }
        int colon = control.lastIndexOf(':');
        String hostName = control.substring(0, colon);
        int port = Integer.parseInt(control.substring(colon + 1));

        int code = 0;
        try (Socket socket = new Socket()) {
            socket.connect(new InetSocketAddress(hostName, port), 30_000);
            try (ControlChannel channel = new ControlChannel(socket)) {
                InteropHost host = new InteropHost(channel);
                Thread.setDefaultUncaughtExceptionHandler((thread, error) -> {
                    System.err.println("Uncaught exception on " + thread.getName() + ": " + Errors.stack(error));
                    host.hostError("Uncaught exception on " + thread.getName() + ": " + error, error);
                });
                host.hello(token);
                host.run();
            }
        } catch (IOException e) {
            System.err.println("The JVM interop host failed: " + Errors.stack(e));
            code = 1;
        }
        System.exit(code);
    }
}
