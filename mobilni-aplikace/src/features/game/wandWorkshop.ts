import { accountRequest, accountTransport } from '../identity/session';

export interface WorkshopQuestion { id: string; revision: number; number: number; total: number; text: string; usedFallback?: boolean }
export interface WorkshopStep {
  questionId: string; questionRevision: number; number: number; total?: number; question: string;
  answerId: string | null; answerRevision: number; answer: string | null; skipped: boolean; sourceMode: 'text' | 'voice'; usedFallback?: boolean;
}
export interface WorkshopMessage { messageId: string; revision: number; role: 'user' | 'assistant'; text: string; sourceMode: string; createdAt: number }
export interface WorkshopSession {
  id: string; profileId: string; profileRevision: number; tier: number;
  status: 'authoring' | 'asking' | 'generating' | 'complete' | 'failed' | 'paused'; question: WorkshopQuestion | null;
  steps: WorkshopStep[]; messages: WorkshopMessage[]; wand: unknown; verdict?: string | null; canRetry?: boolean;
  artwork: { locator: string; assetId: string | null; status: string; canRetry: boolean; errorCode?: string | null; version: number; synthetic?: boolean; sha256?: string; canRepair?: boolean } | null;
}
export interface WorkshopEntry {
  eligible: boolean; reason: string | null; tier: number;
  profile: { profileId: string; revision: number; name: string; universe: string; version: string; description: string; status?: string } | null;
  session: WorkshopSession | null;
}
export const workshopBase = '/app/v1/customer/wand/workshop';
export function workshopSessionPath(id: string): string {
  if (!/^[a-f0-9]{32}$/.test(id)) throw new Error('Neplatná relace dílny.');
  return `${workshopBase}/sessions/${id}`;
}
interface WorkshopWireSession extends Omit<WorkshopSession, 'question'> {
  question: WorkshopStep | null;
}
interface WorkshopWireEntry extends Omit<WorkshopEntry, 'session' | 'profile'> {
  profile: (Omit<NonNullable<WorkshopEntry['profile']>, 'universe'> & { world: string }) | null;
  session: WorkshopWireSession | null;
}
export function normalizeSession(value: WorkshopWireSession): WorkshopSession {
  return { ...value, messages: value.messages ?? [], question: value.question ? {
    id: value.question.questionId, revision: value.question.questionRevision,
    number: value.question.number, total: value.question.total ?? 0, text: value.question.question, usedFallback: value.question.usedFallback,
  } : null };
}
export async function loadWorkshop(signal: AbortSignal, upgrade = false): Promise<WorkshopEntry> {
  const value = await accountRequest<WorkshopWireEntry>(`${workshopBase}${upgrade ? '?upgrade=1' : ''}`, 'GET', undefined, signal);
  return { ...value, profile: value.profile ? { ...value.profile, universe: value.profile.world } : null,
    session: value.session ? normalizeSession(value.session) : null };
}
export async function workshopSessionRequest(path: string, method: string, body: unknown, signal: AbortSignal): Promise<WorkshopSession> {
  const value = await accountRequest<{ session: WorkshopWireSession }>(path, method, body, signal);
  return normalizeSession(value.session);
}
export const loadWorkshopSession = (id: string, signal: AbortSignal) => workshopSessionRequest(workshopSessionPath(id), 'GET', undefined, signal);
export class WorkshopAudioConflict extends Error {}

export async function workshopAudio(id: string, proof: { questionId?: string; questionRevision?: number; wandId?: string }, signal: AbortSignal): Promise<Blob> {
  const response = await accountTransport(`${workshopSessionPath(id)}/audio`, { method: 'POST', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(proof) });
  if (response.status === 409) throw new WorkshopAudioConflict();
  if (!response.ok) throw new Error('Přednes otázky není dostupný. Můžeš pokračovat psaním.');
  return response.blob();
}
export async function transcribeWorkshopAudio(id: string, question: WorkshopQuestion, audio: Blob, signal: AbortSignal): Promise<string> {
  const response = await accountTransport(`${workshopSessionPath(id)}/transcribe`, {
    method: 'POST', body: audio, signal, headers: { 'Content-Type': audio.type || 'audio/webm',
      'X-Wand-Question-Id': question.id, 'X-Wand-Question-Revision': String(question.revision) },
  });
  if (!response.ok) throw new Error('Hlas se nepodařilo přepsat. Napiš odpověď nebo nahraj novou.');
  const result = await response.json() as { text: string };
  return result.text;
}

/** A provider URL is never accepted as artwork. Native clients fetch through the account bridge. */
export async function workshopArtwork(path: string, signal: AbortSignal): Promise<Blob> {
  if (!/^\/app\/v1\/archive\/owned\/[a-f0-9-]{36}\/assets\/[a-f0-9-]{36}$/i.test(path)) {
    throw new Error('Ilustrace nemá platnou chráněnou adresu.');
  }
  const response = await accountTransport(path, { signal });
  if (!response.ok) throw new Error('Ilustraci se nepodařilo načíst. Hůlka zůstává uložená.');
  return response.blob();
}
