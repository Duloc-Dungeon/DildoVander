package de.dildovander.companion;

import java.util.ArrayDeque;
import java.util.Deque;

final class LiveAudioAcknowledgements {
    private static final int MAX_BYTES = 192_000;
    private static final int MAX_FRAMES = 64;
    private static final long TIMEOUT_MS = 10_000;
    private static final class Pending {
        final String inputId;
        final int sequence;
        final int bytes;
        final long sentAt;
        Pending(String inputId, int sequence, int bytes, long sentAt) {
            this.inputId = inputId; this.sequence = sequence; this.bytes = bytes; this.sentAt = sentAt;
        }
    }
    private final Deque<Pending> pending = new ArrayDeque<>();
    private int bytes;

    synchronized boolean enqueue(String inputId, int sequence, int frameBytes, long now) {
        if (inputId == null || frameBytes < 1 || frameBytes > 9600 || frameBytes % 2 != 0 ||
            bytes + frameBytes > MAX_BYTES || pending.size() >= MAX_FRAMES) return false;
        pending.addLast(new Pending(inputId, sequence, frameBytes, now));
        bytes += frameBytes;
        return true;
    }

    synchronized boolean acknowledge(String inputId, int sequence) {
        Pending first = pending.peekFirst();
        if (first == null || !first.inputId.equals(inputId) || first.sequence != sequence) return false;
        pending.removeFirst(); bytes -= first.bytes; return true;
    }

    synchronized boolean timedOut(long now) {
        Pending first = pending.peekFirst();
        return first != null && now - first.sentAt >= TIMEOUT_MS;
    }
}
