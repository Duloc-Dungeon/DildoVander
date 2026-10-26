package de.dildovander.companion;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.Set;

import okhttp3.Response;
import okhttp3.ResponseBody;
import okio.BufferedSource;
import okio.ForwardingSource;
import okio.Okio;

/** Serializes cancellation with publishing the next in-flight HTTP operation. */
final class LiveSetupCancellation {
    interface Cancellable { void cancel(); }

    private boolean stopped;
    private final Set<Cancellable> inFlight = Collections.newSetFromMap(new IdentityHashMap<>());

    synchronized boolean begin(Cancellable operation) {
        if (stopped) {
            operation.cancel();
            return false;
        }
        inFlight.add(operation);
        return true;
    }

    synchronized void finish(Cancellable operation) {
        inFlight.remove(operation);
    }

    synchronized Response retainUntilClosed(Response response, Cancellable operation) {
        ResponseBody body = response.body();
        if (body == null) { inFlight.remove(operation); return response; }
        BufferedSource source = Okio.buffer(new ForwardingSource(body.source()) {
            @Override public void close() throws IOException {
                try { super.close(); }
                finally { finish(operation); }
            }
        });
        ResponseBody retained = new ResponseBody() {
            @Override public okhttp3.MediaType contentType() { return body.contentType(); }
            @Override public long contentLength() { return body.contentLength(); }
            @Override public BufferedSource source() { return source; }
        };
        return response.newBuilder().body(retained).build();
    }

    synchronized void requireActive() throws IOException {
        if (stopped) throw new IOException("cancelled");
    }

    void stop() {
        ArrayList<Cancellable> operations;
        synchronized (this) {
            if (stopped) return;
            stopped = true;
            operations = new ArrayList<>(inFlight);
            inFlight.clear();
        }
        for (Cancellable operation : operations) operation.cancel();
    }

    synchronized boolean isStopped() { return stopped; }
}
