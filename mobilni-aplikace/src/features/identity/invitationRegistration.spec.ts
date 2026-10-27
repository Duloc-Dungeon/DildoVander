import { beforeEach, describe, expect, it, vi } from 'vitest';

const api = vi.hoisted(() => ({ request: vi.fn(), begin: vi.fn(), activeAccount: null as any }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  const currentAccount = ref({ id: 'account-a', openAiConsent: true });
  api.activeAccount = currentAccount;
  return { account: currentAccount, accountDeletionRequested: ref(false), accountRequest: api.request };
});
vi.mock('../launch/launchPresentation', () => ({ beginInvitationPresentation: api.begin }));

import {
  clearInvitationRegistration,
  getInvitationRegistration,
  invitationRegistration,
  saveInvitationCharacter,
  setInvitationDraft,
} from './invitationRegistration';

beforeEach(() => {
  localStorage.clear();
  invitationRegistration.value = null;
  api.activeAccount.value = { id: 'account-a', openAiConsent: true };
  api.request.mockReset();
  api.begin.mockReset();
});

describe('invitation registration persistence', () => {
  it('stores a per-account draft and never sends it without consent', async () => {
    const draft = setInvitationDraft('account-a', ' Luna ', '  Hogwarts  ');
    expect(draft.name).toBe('Luna');
    expect(draft.universe).toBe('Hogwarts');
    expect(getInvitationRegistration('account-a')?.creationId).toBe(draft.creationId);
    expect(getInvitationRegistration('account-b')).toBeNull();

    await saveInvitationCharacter('account-a', false);
    expect(api.request).not.toHaveBeenCalled();
    expect(api.begin).not.toHaveBeenCalled();
  });

  it('starts presentation immediately before the consented conditional PUT and keeps confirmation', async () => {
    api.request
      .mockResolvedValueOnce({ character: null })
      .mockResolvedValueOnce({ character: { profileId: 'profile-a', revision: 1, name: 'Luna', universe: 'Hogwarts', status: 'ready' } });
    const draft = setInvitationDraft('account-a', 'Luna', 'Hogwarts');

    const result = await saveInvitationCharacter('account-a', true);

    expect(api.request.mock.calls.map(call => call.slice(0, 2))).toEqual([
      ['/app/v1/prelaunch/character', 'GET'],
      ['/app/v1/prelaunch/character', 'PUT'],
    ]);
    expect(api.begin).toHaveBeenCalledWith('account-a', draft.creationId);
    expect(api.request.mock.calls[1][2]).toEqual({
      expectedRevision: 0,
      name: 'Luna',
      universe: 'Hogwarts',
      openAiConsent: true,
    });
    expect(result).toMatchObject({ status: 'confirmed', profileId: 'profile-a', revision: 1 });
    expect(getInvitationRegistration('account-a')?.status).toBe('confirmed');
    expect(await saveInvitationCharacter('account-a', true)).toMatchObject({ status: 'confirmed' });
    expect(api.request).toHaveBeenCalledTimes(2);
  });

  it('reconciles a timed out PUT and never blindly issues a second PUT', async () => {
    setInvitationDraft('account-a', 'Luna', 'Hogwarts');
    api.request
      .mockResolvedValueOnce({ character: null })
      .mockRejectedValueOnce(new Error('network timeout'))
      .mockResolvedValueOnce({ character: { profileId: 'profile-a', revision: 1, name: 'Luna', universe: 'Hogwarts' } });

    const result = await saveInvitationCharacter('account-a', true);
    expect(result).toMatchObject({ status: 'confirmed', profileId: 'profile-a', revision: 1 });
    expect(api.request).toHaveBeenCalledTimes(3);
    expect(api.request.mock.calls[2].slice(0, 2)).toEqual(['/app/v1/prelaunch/character', 'GET']);
  });

  it('isolates the visible snapshot across accounts and restores the same account draft', () => {
    const draft = setInvitationDraft('account-a', 'Luna', 'Hogwarts');
    api.activeAccount.value = { id: 'account-b' };
    expect(invitationRegistration.value).toBeNull();
    expect(getInvitationRegistration('account-a')?.creationId).toBe(draft.creationId);
    api.activeAccount.value = { id: 'account-a', openAiConsent: true };
    expect(invitationRegistration.value?.name).toBe('Luna');
  });

  it('clears only the requested account draft', () => {
    setInvitationDraft('account-a', 'Luna', 'Hogwarts');
    setInvitationDraft('account-b', 'Harry', 'Hogwarts');
    clearInvitationRegistration('account-a');
    expect(getInvitationRegistration('account-a')).toBeNull();
    expect(getInvitationRegistration('account-b')?.name).toBe('Harry');
  });
});

it('a late rejected preliminary read cannot resurrect a cleared draft after an account switch', async () => {
  let rejectRead!: (error: Error) => void;
  setInvitationDraft('account-a', 'Luna', 'Hogwarts');
  api.request.mockImplementation(() => new Promise((_resolve, reject) => { rejectRead = reject; }));
  const pending = saveInvitationCharacter('account-a', true);
  clearInvitationRegistration('account-a');
  api.activeAccount.value = { id: 'account-b' };
  rejectRead(new Error('offline'));
  await pending;
  expect(invitationRegistration.value).toBeNull();
  expect(getInvitationRegistration('account-a')).toBeNull();
});
it('a failed preliminary read never starts or persists a presentation before an actual PUT', async () => {
  setInvitationDraft('account-a', 'Luna', 'Hogwarts');
  api.request.mockRejectedValue(new Error('offline'));
  await saveInvitationCharacter('account-a', true);
  expect(getInvitationRegistration('account-a')?.writeStarted).toBe(false);
  expect(api.begin).not.toHaveBeenCalled();
});
