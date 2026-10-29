import { describe, expect, it } from 'vitest';
import { SpeechLevel } from './speechLevel';

describe('spoken PCM level', () => {
  it('raises a quiet spoken signal while preserving its shape and bounded gain', () => {
    const level = new SpeechLevel();
    const samples = Float32Array.from({ length: 6000 }, (_, index) => Math.sin(index * .1) * .025);
    const original = samples.slice();
    level.process(samples);
    expect(Math.max(...samples)).toBeGreaterThan(.07);
    for (let index = 0; index < samples.length; index++) {
      expect(Math.abs(samples[index])).toBeLessThanOrEqual(Math.abs(original[index]) * 4 + .000001);
      expect(Math.sign(samples[index])).toBe(Math.sign(original[index]));
    }
  });

  it('leaves silence and low background noise untouched', () => {
    const level = new SpeechLevel();
    for (const value of [0, .001]) {
      const samples = new Float32Array(6000).fill(value);
      level.process(samples);
      expect(samples).toEqual(new Float32Array(6000).fill(value));
    }
  });

  it('keeps a loud peak below the output ceiling after a quiet utterance', () => {
    const level = new SpeechLevel();
    level.process(new Float32Array(6000).fill(.025));
    const loud = Float32Array.from([1, -.99, .2, -.5]);
    level.process(loud);
    expect(Math.max(...loud.map(Math.abs))).toBeLessThanOrEqual(.920001);
  });

  it('resets gain between canceled or new responses', () => {
    const level = new SpeechLevel();
    level.process(new Float32Array(6000).fill(.025));
    level.reset();
    const afterReset = new Float32Array(6000).fill(.1);
    const fresh = afterReset.slice();
    level.process(afterReset); new SpeechLevel().process(fresh);
    expect(afterReset).toEqual(fresh);
  });
});
