package de.dildovander.companion;

import java.io.IOException;

/** Binds account transport preflight and its final request to one cancellable lifetime. */
final class AccountRequestCancellation {
    interface Cancellable { void cancel(); }

    private boolean cancelled;
    private Cancellable inFlight;

    synchronized boolean attach(Cancellable call) {
        if (cancelled) {
            call.cancel();
            return false;
        }
        inFlight = call;
        return true;
    }

    synchronized void finish(Cancellable call) {
        if (inFlight == call) inFlight = null;
    }

    synchronized void requireActive() throws IOException {
        if (cancelled) throw new IOException("cancelled");
    }

    synchronized boolean isCancelled() { return cancelled; }

    void cancel() {
        Cancellable call;
        synchronized (this) {
            if (cancelled) return;
            cancelled = true;
            call = inFlight;
            inFlight = null;
        }
        if (call != null) call.cancel();
    }
}
