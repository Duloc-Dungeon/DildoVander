/** Lift quiet spoken PCM without clipping peaks or amplifying near-silence. */
export class SpeechLevel {
  private gain = 1;

  reset(): void { this.gain = 1; }

  process(samples: Float32Array): void {
    if (!samples.length) return;
    let peak = 0;
    let energy = 0;
    for (const sample of samples) {
      peak = Math.max(peak, Math.abs(sample));
      energy += sample * sample;
    }
    const rms = Math.sqrt(energy / samples.length);
    if (rms < .003 || peak === 0) return;

    const ceiling = .92 / peak;
    const desired = Math.min(4, Math.max(1, .12 / rms), ceiling);
    // Speech starts promptly; volume recovery is slower to avoid pumping.
    const timeConstant = desired > this.gain ? .06 : .5;
    const smoothing = 1 - Math.exp(-samples.length / (24000 * timeConstant));
    const start = Math.min(this.gain, ceiling);
    const target = Math.min(start + (desired - start) * smoothing, ceiling);
    for (let index = 0; index < samples.length; index++) {
      samples[index] *= start + (target - start) * (index + 1) / samples.length;
    }
    this.gain = target;
  }
}
