package de.dildovander.companion;

import org.junit.Test;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import okhttp3.Call;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

import static org.junit.Assert.*;

public final class LiveSetupCancellationTest {
    @Test public void stopDuringDiagnosticsCancelsCallAndPreventsConversationCreation() throws Exception {
        LiveSetupCancellation gate = new LiveSetupCancellation();
        CountDownLatch diagnosticsStarted = new CountDownLatch(1), releaseDiagnostics = new CountDownLatch(1);
        AtomicBoolean diagnosticsCancelled = new AtomicBoolean(), createPosted = new AtomicBoolean();
        Thread setup = new Thread(() -> {
            LiveSetupCancellation.Cancellable diagnostics = () -> diagnosticsCancelled.set(true);
            try {
                if (!gate.begin(diagnostics)) return;
                diagnosticsStarted.countDown();
                releaseDiagnostics.await();
                gate.finish(diagnostics);
                gate.requireActive();
                createPosted.set(true);
            } catch (IOException expected) { }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        });
        setup.start();
        assertTrue(diagnosticsStarted.await(1, TimeUnit.SECONDS));
        gate.stop();
        releaseDiagnostics.countDown();
        setup.join(1000);
        assertFalse(setup.isAlive());
        assertTrue(diagnosticsCancelled.get());
        assertFalse(createPosted.get());
    }

    @Test public void stopDuringConversationCreationCancelsCallAndPreventsLiveStream() throws Exception {
        LiveSetupCancellation gate = new LiveSetupCancellation();
        CountDownLatch createStarted = new CountDownLatch(1), releaseCreate = new CountDownLatch(1);
        AtomicBoolean createCancelled = new AtomicBoolean(), streamPosted = new AtomicBoolean();
        Thread setup = new Thread(() -> {
            LiveSetupCancellation.Cancellable create = () -> createCancelled.set(true);
            try {
                if (!gate.begin(create)) return;
                createStarted.countDown();
                releaseCreate.await();
                gate.finish(create);
                gate.requireActive();
                streamPosted.set(true);
            } catch (IOException expected) { }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        });
        setup.start();
        assertTrue(createStarted.await(1, TimeUnit.SECONDS));
        gate.stop();
        releaseCreate.countDown();
        setup.join(1000);
        assertFalse(setup.isAlive());
        assertTrue(createCancelled.get());
        assertFalse(streamPosted.get());
    }

    @Test public void stopCancelsStalledResponseBodyAfterHeaders() throws Exception {
        CountDownLatch headersSent = new CountDownLatch(1), releaseServer = new CountDownLatch(1), readStarted = new CountDownLatch(1);
        AtomicReference<Throwable> readFailure = new AtomicReference<>();
        try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            Thread serverThread = new Thread(() -> {
                try (Socket socket = server.accept()) {
                    InputStream input = socket.getInputStream();
                    int a = -1, b = -1, c = -1, d;
                    while ((d = input.read()) != -1) {
                        if (a == '\r' && b == '\n' && c == '\r' && d == '\n') break;
                        a = b; b = c; c = d;
                    }
                    OutputStream output = socket.getOutputStream();
                    output.write("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: keep-alive\r\n\r\nx".getBytes(java.nio.charset.StandardCharsets.US_ASCII));
                    output.flush();
                    headersSent.countDown();
                    releaseServer.await();
                } catch (Exception ignored) { }
            });
            serverThread.start();

            LiveSetupCancellation gate = new LiveSetupCancellation();
            OkHttpClient client = new OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).build();
            Call call = client.newCall(new Request.Builder().url("http://127.0.0.1:" + server.getLocalPort() + "/stall").build());
            LiveSetupCancellation.Cancellable cancellation = call::cancel;
            assertTrue(gate.begin(cancellation));
            Response response = gate.retainUntilClosed(call.execute(), cancellation);
            assertTrue(headersSent.await(1, TimeUnit.SECONDS));
            Thread reader = new Thread(() -> {
                try {
                    readStarted.countDown();
                    response.body().source().readByteArray(100);
                } catch (Throwable error) { readFailure.set(error); }
            });
            reader.start();
            assertTrue(readStarted.await(1, TimeUnit.SECONDS));
            gate.stop();
            reader.join(1500);
            assertFalse("body read should unblock on stop", reader.isAlive());
            assertTrue("body read should fail after cancellation", readFailure.get() instanceof IOException);
            response.close();
            releaseServer.countDown();
            serverThread.join(1000);
            client.dispatcher().executorService().shutdownNow();
            client.connectionPool().evictAll();
        } finally { releaseServer.countDown(); }
    }

    @Test public void stopCancelsEveryConcurrentRequest() {
        LiveSetupCancellation gate = new LiveSetupCancellation();
        AtomicBoolean streamCancelled = new AtomicBoolean(), controlCancelled = new AtomicBoolean();
        assertTrue(gate.begin(() -> streamCancelled.set(true)));
        assertTrue(gate.begin(() -> controlCancelled.set(true)));
        gate.stop();
        assertTrue(streamCancelled.get());
        assertTrue(controlCancelled.get());
    }
}
