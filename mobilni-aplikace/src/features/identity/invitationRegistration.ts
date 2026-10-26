import { ref, watch } from 'vue';
import { account, accountDeletionRequested, accountRequest } from './session';
import { beginInvitationPresentation } from '../launch/launchPresentation';

export type InvitationRegistrationStatus = 'draft' | 'saving' | 'uncertain' | 'confirmed' | 'error';

export interface InvitationRegistrationSnapshot {
  owner: string;
  name: string;
  universe: string;
  expectedProfileId?: string;
  expectedRevision: number;
  status: InvitationRegistrationStatus;
  creationId: string;
  writeStarted?: boolean;
  profileId?: string;
  revision?: number;
  error?: string;
}

const storagePrefix = 'wand-night-stand:invitation-registration:';
export const invitationRegistration = ref<InvitationRegistrationSnapshot | null>(null);

function storage(): Storage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage; }
  catch { return undefined; }
}

function storageKey(owner: string): string { return `${storagePrefix}${encodeURIComponent(owner)}`; }

function save(snapshot: InvitationRegistrationSnapshot): void {
  invitationRegistration.value = snapshot;
  try { storage()?.setItem(storageKey(snapshot.owner), JSON.stringify(snapshot)); } catch { /* Keep the in-memory draft available. */ }
}

function newCreationId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export function setInvitationDraft(owner: string, name: string, universe: string): InvitationRegistrationSnapshot {
  const current = getInvitationRegistration(owner);
  const normalizedName = name.trim();
  const normalizedUniverse = universe.trim();
  const unchanged = current?.name === normalizedName && current.universe === normalizedUniverse;
  const snapshot: InvitationRegistrationSnapshot = {
    owner,
    name: normalizedName,
    universe: normalizedUniverse,
    expectedProfileId: unchanged ? current?.expectedProfileId : undefined,
    expectedRevision: unchanged ? current?.expectedRevision ?? 0 : 0,
    status: unchanged && (current?.status === 'saving' || current?.status === 'uncertain' || current?.status === 'confirmed') ? current.status : 'draft',
    creationId: unchanged ? current?.creationId ?? newCreationId() : newCreationId(),
    writeStarted: unchanged ? current?.writeStarted ?? false : false,
    profileId: unchanged ? current?.profileId : undefined,
    revision: unchanged ? current?.revision : undefined,
    error: unchanged ? current?.error : undefined,
  };
  save(snapshot);
  return snapshot;
}

export function getInvitationRegistration(owner: string): InvitationRegistrationSnapshot | null {
  if (invitationRegistration.value?.owner === owner) return invitationRegistration.value;
  try {
    const raw = storage()?.getItem(storageKey(owner));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as InvitationRegistrationSnapshot;
    if (parsed.owner !== owner || typeof parsed.name !== 'string' || typeof parsed.universe !== 'string' || typeof parsed.creationId !== 'string') return null;
    const recovered = parsed.status === 'saving' ? { ...parsed, status: 'uncertain' as const, error: 'reload_during_save' } : parsed;
    invitationRegistration.value = recovered;
    if (recovered !== parsed) save(recovered);
    return recovered;
  } catch { return null; }
}

export function clearInvitationRegistration(owner: string): void {
  try { storage()?.removeItem(storageKey(owner)); } catch { /* Storage may be unavailable. */ }
  if (invitationRegistration.value?.owner === owner) invitationRegistration.value = null;
}

interface PrelaunchState {
  character: { profileId: string; revision: number; name: string; universe: string; status: string } | null;
}

function matchesDraft(character: NonNullable<PrelaunchState['character']>, snapshot: InvitationRegistrationSnapshot): boolean {
  return character.name === snapshot.name && character.universe === snapshot.universe;
}

/** Save only after explicit consent. Reconcile uncertain writes before issuing another PUT. */
export function reconcileInvitationCharacter(owner: string, character: NonNullable<PrelaunchState['character']> | null): InvitationRegistrationSnapshot | null {
  const snapshot = getInvitationRegistration(owner);
  if (!snapshot || !character) return snapshot;
  const next = matchesDraft(character, snapshot)
    ? { ...snapshot, status: 'confirmed' as const, profileId: character.profileId, revision: character.revision, error: undefined }
    : { ...snapshot, status: 'error' as const, error: 'existing_character' };
  save(next);
  return next;
}

export async function saveInvitationCharacter(owner: string, consented: boolean): Promise<InvitationRegistrationSnapshot | null> {
  let snapshot = getInvitationRegistration(owner);
  if (!snapshot || account.value?.id !== owner || !consented || !snapshot.name || !snapshot.universe || snapshot.status === 'confirmed') return snapshot;
  if (snapshot.status === 'saving') return snapshot;

  if (snapshot.status === 'uncertain' || snapshot.status === 'error') {
    snapshot = { ...snapshot, status: 'uncertain', error: undefined };
    save(snapshot);
    try {
      const reconciled = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'GET');
      if (account.value?.id !== owner || invitationRegistration.value?.owner !== owner
      || invitationRegistration.value.creationId !== snapshot.creationId) return null;
      if (reconciled.character) {
        if (matchesDraft(reconciled.character, snapshot)) {
          snapshot = { ...snapshot, status: 'confirmed', profileId: reconciled.character.profileId, revision: reconciled.character.revision };
          save(snapshot);
          return snapshot;
        }
        snapshot = { ...snapshot, status: 'error', error: 'existing_character' };
        save(snapshot);
        return snapshot;
      }
    } catch {
      if (account.value?.id !== owner || invitationRegistration.value?.creationId !== snapshot.creationId) return null;
      snapshot = { ...snapshot, status: 'uncertain', error: 'reconcile_failed' };
      save(snapshot);
      return snapshot;
    }
  }

  // A fresh read protects an invitation save from replacing a character created elsewhere.
  let current: PrelaunchState;
  try { current = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'GET'); }
  catch {
    if (account.value?.id !== owner || invitationRegistration.value?.creationId !== snapshot.creationId) return null;
    snapshot = { ...snapshot, status: 'error', error: 'read_failed' };
    save(snapshot);
    return snapshot;
  }
  if (account.value?.id !== owner || invitationRegistration.value?.owner !== owner
      || invitationRegistration.value.creationId !== snapshot.creationId) return null;
  if (current.character) {
    snapshot = matchesDraft(current.character, snapshot)
      ? { ...snapshot, status: 'confirmed', profileId: current.character.profileId, revision: current.character.revision }
      : { ...snapshot, status: 'error', error: 'existing_character' };
    save(snapshot);
    return snapshot;
  }

  snapshot = { ...snapshot, status: 'saving', writeStarted: true, expectedProfileId: undefined, expectedRevision: 0, error: undefined };
  save(snapshot);
  beginInvitationPresentation(owner, snapshot.creationId);
  try {
    const result = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'PUT', {
      expectedRevision: 0,
      name: snapshot.name,
      universe: snapshot.universe,
      openAiConsent: true,
    });
    if (account.value?.id !== owner || invitationRegistration.value?.owner !== owner
      || invitationRegistration.value.creationId !== snapshot.creationId) return null;
    if (result.character && matchesDraft(result.character, snapshot)) {
      snapshot = { ...snapshot, status: 'confirmed', profileId: result.character.profileId, revision: result.character.revision };
      save(snapshot);
    } else {
      snapshot = { ...snapshot, status: 'uncertain', error: 'save_unconfirmed' };
      save(snapshot);
    }
  } catch (cause) {
    if (account.value?.id !== owner || invitationRegistration.value?.owner !== owner
      || invitationRegistration.value.creationId !== snapshot.creationId) return null;
    const status = cause && typeof cause === 'object' && 'status' in cause && typeof cause.status === 'number'
      && cause.status >= 400 && cause.status < 500 ? 'error' : 'uncertain';
    snapshot = { ...snapshot, status, error: status === 'error' ? 'request_rejected' : 'save_uncertain' };
    save(snapshot);
    // A response can be lost after the server commits. Reconcile; never blindly retry here.
    try {
      const reconciled = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'GET');
      if (account.value?.id !== owner || invitationRegistration.value?.owner !== owner
      || invitationRegistration.value.creationId !== snapshot.creationId) return null;
      if (reconciled.character && matchesDraft(reconciled.character, snapshot)) {
        snapshot = { ...snapshot, status: 'confirmed', profileId: reconciled.character.profileId, revision: reconciled.character.revision, error: undefined };
        save(snapshot);
      }
    } catch { /* Keep uncertain for a later explicit reconciliation. */ }
  }
  return snapshot;
}

let lastAccountId = account.value?.id ?? null;
watch(() => account.value?.id ?? null, owner => {
  invitationRegistration.value = owner ? getInvitationRegistration(owner) : null;
  if (owner) lastAccountId = owner;
}, { flush: 'sync' });
watch(accountDeletionRequested, deleted => {
  if (deleted && lastAccountId) clearInvitationRegistration(lastAccountId);
}, { flush: 'sync' });
