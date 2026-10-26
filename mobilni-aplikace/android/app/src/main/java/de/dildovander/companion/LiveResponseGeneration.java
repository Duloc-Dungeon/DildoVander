package de.dildovander.companion;

final class LiveResponseGeneration {
    enum Decision { ACCEPT, DROP, INVALID }
    private int current;
    private int retired = -1;

    int current() { return current; }

    boolean advance(int generation) {
        if (generation <= current) return false;
        retired = current;
        current = generation;
        return true;
    }

    Decision accepts(int generation) {
        if (generation < 0) return retired >= 0 ? Decision.DROP : (current == 0 ? Decision.ACCEPT : Decision.INVALID);
        if (generation <= retired) return Decision.DROP;
        if (generation > current) return Decision.INVALID;
        return generation == current ? Decision.ACCEPT : Decision.DROP;
    }
}
