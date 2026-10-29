import { isUuid } from '../archive/client';
import { account, accountDeletionRequested, accountRequest, accountTransport, offlineSession, signingOut } from '../identity/session';

export type ChronicleKind = 'yolo' | 'duel' | 'team' | 'boss' | 'case' | 'wand' | 'evening' | 'conversation';

export interface ChronicleParticipant {
  accountId: string;
  displayName: string;
  role: string;
  characterName?: string;
  wandName?: string;
  visualReferenceVersion: string;
}

export interface ChronicleAward {
  id: string;
  title: string;
  scope: string;
  evidence: string;
  eventId: string;
  acquiredAt: number;
}

export interface ChronicleDecision {
  accountId: string;
  choice: string;
  consequence: string;
  evidence?: string;
}

export interface ChronicleEvent {
  id: string;
  sourceKind: string;
  sourceId: string;
  groupId: string;
  groupName?: string | null;
  occurredAt: number;
  title: string;
  summary: string;
  participants: ChronicleParticipant[];
  result?: string;
  winnerId?: string;
  story: string[];
  decisions: ChronicleDecision[];
  awards: ChronicleAward[];
  illustrationState: 'none' | 'queued' | 'running' | 'ready' | 'failed' | string;
  revealed: boolean;
  shareText?: string;
  shareAllowed: boolean;
  revision: number;
  decisiveChoice?: string;
}

export interface ChronicleLockedEvent {
  id: string;
  sourceKind: string;
  sourceId: string;
  occurredAt: number;
  illustrationState: string;
  revealed: false;
}

export type ChronicleTimelineItem = ChronicleEvent | ChronicleLockedEvent;

export interface ChroniclePage {
  items: ChronicleTimelineItem[];
  nextCursor: string | null;
  badges: ChronicleAward[];
}

export interface ChronicleFilter {
  kind?: string;
  participant?: string;
  badge?: string;
  before?: string;
}

const kinds = new Set(['yolo', 'duel', 'team', 'boss', 'case', 'wand', 'evening', 'conversation']);
const states = new Set(['none', 'not_requested', 'deferred', 'queued', 'running', 'generating', 'awaiting_reconciliation', 'ready', 'failed']);
const safeChronicleIdentifier = (value: unknown): value is string => typeof value === 'string'
  && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$/.test(value);
export const isChronicleAccountId = (value: string): boolean => /^[0-9a-f]{32}$/i.test(value) || isUuid(value);
const sourceIdentifier = safeChronicleIdentifier;

function object(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

function boundedText(value: unknown, max = 2000): value is string {
  return typeof value === 'string' && value.length <= max && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(value);
}

function validTimestamp(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) >= -62135596800 && (value as number) <= 253402300799;
}

function parseAward(value: unknown): ChronicleAward | null {
  const row = object(value);
  if (!row || !boundedText(row.id, 120) || !boundedText(row.title, 120)
    || !boundedText(row.scope, 80) || !boundedText(row.evidence, 500)
    || !isUuid(row.eventId) || !validTimestamp(row.acquiredAt)) return null;
  return { id: row.id, title: row.title, scope: row.scope, evidence: row.evidence, eventId: row.eventId, acquiredAt: row.acquiredAt as number };
}

function parseParticipant(value: unknown): ChronicleParticipant | null {
  const row = object(value);
  if (!row || !safeChronicleIdentifier(row.accountId) || !boundedText(row.displayName, 120) || !boundedText(row.role, 80)
    || row.characterName !== undefined && !boundedText(row.characterName, 120)
    || row.wandName !== undefined && !boundedText(row.wandName, 120)
    || !boundedText(row.visualReferenceVersion, 120)) return null;
  return {
    accountId: row.accountId, displayName: row.displayName, role: row.role,
    ...(typeof row.characterName === 'string' ? { characterName: row.characterName } : {}),
    ...(typeof row.wandName === 'string' ? { wandName: row.wandName } : {}),
    visualReferenceVersion: row.visualReferenceVersion,
  };
}

function parseDecision(value: unknown): ChronicleDecision | null {
  const row = object(value);
  if (!row || !safeChronicleIdentifier(row.accountId) || !boundedText(row.choice, 160) || !boundedText(row.consequence, 4000)
    || row.evidence !== undefined && !boundedText(row.evidence, 500)) return null;
  return { accountId: row.accountId, choice: row.choice, consequence: row.consequence,
    ...(typeof row.evidence === 'string' ? { evidence: row.evidence } : {}) };
}

/** Locked placeholders have an intentionally smaller shape so hidden facts cannot enter the UI. */
export function parseChronicleItem(value: unknown): ChronicleTimelineItem | null {
  const row = object(value);
  if (!row || !isUuid(row.id) || typeof row.sourceKind !== 'string' || !kinds.has(row.sourceKind)
    || !sourceIdentifier(row.sourceId) || !validTimestamp(row.occurredAt)
    || typeof row.illustrationState !== 'string' || !states.has(row.illustrationState)
    || typeof row.revealed !== 'boolean') return null;
  if (!row.revealed) return { id: row.id, sourceKind: row.sourceKind, sourceId: row.sourceId,
    occurredAt: row.occurredAt as number, illustrationState: row.illustrationState, revealed: false };
  if (!boundedText(row.groupId, 100) || row.groupName !== undefined && row.groupName !== null && !boundedText(row.groupName, 120)
    || !boundedText(row.title, 160) || !boundedText(row.summary, 1000)
    || !Array.isArray(row.participants) || row.participants.length > 32
    || !Array.isArray(row.story) || row.story.length > 256 || row.story.some(text => !boundedText(text, 4000))
    || !Array.isArray(row.decisions) || row.decisions.length > 256
    || !Array.isArray(row.awards) || row.awards.length > 20
    || !Number.isSafeInteger(row.revision)
    || row.result !== undefined && !boundedText(row.result, 80)
    || row.winnerId !== undefined && !safeChronicleIdentifier(row.winnerId)
    || row.shareText !== undefined && !boundedText(row.shareText, 500)
    || row.decisiveChoice !== undefined && !boundedText(row.decisiveChoice, 1000)) return null;
  const participants = row.participants.map(parseParticipant);
  const decisions = row.decisions.map(parseDecision);
  const awards = row.awards.map(parseAward);
  if (participants.some(item => !item) || decisions.some(item => !item) || awards.some(item => !item)) return null;
  if (awards.some(item => item!.eventId !== row.id)) return null;
  return {
    id: row.id, sourceKind: row.sourceKind, sourceId: row.sourceId, groupId: row.groupId,
    ...(row.groupName === null || typeof row.groupName === 'string' ? { groupName: row.groupName as string | null } : {}),
    occurredAt: row.occurredAt as number, title: row.title, summary: row.summary,
    participants: participants as ChronicleParticipant[], story: row.story as string[],
    decisions: decisions as ChronicleDecision[], awards: awards as ChronicleAward[],
    illustrationState: row.illustrationState, revealed: true, revision: row.revision as number,
    ...(typeof row.result === 'string' ? { result: row.result } : {}),
    ...(typeof row.winnerId === 'string' ? { winnerId: row.winnerId } : {}),
    ...(typeof row.shareText === 'string' ? { shareText: row.shareText } : {}),
    shareAllowed: row.shareAllowed === true,
    ...(typeof row.decisiveChoice === 'string' ? { decisiveChoice: row.decisiveChoice } : {}),
  };
}

export function isChronicleEvent(item: ChronicleTimelineItem): item is ChronicleEvent {
  return item.revealed;
}

function parsePage(value: unknown): ChroniclePage {
  const row = object(value);
  if (!row || !Array.isArray(row.items) || row.items.length > 100
    || row.nextCursor !== null && row.nextCursor !== undefined && !boundedText(row.nextCursor, 1024)
    || !Array.isArray(row.badges) || row.badges.length > 100) throw new Error('Chronicle response is invalid.');
  const items = row.items.map(parseChronicleItem);
  const badges = row.badges.map(parseAward);
  if (items.some(item => !item) || badges.some(item => !item)) throw new Error('Chronicle response is invalid.');
  return { items: items as ChronicleTimelineItem[], nextCursor: typeof row.nextCursor === 'string' ? row.nextCursor : null,
    badges: badges as ChronicleAward[] };
}

export function chronicleCanRead(): boolean {
  return Boolean(account.value && !offlineSession.value && !signingOut.value && !accountDeletionRequested.value
    && typeof navigator !== 'undefined' && navigator.onLine);
}

export class ChronicleClient {
  async list(filter: ChronicleFilter, signal: AbortSignal): Promise<ChroniclePage> {
    const query = new URLSearchParams();
    if (filter.kind) query.set('kind', filter.kind);
    if (filter.participant) query.set('participant', filter.participant);
    if (filter.badge) query.set('badge', filter.badge);
    if (filter.before) query.set('before', filter.before);
    query.set('limit', '50');
    return parsePage(await accountRequest<unknown>(`/app/v1/chronicle?${query}`, 'GET', undefined, signal));
  }

  async source(kind: string, sourceId: string, signal: AbortSignal): Promise<ChronicleTimelineItem> {
    if (!kinds.has(kind) || !sourceIdentifier(sourceId)) throw new Error('Invalid chronicle source.');
    const value = await accountRequest<unknown>(`/app/v1/chronicle/source/${kind}/${sourceId}`, 'GET', undefined, signal);
    const row = object(value);
    const item = parseChronicleItem(row?.item ?? value);
    if (!item) throw new Error('Chronicle event is invalid.');
    return item;
  }

  async detail(id: string, signal: AbortSignal): Promise<ChronicleTimelineItem> {
    if (!isUuid(id)) throw new Error('Invalid chronicle event.');
    const value = await accountRequest<unknown>(`/app/v1/chronicle/${id}`, 'GET', undefined, signal);
    const row = object(value);
    const item = parseChronicleItem(row?.item ?? value);
    if (!item) throw new Error('Chronicle event is invalid.');
    return item;
  }

  async reveal(id: string, signal: AbortSignal): Promise<ChronicleEvent> {
    const value = await accountRequest<unknown>(`/app/v1/chronicle/${id}/reveal`, 'POST', {}, signal);
    const row = object(value);
    const item = parseChronicleItem(row?.item ?? value);
    if (!item?.revealed) throw new Error('Chronicle event has not been revealed.');
    return item;
  }

  async requestIllustration(id: string, signal: AbortSignal): Promise<void> {
    await accountRequest<unknown>(`/app/v1/chronicle/${id}/illustration`, 'POST', {}, signal);
  }

  async image(id: string, share = false, signal?: AbortSignal): Promise<Blob> {
    if (!isUuid(id)) throw new Error('Invalid chronicle event.');
    const path = `/app/v1/chronicle/${id}/${share ? 'share' : 'image'}`;
    return this.imagePath(path, signal);
  }

  async badgeImage(eventId: string, badgeId: string, signal?: AbortSignal): Promise<Blob> {
    if (!isUuid(eventId) || !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$/.test(badgeId)) throw new Error('Invalid chronicle badge.');
    return this.imagePath(`/app/v1/chronicle/${eventId}/badges/${badgeId}/image`, signal, ['image/png']);
  }

  private async imagePath(path: string, signal?: AbortSignal, allowed = ['image/png', 'image/jpeg', 'image/webp']): Promise<Blob> {
    const response = await accountTransport(path, { signal, cache: 'no-store', redirect: 'error' });
    if (!response.ok) throw new Error(`Chronicle image failed (${response.status}).`);
    const mediaType = response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase();
    if (!mediaType || !allowed.includes(mediaType)) {
      throw new Error('Chronicle image has an unsupported format.');
    }
    const blob = await response.blob();
    if (blob.size <= 0 || blob.size > 8 * 1024 * 1024) throw new Error('Chronicle image size is invalid.');
    return blob;
  }
}
