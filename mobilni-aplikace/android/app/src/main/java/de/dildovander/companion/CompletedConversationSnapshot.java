package de.dildovander.companion;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Retains the last completed Live result long enough for the web bridge to read it. */
final class CompletedConversationSnapshot {
    private static final int MAX_TRANSCRIPT_ENTRIES = 200;
    private static final int MAX_TEXT_LENGTH = 1000;

    static final class TranscriptEntry {
        final String role;
        final String text;
        TranscriptEntry(String role, String text) { this.role = role; this.text = text; }
    }

    private String conversationId;
    private String errorCode;
    private List<TranscriptEntry> transcript = Collections.emptyList();
    private Integer selected;

    synchronized void capture(String id, List<TranscriptEntry> entries, Integer selection, String error) {
        conversationId = id;
        errorCode = bounded(error, 128);
        selected = selection != null && selection >= 1 && selection <= 6 ? selection : null;
        transcript = new ArrayList<>();
        if (entries == null) return;
        int first = Math.max(0, entries.size() - MAX_TRANSCRIPT_ENTRIES);
        for (int i = first; i < entries.size(); i++) {
            TranscriptEntry entry = entries.get(i);
            if (entry == null) continue;
            String role = entry.role;
            String text = entry.text;
            if (!("visitor".equals(role) || "kufr".equals(role)) || text.length() > MAX_TEXT_LENGTH) continue;
            transcript.add(new TranscriptEntry(role, text));
        }
    }

    synchronized void clear() {
        conversationId = null;
        errorCode = null;
        selected = null;
        transcript = Collections.emptyList();
    }

    synchronized String conversationId() { return conversationId; }
    synchronized String errorCode() { return errorCode; }
    synchronized Integer selected() { return selected; }
    synchronized List<TranscriptEntry> transcript() { return new ArrayList<>(transcript); }

    private static String bounded(String value, int max) {
        return value == null || value.length() > max ? null : value;
    }
}
