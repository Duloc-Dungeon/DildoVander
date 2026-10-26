package de.dildovander.companion;

import java.util.ArrayList;
import java.util.List;
import org.junit.Test;

import static org.junit.Assert.*;

public class CompletedConversationSnapshotTest {
    @Test public void preservesBoundedResultAndValidSelectionUntilCleared() {
        List<CompletedConversationSnapshot.TranscriptEntry> entries = new ArrayList<>();
        for (int i = 0; i < 205; i++) entries.add(new CompletedConversationSnapshot.TranscriptEntry("visitor", "line-" + i));
        CompletedConversationSnapshot snapshot = new CompletedConversationSnapshot();
        snapshot.capture("conversation", entries, 4, "realtime_live_failed");
        entries.add(new CompletedConversationSnapshot.TranscriptEntry("kufr", "late"));

        assertEquals("conversation", snapshot.conversationId());
        assertEquals("realtime_live_failed", snapshot.errorCode());
        assertEquals(Integer.valueOf(4), snapshot.selected());
        assertEquals(200, snapshot.transcript().size());
        assertEquals("line-5", snapshot.transcript().get(0).text);
        assertEquals("line-204", snapshot.transcript().get(199).text);

        snapshot.clear();
        assertNull(snapshot.conversationId());
        assertNull(snapshot.errorCode());
        assertNull(snapshot.selected());
        assertEquals(0, snapshot.transcript().size());
    }

    @Test public void invalidSelectionIsNotExposed() {
        CompletedConversationSnapshot snapshot = new CompletedConversationSnapshot();
        snapshot.capture("conversation", new ArrayList<>(), -1, null);
        assertNull(snapshot.selected());
        snapshot.capture("conversation", new ArrayList<>(), 7, null);
        assertNull(snapshot.selected());
        snapshot.capture("conversation", new ArrayList<>(), 1, null);
        assertEquals(Integer.valueOf(1), snapshot.selected());
    }
}
