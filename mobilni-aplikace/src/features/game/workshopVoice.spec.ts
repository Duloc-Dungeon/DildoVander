import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WorkshopVoice } from './workshopVoice';
class Recorder {
  static instances: Recorder[] = [];
  static isTypeSupported = () => true;
  mimeType = 'audio/webm'; state = 'inactive';
  ondataavailable?: (event: { data: Blob }) => void;
  onstop?: () => void; onerror?: () => void;
  constructor(_stream: unknown, _options: unknown) { Recorder.instances.push(this); }
  start() { this.state = 'recording'; }
  stop() { this.state = 'inactive'; }
  deliver() { this.ondataavailable?.({ data: new Blob(['recorded']) }); this.onstop?.(); }
}
const microphone = vi.fn();
const track = () => ({ stop: vi.fn() });
const observer = () => ({ phase: vi.fn(), level: vi.fn() });
beforeEach(() => {
  Recorder.instances = []; microphone.mockReset(); vi.stubGlobal('MediaRecorder', Recorder);
  vi.stubGlobal('navigator', { mediaDevices: { getUserMedia: microphone } });
});
afterEach(() => vi.unstubAllGlobals());
describe('workshop microphone lifecycle', () => {
  it('never acquires audio on construction and releases a late permission after leaving', async () => {
    let grant!: (stream: unknown) => void;
    microphone.mockReturnValue(new Promise(resolve => { grant = resolve; }));
    const voice = new WorkshopVoice(observer()); expect(microphone).not.toHaveBeenCalled();
    const recording = voice.record(); voice.stop(); const input = track(); grant({ getTracks: () => [input] });
    expect(await recording).toBeNull(); expect(input.stop).toHaveBeenCalledOnce();
    expect(Recorder.instances).toHaveLength(0);
  });
  it('late completion of an abandoned recording cannot stop a newer microphone', async () => {
    const first = track(); const second = track();
    microphone.mockResolvedValueOnce({ getTracks: () => [first] }).mockResolvedValueOnce({ getTracks: () => [second] });
    const voice = new WorkshopVoice(observer());
    const abandoned = voice.record(); await Promise.resolve();
    voice.stop(); const active = voice.record(); await Promise.resolve();
    Recorder.instances[0].deliver(); expect(await abandoned).toBeNull();
    expect(second.stop).not.toHaveBeenCalled();
    voice.finishRecording(); Recorder.instances[1].deliver();
    expect((await active)?.size).toBeGreaterThan(0); expect(second.stop).toHaveBeenCalled();
    voice.dispose();
  });
  it('recording failure always releases the physical microphone', async () => {
    const input = track(); microphone.mockResolvedValue({ getTracks: () => [input] });
    const voice = new WorkshopVoice(observer()); const recording = voice.record(); await Promise.resolve();
    Recorder.instances[0].onerror?.(); await expect(recording).rejects.toThrow();
    expect(input.stop).toHaveBeenCalled(); voice.dispose();
  });
});
