package de.dildovander.companion;

final class AudioFocusLossPolicy {
    private AudioFocusLossPolicy() { }
    static boolean shouldStop(int focusChange) { return focusChange < 0; }
}
