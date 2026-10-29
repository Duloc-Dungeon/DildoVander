interface CountdownObservation {
  launchAt: number | null;
  remaining: number;
  now: number;
  watching: boolean;
  waiting: boolean;
}

/** A page-local surprise: missed deadlines and new page loads never replay it. */
export class CountdownRickroll {
  private started = false;
  private previous: CountdownObservation | undefined;

  constructor(private readonly openVideo: () => void) {}

  reset(): void {
    this.previous = undefined;
  }

  observe(current: CountdownObservation): void {
    if (this.started) return;
    const previous = this.previous;
    this.previous = current.watching && current.waiting && current.remaining > 0 ? current : undefined;
    // Ticks run every second. A long gap means the page slept through zero.
    if (!current.watching || current.launchAt === null || current.remaining > 0
      || !previous || previous.launchAt !== current.launchAt
      || current.now - previous.now < 0 || current.now - previous.now > 2_000) return;
    this.started = true;
    this.openVideo();
  }
}
