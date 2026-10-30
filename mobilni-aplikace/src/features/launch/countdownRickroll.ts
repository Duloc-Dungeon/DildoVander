import { withinRickrollWindow } from './rickrollWindow';

interface CountdownObservation {
  launchAt: number | null;
  remaining: number;
  now: number;
  watching: boolean;
  waiting: boolean;
}

/** Eligible on arrival as well as at countdown zero; clocks use server time. */
export class CountdownRickroll {
  private started = false;

  constructor(private readonly openVideo: () => void) {}

  reset(): void {
    // Visibility changes do not replay a completed presentation.
  }

  observe(current: CountdownObservation): void {
    if (this.started) return;
    if (!current.watching || !withinRickrollWindow(current.launchAt, current.now)) return;
    this.started = true;
    this.openVideo();
  }
}
