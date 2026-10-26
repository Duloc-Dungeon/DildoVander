package de.dildovander.companion;

final class ProfileGeneration {
    private long value;

    synchronized long begin() { return ++value; }
    synchronized long invalidate() { return ++value; }
    synchronized boolean isCurrent(long candidate) { return candidate == value; }
}
